using System.Net;
using Dam.Domain.Authorization;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class FolderTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<Guid> Folder(string token, string name, Guid? parent = null, string? label = null, object? inherited = null)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/folders", token, new { parentId = parent, name, label, inheritedMetadata = inherited });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return Id(r.Body, "folder", "id");
    }

    private async Task<Dictionary<string, string>> Paths(string token) =>
        (await Call(HttpMethod.Get, "/api/v1/folders", token)).Body.EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("path").GetString()!);

    [Fact]
    public async Task Folders_get_readable_labels_paths_and_unique_sibling_labels()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var brand = await Folder(admin, "Brand Assets!");
        var stills = await Folder(admin, "Stills", brand);
        var again = await Folder(admin, "Stills", brand); // same name, same parent
        var paths = (await Call(HttpMethod.Get, "/api/v1/folders", admin)).Body.EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.Equal(["brand_assets", "brand_assets.stills", "brand_assets.stills_2"], paths);
        Assert.NotEqual(stills, again);

        var dup = await Call(HttpMethod.Post, "/api/v1/folders", admin, new { parentId = brand, name = "x", label = "stills" });
        Assert.Equal(HttpStatusCode.Conflict, dup.Status);
        var bad = await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "x", label = "Bad Label" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/folders", admin, new { parentId = Guid.NewGuid(), name = "orphan" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "" })).Status);
    }

    [Fact]
    public async Task The_tree_is_nested_sorted_and_a_folder_reports_its_ancestors_and_inherited_metadata()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var brand = await Folder(admin, "Brand", inherited: new { credit = "HQ", audience = "public" });
        var trail = await Folder(admin, "Trail", brand, inherited: new { credit = "Trail team" }); // overrides credit
        var hero = await Folder(admin, "Hero", trail);
        await Folder(admin, "Archive"); // second root, sorts first

        var tree = (await Call(HttpMethod.Get, "/api/v1/folders?tree=true", admin)).Body;
        Assert.Equal(["Archive", "Brand"], tree.EnumerateArray().Select(n => n.GetProperty("folder").GetProperty("name").GetString()!).ToArray());
        var brandNode = tree.EnumerateArray().First(n => n.GetProperty("folder").GetProperty("name").GetString() == "Brand");
        Assert.Equal("Trail", brandNode.GetProperty("children")[0].GetProperty("folder").GetProperty("name").GetString());
        Assert.Equal("Hero", brandNode.GetProperty("children")[0].GetProperty("children")[0].GetProperty("folder").GetProperty("name").GetString());

        var detail = (await Call(HttpMethod.Get, $"/api/v1/folders/{hero}", admin)).Body;
        Assert.Equal(["Brand", "Trail"], detail.GetProperty("ancestors").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("Trail team", detail.GetProperty("effectiveMetadata").GetProperty("credit").GetString()); // deeper wins
        Assert.Equal("public", detail.GetProperty("effectiveMetadata").GetProperty("audience").GetString());   // inherited
        Assert.Equal(0, detail.GetProperty("childCount").GetInt32());
    }

    [Fact]
    public async Task Moving_a_folder_rewrites_everything_beneath_it_and_refuses_cycles_and_collisions()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var brand = await Folder(admin, "Brand"); var trail = await Folder(admin, "Trail", brand); var hero = await Folder(admin, "Hero", trail);
        var archive = await Folder(admin, "Archive");
        await Folder(admin, "Old trail", archive, label: "trail"); // a folder with the same LABEL already sits in the destination

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/folders/{trail}/move", admin, new { newParentId = archive })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/folders/{brand}/move", admin, new { newParentId = hero })).Status); // into own descendant
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/folders/{brand}/move", admin, new { newParentId = brand })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/folders/{brand}/move", admin, new { newParentId = Guid.NewGuid() })).Status);

        var moved = await Call(HttpMethod.Post, $"/api/v1/folders/{brand}/move", admin, new { newParentId = archive });
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        var p = await Paths(admin);
        Assert.Equal("archive.brand", p["Brand"]);
        Assert.Equal("archive.brand.trail", p["Trail"]);
        Assert.Equal("archive.trail", p["Old trail"]); // untouched
        Assert.Equal("archive.brand.trail.hero", p["Hero"]);

        await Call(HttpMethod.Post, $"/api/v1/folders/{brand}/move", admin, new { newParentId = (Guid?)null }); // back to the top
        Assert.Equal("brand.trail.hero", (await Paths(admin))["Hero"]);
    }

    [Fact]
    public async Task Update_and_delete_folders()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var brand = await Folder(admin, "Brand"); var kid = await Folder(admin, "Kid", brand);

        var upd = await Call(HttpMethod.Patch, $"/api/v1/folders/{brand}", admin, new { name = "Brand assets", inheritedMetadata = new { credit = "HQ" } });
        Assert.Equal("Brand assets", upd.Body.GetProperty("folder").GetProperty("name").GetString());
        Assert.Equal("brand", upd.Body.GetProperty("folder").GetProperty("label").GetString()); // the label (and so the path) is permanent
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/folders/{brand}", admin, new { inheritedMetadata = "nope" })).Status);

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/api/v1/folders/{brand}", admin)).Status); // has a sub-folder
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/folders/{kid}", admin)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/folders/{brand}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/folders/{brand}", admin)).Status);
    }

    [Fact]
    public async Task Folders_cannot_be_nested_deeper_than_the_limit()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        Guid? parent = null;
        for (var i = 1; i <= 12; i++) parent = await Folder(admin, $"L{i}", parent);
        var tooDeep = await Call(HttpMethod.Post, "/api/v1/folders", admin, new { parentId = parent, name = "L13" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooDeep.Status);
    }

    [Fact]
    public async Task Everyone_with_read_access_can_browse_but_only_folder_managers_can_change()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var brand = await Folder(admin, "Brand");
        foreach (var role in new[] { "Editor", "Viewer", "Approver", "Contributor" })
        {
            var token = As(t, role);
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/folders?tree=true", token)).Status);
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/folders/{brand}", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/folders", token, new { name = "x" })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Patch, $"/api/v1/folders/{brand}", token, new { name = "x" })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/folders/{brand}", token)).Status);
        }
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/folders", As(t, "Librarian"), new { name = "By librarian" })).Status);
    }

    [Fact]
    public async Task An_access_rule_lets_someone_manage_folders_only_inside_their_assigned_subtree()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var brand = await Folder(admin, "Brand"); var trail = await Folder(admin, "Trail", brand); var campaigns = await Folder(admin, "Campaigns");

        var sub = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(t, sub, ["Viewer"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
            { principalType = "user", principalId = userId, scopeType = "folder", scopeValue = "brand", permissions = new[] { Permissions.FoldersManage } })).Status);

        // inside brand.*: allowed
        var made = await Call(HttpMethod.Post, "/api/v1/folders", token, new { parentId = trail, name = "Hero" });
        Assert.Equal(HttpStatusCode.Created, made.Status);
        var hero = Id(made.Body, "folder", "id");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/folders/{hero}", token, new { name = "Hero shots" })).Status);
        // outside it, or at the top level: refused
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/folders", token, new { parentId = campaigns, name = "Nope" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/folders", token, new { name = "Root" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Patch, $"/api/v1/folders/{campaigns}", token, new { name = "Nope" })).Status);
        // moving needs authority at both ends
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/folders/{hero}/move", token, new { newParentId = campaigns })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/folders/{hero}/move", token, new { newParentId = (Guid?)null })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/folders/{hero}/move", token, new { newParentId = brand })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/folders/{hero}", token)).Status);
    }

    [Fact]
    public async Task Folders_are_private_to_their_tenant()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var inA = await Folder(Admin(a), "Shared name");
        var inB = await Folder(Admin(b), "Shared name"); // same label in another tenant is fine
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/folders/{inA}", Admin(b))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/folders/{inA}", Admin(b))).Status);
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/folders", Admin(b))).Body.GetArrayLength());
        Assert.NotEqual(inA, inB);
        Assert.Equal(1, Count(a, "SELECT count(*) FROM folders"));
    }
}
