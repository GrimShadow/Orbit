using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Content;
using Dam.Domain.Licensing;
using Dam.Shared.Licensing;

namespace Dam.Application.Licensing;

internal static class RightsMapping
{
    public static RightsDto ToDto(Rights r, int assetCount, int documentCount, DateOnly today, int pendingAssets = 0)
    {
        var v = r.Validity(today);
        return new RightsDto(r.Id, r.Name, r.LicenceType, r.Licensor, r.Channels, r.Regions, r.ValidFrom, r.ValidTo, r.WatermarkRequired, r.Notes,
            v.Status, v.DaysRemaining, assetCount, documentCount, r.CreatedBy, r.CreatedAt, r.UpdatedAt, pendingAssets);
    }

    /// <param name="manager">Whether the caller holds rights.manage: only then are the licensor and the notes shown.</param>
    public static RightsSummaryDto ToSummary(Rights r, int documentCount, bool manager, DateOnly today)
    {
        var v = r.Validity(today);
        return new RightsSummaryDto(r.Id, r.Name, r.LicenceType, r.Channels, r.Regions, r.ValidFrom, r.ValidTo, v.Status, v.DaysRemaining,
            r.WatermarkRequired, documentCount, manager ? r.Licensor : null, manager ? r.Notes : null);
    }

    public static RightsDocumentDto ToDto(RightsDocument d) => new(d.Id, d.RightsId, d.Name, d.ContentType, d.SizeBytes, d.UploadedBy, d.CreatedAt);
}

/// <summary>
/// Checks the channel and region codes of a rights definition against the tenant's own vocabularies. The codes are tenant data: the terms the
/// metadata schema's channel and region fields (the ones tagged with an access dimension) draw from. Where such a vocabulary exists and has
/// terms, a rights value must be one of them (matched ignoring case and stored with the term's own spelling); where it does not, a lower-case
/// slug (letters, digits, '-' and '_') is accepted, so a tenant without a taxonomy can still use rights.
/// </summary>
public sealed class RightsVocabularies(IRepository<MetadataSchema> schemas, IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IQueryExecutor q)
{
    private const int ListedInMessage = 20;

    /// <summary>The normalised channels and regions, or the problems keyed by field.</summary>
    public async Task<(List<string> Channels, List<string> Regions, Dictionary<string, string[]> Errors)> NormalizeAsync(
        IReadOnlyList<string>? channels, IReadOnlyList<string>? regions, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var c = await NormalizeAsync("channels", Dimensions.Channel, channels, errors, ct);
        var r = await NormalizeAsync("regions", Dimensions.Region, regions, errors, ct);
        return (c, r, errors);
    }

    private async Task<List<string>> NormalizeAsync(string field, string dimension, IReadOnlyList<string>? values, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var list = RightsRules.NormalizeValues(values);
        if (list.Count == 0) return [];
        var known = await KnownCodesAsync(dimension, ct);
        var result = new List<string>();
        var bad = new List<string>();
        foreach (var value in list)
        {
            if (known is not null)
            {
                var match = known.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase));
                if (match is null) bad.Add(value); else result.Add(match);
            }
            else if (RightsRules.IsSlug(value.ToLowerInvariant())) result.Add(value.ToLowerInvariant());
            else bad.Add(value);
        }
        if (bad.Count > 0)
            errors[field] = [known is not null
                ? $"Unknown {dimension}: {string.Join(", ", bad)}. Known: {string.Join(", ", known.Take(ListedInMessage))}{(known.Count > ListedInMessage ? ", ..." : "")}."
                : $"Not a valid {dimension} code: {string.Join(", ", bad)}. Use lower-case letters, digits, '-' and '_' (up to {RightsRules.MaxValueLength} characters)."];
        return RightsRules.NormalizeValues(result);
    }

    /// <summary>Active term codes of the vocabularies the tenant's access-dimension fields use (else a vocabulary keyed by the dimension's name); null when there is none or it is empty.</summary>
    private async Task<List<string>?> KnownCodesAsync(string dimension, CancellationToken ct)
    {
        var current = await q.ToListAsync(schemas.Query().Where(s => s.IsCurrent), ct);
        var keys = current.SelectMany(s => s.Fields).Where(f => f.AccessDimension == dimension && f.Vocabulary != null).Select(f => f.Vocabulary!).Distinct().ToList();
        if (keys.Count == 0) keys = [dimension];
        var ids = await q.ToListAsync(vocabularies.Query().Where(v => keys.Contains(v.Key)).Select(v => v.Id), ct);
        if (ids.Count == 0) return null;
        var codes = await q.ToListAsync(terms.Query().Where(t => ids.Contains(t.VocabularyId) && t.Status == TermStatus.Active).Select(t => t.Code), ct);
        return codes.Count == 0 ? null : codes.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Looks up an asset's rights and checks them for today (UTC) and, optionally, a consumer's channel and region. One instance serves a
/// request, so assets sharing a rights record read it once.</summary>
public sealed class RightsGuard(IRepository<Rights> rights, IQueryExecutor q, IClock clock)
{
    private readonly Dictionary<Guid, Rights?> _cache = [];

    public DateOnly Today => RightsRules.Today(clock.UtcNow);

    public async Task<Rights?> FindAsync(Guid? rightsId, CancellationToken ct)
    {
        if (rightsId is not { } id) return null;
        if (_cache.TryGetValue(id, out var cached)) return cached;
        return _cache[id] = await q.FirstOrDefaultAsync(rights.Query().Where(r => r.Id == id), ct);
    }

    public async Task<RightsCheck> CheckAsync(Asset asset, string? channel, string? region, CancellationToken ct) =>
        RightsChecker.Check(await FindAsync(asset.RightsId, ct), asset.Attributes, channel, region, Today);

    /// <summary>The message that explains why an asset cannot be published or delivered under its licence today, or null when the licence is in force (or there is none).</summary>
    public static string? OutOfForceMessage(RightsCheck check) => check.OutsideValidity ? (check.Reasons.Count > 0 ? check.Reasons[0] : null) : null;
}

/// <summary>Builds <see cref="RightsDto"/>s, adding the live counts of assigned assets and attached documents.</summary>
public sealed class RightsMapper(IRepository<Asset> assets, IRepository<RightsDocument> documents, IQueryExecutor q, IClock clock)
{
    public DateOnly Today => RightsRules.Today(clock.UtcNow);

    public async Task<List<RightsDto>> ToDtosAsync(IReadOnlyList<Rights> list, IReadOnlyDictionary<Guid, int>? pending = null, CancellationToken ct = default)
    {
        if (list.Count == 0) return [];
        var ids = list.Select(r => r.Id).ToList();
        var assetCounts = (await q.ToListAsync(assets.Query().Where(a => a.RightsId != null && ids.Contains(a.RightsId.Value) && a.Status != AssetStatuses.Deleted)
            .GroupBy(a => a.RightsId!.Value).Select(g => new { Id = g.Key, N = g.Count() }), ct)).ToDictionary(x => x.Id, x => x.N);
        var docCounts = (await q.ToListAsync(documents.Query().Where(d => ids.Contains(d.RightsId))
            .GroupBy(d => d.RightsId).Select(g => new { Id = g.Key, N = g.Count() }), ct)).ToDictionary(x => x.Id, x => x.N);
        var today = Today;
        return list.Select(r => RightsMapping.ToDto(r, assetCounts.GetValueOrDefault(r.Id), docCounts.GetValueOrDefault(r.Id), today, pending?.GetValueOrDefault(r.Id) ?? 0)).ToList();
    }

    public async Task<RightsDto> ToDtoAsync(Rights r, int pendingAssets, CancellationToken ct) =>
        (await ToDtosAsync([r], new Dictionary<Guid, int> { [r.Id] = pendingAssets }, ct))[0];
}
