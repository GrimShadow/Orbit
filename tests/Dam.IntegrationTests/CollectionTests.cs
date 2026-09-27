using System.Net;

namespace Dam.IntegrationTests;

/// <summary>CRUD, membership and sharing for saved searches (spec A4.1 `Collection`, Step 1.7c). The search-index side
/// (a static collection's members getting real `collection_ids`, and a collection-scoped access rule actually
/// narrowing results) is covered separately in <see cref="CollectionSearchTests"/>, which needs a real OpenSearch.</summary>
[Collection("pg")]
public sealed class CollectionTests(PostgresFixture pg) : AssetTestBase(pg)
{
    [Fact]
    public async Task A_static_collection_is_created_with_its_initial_members_and_can_be_renamed()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand);
        var b = await NewAsset(admin, brand);

        var create = await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "Roadster picks", type = "static", assetIds = new[] { a, b } });
        Assert.Equal(HttpStatusCode.Created, create.Status);
        var id = Id(create.Body, "id");
        Assert.Equal("static", create.Body.GetProperty("type").GetString());
        Assert.Equal("private", create.Body.GetProperty("visibility").GetString());
        Assert.Equal([a, b], Strings(create.Body.GetProperty("assetIds")).Select(Guid.Parse));

        var renamed = await Call(HttpMethod.Put, $"/api/v1/collections/{id}", admin, new { name = "Hero picks" });
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal("Hero picks", renamed.Body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Creating_a_static_collection_with_an_unknown_asset_id_is_rejected()
    {
        var (t, admin, _) = await TenantWithPacks();
        var r = await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "static", assetIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
    }

    [Fact]
    public async Task A_smart_collection_stores_its_saved_search_and_can_have_it_replaced()
    {
        var (t, admin, _) = await TenantWithPacks();
        var create = await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "Saved search", type = "smart", query = new { query = "roadster" } });
        Assert.Equal(HttpStatusCode.Created, create.Status);
        var id = Id(create.Body, "id");
        Assert.Equal("smart", create.Body.GetProperty("type").GetString());

        var updated = await Call(HttpMethod.Put, $"/api/v1/collections/{id}/query", admin, new { query = new { query = "sedan" } });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
    }

    [Fact]
    public async Task Assets_can_only_be_added_to_or_removed_from_a_static_collection()
    {
        var (t, admin, _) = await TenantWithPacks();
        var smart = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "smart", query = new { } })).Body, "id");
        var r = await Call(HttpMethod.Post, $"/api/v1/collections/{smart}/assets", admin, new { assetIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
    }

    [Fact]
    public async Task Adding_and_removing_assets_updates_membership()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand);
        var b = await NewAsset(admin, brand);
        var id = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "static", assetIds = new[] { a } })).Body, "id");

        var added = await Call(HttpMethod.Post, $"/api/v1/collections/{id}/assets", admin, new { assetIds = new[] { b } });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        Assert.Equal([a, b], Strings(added.Body.GetProperty("assetIds")).Select(Guid.Parse));

        var removed = await Call(HttpMethod.Post, $"/api/v1/collections/{id}/assets", admin, new { assetIds = new[] { a }, remove = true });
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Equal([b], Strings(removed.Body.GetProperty("assetIds")).Select(Guid.Parse));
    }

    /// <summary>Only Librarian/Editor/Admin can create collections at all (collections.manage), and any of those
    /// curator roles can already see every collection - Private meaningfully hides a collection from everyone else
    /// (an ordinary Viewer/Contributor/Dealer), not from fellow curators, until it is explicitly shared tenant-wide.</summary>
    [Fact]
    public async Task A_private_collection_is_visible_to_its_owner_and_other_curators_but_not_an_ordinary_viewer()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var owner = As(t, "Editor");
        var otherCurator = As(t, "Librarian");
        var viewer = As(t, "Viewer");
        var id = Id((await Call(HttpMethod.Post, "/api/v1/collections", owner, new { name = "Mine", type = "static", assetIds = Array.Empty<Guid>() })).Body, "id");

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/collections/{id}", owner)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/collections/{id}", otherCurator)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/collections/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/collections/{id}", viewer)).Status);

        var share = await Call(HttpMethod.Post, $"/api/v1/collections/{id}/share", owner, new { visibility = "tenant" });
        Assert.Equal(HttpStatusCode.OK, share.Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/collections/{id}", viewer)).Status);
    }

    [Fact]
    public async Task Listing_returns_owned_and_tenant_shared_collections_but_not_someone_elses_private_one()
    {
        var (t, admin, _) = await TenantWithPacks();
        var owner = As(t, "Editor");
        var viewer = As(t, "Viewer");
        var mine = Id((await Call(HttpMethod.Post, "/api/v1/collections", owner, new { name = "Mine", type = "static", assetIds = Array.Empty<Guid>() })).Body, "id");
        var shared = Id((await Call(HttpMethod.Post, "/api/v1/collections", owner, new { name = "Shared", type = "static", assetIds = Array.Empty<Guid>() })).Body, "id");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/collections/{shared}/share", owner, new { visibility = "tenant" })).Status);

        var seenByViewer = (await Call(HttpMethod.Get, "/api/v1/collections", viewer)).Body.EnumerateArray().Select(c => Id(c, "id")).ToList();
        Assert.DoesNotContain(mine, seenByViewer);
        Assert.Contains(shared, seenByViewer);

        var seenByOwner = (await Call(HttpMethod.Get, "/api/v1/collections", owner)).Body.EnumerateArray().Select(c => Id(c, "id")).ToList();
        Assert.Contains(mine, seenByOwner);
        Assert.Contains(shared, seenByOwner);
    }

    [Fact]
    public async Task Managing_collections_requires_the_collections_manage_permission()
    {
        var (t, _, _) = await TenantWithPacks();
        var viewer = As(t, "Viewer"); // Viewer holds assets.read only, not collections.manage
        var r = await Call(HttpMethod.Post, "/api/v1/collections", viewer, new { name = "x", type = "static", assetIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
    }

    [Fact]
    public async Task Deleting_a_collection_removes_it()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "static", assetIds = Array.Empty<Guid>() })).Body, "id");
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/collections/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/collections/{id}", admin)).Status);
    }
}
