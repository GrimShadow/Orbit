using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>GET /api/v1/users/directory: the tenant's active people (id, name, email) for pickers, open to anyone who chooses people.</summary>
[Collection("pg")]
public sealed class UserDirectoryTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string Route = "/api/v1/users/directory";

    /// <summary>Signs a person in once so the first request provisions them, and returns their Orbit id.</summary>
    private async Task<Guid> SignIn(Guid tenant, string name, string email, params string[] roles)
    {
        var subject = Guid.NewGuid();
        var me = await Call(HttpMethod.Get, "/api/v1/me", Pads(tenant, subject, name, email, roles));
        Assert.Equal(HttpStatusCode.OK, me.Status);
        return me.Body.GetProperty("userId").GetGuid();
    }

    private static string Pads(Guid tenant, Guid subject, string name, string email, string[] roles) =>
        ApiTestHost.TokenFor(tenant, subject, roles: roles, name: name, email: email);

    private static string[] Names(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayName").GetString()!).ToArray();

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Librarian", true)]
    [InlineData("Editor", true)]
    [InlineData("Approver", true)]
    [InlineData("Contributor", true)]
    [InlineData("Viewer", false)]
    [InlineData("Dealer", false)]
    [InlineData("ApiClient", false)]
    public async Task Each_built_in_role_gets_the_directory_or_a_403(string role, bool allowed)
    {
        var t = await NewTenantAsync();
        var res = await Call(HttpMethod.Get, Route, As(t, role));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, res.Status);
    }

    [Fact]
    public async Task A_caller_without_a_token_gets_401()
    {
        using var http = Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task A_librarian_who_cannot_list_users_can_use_the_directory()
    {
        var t = await NewTenantAsync();
        var librarian = As(t, "Librarian");
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/users", librarian)).Status);
        await SignIn(t, "Priya Acme", "priya@acme.example", "Editor");
        var dir = await Call(HttpMethod.Get, Route, librarian);
        Assert.Equal(HttpStatusCode.OK, dir.Status);
        Assert.Contains("Priya Acme", Names(dir.Body));
    }

    [Fact]
    public async Task Only_the_callers_tenant_is_listed()
    {
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        await SignIn(a, "Alice Acme", "alice@acme.example", "Editor");
        await SignIn(b, "Bob Other", "bob@other.example", "Editor");
        var inA = await Call(HttpMethod.Get, Route, As(a, "Librarian"));
        var inB = await Call(HttpMethod.Get, Route, As(b, "Librarian"));
        Assert.Contains("Alice Acme", Names(inA.Body));
        Assert.DoesNotContain("Bob Other", Names(inA.Body));
        Assert.Contains("Bob Other", Names(inB.Body));
        Assert.DoesNotContain("Alice Acme", Names(inB.Body));
        // a search for the other tenant's person finds nothing, whatever the text
        Assert.Empty(Names((await Call(HttpMethod.Get, Route + "?q=bob", As(a, "Librarian"))).Body));
    }

    [Fact]
    public async Task Disabled_people_are_not_listed()
    {
        var t = await NewTenantAsync();
        var gone = await SignIn(t, "Gone Acme", "gone@acme.example", "Editor");
        await SignIn(t, "Here Acme", "here@acme.example", "Editor");
        var off = await Call(HttpMethod.Patch, $"/api/v1/users/{gone}", Admin(t), new { status = "disabled" });
        Assert.Equal(HttpStatusCode.OK, off.Status);
        var names = Names((await Call(HttpMethod.Get, Route, As(t, "Librarian"))).Body);
        Assert.Contains("Here Acme", names);
        Assert.DoesNotContain("Gone Acme", names);
    }

    [Fact]
    public async Task Search_matches_name_or_email_by_prefix_or_substring_ignoring_case()
    {
        var t = await NewTenantAsync();
        await SignIn(t, "Margaret Acme", "mags@acme.example", "Editor");
        await SignIn(t, "Tom Hargreaves", "tom@acme.example", "Editor");
        await SignIn(t, "Zed Zulu", "zed@harbour.example", "Editor");
        await SignIn(t, "Unrelated", "nobody@acme.example", "Editor");
        var lib = As(t, "Librarian");
        Assert.Equal(["Margaret Acme"], Names((await Call(HttpMethod.Get, Route + "?q=MARG", lib)).Body));              // prefix, upper case
        Assert.Equal(["Margaret Acme"], Names((await Call(HttpMethod.Get, Route + "?q=garet", lib)).Body));              // substring in a name
        Assert.Equal(["Tom Hargreaves"], Names((await Call(HttpMethod.Get, Route + "?q=greaves", lib)).Body));
        Assert.Equal(["Zed Zulu"], Names((await Call(HttpMethod.Get, Route + "?q=HARBOUR", lib)).Body));                 // email domain
        Assert.Equal(["Margaret Acme"], Names((await Call(HttpMethod.Get, Route + "?q=mags@", lib)).Body));              // email prefix
        Assert.Empty(Names((await Call(HttpMethod.Get, Route + "?q=%25", lib)).Body));                                   // a literal percent is not a wildcard
        Assert.Empty(Names((await Call(HttpMethod.Get, Route + "?q=no-such-person", lib)).Body));
    }

    [Fact]
    public async Task Results_are_ordered_by_display_name_and_page_with_a_cursor()
    {
        var t = await NewTenantAsync();
        foreach (var n in new[] { "delta", "Alpha", "echo", "Bravo", "charlie", "Alpha" })
            await SignIn(t, n, $"{n}-{Guid.NewGuid():N}@acme.example", "Contributor");
        var lib = Admin(t);
        string[] mine = ["Alpha", "Alpha", "Bravo", "charlie", "delta", "echo"];

        var all = Names((await Call(HttpMethod.Get, Route + "?limit=50", lib)).Body);   // plus the caller, who signed in too
        Assert.Equal(mine, all.Where(mine.Contains).ToArray());
        Assert.Equal(all.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), all);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var res = await Call(HttpMethod.Get, Route + "?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), lib);
            Assert.Equal(HttpStatusCode.OK, res.Status);
            Assert.True(res.Body.GetProperty("items").GetArrayLength() <= 2);
            seen.AddRange(Names(res.Body));
            cursor = res.Body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : res.Body.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(all, seen);   // every person once, in the same order, none skipped or repeated across the Alpha tie
    }

    [Fact]
    public async Task The_limit_defaults_to_20_and_is_capped_at_50()
    {
        var t = await NewTenantAsync();
        for (var i = 0; i < 52; i++) await SignIn(t, $"Person {i:D2}", $"p{i:D2}-{Guid.NewGuid():N}@acme.example", "Contributor");
        var lib = Admin(t);
        var def = await Call(HttpMethod.Get, Route, lib);
        Assert.Equal(20, def.Body.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, def.Body.GetProperty("nextCursor").ValueKind);
        Assert.Equal(50, (await Call(HttpMethod.Get, Route + "?limit=500", lib)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Call(HttpMethod.Get, Route + "?limit=0", lib)).Body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_bad_cursor_is_rejected()
    {
        var t = await NewTenantAsync();
        var res = await Call(HttpMethod.Get, Route + "?cursor=not-a-cursor!", As(t, "Librarian"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.Status);
    }

    [Fact]
    public async Task Entries_carry_only_id_name_and_email()
    {
        var t = await NewTenantAsync();
        var id = await SignIn(t, "Dana Acme", "dana@acme.example", "Dealer");
        var res = await Call(HttpMethod.Get, Route + "?q=dana", As(t, "Librarian"));
        var item = res.Body.GetProperty("items")[0];
        Assert.Equal(["displayName", "email", "id"], item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("dana@acme.example", item.GetProperty("email").GetString());
        var raw = res.Body.GetRawText().ToLowerInvariant();
        foreach (var leak in new[] { "externalsubject", "attributes", "roles", "groups", "lastseen", "status" }) Assert.DoesNotContain(leak, raw);
    }
}
