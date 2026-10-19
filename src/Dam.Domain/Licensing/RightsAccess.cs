using Dam.Domain.Authorization;

namespace Dam.Domain.Licensing;

/// <summary>
/// How a licence narrows an asset's access attributes (ADR-0033). An asset's channel and region attributes come from its metadata; a licence that
/// names channels or regions narrows them to the INTERSECTION, so the existing attribute-based access control (search, listings, single-asset
/// checks) hides the asset from consumers outside the licence with no special case.
/// </summary>
public static class RightsScope
{
    /// <summary>
    /// Stored as the attribute value when the metadata and the licence share nothing: an asset "restricted to nothing". A resource that has any
    /// value in a dimension (and not Global) is invisible to an attribute-restricted consumer unless they hold one of its values, and nobody can
    /// hold this one (it is not a legal term code), so the asset stays visible to global-access roles only. An empty list could not say this: for
    /// attributes, empty means "unrestricted".
    /// </summary>
    public const string RestrictedToNothing = "~restricted";

    /// <summary>The dimensions a licence narrows.</summary>
    public static readonly IReadOnlyList<string> Narrowed = [Dimensions.Channel, Dimensions.Region];

    /// <summary>
    /// The effective values of one dimension. A side is "open" when it is empty or names Global. Both open: the metadata values stay as they are.
    /// One open: the other side applies. Both restricted: their intersection (ignoring case), or <see cref="RestrictedToNothing"/> when it is empty.
    /// </summary>
    public static string[] Effective(IReadOnlyCollection<string>? fromMetadata, IReadOnlyCollection<string>? fromRights)
    {
        var meta = (fromMetadata ?? []).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var licence = (fromRights ?? []).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var metaOpen = meta.Length == 0 || meta.Any(IsGlobal);
        var licenceOpen = licence.Length == 0;
        if (licenceOpen) return meta;
        if (metaOpen) return licence.Order(StringComparer.Ordinal).ToArray();
        var shared = meta.Where(m => licence.Contains(m, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        return shared.Length == 0 ? [RestrictedToNothing] : shared;
    }

    /// <summary>The asset's attributes with the licence's channels and regions applied; dimensions other than channel and region are untouched.</summary>
    public static Dictionary<string, string[]> Apply(IReadOnlyDictionary<string, string[]> fromMetadata, IReadOnlyCollection<string> channels, IReadOnlyCollection<string> regions)
    {
        var result = fromMetadata.ToDictionary(kv => kv.Key, kv => kv.Value);
        Set(result, Dimensions.Channel, Effective(fromMetadata.GetValueOrDefault(Dimensions.Channel), channels));
        Set(result, Dimensions.Region, Effective(fromMetadata.GetValueOrDefault(Dimensions.Region), regions));
        return result;
    }

    private static void Set(Dictionary<string, string[]> attributes, string dimension, string[] values)
    {
        if (values.Length == 0) attributes.Remove(dimension);
        else attributes[dimension] = values;
    }

    /// <summary>Whether two attribute sets hold the same values per dimension (key order does not matter; jsonb does not keep it).</summary>
    public static bool SameAttributes(IReadOnlyDictionary<string, string[]> a, IReadOnlyDictionary<string, string[]> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.SequenceEqual(other, StringComparer.Ordinal));

    public static bool IsGlobal(string value) => string.Equals(value, Dimensions.Global, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// May a consumer on <paramref name="value"/> (a channel or region code) use an asset with these effective attribute values? Unrestricted
    /// (empty or Global) allows everything; restricted-to-nothing allows nothing; otherwise the value must be listed.
    /// </summary>
    public static bool Allows(IReadOnlyCollection<string>? effective, string value)
    {
        if (effective is null || effective.Count == 0 || effective.Any(IsGlobal)) return true;
        if (effective.Contains(RestrictedToNothing)) return false;
        return effective.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The values to show for a dimension: the effective list without the internal marker, and whether the asset is restricted to nothing.</summary>
    public static (string[] Values, bool None) ForDisplay(IReadOnlyDictionary<string, string[]> attributes, string dimension)
    {
        var values = attributes.GetValueOrDefault(dimension) ?? [];
        return values.Contains(RestrictedToNothing) ? ([], true) : (values, false);
    }
}

/// <summary>The outcome of checking an asset's licence against a moment and, optionally, a consumer's channel and region.</summary>
/// <param name="Status">valid, expiring, expired or not_yet_valid; null when the asset has no rights record.</param>
public sealed record RightsCheck(
    string? Status, int? DaysRemaining, bool Expired, bool NotYetValid, bool WatermarkRequired, bool ChannelAllowed, bool RegionAllowed, IReadOnlyList<string> Reasons)
{
    public bool Allowed => Reasons.Count == 0;
    /// <summary>The licence is not in force today (ended or not begun): nothing may be published or delivered under it.</summary>
    public bool OutsideValidity => Expired || NotYetValid;

    public static readonly RightsCheck NoRights = new(null, null, false, false, false, true, true, []);
}

public static class RightsChecker
{
    /// <summary>
    /// Checks <paramref name="rights"/> (null: the asset has none) and the asset's effective <paramref name="attributes"/> against today and the
    /// consumer's <paramref name="channel"/> and <paramref name="region"/> (each optional). Every reason is a sentence fit to show a person.
    /// </summary>
    public static RightsCheck Check(Rights? rights, IReadOnlyDictionary<string, string[]> attributes, string? channel, string? region, DateOnly today)
    {
        var reasons = new List<string>();
        string? status = null;
        int? days = null;
        var expired = false;
        var notYet = false;

        if (rights is not null)
        {
            var v = rights.Validity(today);
            (status, days) = (v.Status, v.DaysRemaining);
            expired = v.Status == RightsStatuses.Expired;
            notYet = v.Status == RightsStatuses.NotYetValid;
            if (expired) reasons.Add($"The licence '{rights.Name}' ended on {rights.ValidTo:yyyy-MM-dd}.");
            if (notYet) reasons.Add($"The licence '{rights.Name}' starts on {rights.ValidFrom:yyyy-MM-dd}.");
        }

        var channelOk = Covered(rights?.Channels, attributes, Dimensions.Channel, channel);
        var regionOk = Covered(rights?.Regions, attributes, Dimensions.Region, region);
        if (!channelOk) reasons.Add(Why(rights, rights?.Channels, "channel", channel!));
        if (!regionOk) reasons.Add(Why(rights, rights?.Regions, "region", region!));

        return new RightsCheck(status, days, expired, notYet, rights?.WatermarkRequired ?? false, channelOk, regionOk, reasons);
    }

    private static bool Covered(IReadOnlyCollection<string>? licence, IReadOnlyDictionary<string, string[]> attributes, string dimension, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (licence is { Count: > 0 } && !licence.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
        return RightsScope.Allows(attributes.GetValueOrDefault(dimension), value);
    }

    private static string Why(Rights? rights, IReadOnlyCollection<string>? licence, string noun, string value)
    {
        if (rights is not null && licence is { Count: > 0 } && !licence.Contains(value, StringComparer.OrdinalIgnoreCase))
            return $"The licence '{rights.Name}' does not cover the '{value}' {noun} (it covers: {string.Join(", ", licence)}).";
        return $"This asset is not available for the '{value}' {noun}.";
    }
}
