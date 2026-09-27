using System.Text;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Authorization;
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

public sealed class OpenSearchIndexService(IOpenSearchLowLevelClient client, DamDbContext db, ITenantContext tenant, IClock clock, ILogger<OpenSearchIndexService> log)
    : ISearchIndexAdmin, ISearchIndexWriter
{
    private const int ReindexBatchSize = 500;

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        var slug = await TenantSlugAsync(ct);
        var alias = IndexNaming.Alias(slug);
        var current = await CurrentBackingIndexAsync(alias, ct);
        if (current is null) { await BuildAndSwapAsync(slug, alias, [], ct); return; }

        // The index already exists: merge in any metadata fields the schema gained since it was built. A field whose
        // *type* changed is rejected by OpenSearch's put-mapping API - that needs a real reindex, not a merge, so it is
        // logged and left for ReindexAsync rather than silently failing the whole request.
        var schemas = await TenantSchemaIndex.LoadAsync(db, ct);
        var body = new Dictionary<string, object?> { ["properties"] = new Dictionary<string, object?> { ["metadata"] = new Dictionary<string, object?> { ["properties"] = IndexMappingBuilder.BuildMetadataProperties(schemas.MergedFields(log)) } } };
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"/{current}/_mapping", ct, Body(body));
        if (!response.Success)
            log.LogWarning("Could not merge the search mapping for tenant {Tenant} (index {Index}): {Error}. A reindex will pick up the change.", tenant.TenantId, current, response.Body);
    }

    public async Task<int> ReindexAsync(CancellationToken ct)
    {
        var slug = await TenantSlugAsync(ct);
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
        var slug = await TenantSlugAsync(ct);
        var alias = IndexNaming.Alias(slug);
        var folder = asset.FolderId is { } fid ? await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fid, ct) : null;
        var schemas = await TenantSchemaIndex.LoadAsync(db, ct);
        var taxonomy = await TenantTaxonomyIndex.LoadAsync(db, ct);
        var doc = SearchDocumentBuilder.Build(asset, folder, schemas, taxonomy);

        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"/{alias}/_doc/{assetId}", ct, Body(doc));
        if (!response.Success) throw new InvalidOperationException($"Could not index asset {assetId}: {response.Body}");
    }

    public async Task DeleteAsync(Guid assetId, CancellationToken ct)
    {
        var slug = await TenantSlugAsync(ct);
        var alias = IndexNaming.Alias(slug);
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
        var newIndex = IndexNaming.BackingIndex(slug, clock.UtcNow);

        var create = await client.DoRequestAsync<StringResponse>(HttpMethod.PUT, $"/{newIndex}", ct, Body(IndexMappingBuilder.BuildIndexBody(schemas.MergedFields(log))));
        if (!create.Success) throw new InvalidOperationException($"Could not create index {newIndex}: {create.Body}");

        var count = await BulkLoadAsync(newIndex, schemas, taxonomy, ct);

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

    private async Task<int> BulkLoadAsync(string index, TenantSchemaIndex schemas, TenantTaxonomyIndex taxonomy, CancellationToken ct)
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

            var sb = new StringBuilder();
            foreach (var asset in page)
            {
                var folder = asset.FolderId is { } fid && folders.TryGetValue(fid, out var f) ? f : null;
                var doc = SearchDocumentBuilder.Build(asset, folder, schemas, taxonomy);
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

    /// <summary>The alias's one current backing index, or null when the alias does not exist yet.</summary>
    private async Task<string?> CurrentBackingIndexAsync(string alias, CancellationToken ct)
    {
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.GET, $"/_alias/{alias}", ct);
        if (!response.Success) return null;
        using var doc = JsonDocument.Parse(response.Body);
        var indices = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        return indices.Count > 0 ? indices[0] : null;
    }

    private async Task<string> TenantSlugAsync(CancellationToken ct)
    {
        var slug = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Slug).FirstOrDefaultAsync(ct);
        if (slug is not { Length: > 0 }) throw new InvalidOperationException($"Tenant {tenant.TenantId} was not found (or has no slug).");
        return slug;
    }

    private static PostData Body(object value) => PostData.String(JsonSerializer.Serialize(value));
}
