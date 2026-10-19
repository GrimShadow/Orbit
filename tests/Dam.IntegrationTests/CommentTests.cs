using System.Net;
using System.Text.Json;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>Comments, threads, annotations, mentions, the activity feed and the mention picker (ADR-0032) against a real Postgres.</summary>
[Collection("pg")]
public sealed class CommentTests(PostgresFixture pg) : AssetTestBase(pg)
{
    // ---- builders ----------------------------------------------------------------------------------------------

    private sealed record Person(string Token, Guid Id, string Email, string Name);

    private async Task<Person> Provision(Guid tenant, string role, string name, Dictionary<string, string[]>? attributes = null)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), [role], attributes: attributes, name: name);
        var me = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body;
        return new Person(token, me.GetProperty("userId").GetGuid(), me.GetProperty("email").GetString()!, name);
    }

    private static string Comments(Guid asset) => $"/api/v1/assets/{asset}/comments";

    private static string Mention(Person p) => $"@[{p.Name}](user:{p.Id})";

    private Task<(HttpStatusCode Status, JsonElement Body)> Add(string token, Guid asset, string body, Guid? parent = null, object? anchor = null, Guid? version = null) =>
        Call(HttpMethod.Post, Comments(asset), token, new { body, parentId = parent, anchor, versionId = version });

    private async Task<JsonElement> Posted(string token, Guid asset, string body, Guid? parent = null, object? anchor = null, Guid? version = null)
    {
        var r = await Add(token, asset, body, parent, anchor, version);
        Assert.True(r.Status == HttpStatusCode.Created, r.Body.ToString());
        return r.Body;
    }

    private static object Region(double x = 0.12, double y = 0.30, double w = 0.25, double h = 0.18, int? page = null) => new { type = "region", x, y, w, h, page };

    private async Task<JsonElement> Threads(string token, Guid asset, string query = "")
    {
        var r = await Call(HttpMethod.Get, Comments(asset) + query, token);
        Assert.True(r.Status == HttpStatusCode.OK, r.Body.ToString());
        return r.Body;
    }

    private static Guid[] RootIds(JsonElement threads) => threads.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("root").GetProperty("id").GetGuid()).ToArray();

    private static (int Open, int Resolved, int Annotations) Counts(JsonElement threads)
    {
        var c = threads.GetProperty("counts");
        return (c.GetProperty("open").GetInt32(), c.GetProperty("resolved").GetInt32(), c.GetProperty("annotations").GetInt32());
    }

    private static string[] Errors(JsonElement problem) => problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();

    private Task<(HttpStatusCode Status, JsonElement Body)> Patch(string token, Guid comment, object body) => Call(HttpMethod.Patch, $"/api/v1/comments/{comment}", token, body);

    /// <summary>Gives an asset another version without running the upload pipeline; returns the new version's id.</summary>
    private Guid AddVersion(Guid asset)
    {
        var id = Guid.NewGuid();
        Sql("INSERT INTO asset_versions (id, tenant_id, asset_id, version_no, bucket, storage_key, size_bytes, mime, sha256, technical_metadata, change_note, upload_id, created_at) "
            + $"SELECT '{id}', tenant_id, id, (SELECT coalesce(max(version_no), 0) + 1 FROM asset_versions WHERE asset_id = '{asset}'), 'dam-originals', gen_random_uuid()::text, 10, 'image/png', repeat('a', 64), '{{}}'::jsonb, '', gen_random_uuid(), now() "
            + $"FROM assets WHERE id = '{asset}'; UPDATE assets SET current_version_id = '{id}' WHERE id = '{asset}'");
        return id;
    }

    private async Task Publish(string admin, Guid asset)
    {
        foreach (var action in new[] { "submit", "approve", "publish" })
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", admin, new { reason = (string?)null })).Status);
    }

    private sealed record Scene(Guid Tenant, string Admin, Guid Folder, Guid Asset, Person Ann, Person Bea);

    /// <summary>A tenant with one draft image asset (owned by the admin) and two Editors, Ann and Bea.</summary>
    private async Task<Scene> NewScene()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await Call(HttpMethod.Get, "/api/v1/me", admin);
        var asset = await NewAsset(admin, folder, title: "Acme brochure");
        return new Scene(t, admin, folder, asset, await Provision(t, "Editor", "Ann Author"), await Provision(t, "Editor", "Bea Brooks"));
    }

    // ---- adding ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_comment_is_attached_to_the_current_version_and_returned_in_full()
    {
        var s = await NewScene();
        var v1 = AddVersion(s.Asset);
        var c = await Posted(s.Ann.Token, s.Asset, "  Please check the logo.  ");

        Assert.Equal("Please check the logo.", c.GetProperty("body").GetString());   // trimmed
        Assert.Equal((s.Asset, v1, 1), (c.GetProperty("assetId").GetGuid(), c.GetProperty("versionId").GetGuid(), c.GetProperty("versionNo").GetInt32()));
        Assert.Equal((s.Ann.Id, "Ann Author"), (c.GetProperty("authorId").GetGuid(), c.GetProperty("authorName").GetString()));
        Assert.Equal(JsonValueKind.Null, c.GetProperty("parentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("anchor").ValueKind);
        Assert.Empty(c.GetProperty("mentions").EnumerateArray());
        Assert.Equal((false, false, false, false), (c.GetProperty("resolved").GetBoolean(), c.GetProperty("edited").GetBoolean(), c.GetProperty("deleted").GetBoolean(), c.GetProperty("resolvedBy").ValueKind != JsonValueKind.Null));
        Assert.True(Math.Abs((c.GetProperty("createdAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalMinutes) < 2);

        // A later version does not move the old comment; the next comment goes on the new version.
        var v2 = AddVersion(s.Asset);
        var later = await Posted(s.Ann.Token, s.Asset, "On the new one");
        Assert.Equal((v2, 2), (later.GetProperty("versionId").GetGuid(), later.GetProperty("versionNo").GetInt32()));
        var onOld = await Posted(s.Ann.Token, s.Asset, "Back on version 1", version: v1);
        Assert.Equal(1, onOld.GetProperty("versionNo").GetInt32());
        Assert.Equal(c.GetProperty("id").GetGuid(), RootIds(await Threads(s.Ann.Token, s.Asset, $"?versionId={v1}"))[0]);
    }

    [Fact]
    public async Task An_asset_with_no_file_yet_takes_comments_without_a_version_and_a_foreign_version_is_refused()
    {
        var s = await NewScene();
        var c = await Posted(s.Ann.Token, s.Asset, "No file yet");
        Assert.Equal(JsonValueKind.Null, c.GetProperty("versionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("versionNo").ValueKind);

        var other = await NewAsset(s.Admin, s.Folder, title: "Other");
        var foreign = AddVersion(other);
        var bad = await Add(s.Ann.Token, s.Asset, "x", version: foreign);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal(["versionId"], Errors(bad.Body));
        Assert.Equal(["versionId"], Errors((await Add(s.Ann.Token, s.Asset, "x", version: Guid.NewGuid())).Body));
    }

    [Fact]
    public async Task Adding_a_comment_goes_through_the_outbox_and_audit_log_without_the_text()
    {
        var s = await NewScene();
        var bea = s.Bea;
        var c = await Posted(s.Ann.Token, s.Asset, $"Secret wording for {Mention(bea)}", anchor: Region());
        var id = c.GetProperty("id").GetGuid();

        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM outbox WHERE event_type = 'comment.added' AND payload->>'CommentId' = '{id}'"));
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM audit_log WHERE action = 'comment.added' AND entity_type = 'comment' AND entity_id = '{id}'"));
        Assert.Equal(0, Count(s.Tenant, "SELECT count(*) FROM outbox WHERE event_type = 'comment.added' AND payload::text ILIKE '%Secret wording%'"));
        Assert.Equal(0, Count(s.Tenant, "SELECT count(*) FROM audit_log WHERE action = 'comment.added' AND after::text ILIKE '%Secret wording%'"));
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM outbox WHERE event_type = 'comment.added' AND payload->>'AnchorType' = 'region' AND payload->'Mentions' ? '{bea.Id}' AND (payload->>'Resolved')::boolean = false"));
    }

    [Fact]
    public async Task The_body_is_checked_and_problems_are_reported_under_the_field_name()
    {
        var s = await NewScene();
        foreach (var bad in new[] { "", "   ", "\n\t " })
            Assert.Equal(["body"], Errors((await Add(s.Ann.Token, s.Asset, bad)).Body));
        Assert.Equal(["body"], Errors((await Call(HttpMethod.Post, Comments(s.Asset), s.Ann.Token, new { })).Body));
        Assert.Equal(["body"], Errors((await Add(s.Ann.Token, s.Asset, new string('x', 4001))).Body));
        Assert.Equal(HttpStatusCode.Created, (await Add(s.Ann.Token, s.Asset, new string('x', 4000))).Status);
        Assert.Equal(HttpStatusCode.Created, (await Add(s.Ann.Token, s.Asset, " " + new string('y', 4000) + " ")).Status);   // the limit is on the trimmed text
        var many = string.Join(" ", Enumerable.Range(0, 21).Select(i => $"@[P{i}](user:{Guid.NewGuid()})"));
        Assert.Equal(["body"], Errors((await Add(s.Ann.Token, s.Asset, many)).Body));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Add(s.Ann.Token, s.Asset, "nul\0byte")).Status);
        Assert.Equal(2, Count(s.Tenant, "SELECT count(*) FROM comments"));   // nothing else was stored
    }

    // ---- threads -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Threads_come_oldest_first_with_their_replies_oldest_first_and_counts()
    {
        var s = await NewScene();
        var first = (await Posted(s.Ann.Token, s.Asset, "First")).GetProperty("id").GetGuid();
        var second = (await Posted(s.Ann.Token, s.Asset, "Pin here", anchor: Region())).GetProperty("id").GetGuid();
        var third = (await Posted(s.Bea.Token, s.Asset, "Third")).GetProperty("id").GetGuid();
        var r1 = (await Posted(s.Bea.Token, s.Asset, "Reply 1", parent: first)).GetProperty("id").GetGuid();
        var r2 = (await Posted(s.Ann.Token, s.Asset, "Reply 2", parent: first)).GetProperty("id").GetGuid();
        await Posted(s.Bea.Token, s.Asset, "Reply on second", parent: second);

        var all = await Threads(s.Ann.Token, s.Asset);
        Assert.Equal([first, second, third], RootIds(all));
        var firstThread = all.GetProperty("threads")[0];
        Assert.Equal([r1, r2], firstThread.GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.All(firstThread.GetProperty("replies").EnumerateArray(), r => Assert.Equal(first, r.GetProperty("parentId").GetGuid()));
        Assert.Equal((3, 0, 1), Counts(all));

        // Resolve the pinned thread: the counts follow, and so do the filters.
        Assert.Equal(HttpStatusCode.OK, (await Patch(s.Bea.Token, second, new { resolved = true })).Status);
        Assert.Equal((2, 1, 1), Counts(await Threads(s.Ann.Token, s.Asset)));
        Assert.Equal([second], RootIds(await Threads(s.Ann.Token, s.Asset, "?state=resolved")));
        Assert.Equal([first, third], RootIds(await Threads(s.Ann.Token, s.Asset, "?state=open")));
        Assert.Equal([second], RootIds(await Threads(s.Ann.Token, s.Asset, "?kind=annotation")));
        Assert.Equal([first, third], RootIds(await Threads(s.Ann.Token, s.Asset, "?kind=comment")));
        Assert.Empty(RootIds(await Threads(s.Ann.Token, s.Asset, "?kind=annotation&state=open")));
        Assert.Equal([first, second, third], RootIds(await Threads(s.Ann.Token, s.Asset, "?state=all&kind=all")));
        // The counts describe the whole version, whatever the filters hide.
        Assert.Equal((2, 1, 1), Counts(await Threads(s.Ann.Token, s.Asset, "?state=resolved")));

        foreach (var bad in new[] { "?state=nope", "?kind=nope" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, Comments(s.Asset) + bad, s.Ann.Token)).Status);
    }

    [Fact]
    public async Task Counts_and_lists_can_be_narrowed_to_one_version()
    {
        var s = await NewScene();
        var v1 = AddVersion(s.Asset);
        var a = (await Posted(s.Ann.Token, s.Asset, "On v1", anchor: Region())).GetProperty("id").GetGuid();
        var v2 = AddVersion(s.Asset);
        var b = (await Posted(s.Ann.Token, s.Asset, "On v2")).GetProperty("id").GetGuid();
        await Posted(s.Bea.Token, s.Asset, "Reply to the v1 thread", parent: a);

        var forV1 = await Threads(s.Ann.Token, s.Asset, $"?versionId={v1}");
        Assert.Equal([a], RootIds(forV1));
        Assert.Equal((1, 0, 1), Counts(forV1));
        Assert.Single(forV1.GetProperty("threads")[0].GetProperty("replies").EnumerateArray());
        var forV2 = await Threads(s.Ann.Token, s.Asset, $"?versionId={v2}");
        Assert.Equal([b], RootIds(forV2));
        Assert.Equal((1, 0, 0), Counts(forV2));
        Assert.Equal([a, b], RootIds(await Threads(s.Ann.Token, s.Asset)));
        Assert.Empty(RootIds(await Threads(s.Ann.Token, s.Asset, $"?versionId={Guid.NewGuid()}")));
    }

    [Fact]
    public async Task A_reply_can_only_target_the_first_comment_of_a_thread_and_carries_no_anchor()
    {
        var s = await NewScene();
        var v = AddVersion(s.Asset);
        var root = (await Posted(s.Ann.Token, s.Asset, "Root")).GetProperty("id").GetGuid();
        var reply = await Posted(s.Bea.Token, s.Asset, "Reply", parent: root);
        Assert.Equal((root, v), (reply.GetProperty("parentId").GetGuid(), reply.GetProperty("versionId").GetGuid()));   // inherits the thread's version

        var toReply = await Add(s.Ann.Token, s.Asset, "Reply to a reply", parent: reply.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toReply.Status);
        Assert.Equal(["parentId"], Errors(toReply.Body));
        Assert.Equal(["parentId"], Errors((await Add(s.Ann.Token, s.Asset, "x", parent: Guid.NewGuid())).Body));
        var otherAsset = await NewAsset(s.Admin, s.Folder, title: "Other");
        Assert.Equal(["parentId"], Errors((await Add(s.Ann.Token, otherAsset, "x", parent: root)).Body));   // the thread lives on another asset
        Assert.Equal(["anchor"], Errors((await Add(s.Ann.Token, s.Asset, "x", parent: root, anchor: Region())).Body));
        AddVersion(s.Asset);
        Assert.Equal(["versionId"], Errors((await Add(s.Ann.Token, s.Asset, "x", parent: root, version: Guid.NewGuid())).Body));
        var afterNewVersion = await Posted(s.Ann.Token, s.Asset, "Still on the thread's version", parent: root);
        Assert.Equal(v, afterNewVersion.GetProperty("versionId").GetGuid());
        Assert.Equal(3, Count(s.Tenant, "SELECT count(*) FROM comments"));
    }

    // ---- anchors -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_region_annotation_persists_with_its_normalised_coordinates_exactly()
    {
        var s = await NewScene();
        var made = await Posted(s.Ann.Token, s.Asset, "Fix this corner", anchor: Region(0.123456789, 0.3, 0.25, 0.1 + 0.2, page: null));
        var id = made.GetProperty("id").GetGuid();
        var anchor = made.GetProperty("anchor");
        Assert.Equal(("region", 0.123456789, 0.3, 0.25, 0.1 + 0.2), (anchor.GetProperty("type").GetString(), anchor.GetProperty("x").GetDouble(), anchor.GetProperty("y").GetDouble(), anchor.GetProperty("w").GetDouble(), anchor.GetProperty("h").GetDouble()));
        Assert.Equal(JsonValueKind.Null, anchor.GetProperty("page").ValueKind);
        Assert.Equal(JsonValueKind.Null, anchor.GetProperty("startMs").ValueKind);

        // Read back later it is the same, and the table holds it as jsonb with its own indexed type.
        var back = (await Threads(s.Bea.Token, s.Asset)).GetProperty("threads")[0].GetProperty("root").GetProperty("anchor");
        Assert.Equal(anchor.GetRawText(), back.GetRawText());
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM comments WHERE id = '{id}' AND anchor_type = 'region' AND (anchor->>'x')::numeric = 0.123456789 AND (anchor->>'w')::numeric = 0.25"));
    }

    [Fact]
    public async Task A_pin_and_a_page_region_are_stored_as_given()
    {
        var s = await NewScene();
        var pin = await Posted(s.Ann.Token, s.Asset, "Here", anchor: new { type = "point", x = 0.5, y = 0.75 });
        Assert.Equal(("point", 0.5, 0.75), (pin.GetProperty("anchor").GetProperty("type").GetString(), pin.GetProperty("anchor").GetProperty("x").GetDouble(), pin.GetProperty("anchor").GetProperty("y").GetDouble()));
        Assert.Equal(JsonValueKind.Null, pin.GetProperty("anchor").GetProperty("w").ValueKind);

        var doc = await NewAsset(s.Admin, s.Folder, type: "document", title: "Manual");
        var onPage = await Posted(s.Ann.Token, doc, "Typo", anchor: Region(0.1, 0.2, 0.3, 0.1, page: 4));
        Assert.Equal(4, onPage.GetProperty("anchor").GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task A_timecode_annotation_on_a_video_persists_its_milliseconds()
    {
        var s = await NewScene();
        var video = await NewAsset(s.Admin, s.Folder, type: "video", title: "Teaser");
        var range = await Posted(s.Ann.Token, video, "Cut here", anchor: new { type = "timecode", startMs = 83_500, endMs = 90_000 });
        var a = range.GetProperty("anchor");
        Assert.Equal(("timecode", 83_500L, 90_000L), (a.GetProperty("type").GetString(), a.GetProperty("startMs").GetInt64(), a.GetProperty("endMs").GetInt64()));
        Assert.Equal(JsonValueKind.Null, a.GetProperty("x").ValueKind);

        var moment = await Posted(s.Ann.Token, video, "Audio pop", anchor: new { type = "timecode", startMs = 1000 });
        Assert.Equal(JsonValueKind.Null, moment.GetProperty("anchor").GetProperty("endMs").ValueKind);
        var back = (await Threads(s.Bea.Token, video)).GetProperty("threads")[0].GetProperty("root").GetProperty("anchor");
        Assert.Equal(a.GetRawText(), back.GetRawText());
        Assert.Equal(2, Count(s.Tenant, $"SELECT count(*) FROM comments WHERE asset_id = '{video}' AND anchor_type = 'timecode'"));
    }

    [Fact]
    public async Task Anchors_that_do_not_fit_the_asset_or_the_image_are_refused_naming_the_field()
    {
        var s = await NewScene();
        var video = await NewAsset(s.Admin, s.Folder, type: "video", title: "Teaser");
        var audio = await NewAsset(s.Admin, s.Folder, type: "audio", title: "Jingle");

        async Task<string[]> Refused(Guid asset, object anchor)
        {
            var r = await Add(s.Ann.Token, asset, "x", anchor: anchor);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return Errors(r.Body);
        }

        // by asset type
        Assert.Equal(["anchor.type"], await Refused(video, Region()));
        Assert.Equal(["anchor.type"], await Refused(video, new { type = "point", x = 0.5, y = 0.5 }));
        Assert.Equal(["anchor.type"], await Refused(audio, Region()));
        Assert.Equal(["anchor.type"], await Refused(s.Asset, new { type = "timecode", startMs = 1000 }));
        // by shape
        Assert.Equal(["anchor.type"], await Refused(s.Asset, new { type = "circle", x = 0.5 }));
        Assert.Equal(["anchor.x"], await Refused(s.Asset, Region(x: 1.2)));
        Assert.Equal(["anchor.y"], await Refused(s.Asset, Region(y: -0.1)));
        Assert.Equal(["anchor.w"], await Refused(s.Asset, Region(x: 0.8, w: 0.3)));    // leaves the image
        Assert.Equal(["anchor.h"], await Refused(s.Asset, Region(y: 0.9, h: 0.2)));
        Assert.Equal(["anchor.w"], await Refused(s.Asset, Region(w: 0)));
        Assert.Equal(["anchor.h", "anchor.w", "anchor.x", "anchor.y"], await Refused(s.Asset, new { type = "region" }));
        Assert.Equal(["anchor.page"], await Refused(s.Asset, Region(page: 0)));
        Assert.Equal(["anchor.startMs"], await Refused(video, new { type = "timecode", startMs = -5 }));
        Assert.Equal(["anchor.endMs"], await Refused(video, new { type = "timecode", startMs = 5000, endMs = 5000 }));
        Assert.Equal(["anchor.endMs"], await Refused(video, new { type = "timecode", startMs = 5000, endMs = 86_400_001L }));
        Assert.Equal(["anchor.x"], await Refused(video, new { type = "timecode", startMs = 5000, x = 0.1 }));
        Assert.Equal(0, Count(s.Tenant, "SELECT count(*) FROM comments"));
    }

    // ---- editing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_author_can_edit_and_an_edit_is_marked()
    {
        var s = await NewScene();
        var c = await Posted(s.Ann.Token, s.Asset, "Original");
        var id = c.GetProperty("id").GetGuid();
        Assert.False(c.GetProperty("edited").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(s.Bea.Token, id, new { body = "Hijacked" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(s.Admin, id, new { body = "Even the admin cannot edit it" })).Status);

        var edited = await Patch(s.Ann.Token, id, new { body = "  Better wording  " });
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        Assert.Equal("Better wording", edited.Body.GetProperty("body").GetString());
        Assert.True(edited.Body.GetProperty("edited").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, edited.Body.GetProperty("editedAt").ValueKind);
        Assert.Equal("Better wording", (await Threads(s.Bea.Token, s.Asset)).GetProperty("threads")[0].GetProperty("root").GetProperty("body").GetString());
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM audit_log WHERE action = 'comment.edited' AND entity_id = '{id}'"));

        // The same text again is not an edit; empty, too long or nothing at all is refused.
        await Patch(s.Ann.Token, id, new { body = "Better wording" });
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM audit_log WHERE action = 'comment.edited' AND entity_id = '{id}'"));
        Assert.Equal(["body"], Errors((await Patch(s.Ann.Token, id, new { body = "  " })).Body));
        Assert.Equal(["body"], Errors((await Patch(s.Ann.Token, id, new { body = new string('z', 4001) })).Body));
        Assert.Equal(["body"], Errors((await Patch(s.Ann.Token, id, new { })).Body));
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(s.Ann.Token, Guid.NewGuid(), new { body = "x" })).Status);
    }

    // ---- resolving ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_thread_can_be_resolved_and_reopened_and_its_replies_share_the_state()
    {
        var s = await NewScene();
        var root = (await Posted(s.Ann.Token, s.Asset, "Root")).GetProperty("id").GetGuid();
        var reply = (await Posted(s.Bea.Token, s.Asset, "Reply", parent: root)).GetProperty("id").GetGuid();

        var done = await Patch(s.Bea.Token, root, new { resolved = true });
        Assert.Equal(HttpStatusCode.OK, done.Status);
        Assert.True(done.Body.GetProperty("resolved").GetBoolean());
        Assert.Equal((s.Bea.Id, "Bea Brooks"), (done.Body.GetProperty("resolvedBy").GetGuid(), done.Body.GetProperty("resolvedByName").GetString()));
        Assert.NotEqual(JsonValueKind.Null, done.Body.GetProperty("resolvedAt").ValueKind);

        var thread = (await Threads(s.Ann.Token, s.Asset)).GetProperty("threads")[0];
        var r = thread.GetProperty("replies")[0];
        Assert.Equal(reply, r.GetProperty("id").GetGuid());
        Assert.Equal((true, s.Bea.Id), (r.GetProperty("resolved").GetBoolean(), r.GetProperty("resolvedBy").GetGuid()));   // the reply shows its thread's state
        Assert.Equal("Reply", r.GetProperty("body").GetString());                                                          // but keeps its own text

        // Resolving twice changes nothing and raises nothing more.
        await Patch(s.Ann.Token, root, new { resolved = true });
        Assert.Equal(1, Count(s.Tenant, "SELECT count(*) FROM outbox WHERE event_type = 'comment.resolved'"));
        Assert.Equal(s.Bea.Id, (await Threads(s.Ann.Token, s.Asset)).GetProperty("threads")[0].GetProperty("root").GetProperty("resolvedBy").GetGuid());

        var reopened = await Patch(s.Ann.Token, root, new { resolved = false });
        Assert.False(reopened.Body.GetProperty("resolved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reopened.Body.GetProperty("resolvedBy").ValueKind);
        Assert.False((await Threads(s.Ann.Token, s.Asset)).GetProperty("threads")[0].GetProperty("replies")[0].GetProperty("resolved").GetBoolean());
        Assert.Equal(1, Count(s.Tenant, "SELECT count(*) FROM outbox WHERE event_type = 'comment.reopened'"));
        Assert.Equal(2, Count(s.Tenant, "SELECT count(*) FROM audit_log WHERE action IN ('comment.resolved', 'comment.reopened')"));
    }

    [Fact]
    public async Task Only_the_first_comment_of_a_thread_can_be_resolved_and_it_needs_comments_write()
    {
        var s = await NewScene();
        var root = (await Posted(s.Ann.Token, s.Asset, "Root")).GetProperty("id").GetGuid();
        var reply = (await Posted(s.Bea.Token, s.Asset, "Reply", parent: root)).GetProperty("id").GetGuid();
        var bad = await Patch(s.Ann.Token, reply, new { resolved = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal(["resolved"], Errors(bad.Body));

        // A Viewer who can read a published asset still cannot write on it.
        var viewer = await Provision(s.Tenant, "Viewer", "Vera Viewer");
        await Publish(s.Admin, s.Asset);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Comments(s.Asset), viewer.Token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(viewer.Token, root, new { resolved = true })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Add(viewer.Token, s.Asset, "May I?")).Status);
    }

    // ---- deleting ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Authors_and_asset_editors_can_delete_other_people_cannot()
    {
        var s = await NewScene();
        var librarian = await Provision(s.Tenant, "Librarian", "Lia Librarian");   // may update assets, did not write the comment
        var c1 = (await Posted(s.Ann.Token, s.Asset, "Mine")).GetProperty("id").GetGuid();
        var c2 = (await Posted(s.Ann.Token, s.Asset, "Also mine")).GetProperty("id").GetGuid();
        var c3 = (await Posted(s.Ann.Token, s.Asset, "Third")).GetProperty("id").GetGuid();

        var contributor = await Provision(s.Tenant, "Contributor", "Cleo Contributor");   // can comment, but may only update assets she owns
        var approver = await Provision(s.Tenant, "Approver", "Abe Approver");              // can comment, cannot update assets
        await Publish(s.Admin, s.Asset);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c1}", contributor.Token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c1}", approver.Token)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c1}", s.Ann.Token)).Status);   // the author
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c2}", librarian.Token)).Status);   // someone who can update the asset
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c3}", s.Bea.Token)).Status);   // an Editor may update assets too
        Assert.Empty(RootIds(await Threads(s.Ann.Token, s.Asset)));
        Assert.Equal((0, 0, 0), Counts(await Threads(s.Ann.Token, s.Asset)));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c1}", s.Ann.Token)).Status);    // already gone
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(s.Ann.Token, c1, new { body = "back from the dead" })).Status);
        Assert.Equal(3, Count(s.Tenant, "SELECT count(*) FROM audit_log WHERE action = 'comment.deleted'"));
        Assert.Equal(3, Count(s.Tenant, "SELECT count(*) FROM comments WHERE deleted_at IS NOT NULL"));
    }

    [Fact]
    public async Task Deleting_a_first_comment_that_has_replies_leaves_a_placeholder_and_erases_the_text()
    {
        var s = await NewScene();
        var root = (await Posted(s.Ann.Token, s.Asset, $"Sensitive words {Mention(s.Bea)}", anchor: Region())).GetProperty("id").GetGuid();
        var r1 = (await Posted(s.Bea.Token, s.Asset, "Keep me", parent: root)).GetProperty("id").GetGuid();
        var r2 = (await Posted(s.Ann.Token, s.Asset, "And me", parent: root)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/comments/{root}", s.Ann.Token)).Status);
        var threads = await Threads(s.Bea.Token, s.Asset);
        var thread = Assert.Single(threads.GetProperty("threads").EnumerateArray());
        var placeholder = thread.GetProperty("root");
        Assert.Equal((root, "[deleted]", true), (placeholder.GetProperty("id").GetGuid(), placeholder.GetProperty("body").GetString(), placeholder.GetProperty("deleted").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("anchor").ValueKind);
        Assert.Empty(placeholder.GetProperty("mentions").EnumerateArray());
        Assert.Equal([r1, r2], thread.GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal((1, 0, 0), Counts(threads));   // a deleted annotation is no longer one
        Assert.Equal(0, Count(s.Tenant, "SELECT count(*) FROM comments WHERE body ILIKE '%Sensitive%'"));   // the words are gone from the database
        Assert.Equal(0, Count(s.Tenant, $"SELECT count(*) FROM comment_mentions WHERE comment_id = '{root}'"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Add(s.Bea.Token, s.Asset, "late reply", parent: root)).Status);   // nobody can reply to a placeholder

        // Once the last reply goes the empty placeholder is dropped too.
        await Call(HttpMethod.Delete, $"/api/v1/comments/{r1}", s.Bea.Token);
        Assert.Single((await Threads(s.Bea.Token, s.Asset)).GetProperty("threads").EnumerateArray());
        await Call(HttpMethod.Delete, $"/api/v1/comments/{r2}", s.Ann.Token);
        Assert.Empty((await Threads(s.Bea.Token, s.Asset)).GetProperty("threads").EnumerateArray());
    }

    // ---- scope -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Someone_who_cannot_see_the_asset_gets_not_found_for_everything_and_a_reader_without_comments_write_gets_forbidden()
    {
        var s = await NewScene();   // a draft asset
        var c = (await Posted(s.Ann.Token, s.Asset, "Private")).GetProperty("id").GetGuid();
        var viewer = await Provision(s.Tenant, "Viewer", "Vera Viewer");          // sees published assets only
        var contributor = await Provision(s.Tenant, "Contributor", "Cleo Contributor");   // sees their own drafts only

        foreach (var outsider in new[] { viewer, contributor })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, Comments(s.Asset), outsider.Token)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Add(outsider.Token, s.Asset, "x")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Patch(outsider.Token, c, new { resolved = true })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c}", outsider.Token)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/activity", outsider.Token)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable", outsider.Token)).Status);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, Comments(Guid.NewGuid()), s.Admin)).Status);

        // Once published, the Viewer reads (threads, activity) but may not write or ask for the picker.
        await Publish(s.Admin, s.Asset);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Comments(s.Asset), viewer.Token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/activity", viewer.Token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Add(viewer.Token, s.Asset, "x")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable", viewer.Token)).Status);
        Assert.Equal(HttpStatusCode.Created, (await Add(contributor.Token, s.Asset, "A Contributor may comment on what they can read")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, Comments(s.Asset), "not-a-token")).Status);
    }

    [Fact]
    public async Task Exactly_the_roles_that_hold_comments_write_can_comment()
    {
        var s = await NewScene();
        await Publish(s.Admin, s.Asset);
        foreach (var role in new[] { "Admin", "Librarian", "Editor", "Approver", "Contributor" })
            Assert.Equal(HttpStatusCode.Created, (await Add((await Provision(s.Tenant, role, role + " Person")).Token, s.Asset, $"As {role}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Add((await Provision(s.Tenant, "Viewer", "Vera")).Token, s.Asset, "As Viewer")).Status);
    }

    [Fact]
    public async Task A_deleted_asset_still_shows_its_comments_to_those_who_can_see_it_but_takes_no_more()
    {
        var s = await NewScene();
        var c = (await Posted(s.Ann.Token, s.Asset, "Before deletion")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/assets/{s.Asset}", s.Admin)).Status);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Comments(s.Asset), s.Admin)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Add(s.Admin, s.Asset, "Too late")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(s.Ann.Token, c, new { body = "Too late" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(s.Ann.Token, c, new { resolved = true })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c}", s.Admin)).Status);
    }

    // ---- mentions ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_people_who_can_see_the_asset_stay_mentioned_everyone_else_becomes_plain_text()
    {
        var s = await NewScene();   // a draft: Editors see it, a Viewer does not
        var viewer = await Provision(s.Tenant, "Viewer", "Vera Viewer");
        var disabled = await Provision(s.Tenant, "Editor", "Dave Disabled");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/users/{disabled.Id}", s.Admin, new { status = "disabled" })).Status);
        var ghost = Guid.NewGuid();

        var text = $"{Mention(s.Bea)} {Mention(viewer)} {Mention(disabled)} @[Nobody Here](user:{ghost}) {Mention(s.Bea)}";
        var c = await Posted(s.Ann.Token, s.Asset, text);

        var mentions = c.GetProperty("mentions").EnumerateArray().ToList();
        var only = Assert.Single(mentions);   // Bea once, though she is named twice
        Assert.Equal((s.Bea.Id, "Bea Brooks"), (only.GetProperty("userId").GetGuid(), only.GetProperty("displayName").GetString()));
        Assert.Equal($"{Mention(s.Bea)} @Vera Viewer @Dave Disabled @Nobody Here {Mention(s.Bea)}", c.GetProperty("body").GetString());
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM comment_mentions WHERE comment_id = '{c.GetProperty("id").GetGuid()}'"));
        Assert.Equal(0, Count(s.Tenant, $"SELECT count(*) FROM comment_mentions WHERE user_id IN ('{viewer.Id}', '{disabled.Id}', '{ghost}')"));
    }

    [Fact]
    public async Task Mentions_follow_attribute_based_access_not_just_roles()
    {
        var s = await NewScene();   // the folder tags the asset with region "north"
        await Publish(s.Admin, s.Asset);
        var north = await Provision(s.Tenant, "Dealer", "Dina North", new() { ["region"] = ["north"] });
        var south = await Provision(s.Tenant, "Dealer", "Sven South", new() { ["region"] = ["south"] });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Comments(s.Asset), north.Token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, Comments(s.Asset), south.Token)).Status);

        var c = await Posted(s.Ann.Token, s.Asset, $"{Mention(north)} {Mention(south)}");
        Assert.Equal([north.Id], c.GetProperty("mentions").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()));
        Assert.Equal($"{Mention(north)} @Sven South", c.GetProperty("body").GetString());
        var picker = (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable?q=dealer", s.Ann.Token)).Body.EnumerateArray().Select(u => u.GetProperty("id").GetGuid());
        Assert.DoesNotContain(south.Id, picker);
        Assert.Contains(north.Id, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable?q=north", s.Ann.Token)).Body.EnumerateArray().Select(u => u.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_token_cannot_claim_a_different_name_than_the_person_it_points_to()
    {
        var s = await NewScene();
        var c = await Posted(s.Ann.Token, s.Asset, $"Hello @[The Boss](user:{s.Bea.Id})");
        Assert.Equal($"Hello {Mention(s.Bea)}", c.GetProperty("body").GetString());   // relabelled with her real name
        Assert.Equal("Bea Brooks", c.GetProperty("mentions")[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Editing_replaces_the_mentions_and_drops_the_ones_removed()
    {
        var s = await NewScene();
        var c = (await Posted(s.Ann.Token, s.Asset, $"cc {Mention(s.Bea)}")).GetProperty("id").GetGuid();
        var edited = await Patch(s.Ann.Token, c, new { body = $"cc {Mention(s.Ann)} only" });   // now names herself, no longer Bea
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        Assert.Equal([s.Ann.Id], edited.Body.GetProperty("mentions").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()));
        Assert.Equal(0, Count(s.Tenant, $"SELECT count(*) FROM comment_mentions WHERE comment_id = '{c}' AND user_id = '{s.Bea.Id}'"));
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM comment_mentions WHERE comment_id = '{c}' AND user_id = '{s.Ann.Id}'"));
    }

    // ---- the mention picker ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_picker_offers_active_people_who_can_read_the_asset_matching_the_text_and_never_the_caller()
    {
        var s = await NewScene();
        var carlos = await Provision(s.Tenant, "Librarian", "Carlos Cruz");
        var viewer = await Provision(s.Tenant, "Viewer", "Cara Viewer");   // cannot see the draft
        var disabled = await Provision(s.Tenant, "Editor", "Cal Disabled");
        await Call(HttpMethod.Patch, $"/api/v1/users/{disabled.Id}", s.Admin, new { status = "disabled" });

        async Task<List<JsonElement>> Pick(string token, string query) =>
            (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable?q={Uri.EscapeDataString(query)}", token)).Body.EnumerateArray().ToList();

        var c = await Pick(s.Ann.Token, "car");
        Assert.Equal([carlos.Id], c.Select(u => u.GetProperty("id").GetGuid()));         // Cara cannot see the asset
        Assert.Empty(await Pick(s.Ann.Token, "Disabled"));                               // Cal is disabled
        Assert.Equal(("Carlos Cruz", carlos.Email), (c[0].GetProperty("displayName").GetString(), c[0].GetProperty("email").GetString()));
        Assert.Equal([carlos.Id], (await Pick(s.Ann.Token, "CRUZ")).Select(u => u.GetProperty("id").GetGuid()));              // substring, any case
        Assert.Equal([carlos.Id], (await Pick(s.Ann.Token, carlos.Email[..8])).Select(u => u.GetProperty("id").GetGuid()));   // or by email
        Assert.Empty(await Pick(s.Ann.Token, "zzz"));
        Assert.DoesNotContain(await Pick(s.Ann.Token, ""), u => u.GetProperty("id").GetGuid() == s.Ann.Id);   // never the caller
        Assert.Contains(await Pick(s.Ann.Token, ""), u => u.GetProperty("id").GetGuid() == s.Bea.Id);
        Assert.DoesNotContain(await Pick(s.Ann.Token, ""), u => u.GetProperty("id").GetGuid() == viewer.Id);

        // Once the asset is published everyone can see it, so the Viewer joins the list.
        await Publish(s.Admin, s.Asset);
        Assert.Contains(await Pick(s.Ann.Token, "cara"), u => u.GetProperty("id").GetGuid() == viewer.Id);
    }

    [Fact]
    public async Task The_picker_returns_at_most_ten_people_and_nothing_from_another_tenant()
    {
        var s = await NewScene();
        for (var i = 0; i < 12; i++) await Provision(s.Tenant, "Editor", $"Crowd {i:00}");
        var other = await NewTenantAsync();
        await Provision(other, "Editor", "Crowd Elsewhere");

        var r = await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable?q=crowd", s.Ann.Token);
        var names = r.Body.EnumerateArray().Select(u => u.GetProperty("displayName").GetString()).ToList();
        Assert.Equal(10, names.Count);
        Assert.DoesNotContain("Crowd Elsewhere", names);
        Assert.Equal(["Crowd 00", "Crowd 01", "Crowd 02", "Crowd 03", "Crowd 04", "Crowd 05", "Crowd 06", "Crowd 07", "Crowd 08", "Crowd 09"], names);

        // Another tenant's user learns nothing: the asset is not found.
        var stranger = await Provision(other, "Editor", "Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/mentionable?q=crowd", stranger.Token)).Status);
    }

    // ---- the activity feed -------------------------------------------------------------------------------------

    private async Task<(List<JsonElement> Items, string? Next)> Feed(string token, Guid asset, string query = "")
    {
        var r = await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/activity{query}", token);
        Assert.True(r.Status == HttpStatusCode.OK, r.Body.ToString());
        return (r.Body.GetProperty("items").EnumerateArray().ToList(), r.Body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : r.Body.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task The_feed_merges_comments_and_lifecycle_newest_first_without_leaking_the_audit_payload()
    {
        var s = await NewScene();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/assets/{s.Asset}", s.Admin, new { metadata = new { description = new { en = "Changed" } } })).Status);
        var root = (await Posted(s.Ann.Token, s.Asset, "Please review", anchor: Region())).GetProperty("id").GetGuid();
        var reply = (await Posted(s.Bea.Token, s.Asset, "Looks fine to me", parent: root)).GetProperty("id").GetGuid();
        await Patch(s.Bea.Token, root, new { resolved = true });
        await Patch(s.Bea.Token, root, new { resolved = false });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{s.Asset}/transitions/submit", s.Admin, new { reason = "Ready" })).Status);

        var (items, next) = await Feed(s.Bea.Token, s.Asset);
        Assert.Null(next);
        var kinds = items.Select(i => i.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["status_changed", "reopened", "resolved", "reply", "comment", "metadata_updated", "created"], kinds);
        Assert.Equal(items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).OrderByDescending(t => t), items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()));   // newest first

        var comment = items.Single(i => i.GetProperty("kind").GetString() == "comment");
        Assert.Equal((root, s.Ann.Id, "Ann Author", "added an annotation", "Please review"),
            (comment.GetProperty("commentId").GetGuid(), comment.GetProperty("actorId").GetGuid(), comment.GetProperty("actorName").GetString(), comment.GetProperty("summary").GetString(), comment.GetProperty("detail").GetString()));
        var replyItem = items.Single(i => i.GetProperty("kind").GetString() == "reply");
        Assert.Equal((reply, "Bea Brooks", "replied to a comment"), (replyItem.GetProperty("commentId").GetGuid(), replyItem.GetProperty("actorName").GetString(), replyItem.GetProperty("summary").GetString()));
        var resolved = items.Single(i => i.GetProperty("kind").GetString() == "resolved");
        Assert.Equal((root, "Bea Brooks"), (resolved.GetProperty("commentId").GetGuid(), resolved.GetProperty("actorName").GetString()));
        var status = items[0];
        Assert.Equal(("changed the status to in review", "from draft", "Admin"), (status.GetProperty("summary").GetString(), status.GetProperty("detail").GetString(), status.GetProperty("actorName").GetString()));
        Assert.Equal(JsonValueKind.Null, status.GetProperty("commentId").ValueKind);
        Assert.Equal("created the asset", items[^1].GetProperty("summary").GetString());

        // Only the curated fields are ever sent: no payloads, no addresses, no hashes.
        var allowed = new[] { "actorId", "actorName", "commentId", "detail", "id", "kind", "occurredAt", "summary", "versionNo" };
        Assert.All(items, i => Assert.Equal(allowed, i.EnumerateObject().Select(p => p.Name).Order()));
        var raw = JsonSerializer.Serialize(items);
        Assert.DoesNotContain("before", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("after", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ip\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rendition", raw, StringComparison.OrdinalIgnoreCase);   // internal events never appear

        // The audit log itself is still closed to someone without audit.read.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/audit", s.Bea.Token)).Status);
    }

    [Fact]
    public async Task The_feed_pages_with_a_cursor_without_gaps_or_repeats()
    {
        var s = await NewScene();
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++) ids.Add((await Posted(i % 2 == 0 ? s.Ann.Token : s.Bea.Token, s.Asset, $"Note {i}")).GetProperty("id").GetGuid());
        await Patch(s.Ann.Token, ids[0], new { resolved = true });

        var all = (await Feed(s.Ann.Token, s.Asset, "?limit=200")).Items;
        Assert.Equal(9, all.Count);   // created + 7 comments + 1 resolution

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (items, next) = await Feed(s.Ann.Token, s.Asset, "?limit=3" + (cursor is null ? "" : $"&cursor={cursor}"));
            Assert.True(items.Count <= 3);
            seen.AddRange(items.Select(i => i.GetProperty("id").GetGuid()));
            cursor = next;
            pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(3, pages);
        Assert.Equal(all.Select(i => i.GetProperty("id").GetGuid()), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"/api/v1/assets/{s.Asset}/activity?cursor=garbage", s.Ann.Token)).Status);
    }

    [Fact]
    public async Task Deleted_comments_and_the_resolutions_of_deleted_threads_leave_the_feed()
    {
        var s = await NewScene();
        var gone = (await Posted(s.Ann.Token, s.Asset, "Oops wrong asset")).GetProperty("id").GetGuid();
        await Patch(s.Ann.Token, gone, new { resolved = true });
        await Call(HttpMethod.Delete, $"/api/v1/comments/{gone}", s.Ann.Token);
        var (items, _) = await Feed(s.Ann.Token, s.Asset);
        Assert.Equal(["created"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.DoesNotContain("Oops", JsonSerializer.Serialize(items));
    }

    [Fact]
    public async Task A_new_version_shows_in_the_feed_with_its_number_and_who_uploaded_it()
    {
        var s = await NewScene();
        var version = AddVersion(s.Asset);
        Sql($"UPDATE asset_versions SET created_by = '{s.Ann.Id}' WHERE id = '{version}'");
        // The ingest worker records the event as the system; the feed names the uploader from the version itself.
        Sql("INSERT INTO audit_log (id, tenant_id, ts, actor, action, entity_type, entity_id, after) "
            + $"VALUES (gen_random_uuid(), '{s.Tenant}', now(), 'worker', 'asset.version.added', 'asset', '{s.Asset}', '{{\"AssetId\":\"{s.Asset}\",\"VersionId\":\"{version}\",\"VersionNo\":1}}'::jsonb)");
        var (items, _) = await Feed(s.Bea.Token, s.Asset);
        var added = items.Single(i => i.GetProperty("kind").GetString() == "version_added");
        Assert.Equal(("uploaded version 1", 1, "Ann Author", s.Ann.Id), (added.GetProperty("summary").GetString(), added.GetProperty("versionNo").GetInt32(), added.GetProperty("actorName").GetString(), added.GetProperty("actorId").GetGuid()));
    }

    // ---- isolation and storage ---------------------------------------------------------------------------------

    [Fact]
    public async Task Comments_are_isolated_per_tenant_by_the_api_and_by_row_level_security()
    {
        var a = await NewScene();
        var b = await NewScene();
        var c = (await Posted(a.Ann.Token, a.Asset, $"Tenant A only {Mention(a.Bea)}")).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, Comments(a.Asset), b.Admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Add(b.Admin, a.Asset, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(b.Admin, c, new { resolved = true })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/comments/{c}", b.Admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{a.Asset}/activity", b.Admin)).Status);

        foreach (var table in new[] { "comments", "comment_mentions" })
        {
            Assert.True(Count(a.Tenant, $"SELECT count(*) FROM {table}") > 0, table);
            Assert.Equal(0, Count(b.Tenant, $"SELECT count(*) FROM {table} WHERE tenant_id = '{a.Tenant}'"));
            using var none = Pg.RawAppConnection(null);
            using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", none);
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));   // no tenant set: no rows
        }
        // Another tenant's rows cannot be written either.
        using var conn = Pg.RawAppConnection(b.Tenant);
        using var insert = new NpgsqlCommand($"INSERT INTO comments (id, tenant_id, asset_id, author_id, body, resolved, created_at) VALUES (gen_random_uuid(), '{a.Tenant}', '{a.Asset}', gen_random_uuid(), 'x', false, now())", conn);
        Assert.Throws<PostgresException>(() => insert.ExecuteNonQuery());
    }

    [Fact]
    public async Task The_app_role_can_never_delete_comments_and_a_hard_deleted_asset_leaves_no_orphans()
    {
        var s = await NewScene();
        var root = (await Posted(s.Ann.Token, s.Asset, $"Hello {Mention(s.Bea)}")).GetProperty("id").GetGuid();
        await Posted(s.Bea.Token, s.Asset, "Reply", parent: root);

        using (var c = Pg.RawAppConnection(s.Tenant))
        using (var cmd = new NpgsqlCommand("DELETE FROM comments", c))
            Assert.Equal("42501", Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery()).SqlState);

        // Should an asset ever be removed outright (the app never does), its comments, replies and mentions go with it.
        Assert.Equal(3, Count(s.Tenant, "SELECT count(*) FROM comments") + Count(s.Tenant, "SELECT count(*) FROM comment_mentions"));
        Sql($"DELETE FROM assets WHERE id = '{s.Asset}'");
        Assert.Equal(0, Count(s.Tenant, "SELECT count(*) FROM comments"));
        Assert.Equal(0, Count(s.Tenant, "SELECT count(*) FROM comment_mentions"));
    }
}
