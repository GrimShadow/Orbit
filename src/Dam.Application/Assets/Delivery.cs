using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;

namespace Dam.Application.Assets;

/// <summary>
/// Signs links to an asset's current version (spec A5 "Delivery"). The link carries what it may fetch, when it expires and,
/// optionally, the one address that may use it. Assets that are not published get watermarked output.
/// </summary>
public sealed record CreateDeliveryLinkCommand(Guid AssetId, CreateLinkRequest Request) : ICommand<DeliveryLinkDto>;

public sealed class CreateDeliveryLinkHandler(
    IRepository<Asset> assets, IRepository<AssetVersion> versions, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz,
    DeliveryTokenSigner signer, DeliverySettings settings, ITenantContext tenant, IClock clock)
    : IRequestHandler<CreateDeliveryLinkCommand, DeliveryLinkDto>
{
    private static string? Text(int? n) => n?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<Result<DeliveryLinkDto>> Handle(CreateDeliveryLinkCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        var resource = asset?.ToResource(path);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Asset not found.");
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return Error.NotFound("Asset not found.");
        if (asset.CurrentVersionId is null) return Error.Conflict("The asset has no file yet.");
        var version = await q.FirstOrDefaultAsync(versions.Query().Where(v => v.Id == asset.CurrentVersionId), ct);
        var kind = version is null ? null : RenditionProfiles.KindOf(version.Mime);

        var r = c.Request;
        var problems = new Dictionary<string, string[]>();

        var ttl = r.TtlSeconds is { } s ? TimeSpan.FromSeconds(s) : settings.DefaultTokenTtl;
        if (ttl <= TimeSpan.Zero || ttl > settings.MaxTokenTtl) problems["ttlSeconds"] = [$"Must be between 1 and {(int)settings.MaxTokenTtl.TotalSeconds} seconds."];
        if (r.Ip is not null && !System.Net.IPAddress.TryParse(r.Ip, out _)) problems["ip"] = ["Not a valid IP address."];

        string scope;
        List<string> names;
        if (r.Transform is { } t)
        {
            var (spec, error) = TransformSpec.Parse(Text(t.W), Text(t.H), t.Fit, t.Fmt, Text(t.Q), t.Focal);
            if (spec is null) { problems["transform"] = [error!]; scope = ""; names = []; }
            else { scope = "t:" + spec.Canonical(); names = ["transform"]; }
        }
        else
        {
            names = (r.Renditions is { Count: > 0 } ? r.Renditions : ["preview"]).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
            var unknown = names.Where(n => !RenditionProfiles.IsKnown(n)).ToList();
            if (unknown.Count > 0) problems["renditions"] = [$"Unknown: {string.Join(", ", unknown)}."];
            scope = "r:" + string.Join(',', names);
        }
        if (problems.Count > 0) return Error.Validation(problems);

        if (names.Contains(RenditionProfiles.Original) && !await authz.CanAsync(Permissions.AssetsDownloadOriginal, resource, ct))
            return Error.Forbidden("You may not download the original file of this asset.");

        var watermark = asset.Status != AssetStatuses.Published;
        if (watermark && names.Contains(RenditionProfiles.Original))
            return Error.Conflict("The original file of an unpublished asset cannot be shared by link; other renditions carry a watermark.");
        if (watermark && kind == SourceKind.Video && names.Any(n => RenditionProfiles.Videos.Any(v => v.Name == n)))
            return Error.Conflict("Video renditions of an unpublished asset are not available yet; only its still images (thumb, poster) carry a watermark.");
        var expires = clock.UtcNow.Add(ttl);
        var token = signer.Sign(new DeliveryClaims(tenant.TenantId, asset.Id, scope, expires, r.Ip, watermark));
        var baseUrl = $"{settings.LinkBase}/d/{token}/{asset.Id}";
        var links = r.Transform is not null
            ? new Dictionary<string, string> { ["transform"] = $"{baseUrl}/t?{scope[2..]}" }
            : names.ToDictionary(n => n, n => $"{baseUrl}/{n}");
        return new DeliveryLinkDto(expires, watermark, links);
    }
}

/// <summary>
/// Signed thumbnail links for many assets in one call - what a library/search grid needs to show real image thumbnails
/// instead of one <c>POST /assets/{id}/links</c> per card (which doesn't scale to a grid of dozens). Deliberately
/// narrow: always the "thumb" profile (the one name shared by every renderable kind - image, video poster, PDF/Office
/// first page, spec Step 1.5), no transform, no IP binding, the default TTL. A generic "batch anything" endpoint is
/// not needed here and would just widen the surface for no current caller.
///
/// An asset that cannot be shown a thumbnail for any reason - not found, no read permission, deleted/blocked, no
/// version yet - maps to a null link rather than failing the whole batch; a grid always has some assets like this
/// (still uploading, no permission on a mixed selection) and one bad id must not blank out every other thumbnail.
/// </summary>
/// <remarks>Keyed by the asset id's string form, not <see cref="Guid"/> itself - a plain string key serializes
/// unambiguously as JSON object property names; a Guid-keyed dictionary's wire format is not worth relying on.</remarks>
public sealed record CreateThumbnailLinksBatchCommand(IReadOnlyList<Guid> AssetIds) : ICommand<IReadOnlyDictionary<string, string?>>;

public sealed class CreateThumbnailLinksBatchHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz,
    DeliveryTokenSigner signer, DeliverySettings settings, ITenantContext tenant, IClock clock)
    : IRequestHandler<CreateThumbnailLinksBatchCommand, IReadOnlyDictionary<string, string?>>
{
    private const int MaxBatchSize = 200;
    private const string ThumbProfile = "thumb";

    public async Task<Result<IReadOnlyDictionary<string, string?>>> Handle(CreateThumbnailLinksBatchCommand c, CancellationToken ct)
    {
        if (c.AssetIds.Count > MaxBatchSize)
            return Error.Validation(new Dictionary<string, string[]> { ["assetIds"] = [$"At most {MaxBatchSize} at a time."] });

        var expires = clock.UtcNow.Add(settings.DefaultTokenTtl);
        var result = new Dictionary<string, string?>();
        foreach (var id in c.AssetIds.Distinct())
            result[id.ToString()] = await LinkForAsync(id, expires, ct);
        return result;
    }

    /// <summary>Null for anything that isn't a plain, readable asset with a file - the same checks
    /// <see cref="CreateDeliveryLinkHandler"/> makes, just turned into "no thumbnail" instead of an error.</summary>
    private async Task<string?> LinkForAsync(Guid id, DateTimeOffset expires, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, id, ct);
        if (asset is null || asset.CurrentVersionId is null) return null;
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return null;
        if (!await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return null;

        var watermark = asset.Status != AssetStatuses.Published;
        var token = signer.Sign(new DeliveryClaims(tenant.TenantId, asset.Id, $"r:{ThumbProfile}", expires, null, watermark));
        return $"{settings.LinkBase}/d/{token}/{asset.Id}/{ThumbProfile}";
    }
}

/// <summary>The renditions already made for an asset's current version. Missing ones are made on first request.</summary>
public sealed record ListRenditionsQuery(Guid AssetId) : IQuery<IReadOnlyList<RenditionDto>>;

public sealed class ListRenditionsHandler(
    IRepository<Asset> assets, IRepository<Rendition> renditions, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListRenditionsQuery, IReadOnlyList<RenditionDto>>
{
    public async Task<Result<IReadOnlyList<RenditionDto>>> Handle(ListRenditionsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        var versionId = asset.CurrentVersionId;
        var list = await q.ToListAsync(renditions.Query().Where(x => x.VersionId == versionId).OrderBy(x => x.Profile), ct);
        return list.Select(x => new RenditionDto(x.Profile, x.Mime, x.Width, x.Height, x.SizeBytes, x.Status, x.Duration, x.PageNo > 0 ? x.PageNo : null, x.CreatedAt)).ToList();
    }
}
