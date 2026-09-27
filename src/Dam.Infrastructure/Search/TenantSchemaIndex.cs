using Dam.Domain.Content;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Search;

/// <summary>Every asset type's current metadata schema for the tenant, loaded once and reused across many documents.</summary>
public sealed class TenantSchemaIndex
{
    private readonly Dictionary<string, IReadOnlyList<FieldDefinition>> _fieldsByAssetType;

    private TenantSchemaIndex(Dictionary<string, IReadOnlyList<FieldDefinition>> fieldsByAssetType) => _fieldsByAssetType = fieldsByAssetType;

    public static async Task<TenantSchemaIndex> LoadAsync(DamDbContext db, CancellationToken ct)
    {
        var schemas = await db.MetadataSchemas.AsNoTracking().Where(s => s.IsCurrent).ToListAsync(ct);
        return new TenantSchemaIndex(schemas.ToDictionary(s => s.AssetType, s => (IReadOnlyList<FieldDefinition>)s.Fields));
    }

    public IReadOnlyList<FieldDefinition> FieldsFor(string assetType) => _fieldsByAssetType.GetValueOrDefault(assetType, []);

    /// <summary>Every field across every asset type, merged by name for the one shared index mapping. A name two asset
    /// types disagree on keeps whichever schema was merged in first (asset types are visited alphabetically, so the
    /// result is at least deterministic) and logs the conflict rather than failing the whole build.</summary>
    public IReadOnlyList<FieldDefinition> MergedFields(ILogger? log = null)
    {
        var merged = new Dictionary<string, FieldDefinition>();
        var keptFromType = new Dictionary<string, string>();
        foreach (var (assetType, fields) in _fieldsByAssetType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            foreach (var f in fields)
            {
                if (merged.TryAdd(f.Name, f)) { keptFromType[f.Name] = assetType; continue; }
                if (merged[f.Name].Type != f.Type)
                    log?.LogWarning(
                        "Metadata field '{Field}' is {KeptType} on asset type '{KeptFrom}' but {OtherType} on '{OtherAssetType}'; the search mapping keeps the first.",
                        f.Name, merged[f.Name].Type, keptFromType[f.Name], f.Type, assetType);
            }
        return merged.Values.ToList();
    }
}
