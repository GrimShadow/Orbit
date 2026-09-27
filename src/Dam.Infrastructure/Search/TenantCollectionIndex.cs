using Dam.Domain.Content;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Search;

/// <summary>
/// Every static collection's membership for the current tenant, loaded once and reused across many documents - the
/// reverse of <c>Collection.AssetIds</c> (asset -> the static collections it belongs to), which is what a search
/// document's own `collection_ids` field needs (spec Step 1.7c). Smart collections have no stored membership (their
/// contents are whatever their saved query currently matches, spec: "Smart = saved search") and never feed
/// <see cref="Dam.Domain.Authorization.ScopeType.Collection"/> access rules for exactly that reason - see ADR-0013.
/// </summary>
public sealed class TenantCollectionIndex
{
    private readonly ILookup<Guid, Guid> _collectionIdsByAsset;

    private TenantCollectionIndex(ILookup<Guid, Guid> collectionIdsByAsset) => _collectionIdsByAsset = collectionIdsByAsset;

    public static async Task<TenantCollectionIndex> LoadAsync(DamDbContext db, CancellationToken ct)
    {
        var statics = await db.Collections.AsNoTracking().Where(c => c.Type == CollectionType.Static)
            .Select(c => new { c.Id, c.AssetIds }).ToListAsync(ct);
        var pairs = statics.SelectMany(c => c.AssetIds.Select(assetId => (AssetId: assetId, CollectionId: c.Id)));
        return new TenantCollectionIndex(pairs.ToLookup(p => p.AssetId, p => p.CollectionId));
    }

    public IReadOnlyList<Guid> For(Guid assetId) => _collectionIdsByAsset[assetId].ToList();
}
