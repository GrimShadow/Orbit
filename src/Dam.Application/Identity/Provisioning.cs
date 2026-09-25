using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using FluentValidation;

namespace Dam.Application.Identity;

/// <summary>Creates any built-in role that is missing and brings existing built-ins up to the definition in code.</summary>
public sealed class BuiltInRoleSeeder(IRepository<Role> roles, IQueryExecutor q, ITenantContext tenant)
{
    public async Task EnsureAsync(CancellationToken ct)
    {
        var existing = await q.ToListAsync(roles.Query().Where(r => r.IsBuiltIn), ct);
        foreach (var def in BuiltInRoles.All)
        {
            var current = existing.FirstOrDefault(r => string.Equals(r.Name, def.Name, StringComparison.OrdinalIgnoreCase));
            if (current is null) roles.Add(Role.FromBuiltIn(tenant.TenantId, def));
            else current.SyncFromBuiltIn(def);
        }
    }
}

/// <summary>Idempotent: creates the tenant if absent and seeds its built-in roles. Used at startup, not exposed over HTTP.</summary>
public sealed record EnsureTenantCommand(Guid Id, string Name, string Slug) : ICommand<Guid>;

public sealed class EnsureTenantValidator : AbstractValidator<EnsureTenantCommand>
{
    public EnsureTenantValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9][a-z0-9-]{1,62}$").WithMessage("Use lowercase letters, digits and dashes.");
    }
}

public sealed class EnsureTenantHandler(
    IRepository<Tenant> tenants, IQueryExecutor q, BuiltInRoleSeeder seeder, IClock clock, IEventCollector events)
    : IRequestHandler<EnsureTenantCommand, Guid>
{
    public async Task<Result<Guid>> Handle(EnsureTenantCommand c, CancellationToken ct)
    {
        if (!await q.AnyAsync(tenants.Query().Where(t => t.Id == c.Id), ct))
        {
            tenants.Add(Tenant.Create(c.Id, c.Name, c.Slug, clock.UtcNow));
            events.Raise(Events.Changed("tenant.created", "tenant", c.Id));
        }
        await seeder.EnsureAsync(ct);
        return c.Id;
    }
}

/// <summary>Just-in-time provisioning from validated token claims. The identity provider stays the source of truth.</summary>
public sealed record ProvisionUserCommand(
    string Subject, string Email, string DisplayName,
    IReadOnlyDictionary<string, string[]> Attributes, IReadOnlyList<string> Groups) : ICommand<ProvisionedUser>;

public sealed record ProvisionedUser(Guid UserId, bool Created, bool Changed);

public sealed class ProvisionUserValidator : AbstractValidator<ProvisionUserCommand>
{
    public ProvisionUserValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).MaximumLength(320);
        RuleFor(x => x.DisplayName).MaximumLength(200);
        RuleFor(x => x.Groups).Must(g => g.Count <= 200).WithMessage("Too many groups in token.");
    }
}

public sealed class ProvisionUserHandler(
    IRepository<User> users, IRepository<Group> groups, IRepository<UserGroup> memberships, IRepository<Tenant> tenants,
    IQueryExecutor q, ITenantContext tenant, IClock clock, BuiltInRoleSeeder seeder)
    : IRequestHandler<ProvisionUserCommand, ProvisionedUser>
{
    public async Task<Result<ProvisionedUser>> Handle(ProvisionUserCommand c, CancellationToken ct)
    {
        // The tenant claim comes from a signed token, but the tenant must also exist here (RLS shows only its own row).
        if (!await q.AnyAsync(tenants.Query(), ct)) return Error.Forbidden("Unknown tenant.");

        var email = c.Email.Trim();
        var name = string.IsNullOrWhiteSpace(c.DisplayName) ? (email.Length > 0 ? email : c.Subject) : c.DisplayName.Trim();
        var attrs = c.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value);
        var now = clock.UtcNow;

        var user = await q.FirstOrDefaultAsync(users.Query().Where(u => u.ExternalSubject == c.Subject), ct);
        var created = user is null;
        var changed = created;
        if (user is null)
        {
            user = User.Provision(tenant.TenantId, c.Subject, email, name, attrs, now);
            users.Add(user);
            await seeder.EnsureAsync(ct); // first user of a tenant: make sure the built-in roles exist
        }
        else changed |= user.SyncFromIdp(email, name, attrs, now);

        changed |= await SyncGroupsAsync(user, c.Groups, ct);
        return new ProvisionedUser(user.Id, created, changed);
    }

    /// <summary>Mirrors the token's groups into IdP-sourced groups; memberships in local groups are left alone.</summary>
    private async Task<bool> SyncGroupsAsync(User user, IReadOnlyList<string> tokenGroups, CancellationToken ct)
    {
        var wanted = tokenGroups.Select(g => g.Trim().TrimStart('/')).Where(g => g.Length is > 0 and <= 200)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var idpGroups = await q.ToListAsync(groups.Query().Where(g => g.Source == GroupSource.Idp), ct);
        foreach (var name in wanted.Where(n => !idpGroups.Any(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase))))
        {
            var g = Group.Create(tenant.TenantId, name, GroupSource.Idp);
            groups.Add(g);
            idpGroups.Add(g);
        }
        var wantedIds = idpGroups.Where(g => wanted.Contains(g.Name, StringComparer.OrdinalIgnoreCase)).Select(g => g.Id).ToHashSet();
        var idpIds = idpGroups.Select(g => g.Id).ToHashSet();

        var mine = await q.ToListAsync(memberships.Query().Where(m => m.UserId == user.Id), ct);
        var stale = mine.Where(m => idpIds.Contains(m.GroupId) && !wantedIds.Contains(m.GroupId)).ToList();
        var missing = wantedIds.Where(id => mine.All(m => m.GroupId != id)).ToList();

        memberships.RemoveRange(stale);
        memberships.AddRange(missing.Select(id => new UserGroup { TenantId = tenant.TenantId, UserId = user.Id, GroupId = id }));
        return stale.Count > 0 || missing.Count > 0;
    }
}
