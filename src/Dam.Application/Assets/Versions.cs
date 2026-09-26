using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;

namespace Dam.Application.Assets;

internal static class VersionMapping
{
    public static AssetVersionDto ToDto(AssetVersion v, Guid? currentVersionId) =>
        new(v.Id, v.VersionNo, v.Id == currentVersionId, v.SizeBytes, v.Mime, v.Sha256, v.TechnicalMetadata, v.ChangeNote, v.CreatedAt);
}

/// <summary>All versions of an asset, newest first. Visible to anyone who may see the asset itself.</summary>
public sealed record ListVersionsQuery(Guid AssetId) : IQuery<IReadOnlyList<AssetVersionDto>>;

public sealed class ListVersionsHandler(
    IRepository<Asset> assets, IRepository<AssetVersion> versions, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListVersionsQuery, IReadOnlyList<AssetVersionDto>>
{
    public async Task<Result<IReadOnlyList<AssetVersionDto>>> Handle(ListVersionsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        var list = await q.ToListAsync(versions.Query().Where(v => v.AssetId == asset.Id).OrderByDescending(v => v.VersionNo), ct);
        return list.Select(v => VersionMapping.ToDto(v, asset.CurrentVersionId)).ToList();
    }
}

public sealed record GetVersionQuery(Guid AssetId, int VersionNo) : IQuery<AssetVersionDto>;

public sealed class GetVersionHandler(
    IRepository<Asset> assets, IRepository<AssetVersion> versions, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<GetVersionQuery, AssetVersionDto>
{
    public async Task<Result<AssetVersionDto>> Handle(GetVersionQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        var v = await q.FirstOrDefaultAsync(versions.Query().Where(x => x.AssetId == asset.Id && x.VersionNo == r.VersionNo), ct);
        return v is null ? Error.NotFound("Version not found.") : VersionMapping.ToDto(v, asset.CurrentVersionId);
    }
}

/// <summary>
/// Assets that look like this one (a resized or recompressed copy, a near-identical shot), by perceptual hash. Only assets the
/// caller may read are returned. The comparison scans hashes in memory, which suits tens of thousands of images; a database
/// index for millions is a later optimisation.
/// </summary>
public sealed record FindSimilarQuery(Guid AssetId, int? MaxDistance, int? Limit) : IQuery<IReadOnlyList<SimilarAssetDto>>;

public sealed class FindSimilarHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<FindSimilarQuery, IReadOnlyList<SimilarAssetDto>>
{
    private const int ScanCap = 200_000;

    public async Task<Result<IReadOnlyList<SimilarAssetDto>>> Handle(FindSimilarQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!PerceptualHash.TryParse(asset.Phash, out var mine))
            return Error.Conflict("This asset has no perceptual hash: it has no image version yet, or the image could not be decoded.");

        var maxDistance = Math.Clamp(r.MaxDistance ?? 8, 0, 32);
        var limit = Math.Clamp(r.Limit ?? 20, 1, 100);
        var candidates = await q.ToListAsync(assets.Query().Where(a => a.Phash != null && a.Id != asset.Id && a.Status != AssetStatuses.Deleted && a.Status != AssetStatuses.Blocked)
            .Select(a => new { a.Id, a.Phash }).Take(ScanCap), ct);

        var close = candidates
            .Select(c => PerceptualHash.TryParse(c.Phash, out var h) ? (c.Id, Distance: PerceptualHash.Distance(mine, h)) : (c.Id, Distance: int.MaxValue))
            .Where(c => c.Distance <= maxDistance).OrderBy(c => c.Distance).ThenBy(c => c.Id).Take(limit * 4).ToList();
        if (close.Count == 0) return new List<SimilarAssetDto>();

        // Only report what the caller is allowed to see (ADR-0002); a hidden asset must not be revealed by being "similar".
        var ids = close.Select(c => c.Id).ToList();
        var rows = await q.ToListAsync(assets.Query().Where(a => ids.Contains(a.Id)), ct);
        var pathById = (await q.ToListAsync(folders.Query(), ct)).ToDictionary(f => f.Id, f => f.Path);
        var result = new List<SimilarAssetDto>();
        foreach (var (id, distance) in close)
        {
            var a = rows.First(x => x.Id == id);
            var folderPath = a.FolderId is { } f && pathById.TryGetValue(f, out var p) ? p : null;
            if (!await authz.CanAsync(Permissions.AssetsRead, a.ToResource(folderPath), ct)) continue;
            result.Add(new SimilarAssetDto(a.Id, a.Title, a.Type, a.Status, a.FolderId, distance));
            if (result.Count == limit) break;
        }
        return result;
    }
}
