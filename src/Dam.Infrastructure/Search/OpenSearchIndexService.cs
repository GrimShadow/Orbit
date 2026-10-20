using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Ai;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Dam.Infrastructure.Search;

/// <summary>Keeps a tenant's asset index in sync (spec Step 1.7). Everything operates on the ambient tenant
/// (<see cref="ITenantContext"/>) exactly like a command handler or event consumer does - never an explicit tenant
/// argument - so RLS, the tenant query filter and this service all agree on which tenant is "current".</summary>
public interface ISearchIndexAdmin
{
    /// <summary>Creates the tenant's index and alias if they don't exist yet; otherwise merges any new metadata fields
    /// into the live mapping. Safe to call as often as needed - <see cref="IndexAsync"/> and <see cref="DeleteAsync"/>
    /// both call it before touching a document, so a brand-new tenant needs no separate provisioning step.</summary>
    Task EnsureIndexAsync(CancellationToken ct);

    /// <summary>Builds a brand-new backing index from every current asset (spec accept: "100k synthetic assets indexed"),
    /// then swaps the tenant's alias onto it and drops the old index. The old index keeps serving reads until the swap
    /// (spec accept: "reindex rebuilds without downtime (alias swap)"). Returns how many documents were written.</summary>
    Task<int> ReindexAsync(CancellationToken ct);
}

public interface ISearchIndexWriter
{
    /// <summary>Loads the asset fresh and writes its current document. A soft-deleted or purged asset is removed instead
    /// (spec Step 1.6: assets are never hard-deleted from Postgres, but a deleted asset must not turn up in search).</summary>
    Task IndexAsync(Guid assetId, CancellationToken ct);
    Task DeleteAsync(Guid assetId, CancellationToken ct);
}

public sealed class OpenSearchIndexService(
    IOpenSearchLowLevelClient client, DamDbContext db, ITenantContext tenant, IClock clock, ILogger<OpenSearchIndexService> log, AiOptions? ai = null)
    : ISearchIndexAdmin, ISearchIndexWriter, IVectorIndexInspector
{
    private const int ReindexBatchSize = 500;
    private const int MaxIndexedTextChars = 200_000;

    private readonly VectorDimensions _dims = new((ai ?? new AiOptions()).TextDim, (ai ?? new AiOptions()).ImageDim);

    /// <summary>The vector sizes each live backing index was built with (an index's mapping never changes size, so this never goes stale).</summary>
    private static readonly ConcurrentDictionary<string, (int? Text, int? Image)> LiveDims = new();

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        var slug = await TenantIndexResolver.SlugAsync(db, tenant, ct);
        var alias = IndexNaming.Alias(slug);
        var current = await CurrentBackingIndexAsync(alias, ct);
        if (current is null) { await BuildAndSwapAsync(slug, alias, [], ct); return; }

        // The index already exists: merge in any metadata fields the schema gained since it was built. A field whose
        // *type* changed is rejected by OpenSearch's put-mapping API - that needs a real reindex, not a merge, so it is
        // logged and left for ReindexAsync rather than silently failing the whole request.
        var schemas = await TenantSchemaIndex.LoadAsync(db, ct);
        var properties = new Dictionary<string, object?> { ["metadata"] = new Dictionary<string, object?> { ["properties"] = IndexMappingBuilder.BuildMetadataProperties(schemas.MergedFields(log)) } };
        foreach (var (name, mapping) in IndexMappingBuilder.BuildExtractProperties()) properties[name] = mapping;   // an index made before AI enrichment gains the new fields
        var body = new Dictionary<string, object?> { ["properties"] = properties };
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"/{current}/_mapping", ct, Body(body));
        if (!response.Success)
            log.LogWarning("Could not merge the search mapping for tenant {Tenant} (index {Index}): {Error}. A reindex will pick up the change.", tenant.TenantId, current, response.Body);
    }

    public async Task<int> ReindexAsync(CancellationToken ct)
    {
        var slug = await TenantIndexResolver.SlugAsync(db, tenant, ct);
        var alias = IndexNaming.Alias(slug);
        var oldIndex = await CurrentBackingIndexAsync(alias, ct);
        var count = await BuildAndSwapAsync(slug, alias, oldIndex is { } o ? [o] : [], ct);
        return count;
    }

    public async Task IndexAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null || asset.Status == AssetStatuses.Deleted) { await DeleteAsync(assetId, ct); return; }

        await EnsureIndexAsync(ct);
        var alias = await TenantIndexResolver.AliasAsync(db, tenant, ct);
        var folder = asset.FolderId is { } fid ? await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fid, ct) : null;
        var schemas = await TenantSchemaIndex.LoadAsync(db, ct);
        var taxonomy = await TenantTaxonomyIndex.LoadAsync(db, ct);
        var collections = await TenantCollectionIndex.LoadAsync(db, ct);
        var aiData = (await LoadAiAsync([asset], ct)).GetValueOrDefault(asset.Id);
        // Vectors only go into an index built for the configured sizes; an older one (made before AI, or before the sizes changed) takes the text and waits for a reindex.
        if (aiData is not null && (aiData.TextVector is not null || aiData.ImageVector is not null) && await LiveVectorsMismatchAsync(alias, ct))
            aiData = aiData with { TextVector = null, ImageVector = null };
        var doc = SearchDocumentBuilder.Build(asset, folder, schemas, taxonomy, collections, aiData);

        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"/{alias}/_doc/{assetId}", ct, Body(doc));
        if (!response.Success) throw new InvalidOperationException($"Could not index asset {assetId}: {response.Body}");
    }

    public async Task DeleteAsync(Guid assetId, CancellationToken ct)
    {
        var alias = await TenantIndexResolver.AliasAsync(db, tenant, ct);
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.DELETE, $"/{alias}/_doc/{assetId}", ct);
        if (!response.Success && response.HttpStatusCode != 404)
            throw new InvalidOperationException($"Could not remove asset {assetId} from the index: {response.Body}");
    }

    /// <summary>Builds a fresh backing index, bulk-loads every non-deleted asset into it, then atomically re-points the
    /// alias at it (removing <paramref name="replacing"/>) and drops the indices it replaced.</summary>
    private async Task<int> BuildAndSwapAsync(string slug, string alias, IReadOnlyList<string> replacing, CancellationToken ct)
    {
        var schemas = await TenantSchemaIndex.LoadAsync(db, ct);
        var taxonomy = await TenantTaxonomyIndex.LoadAsync(db, ct);
        var collections = await TenantCollectionIndex.LoadAsync(db, ct);
        var newIndex = IndexNaming.BackingIndex(slug, clock.UtcNow);

        var create = await client.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"/{newIndex}", ct, Body(IndexMappingBuilder.BuildIndexBody(schemas.MergedFields(log), _dims)));
        if (!create.Success) throw new InvalidOperationException($"Could not create index {newIndex}: {create.Body}");

        var count = await BulkLoadAsync(newIndex, schemas, taxonomy, collections, ct);

        var actions = new List<object?>();
        foreach (var old in replacing) actions.Add(new Dictionary<string, object?> { ["remove"] = new Dictionary<string, object?> { ["index"] = old, ["alias"] = alias } });
        actions.Add(new Dictionary<string, object?> { ["add"] = new Dictionary<string, object?> { ["index"] = newIndex, ["alias"] = alias } });
        var swap = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, "/_aliases", ct, Body(new Dictionary<string, object?> { ["actions"] = actions }));
        if (!swap.Success) throw new InvalidOperationException($"Could not swap alias {alias} onto {newIndex}: {swap.Body}");

        foreach (var old in replacing)
        {
            var delete = await client.DoRequestAsync<StringResponse>(HttpMethod.DELETE, $"/{old}", ct);
            if (!delete.Success) log.LogWarning("Reindex for tenant {Tenant} left the old index {Index} behind: {Error}", tenant.TenantId, old, delete.Body);
        }
        return count;
    }

    private async Task<int> BulkLoadAsync(string index, TenantSchemaIndex schemas, TenantTaxonomyIndex taxonomy, TenantCollectionIndex collections, CancellationToken ct)
    {
        var total = 0;
        Guid? after = null;
        while (true)
        {
            var page = await db.Assets.AsNoTracking().Where(a => a.Status != AssetStatuses.Deleted)
                .Where(a => after == null || a.Id.CompareTo(after.Value) > 0)
                .OrderBy(a => a.Id).Take(ReindexBatchSize).ToListAsync(ct);
            if (page.Count == 0) break;

            var folderIds = page.Where(a => a.FolderId != null).Select(a => a.FolderId!.Value).Distinct().ToList();
            var folders = await db.Folders.AsNoTracking().Where(f => folderIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

            var aiByAsset = await LoadAiAsync(page, ct);
            var sb = new StringBuilder();
            foreach (var asset in page)
            {
                var folder = asset.FolderId is { } fid && folders.TryGetValue(fid, out var f) ? f : null;
                var doc = SearchDocumentBuilder.Build(asset, folder, schemas, taxonomy, collections, aiByAsset.GetValueOrDefault(asset.Id));
                sb.Append(JsonSerializer.Serialize(new Dictionary<string, object?> { ["index"] = new Dictionary<string, object?> { ["_index"] = index, ["_id"] = asset.Id } })).Append('\n');
                sb.Append(JsonSerializer.Serialize(doc)).Append('\n');
            }
            var bulk = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, "/_bulk", ct, PostData.String(sb.ToString()));
            if (!bulk.Success) throw new InvalidOperationException($"Bulk index into {index} failed: {bulk.Body}");

            total += page.Count;
            after = page[^1].Id;
        }
        return total;
    }

    /// <summary>
    /// The AI-derived data for the current version of each asset: extracted text, the transcript and the stored vectors (read from the database, so a reindex
    /// never asks the AI provider again). Vectors stored with another size than the configured one are left out (they would be refused), with a warning.
    /// </summary>
    private async Task<Dictionary<Guid, AiIndexData>> LoadAiAsync(IReadOnlyList<Dam.Domain.Assets.Asset> assets, CancellationToken ct)
    {
        var result = new Dictionary<Guid, AiIndexData>();
        var versions = assets.Where(a => a.CurrentVersionId != null).ToDictionary(a => a.Id, a => a.CurrentVersionId!.Value);
        if (versions.Count == 0) return result;
        var ids = versions.Keys.ToList();
        var extracts = (await db.AssetExtracts.AsNoTracking().Where(e => ids.Contains(e.AssetId)).ToListAsync(ct))
            .Where(e => versions[e.AssetId] == e.VersionId).ToDictionary(e => e.AssetId);
        var vectors = (await db.AssetEmbeddings.AsNoTracking().Where(e => ids.Contains(e.AssetId)).ToListAsync(ct))
            .Where(e => versions[e.AssetId] == e.VersionId).ToLookup(e => e.AssetId);

        foreach (var id in ids)
        {
            extracts.TryGetValue(id, out var x);
            float[]? text = null, image = null;
            foreach (var v in vectors[id])
            {
                var expected = v.Kind == EmbeddingKinds.Text ? _dims.Text : _dims.Image;
                if (v.Dim != expected)
                {
                    log.LogWarning("Asset {Asset} has a {Kind} vector of {Dim} dimensions but {Expected} are configured; it is left out of the index until the asset is enriched again", id, v.Kind, v.Dim, expected);
                    continue;
                }
                if (v.Kind == EmbeddingKinds.Text) text = v.ToVector(); else image = v.ToVector();
            }
            if (x is null && text is null && image is null) continue;
            result[id] = new AiIndexData(Clip(x?.OcrText), Clip(x?.TranscriptText), x?.TranscriptSegments ?? [], text, image, Clip(x?.Summary));
        }
        return result;
    }

    private static string? Clip(string? s) => s is { Length: > MaxIndexedTextChars } ? s[..MaxIndexedTextChars] : s;

    private async Task<bool> LiveVectorsMismatchAsync(string alias, CancellationToken ct)
    {
        var index = await CurrentBackingIndexAsync(alias, ct);
        if (index is null) return false;
        var live = await LiveDimensionsAsync(index, ct);
        return (live.Text is { } t && t != _dims.Text) || (live.Image is { } i && i != _dims.Image);
    }

    private async Task<(int? Text, int? Image)> LiveDimensionsAsync(string index, CancellationToken ct)
    {
        if (LiveDims.TryGetValue(index, out var known)) return known;
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.GET, $"/{index}/_mapping", ct);
        if (!response.Success) return (null, null);
        using var doc = JsonDocument.Parse(response.Body);
        var props = doc.RootElement.EnumerateObject().FirstOrDefault().Value;
        int? Dim(string field) =>
            props.ValueKind == JsonValueKind.Object && props.TryGetProperty("mappings", out var m) && m.TryGetProperty("properties", out var p)
            && p.TryGetProperty(field, out var f) && f.TryGetProperty("dimension", out var d) && d.TryGetInt32(out var n) ? n : null;
        var dims = (Dim(IndexMappingBuilder.VectorTextField), Dim(IndexMappingBuilder.VectorImageField));
        LiveDims[index] = dims;
        return dims;
    }

    public async Task<VectorIndexInfo> InspectAsync(CancellationToken ct)
    {
        var alias = await TenantIndexResolver.AliasAsync(db, tenant, ct);
        var index = await CurrentBackingIndexAsync(alias, ct);
        var live = index is null ? (null, null) : await LiveDimensionsAsync(index, ct);
        return new VectorIndexInfo(_dims.Text, _dims.Image, live.Item1, live.Item2);
    }

    /// <summary>The alias's one current backing index, or null when the alias does not exist yet.</summary>
    private async Task<string?> CurrentBackingIndexAsync(string alias, CancellationToken ct)
    {
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.GET, $"/_alias/{alias}", ct);
        if (!response.Success) return null;
        using var doc = JsonDocument.Parse(response.Body);
        var indices = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        return indices.Count > 0 ? indices[0] : null;
    }

    private static PostData Body(object value) => PostData.String(JsonSerializer.Serialize(value));
}
