using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

// ---------------------------------------------------------------------------------------------------- asset scheduling

/// <summary>Sets or clears when an asset should publish or expire itself (spec Step 1.6b). A dedicated action, not folded
/// into the general update, so "clear the schedule" (pass null) is unambiguous: PATCH's convention is that a null field
/// means "leave it alone", which would make a schedule impossible to cancel.</summary>
public sealed record ScheduleAssetCommand(Guid AssetId, DateTimeOffset? PublishAt, DateTimeOffset? ExpireAt) : ICommand<AssetDto>;

public sealed class ScheduleAssetHandler(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<ScheduleAssetCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(ScheduleAssetCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.AssetsUpdate, asset.ToResource(path), ct)) return Error.Forbidden();
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return Error.Conflict($"The asset is {asset.Status}.");
        asset.SetSchedule(c.PublishAt, c.ExpireAt, clock.UtcNow);
        return AssetMapping.ToDto(asset);
    }
}

/// <summary>Assigns or clears an asset's retention policy. Separate from scheduling and from ordinary updates because it is
/// a retention decision (spec RetentionPolicy), gated by retention.manage rather than assets.update.</summary>
public sealed record SetAssetRetentionPolicyCommand(Guid AssetId, Guid? RetentionPolicyId) : ICommand<AssetDto>;

public sealed class SetAssetRetentionPolicyHandler(
    IRepository<Asset> assets, IRepository<RetentionPolicy> policies, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<SetAssetRetentionPolicyCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(SetAssetRetentionPolicyCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.RetentionManage, asset.ToResource(path), ct)) return Error.Forbidden();
        if (c.RetentionPolicyId is { } id && await q.FirstOrDefaultAsync(policies.Query().Where(p => p.Id == id), ct) is null)
            return Error.Validation(ContentMapping.Fail("retentionPolicyId", "Retention policy not found."));
        asset.SetRetentionPolicy(c.RetentionPolicyId, clock.UtcNow);
        return AssetMapping.ToDto(asset);
    }
}

/// <summary>Toggles legal hold, which blocks the retention job's purge step regardless of policy (unless the policy is
/// itself marked legal-hold-exempt).</summary>
public sealed record SetLegalHoldCommand(Guid AssetId, bool Hold) : ICommand<AssetDto>;

public sealed class SetLegalHoldHandler(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<SetLegalHoldCommand, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(SetLegalHoldCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.RetentionManage, asset.ToResource(path), ct)) return Error.Forbidden();
        asset.SetLegalHold(c.Hold, clock.UtcNow);
        return AssetMapping.ToDto(asset);
    }
}

// ------------------------------------------------------------------------------------------------- retention policies

public sealed record CreateRetentionPolicyCommand(string Name, int? ArchiveAfterDays, int? DeleteAfterDays, bool LegalHoldExempt) : ICommand<RetentionPolicyDto>;

public sealed class RetentionPolicyValidator : AbstractValidator<CreateRetentionPolicyCommand>
{
    public RetentionPolicyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ArchiveAfterDays).GreaterThan(0).When(x => x.ArchiveAfterDays is not null);
        RuleFor(x => x.DeleteAfterDays).GreaterThan(0).When(x => x.DeleteAfterDays is not null);
    }
}

public sealed class CreateRetentionPolicyHandler(IRepository<RetentionPolicy> policies, IAuthorizationService authz, ITenantContext tenant, ICurrentUser me, IClock clock)
    : IRequestHandler<CreateRetentionPolicyCommand, RetentionPolicyDto>
{
    public async Task<Result<RetentionPolicyDto>> Handle(CreateRetentionPolicyCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RetentionManage, ct: ct)) return Error.Forbidden();
        var policy = RetentionPolicy.Create(tenant.TenantId, c.Name, c.ArchiveAfterDays, c.DeleteAfterDays, c.LegalHoldExempt, me.UserId, clock.UtcNow);
        policies.Add(policy);
        return PolicyMapping.ToDto(policy);
    }
}

public sealed record UpdateRetentionPolicyCommand(Guid Id, string Name, int? ArchiveAfterDays, int? DeleteAfterDays, bool LegalHoldExempt) : ICommand<RetentionPolicyDto>;

public sealed class UpdateRetentionPolicyValidator : AbstractValidator<UpdateRetentionPolicyCommand>
{
    public UpdateRetentionPolicyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ArchiveAfterDays).GreaterThan(0).When(x => x.ArchiveAfterDays is not null);
        RuleFor(x => x.DeleteAfterDays).GreaterThan(0).When(x => x.DeleteAfterDays is not null);
    }
}

public sealed class UpdateRetentionPolicyHandler(IRepository<RetentionPolicy> policies, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<UpdateRetentionPolicyCommand, RetentionPolicyDto>
{
    public async Task<Result<RetentionPolicyDto>> Handle(UpdateRetentionPolicyCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RetentionManage, ct: ct)) return Error.Forbidden();
        var policy = await q.FirstOrDefaultAsync(policies.Query().Where(p => p.Id == c.Id), ct);
        if (policy is null) return Error.NotFound("Retention policy not found.");
        policy.Update(c.Name, c.ArchiveAfterDays, c.DeleteAfterDays, c.LegalHoldExempt);
        return PolicyMapping.ToDto(policy);
    }
}

public sealed record DeleteRetentionPolicyCommand(Guid Id) : ICommand<Guid>;

public sealed class DeleteRetentionPolicyHandler(IRepository<RetentionPolicy> policies, IRepository<Asset> assets, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<DeleteRetentionPolicyCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteRetentionPolicyCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RetentionManage, ct: ct)) return Error.Forbidden();
        var policy = await q.FirstOrDefaultAsync(policies.Query().Where(p => p.Id == c.Id), ct);
        if (policy is null) return Error.NotFound("Retention policy not found.");
        if (await q.AnyAsync(assets.Query().Where(a => a.RetentionPolicyId == c.Id), ct))
            return Error.Conflict("This policy is still assigned to one or more assets.");
        policies.Remove(policy);
        return c.Id;
    }
}

public sealed record GetRetentionPolicyQuery(Guid Id) : IQuery<RetentionPolicyDto>;

public sealed class GetRetentionPolicyHandler(IRepository<RetentionPolicy> policies, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<GetRetentionPolicyQuery, RetentionPolicyDto>
{
    public async Task<Result<RetentionPolicyDto>> Handle(GetRetentionPolicyQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RetentionManage, ct: ct)) return Error.Forbidden();
        var policy = await q.FirstOrDefaultAsync(policies.Query().Where(p => p.Id == r.Id), ct);
        return policy is null ? Error.NotFound("Retention policy not found.") : PolicyMapping.ToDto(policy);
    }
}

public sealed record ListRetentionPoliciesQuery : IQuery<IReadOnlyList<RetentionPolicyDto>>;

public sealed class ListRetentionPoliciesHandler(IRepository<RetentionPolicy> policies, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListRetentionPoliciesQuery, IReadOnlyList<RetentionPolicyDto>>
{
    public async Task<Result<IReadOnlyList<RetentionPolicyDto>>> Handle(ListRetentionPoliciesQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RetentionManage, ct: ct)) return Error.Forbidden();
        var list = await q.ToListAsync(policies.Query().OrderBy(p => p.Name), ct);
        return list.Select(PolicyMapping.ToDto).ToList();
    }
}

internal static class PolicyMapping
{
    public static RetentionPolicyDto ToDto(RetentionPolicy p) => new(p.Id, p.Name, p.ArchiveAfterDays, p.DeleteAfterDays, p.LegalHoldExempt, p.CreatedAt);
}
