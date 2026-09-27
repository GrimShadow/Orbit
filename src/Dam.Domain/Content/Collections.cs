using System.Text.Json;
using Dam.Domain.Common;

namespace Dam.Domain.Content;

public enum CollectionType { Static, Smart }

/// <summary>Who besides the owner can see a collection (spec A4.1: "share settings"). Deliberately just two levels for
/// now - private or the whole tenant - rather than a per-role/per-user list; that can be layered on later as an
/// additive column without touching this one.</summary>
public enum CollectionVisibility { Private, Tenant }

/// <summary>Canonical event (spec A4.3): any metadata change (rename, saved query, visibility).</summary>
public sealed record CollectionUpdated(Guid CollectionId) : DomainEvent, IEntityEvent
{
    public override string Name => "collection.updated";
    public string EntityType => "collection";
    public Guid EntityId => CollectionId;
}

/// <summary>Raised whenever a static collection's own membership changes - created with members, assets added or
/// removed, or the collection itself deleted (every remaining member counted as "removed"). This is the one event the
/// search index actually needs (spec Step 1.7c): on receipt it re-reads and re-indexes every listed asset so each
/// asset's own `collection_ids` stays correct, without the index consumer needing to know anything else about
/// collections.</summary>
public sealed record CollectionAssetsChanged(Guid CollectionId, IReadOnlyList<Guid> AddedAssetIds, IReadOnlyList<Guid> RemovedAssetIds) : DomainEvent, IEntityEvent
{
    public override string Name => "collection.assets_changed";
    public string EntityType => "collection";
    public Guid EntityId => CollectionId;
}

/// <summary>
/// A named set of assets (spec A4.1). A <see cref="CollectionType.Static"/> collection is an explicit list
/// (<see cref="AssetIds"/>); a <see cref="CollectionType.Smart"/> one is a saved search (<see cref="Query"/>) - "Smart =
/// saved search" per the spec - whose membership is never stored, only ever computed by re-running the query.
/// <para>Only static membership feeds access control (<see cref="Dam.Domain.Authorization.ScopeType.Collection"/>): a
/// smart collection's contents can change the moment anyone else's asset changes, which is not a sound basis for a
/// permission grant, so it is not treated as one.</para>
/// </summary>
public sealed class Collection : AggregateRoot
{
    private readonly List<Guid> _assetIds = [];
    private Collection() { }

    public string Name { get; private set; } = "";
    public CollectionType Type { get; private set; }
    /// <summary>The saved search body (spec: "query jsonb") - present only for <see cref="CollectionType.Smart"/>.</summary>
    public Dictionary<string, JsonElement>? Query { get; private set; }
    public IReadOnlyList<Guid> AssetIds => _assetIds;
    public CollectionVisibility Visibility { get; private set; } = CollectionVisibility.Private;
    public Guid OwnerId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Collection CreateStatic(Guid tenantId, string name, IReadOnlyList<Guid> assetIds, Guid ownerId, DateTimeOffset now)
    {
        var c = new Collection { TenantId = tenantId, Name = name.Trim(), Type = CollectionType.Static, OwnerId = ownerId, CreatedAt = now, UpdatedAt = now };
        var initial = assetIds.Distinct().ToList();
        c._assetIds.AddRange(initial);
        if (initial.Count > 0) c.Raise(new CollectionAssetsChanged(c.Id, initial, []));
        return c;
    }

    public static Collection CreateSmart(Guid tenantId, string name, Dictionary<string, JsonElement> query, Guid ownerId, DateTimeOffset now) =>
        new() { TenantId = tenantId, Name = name.Trim(), Type = CollectionType.Smart, Query = query, OwnerId = ownerId, CreatedAt = now, UpdatedAt = now };

    public void Rename(string name, DateTimeOffset now)
    {
        Name = name.Trim();
        UpdatedAt = now;
        Raise(new CollectionUpdated(Id));
    }

    public void SetQuery(Dictionary<string, JsonElement> query, DateTimeOffset now)
    {
        Query = query;
        UpdatedAt = now;
        Raise(new CollectionUpdated(Id));
    }

    public void SetVisibility(CollectionVisibility visibility, DateTimeOffset now)
    {
        Visibility = visibility;
        UpdatedAt = now;
        Raise(new CollectionUpdated(Id));
    }

    /// <summary>Adds and/or removes members of a static collection in one action. An id already present (adding) or
    /// already absent (removing) is silently ignored rather than erroring - the caller asked for an end state, not for
    /// every id to be individually novel.</summary>
    public void ChangeAssets(IReadOnlyList<Guid> add, IReadOnlyList<Guid> remove, DateTimeOffset now)
    {
        var added = add.Distinct().Where(id => !_assetIds.Contains(id)).ToList();
        var removed = remove.Distinct().Where(id => _assetIds.Contains(id)).ToList();
        if (added.Count == 0 && removed.Count == 0) return;
        _assetIds.AddRange(added);
        foreach (var id in removed) _assetIds.Remove(id);
        UpdatedAt = now;
        Raise(new CollectionAssetsChanged(Id, added, removed));
    }
}
