using System.Text.Json;
using Dam.Domain.Content;

namespace Dam.UnitTests;

public sealed class CollectionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static IReadOnlyList<T> Events<T>(Collection c) => c.DomainEvents.OfType<T>().ToList();

    [Fact]
    public void Creating_a_static_collection_with_initial_members_raises_one_assets_changed_event()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Collection.CreateStatic(Tenant, "  My picks  ", [a, b, a], Owner, Now);

        Assert.Equal("My picks", c.Name); // trimmed
        Assert.Equal(CollectionType.Static, c.Type);
        Assert.Equal([a, b], c.AssetIds); // de-duplicated, order preserved
        Assert.Equal(CollectionVisibility.Private, c.Visibility); // default: owner only
        var raised = Assert.Single(Events<CollectionAssetsChanged>(c));
        Assert.Equal([a, b], raised.AddedAssetIds);
        Assert.Empty(raised.RemovedAssetIds);
    }

    [Fact]
    public void Creating_an_empty_static_collection_raises_no_assets_changed_event()
    {
        var c = Collection.CreateStatic(Tenant, "Empty", [], Owner, Now);
        Assert.Empty(Events<CollectionAssetsChanged>(c));
    }

    [Fact]
    public void Creating_a_smart_collection_stores_its_query_and_has_no_members()
    {
        var query = new Dictionary<string, JsonElement> { ["Text"] = JsonSerializer.SerializeToElement("roadster") };
        var c = Collection.CreateSmart(Tenant, "Roadster shots", query, Owner, Now);

        Assert.Equal(CollectionType.Smart, c.Type);
        Assert.Empty(c.AssetIds);
        Assert.Equal("roadster", c.Query!["Text"].GetString());
        Assert.Empty(Events<CollectionAssetsChanged>(c)); // a saved query has no membership to announce
    }

    [Fact]
    public void Renaming_trims_and_raises_updated()
    {
        var c = Collection.CreateStatic(Tenant, "Old", [], Owner, Now);
        c.Rename("  New  ", Now);
        Assert.Equal("New", c.Name);
        Assert.Single(Events<CollectionUpdated>(c));
    }

    [Fact]
    public void Changing_assets_reports_only_ids_that_actually_moved()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var already = Guid.NewGuid();
        var c = Collection.CreateStatic(Tenant, "Set", [already], Owner, Now);

        c.ChangeAssets(add: [a, already], remove: [b], Now); // 'already' is a no-op add, 'b' is a no-op remove
        Assert.Equal([already, a], c.AssetIds);

        var raised = Events<CollectionAssetsChanged>(c)[^1];
        Assert.Equal([a], raised.AddedAssetIds);
        Assert.Empty(raised.RemovedAssetIds);
    }

    [Fact]
    public void Removing_a_member_takes_it_out_and_raises_the_removal()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Collection.CreateStatic(Tenant, "Set", [a, b], Owner, Now);

        c.ChangeAssets(add: [], remove: [a], Now.AddMinutes(1));
        Assert.Equal([b], c.AssetIds);

        var raised = Events<CollectionAssetsChanged>(c)[^1];
        Assert.Empty(raised.AddedAssetIds);
        Assert.Equal([a], raised.RemovedAssetIds);
    }

    [Fact]
    public void A_no_op_asset_change_raises_nothing()
    {
        var a = Guid.NewGuid();
        var never = Guid.NewGuid();
        var c = Collection.CreateStatic(Tenant, "Set", [a], Owner, Now);

        c.ChangeAssets(add: [a], remove: [never], Now); // adding what's already there, removing what was never there
        Assert.Equal([a], c.AssetIds);
        Assert.Single(Events<CollectionAssetsChanged>(c)); // only the creation event; nothing new
    }

    [Fact]
    public void Sharing_sets_visibility_and_raises_updated()
    {
        var c = Collection.CreateStatic(Tenant, "Set", [], Owner, Now);
        c.SetVisibility(CollectionVisibility.Tenant, Now);
        Assert.Equal(CollectionVisibility.Tenant, c.Visibility);
        Assert.Single(Events<CollectionUpdated>(c));
    }
}
