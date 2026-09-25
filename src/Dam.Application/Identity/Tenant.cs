using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Identity;

internal static class TenantMapping
{
    public static TenantDto ToDto(Tenant t) =>
        new(t.Id, t.Name, t.Slug, t.Status.ToString().ToLower(), JsonDocument.Parse(t.SettingsJson).RootElement.Clone(), t.CreatedAt);
}

/// <summary>Any signed-in user may read their tenant's name (the console shows it).</summary>
public sealed record GetCurrentTenantQuery : IQuery<TenantDto>;

public sealed class GetCurrentTenantHandler(IRepository<Tenant> tenants, IQueryExecutor q)
    : IRequestHandler<GetCurrentTenantQuery, TenantDto>
{
    public async Task<Result<TenantDto>> Handle(GetCurrentTenantQuery request, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(tenants.Query(), ct) is { } t ? TenantMapping.ToDto(t) : Error.NotFound("Tenant not found.");
}

public sealed record UpdateTenantCommand(string? Name, JsonElement? Settings) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.TenantsManage;
}

public sealed class UpdateTenantValidator : AbstractValidator<UpdateTenantCommand>
{
    public UpdateTenantValidator()
    {
        RuleFor(x => x.Name).MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Settings).Must(s => s is null || s.Value.ValueKind == JsonValueKind.Object)
            .WithMessage("Settings must be a JSON object.");
        RuleFor(x => x.Settings).Must(s => s is null || s.Value.GetRawText().Length <= 32_000).WithMessage("Settings are too large.");
    }
}

public sealed class UpdateTenantHandler(IRepository<Tenant> tenants, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<UpdateTenantCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateTenantCommand c, CancellationToken ct)
    {
        var t = await q.FirstOrDefaultAsync(tenants.Query(), ct);
        if (t is null) return Error.NotFound("Tenant not found.");
        t.Update(c.Name, c.Settings?.GetRawText(), null);
        events.Raise(Events.Changed("tenant.updated", "tenant", t.Id));
        return t.Id;
    }
}
