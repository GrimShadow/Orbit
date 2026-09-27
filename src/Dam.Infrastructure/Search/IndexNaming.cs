namespace Dam.Infrastructure.Search;

/// <summary>
/// One index per tenant (spec Step 1.7: "dam-{tenant}-assets"), addressed through a write alias so a reindex can build a
/// whole new backing index and swap the alias onto it atomically — search stays up throughout (spec: "reindex rebuilds
/// without downtime (alias swap)").
/// </summary>
public static class IndexNaming
{
    /// <summary>The stable name callers (search, indexing) always use. Never a real index itself.</summary>
    public static string Alias(string tenantSlug) => $"dam-{tenantSlug}-assets";

    /// <summary>A fresh physical index for a (re)build at <paramref name="at"/>. Never collides with a concurrent build.</summary>
    public static string BackingIndex(string tenantSlug, DateTimeOffset at) => $"{Alias(tenantSlug)}-{at:yyyyMMddHHmmssfff}";
}
