using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Licensing;
using Dam.Shared.Licensing;

namespace Dam.Application.Licensing;

/// <summary>
/// Assigns an asset's rights, or clears them with a null id. The asset's access attributes are re-derived at once (its metadata attributes narrowed by
/// the licence's channels and regions) and it is re-indexed. Needs <c>rights.manage</c> on the asset (an asset the caller cannot read is 404).
/// </summary>
public sealed record SetAssetRightsCommand(Guid AssetId, Guid? RightsId) : ICommand<AssetRightsDto>;

public sealed class SetAssetRightsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz, RightsGuard guard, IClock clock)
    : IRequestHandler<SetAssetRightsCommand, AssetRightsDto>
{
    public async Task<Result<AssetRightsDto>> Handle(SetAssetRightsCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.RightsManage, asset.ToResource(path), ct)) return Error.Forbidden();
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return Error.Conflict($"The asset is {asset.Status}.");

        Rights? rights = null;
        if (c.RightsId is { } id)
        {
            rights = await guard.FindAsync(id, ct);
            if (rights is null) return Error.Validation(ContentMapping.Fail("rightsId", "Rights record not found."));
            if (Refusal(asset, rights, guard.Today) is { } why) return Error.Conflict(why);
        }
        asset.AssignRights(rights?.Id, rights?.Channels ?? [], rights?.Regions ?? [], clock.UtcNow);

        var docCount = rights is null ? 0 : await q.CountAsync(documents.Query().Where(d => d.RightsId == rights.Id), ct);
        return new AssetRightsDto(asset.Id, rights is null ? null : RightsMapping.ToSummary(rights, docCount, manager: true, guard.Today));
    }

    /// <summary>A published asset cannot be put under a licence that is not in force today: it would be unavailable at once.</summary>
    internal static string? Refusal(Asset asset, Rights rights, DateOnly today)
    {
        if (asset.Status != AssetStatuses.Published) return null;
        var status = rights.Validity(today).Status;
        return status switch
        {
            RightsStatuses.Expired => $"The licence '{rights.Name}' ended on {rights.ValidTo:yyyy-MM-dd}; a published asset cannot be put under it.",
            RightsStatuses.NotYetValid => $"The licence '{rights.Name}' starts on {rights.ValidFrom:yyyy-MM-dd}; a published asset cannot be put under it yet.",
            _ => null,
        };
    }
}

/// <summary>
/// Assigns one rights record to many assets (up to 500). Every asset gets its own result: assigned; skipped (already has this record, has other rights and
/// <c>replace</c> is off, deleted or blocked, or the licence is not in force for a published asset); or failed (not found or not permitted).
/// </summary>
public sealed record AssignRightsCommand(Guid RightsId, AssignRightsInput Input) : ICommand<AssignRightsResultDto>;

public sealed class AssignRightsHandler(
    IRepository<Rights> rights, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<AssignRightsCommand, AssignRightsResultDto>
{
    public const int MaxAssets = 500;

    public async Task<Result<AssignRightsResultDto>> Handle(AssignRightsCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var record = await RightsAccess.FindAsync(rights, q, c.RightsId, ct);
        if (record is null) return Error.NotFound("Rights record not found.");

        var ids = (c.Input.AssetIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return Error.Validation(ContentMapping.Fail("assetIds", "Give at least one asset."));
        if (ids.Count > MaxAssets) return Error.Validation(ContentMapping.Fail("assetIds", $"At most {MaxAssets} assets at a time."));

        var now = clock.UtcNow;
        var today = RightsRules.Today(now);
        var items = new List<AssignRightsItemDto>();
        var otherNames = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, id, ct);
            if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) { items.Add(new(id, "failed", "Asset not found.")); continue; }
            if (!await authz.CanAsync(Permissions.RightsManage, asset.ToResource(path), ct)) { items.Add(new(id, "failed", "You may not manage rights for this asset.")); continue; }
            if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) { items.Add(new(id, "skipped", $"The asset is {asset.Status}.")); continue; }
            if (asset.RightsId == record.Id) { items.Add(new(id, "skipped", "Already assigned to this record.")); continue; }
            if (asset.RightsId is { } other && !c.Input.Replace)
            {
                if (!otherNames.TryGetValue(other, out var name))
                    otherNames[other] = name = (await q.FirstOrDefaultAsync(rights.Query().Where(r => r.Id == other).Select(r => r.Name), ct)) ?? "another record";
                items.Add(new(id, "skipped", $"Already has the rights '{name}'; set replace to change them."));
                continue;
            }
            if (SetAssetRightsHandler.Refusal(asset, record, today) is { } why) { items.Add(new(id, "skipped", why)); continue; }
            asset.AssignRights(record.Id, record.Channels, record.Regions, now);
            items.Add(new(id, "assigned", null));
        }
        return new AssignRightsResultDto(items.Count(i => i.Outcome == "assigned"), items.Count(i => i.Outcome == "skipped"), items.Count(i => i.Outcome == "failed"), items);
    }
}

// -------------------------------------------------------------------------------------------------- reading an asset's rights

/// <summary>The rights of an asset, for anyone who can read it. Only a caller with <c>rights.manage</c> also sees the licensor and the notes.</summary>
public sealed record GetAssetRightsQuery(Guid AssetId) : IQuery<AssetRightsDto>;

public sealed class GetAssetRightsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz, RightsGuard guard)
    : IRequestHandler<GetAssetRightsQuery, AssetRightsDto>
{
    public async Task<Result<AssetRightsDto>> Handle(GetAssetRightsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        var resource = asset?.ToResource(path);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Asset not found.");
        var rights = await guard.FindAsync(asset.RightsId, ct);
        if (rights is null) return new AssetRightsDto(asset.Id, null);
        var manager = await authz.CanAsync(Permissions.RightsManage, resource, ct);
        var docCount = await q.CountAsync(documents.Query().Where(d => d.RightsId == rights.Id), ct);
        return new AssetRightsDto(asset.Id, RightsMapping.ToSummary(rights, docCount, manager, guard.Today));
    }
}

/// <summary>May this asset be used on <paramref name="Channel"/> in <paramref name="Region"/> (both optional), and why not. Needs <c>assets.read</c> on the asset.</summary>
public sealed record CheckAssetRightsQuery(Guid AssetId, string? Channel, string? Region) : IQuery<RightsCheckDto>;

public sealed class CheckAssetRightsHandler(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, RightsGuard guard)
    : IRequestHandler<CheckAssetRightsQuery, RightsCheckDto>
{
    public async Task<Result<RightsCheckDto>> Handle(CheckAssetRightsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        var channel = r.Channel?.Trim();
        var region = r.Region?.Trim();
        var errors = ConsumerContext.Validate(channel, region);
        if (errors.Count > 0) return Error.Validation(errors);

        var check = await guard.CheckAsync(asset, channel, region, ct);
        var rights = await guard.FindAsync(asset.RightsId, ct);
        var (channels, noChannels) = RightsScope.ForDisplay(asset.Attributes, Dimensions.Channel);
        var (regions, noRegions) = RightsScope.ForDisplay(asset.Attributes, Dimensions.Region);
        return new RightsCheckDto(check.Allowed, check.Reasons, check.Status, check.WatermarkRequired, rights?.ValidTo, channels, regions, noChannels, noRegions);
    }
}

/// <summary>The consumer's channel and region as sent on a check or a link request: short codes, nothing else.</summary>
public static class ConsumerContext
{
    public static Dictionary<string, string[]> Validate(string? channel, string? region)
    {
        var errors = new Dictionary<string, string[]>();
        if (!string.IsNullOrEmpty(channel) && !RightsRules.IsSlug(channel.ToLowerInvariant())) errors["channel"] = ["Use letters, digits, '-' and '_' (up to 50 characters)."];
        if (!string.IsNullOrEmpty(region) && !RightsRules.IsSlug(region.ToLowerInvariant())) errors["region"] = ["Use letters, digits, '-' and '_' (up to 50 characters)."];
        return errors;
    }
}
