using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Identity;

public sealed record ListUsersQuery(string? Cursor, int? Limit, string? Status, string? Search) : IQuery<Page<UserDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.UsersManage;
}

public sealed class ListUsersHandler(IRepository<User> users, IQueryExecutor q, IdentityReader reader)
    : IRequestHandler<ListUsersQuery, Page<UserDto>>
{
    public async Task<Result<Page<UserDto>>> Handle(ListUsersQuery r, CancellationToken ct)
    {
        var limit = Paging.Clamp(r.Limit);
        var query = users.Query();
        if (Enum.TryParse<UserStatus>(r.Status, ignoreCase: true, out var status)) query = query.Where(u => u.Status == status);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var s = r.Search.Trim().ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(s) || u.DisplayName.ToLower().Contains(s));
        }
        if (Paging.ParseCursor(r.Cursor) is { } after) query = query.Where(u => u.Id.CompareTo(after) > 0);

        var page = await q.ToListAsync(query.OrderBy(u => u.Id).Take(limit + 1), ct);
        var more = page.Count > limit;
        if (more) page.RemoveAt(limit);
        return new Page<UserDto>(await reader.ToDtosAsync(page, ct), more ? page[^1].Id.ToString() : null);
    }
}

/// <summary>
/// The tenant's people for pickers (report recipients, workflow assignees): active users only, names and email only. Open to anyone who holds a
/// permission that needs to choose people, so a Librarian can do it without being able to manage users. Tenant isolation is the database's (RLS).
/// </summary>
public sealed record ListUserDirectoryQuery(string? Q, int? Limit, string? Cursor) : IQuery<Page<DirectoryUserDto>>;

public sealed class ListUserDirectoryHandler(IRepository<User> users, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListUserDirectoryQuery, Page<DirectoryUserDto>>
{
    public const int DefaultLimit = 20, MaxLimit = 50, MaxQueryLength = 100;

    /// <summary>Permissions that need to choose people. Holding any one (unscoped, through a role) opens the directory.</summary>
    public static readonly IReadOnlyList<string> Needs =
        [Permissions.UsersManage, Permissions.WorkflowManage, Permissions.AnalyticsManage, Permissions.AssetsUpdate, Permissions.CommentsWrite];

    public async Task<Result<Page<DirectoryUserDto>>> Handle(ListUserDirectoryQuery r, CancellationToken ct)
    {
        var allowed = false;
        foreach (var p in Needs)
            if (await authz.CanAsync(p, null, ct)) { allowed = true; break; }
        if (!allowed) return Error.Forbidden("Choosing people needs one of: " + string.Join(", ", Needs) + ".");

        var limit = Math.Clamp(r.Limit ?? DefaultLimit, 1, MaxLimit);
        var query = users.Query().Where(u => u.Status == UserStatus.Active);
        var needle = (r.Q ?? "").Trim().ToLowerInvariant();
        if (needle.Length > MaxQueryLength) needle = needle[..MaxQueryLength];
        if (needle.Length > 0) query = query.Where(u => u.DisplayName.ToLower().Contains(needle) || u.Email.ToLower().Contains(needle));

        if (r.Cursor is { Length: > 0 })
        {
            if (!DirectoryCursor.TryDecode(r.Cursor, out var name, out var id))
                return Error.Validation(new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid."] });
            query = query.Where(u => u.DisplayName.ToLower().CompareTo(name) > 0 || (u.DisplayName.ToLower() == name && u.Id.CompareTo(id) > 0));
        }

        var rows = await q.ToListAsync(
            query.OrderBy(u => u.DisplayName.ToLower()).ThenBy(u => u.Id).Take(limit + 1).Select(u => new { u.Id, u.DisplayName, u.Email, Key = u.DisplayName.ToLower() }), ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        var items = rows.Select(u => new DirectoryUserDto(u.Id, string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName, u.Email)).ToList();
        return new Page<DirectoryUserDto>(items, more ? DirectoryCursor.Encode(rows[^1].Key, rows[^1].Id) : null);
    }
}

/// <summary>Opaque keyset cursor: the last row's sort key (the database's lower-cased display name) and id.</summary>
internal static class DirectoryCursor
{
    public static string Encode(string sortKey, Guid id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id.ToString("N") + sortKey)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static bool TryDecode(string cursor, out string name, out Guid id)
    {
        name = ""; id = Guid.Empty;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            if (text.Length < 32 || !Guid.TryParseExact(text[..32], "N", out id)) return false;
            name = text[32..];
            return true;
        }
        catch (FormatException) { return false; }
    }
}

public sealed record GetUserQuery(Guid Id) : IQuery<UserDto>, IAuthorizedRequest
{
    public string Permission => Permissions.UsersManage;
}

public sealed class GetUserHandler(IRepository<User> users, IQueryExecutor q, IdentityReader reader) : IRequestHandler<GetUserQuery, UserDto>
{
    public async Task<Result<UserDto>> Handle(GetUserQuery r, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(users.Query().Where(u => u.Id == r.Id), ct) is { } u
            ? (await reader.ToDtosAsync([u], ct))[0]
            : Error.NotFound("User not found.");
}

/// <summary>Enable or disable a user. Profile and attributes come from the identity provider and are not editable here.</summary>
public sealed record SetUserStatusCommand(Guid Id, string Status) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.UsersManage;
}

public sealed class SetUserStatusValidator : AbstractValidator<SetUserStatusCommand>
{
    public SetUserStatusValidator() =>
        RuleFor(x => x.Status).Must(s => Enum.TryParse<UserStatus>(s, true, out _)).WithMessage("Status must be 'active' or 'disabled'.");
}

public sealed class SetUserStatusHandler(
    IRepository<User> users, IQueryExecutor q, ICurrentUser me, IEventCollector events)
    : IRequestHandler<SetUserStatusCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SetUserStatusCommand c, CancellationToken ct)
    {
        var u = await q.FirstOrDefaultAsync(users.Query().Where(x => x.Id == c.Id), ct);
        if (u is null) return Error.NotFound("User not found.");
        var status = Enum.Parse<UserStatus>(c.Status, true);
        if (status == UserStatus.Disabled && u.ExternalSubject == me.Subject) return Error.Conflict("You cannot disable your own account.");
        u.SetStatus(status);
        events.Raise(Events.Changed(status == UserStatus.Disabled ? "user.disabled" : "user.enabled", "user", u.Id));
        return u.Id;
    }
}

/// <summary>Replaces the user's directly assigned roles (roles that come from groups or the token are unaffected).</summary>
public sealed record SetUserRolesCommand(Guid Id, IReadOnlyList<Guid> RoleIds) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.UsersManage;
}

public sealed class SetUserRolesHandler(
    IRepository<User> users, IRepository<Role> roles, IRepository<UserRole> userRoles, IQueryExecutor q,
    ITenantContext tenant, IEventCollector events)
    : IRequestHandler<SetUserRolesCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SetUserRolesCommand c, CancellationToken ct)
    {
        var u = await q.FirstOrDefaultAsync(users.Query().Where(x => x.Id == c.Id), ct);
        if (u is null) return Error.NotFound("User not found.");
        var wanted = c.RoleIds.Distinct().ToList();
        var found = await q.CountAsync(roles.Query().Where(r => wanted.Contains(r.Id)), ct);
        if (found != wanted.Count) return Error.Validation(new Dictionary<string, string[]> { ["roleIds"] = ["One or more roles do not exist."] });

        var current = await q.ToListAsync(userRoles.Query().Where(x => x.UserId == u.Id), ct);
        userRoles.RemoveRange(current.Where(x => !wanted.Contains(x.RoleId)));
        userRoles.AddRange(wanted.Where(id => current.All(x => x.RoleId != id))
            .Select(id => new UserRole { TenantId = tenant.TenantId, UserId = u.Id, RoleId = id }));
        events.Raise(Events.Changed("user.roles.changed", "user", u.Id));
        return u.Id;
    }
}
