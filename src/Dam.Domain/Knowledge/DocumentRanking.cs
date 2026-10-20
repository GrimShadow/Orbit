using Dam.Domain.Authorization;

namespace Dam.Domain.Knowledge;

/// <summary>A knowledge document the caller may read, as resolution weighs it.</summary>
public sealed record DocumentCandidate(Guid DocumentId, Guid AssetId, string? Variant, int? YearFrom, int? YearTo, string Language, string AssetStatus, DateTimeOffset Updated);

/// <summary>The answer to "which document?": the best one in the chosen language and the best of each other language.</summary>
public sealed record DocumentSelection(DocumentCandidate Chosen, IReadOnlyList<DocumentCandidate> OtherLanguages);

/// <summary>
/// Resolution ranking (ADR-0039 decision 5). A document fits when its variant equals the vehicle's or is empty, and its year range contains the vehicle's year
/// or is empty. Fitting documents are ranked: published first (an asset in review comes only after every published one), then an exact variant over an empty
/// one, then a narrower year range over a wider one over none, then the most recently updated, then the smallest id. The language is the one asked for, else the
/// tenant's default, else English, else the best-ranked document's.
/// </summary>
public static class DocumentRanking
{
    /// <summary>Only these asset statuses ever resolve: a manual that is out (published) or that is ready for review (read by the Ask Assist role).</summary>
    public static bool Resolvable(string assetStatus) => assetStatus is AssetStatuses.Published or AssetStatuses.InReview;

    public static bool Fits(DocumentCandidate d, string? vehicleVariant, int? vehicleYear)
    {
        if (!Resolvable(d.AssetStatus)) return false;
        if (d.Variant is not null && !string.Equals(d.Variant, vehicleVariant, StringComparison.OrdinalIgnoreCase)) return false;
        if (d.YearFrom is { } from && d.YearTo is { } to && (vehicleYear is not { } y || y < from || y > to)) return false;
        return true;
    }

    public static IReadOnlyList<DocumentCandidate> Rank(IEnumerable<DocumentCandidate> candidates, string? vehicleVariant, int? vehicleYear) =>
        candidates.Where(d => Fits(d, vehicleVariant, vehicleYear)).OrderBy(d => d, Comparer).ToList();

    public static DocumentSelection? Select(IEnumerable<DocumentCandidate> candidates, string? vehicleVariant, int? vehicleYear, string? requestedLanguage, string? tenantDefault)
    {
        var ranked = Rank(candidates, vehicleVariant, vehicleYear);
        if (ranked.Count == 0) return null;

        var chosen = ranked[0];
        foreach (var wanted in new[] { requestedLanguage, tenantDefault, "en" })
        {
            if (string.IsNullOrWhiteSpace(wanted)) continue;
            var hit = ranked.FirstOrDefault(d => SameLanguage(d.Language, wanted)) ?? ranked.FirstOrDefault(d => SamePrimary(d.Language, wanted));
            if (hit is null) continue;
            chosen = hit;
            break;
        }

        var others = ranked.Where(d => !string.Equals(d.Language, chosen.Language, StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d.Language.ToLowerInvariant())
            .Select(g => g.First())
            .OrderBy(d => d.Language, StringComparer.Ordinal)
            .ToList();
        return new DocumentSelection(chosen, others);
    }

    private static bool SameLanguage(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SamePrimary(string a, string b) =>
        string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static int Span(DocumentCandidate d) => d.YearFrom is { } f && d.YearTo is { } t ? t - f : int.MaxValue;

    private static int StatusRank(DocumentCandidate d) => d.AssetStatus == AssetStatuses.Published ? 0 : 1;

    private static readonly IComparer<DocumentCandidate> Comparer = System.Collections.Generic.Comparer<DocumentCandidate>.Create((a, b) =>
    {
        var c = StatusRank(a).CompareTo(StatusRank(b));
        if (c != 0) return c;
        c = (a.Variant is null).CompareTo(b.Variant is null);          // an exact variant (not null) sorts before an empty one
        if (c != 0) return c;
        c = Span(a).CompareTo(Span(b));
        if (c != 0) return c;
        c = b.Updated.CompareTo(a.Updated);
        return c != 0 ? c : IdComparer.Instance.Compare(a.DocumentId, b.DocumentId);
    });
}
