using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>Usage events, the "unused assets" list and the analytics overview (ADR-0029), against a real Postgres.</summary>
[Collection("pg")]
public sealed class UsageTests(PostgresFixture pg) : AssetTestBase(pg)
{
    private const string Events = "/api/v1/usage/events";
    private const string Unused = "/api/v1/analytics/unused-assets";
    private const string Overview = "/api/v1/analytics/overview";

    private static string Day(int daysAgo) => DateTime.UtcNow.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An asset that is published and was created <paramref name="createdDaysAgo"/> days ago (assets are born as drafts today).</summary>
    private async Task<Guid> Aged(string admin, Guid? folder, int createdDaysAgo, string title = "Hero", string status = "published", string type = "image")
    {
        var id = await NewAsset(admin, folder, type, title, allowIncomplete: true);
        Sql($"UPDATE assets SET created_at = now() - interval '{createdDaysAgo} days', status = '{status}' WHERE id = '{id}'");
        return id;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Record(string token, params object[] events) =>
        Call(HttpMethod.Post, Events, token, new { events });

    private static object Use(Guid asset, string type = "download", int daysAgo = 0, string? channel = null, string? clientEventId = null, string? rendition = null) =>
        new { assetId = asset, eventType = type, channel, rendition, clientEventId, occurredAt = daysAgo == 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddDays(-daysAgo) };

    private async Task<JsonElement> UnusedOk(string token, string query = "")
    {
        var r = await Call(HttpMethod.Get, Unused + query, token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body;
    }

    private static List<Guid> ItemIds(JsonElement body) => body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    /// <summary>A user with analytics.read (via a custom role) who can read assets only inside <paramref name="folderPath"/>.</summary>
    private async Task<string> ScopedReporter(Guid tenant, string admin, string folderPath, params string[] extraRoles)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), extraRoles);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        var role = Id((await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Reporter", permissions = new[] { "analytics.read" } })).Body, "id");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/users/{userId}/roles", admin, new { ids = new[] { role } })).Status);
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
        { principalType = "user", principalId = userId, scopeType = "folder", scopeValue = folderPath, permissions = new[] { "assets.read" } })).Status);
        return token;
    }

    // ---- recording -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Events_are_accepted_and_update_the_assets_usage_summary()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);
        var b = await Aged(admin, brand, 100);

        var r = await Record(admin, Use(a, "download", channel: "console", rendition: "original"), Use(a, "view", daysAgo: 3), Use(b, "play", daysAgo: 10, channel: "signage"));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal((3, 0, 0), (r.Body.GetProperty("accepted").GetInt32(), r.Body.GetProperty("duplicates").GetInt32(), r.Body.GetProperty("rejected").GetInt32()));

        Assert.Equal(3, Count(t, "SELECT count(*) FROM usage_events"));
        Assert.Equal(2, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{a}'"));
        Assert.Equal(1, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{b}'"));
        // The summary keeps the newest and the oldest use, whatever order they arrived in.
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM asset_usage WHERE asset_id = '{a}' AND last_used_at > now() - interval '1 minute' AND first_used_at < now() - interval '2 days'"));

        var more = await Record(admin, Use(a, "share", daysAgo: 20));
        Assert.Equal(1, more.Body.GetProperty("accepted").GetInt32());
        Assert.Equal(3, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{a}'"));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM asset_usage WHERE asset_id = '{a}' AND last_used_at > now() - interval '1 minute' AND first_used_at < now() - interval '19 days'"));
    }

    [Fact]
    public async Task Resending_a_client_event_id_is_a_duplicate_and_does_not_count_twice()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);

        var first = await Record(admin, Use(a, "play", clientEventId: "pop-1"), Use(a, "play", clientEventId: "pop-2"));
        Assert.Equal(2, first.Body.GetProperty("accepted").GetInt32());

        var again = await Record(admin, Use(a, "play", clientEventId: "pop-1"), Use(a, "play", clientEventId: "pop-3"));
        Assert.Equal((1, 1, 0), (again.Body.GetProperty("accepted").GetInt32(), again.Body.GetProperty("duplicates").GetInt32(), again.Body.GetProperty("rejected").GetInt32()));

        // The same id twice inside one batch is also just one event.
        var within = await Record(admin, Use(a, "play", clientEventId: "pop-4"), Use(a, "play", clientEventId: "pop-4"));
        Assert.Equal((1, 1), (within.Body.GetProperty("accepted").GetInt32(), within.Body.GetProperty("duplicates").GetInt32()));

        // Events without an id are never deduplicated.
        var anon = await Record(admin, Use(a), Use(a));
        Assert.Equal(2, anon.Body.GetProperty("accepted").GetInt32());

        Assert.Equal(6, Count(t, "SELECT count(*) FROM usage_events"));
        Assert.Equal(6, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{a}'"));
    }

    [Fact]
    public async Task Many_requests_racing_with_the_same_client_event_ids_store_each_event_once()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);
        var batch = new[] { Use(a, "play", clientEventId: "r-1"), Use(a, "play", clientEventId: "r-2"), Use(a, "play", clientEventId: "r-3") };

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Record(admin, batch)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(3, results.Sum(r => r.Body.GetProperty("accepted").GetInt32()));
        Assert.Equal(21, results.Sum(r => r.Body.GetProperty("duplicates").GetInt32()));
        Assert.Equal(3, Count(t, "SELECT count(*) FROM usage_events"));
        Assert.Equal(3, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{a}'"));
    }

    [Fact]
    public async Task Concurrent_batches_for_the_same_asset_do_not_lose_counts()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Record(admin, Use(a), Use(a), Use(a))));

        Assert.All(results, r => Assert.Equal(3, r.Body.GetProperty("accepted").GetInt32()));
        Assert.Equal(30, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{a}'"));
    }

    [Fact]
    public async Task Events_for_unknown_or_unreadable_assets_are_rejected_not_failed_and_nothing_is_recorded_for_them()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var readable = await Aged(admin, brand, 100);
        var draft = await Aged(admin, brand, 100, status: "draft");

        // A Viewer reads published assets only: the draft looks exactly like an asset that does not exist.
        var viewer = As(t, "Viewer");
        var r = await Record(viewer, Use(readable), Use(Guid.NewGuid()), Use(draft), Use(readable, "view"));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal((2, 0, 2), (r.Body.GetProperty("accepted").GetInt32(), r.Body.GetProperty("duplicates").GetInt32(), r.Body.GetProperty("rejected").GetInt32()));

        var allRejected = await Record(viewer, Use(draft));
        Assert.Equal(HttpStatusCode.OK, allRejected.Status);
        Assert.Equal((0, 0, 1), (allRejected.Body.GetProperty("accepted").GetInt32(), allRejected.Body.GetProperty("duplicates").GetInt32(), allRejected.Body.GetProperty("rejected").GetInt32()));

        Assert.Equal(2, Count(t, "SELECT count(*) FROM usage_events"));
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM asset_usage WHERE asset_id = '{draft}'"));
        // The curator can record against the draft.
        Assert.Equal(1, (await Record(admin, Use(draft))).Body.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Any_signed_in_user_may_record_events_but_a_scoped_user_only_for_assets_in_scope()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Other" })).Body, "folder", "id");
        var inScope = await Aged(admin, brand, 100);
        var outOfScope = await Aged(admin, other, 100);
        var reporter = await ScopedReporter(t, admin, "brand");

        var r = await Record(reporter, Use(inScope), Use(outOfScope));
        Assert.Equal((1, 0, 1), (r.Body.GetProperty("accepted").GetInt32(), r.Body.GetProperty("duplicates").GetInt32(), r.Body.GetProperty("rejected").GetInt32()));
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM asset_usage WHERE asset_id = '{outOfScope}'"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Post, Events, "not-a-token", new { events = new[] { Use(inScope) } })).Status);
    }

    [Fact]
    public async Task A_malformed_batch_is_a_validation_failure_with_the_position_of_each_bad_event()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);

        var bad = await Record(admin, Use(a), Use(a, "preview"), new { assetId = a, eventType = "view", occurredAt = DateTimeOffset.UtcNow.AddHours(1) },
            new { assetId = a, eventType = "view", occurredAt = DateTimeOffset.UtcNow.AddDays(-365 * 5 - 5) },
            new { assetId = a, eventType = "view", context = new[] { 1 } },
            new { assetId = a, eventType = "view", context = new { note = new string('x', 5000) } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        var errors = bad.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(["Events[1].EventType", "Events[2].OccurredAt", "Events[3].OccurredAt", "Events[4].Context", "Events[5].Context"], errors);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, Events, admin, new { events = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, Events, admin, new { events = Enumerable.Range(0, 501).Select(_ => Use(a)).ToArray() })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, Events, admin, new { events = Enumerable.Range(0, 500).Select(_ => Use(a)).ToArray() })).Status);
        Assert.Equal(500, Count(t, "SELECT count(*) FROM usage_events"));   // the refused batches stored nothing
    }

    [Fact]
    public async Task Occurred_at_defaults_to_now_and_context_and_the_actor_are_kept()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);
        var r = await Call(HttpMethod.Post, Events, admin, new { events = new[] { new { assetId = a, eventType = "play", channel = "  signage ", context = new { screen = "lobby-2", seconds = 12 } } } });
        Assert.Equal(1, r.Body.GetProperty("accepted").GetInt32());

        Assert.Equal(1, Count(t, "SELECT count(*) FROM usage_events WHERE occurred_at > now() - interval '1 minute' AND channel = 'signage' AND context->>'screen' = 'lobby-2' AND actor_id IS NOT NULL"));
    }

    // ---- unused assets: windows ------------------------------------------------------------------------------

    [Fact]
    public async Task Unused_assets_by_days_judges_a_never_used_asset_by_its_creation_date_and_lists_longest_idle_first()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var brandNew = await Aged(admin, brand, 2, "New");
        var twoMonths = await Aged(admin, brand, 60, "Sixty");
        var oldest = await Aged(admin, brand, 400, "Oldest");
        var usedRecently = await Aged(admin, brand, 400, "Used lately");
        var usedLongAgo = await Aged(admin, brand, 400, "Used long ago");
        await Record(admin, Use(usedRecently, daysAgo: 5), Use(usedLongAgo, daysAgo: 100));

        var body = await UnusedOk(admin, "?days=30");
        // Longest idle first: 400 days (never used), 100 days (last use), 60 days (never used).
        Assert.Equal([oldest, usedLongAgo, twoMonths], ItemIds(body));
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.True(body.GetProperty("totalIsExact").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
        Assert.InRange((DateTimeOffset.UtcNow.AddDays(-30) - body.GetProperty("cutoff").GetDateTimeOffset()).Duration().TotalMinutes, 0, 2);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Oldest", items[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(0, items[0].GetProperty("useCount").GetInt32());
        Assert.Equal(400, items[0].GetProperty("idleDays").GetInt32());
        Assert.Equal(items[0].GetProperty("createdAt").GetDateTimeOffset(), items[0].GetProperty("idleSince").GetDateTimeOffset());
        Assert.Equal("image", items[0].GetProperty("type").GetString());
        Assert.Equal("published", items[0].GetProperty("status").GetString());
        Assert.Equal(brand, items[0].GetProperty("folderId").GetGuid());
        Assert.Equal("Brand", items[0].GetProperty("folderPath").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("sizeBytes").ValueKind);

        Assert.Equal("Used long ago", items[1].GetProperty("title").GetString());
        Assert.Equal(1, items[1].GetProperty("useCount").GetInt32());
        Assert.Equal(100, items[1].GetProperty("idleDays").GetInt32());
        Assert.Equal(items[1].GetProperty("lastUsedAt").GetDateTimeOffset(), items[1].GetProperty("idleSince").GetDateTimeOffset());

        // The default window is 30 days; a wider one drops the 60-day asset, a very wide one the 100-day use.
        Assert.Equal(ItemIds(body), ItemIds(await UnusedOk(admin)));
        Assert.Equal([oldest, usedLongAgo], ItemIds(await UnusedOk(admin, "?days=90")));
        Assert.Equal([oldest], ItemIds(await UnusedOk(admin, "?days=180")));
        Assert.Empty(ItemIds(await UnusedOk(admin, "?days=500")));
        Assert.Contains(brandNew, ItemIds(await UnusedOk(admin, "?days=1")));   // created 2 days ago: idle for 2 days
        Assert.DoesNotContain(brandNew, ItemIds(await UnusedOk(admin, "?days=3")));
    }

    [Fact]
    public async Task A_brand_new_asset_is_never_flagged()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var fresh = await NewAsset(admin, brand, allowIncomplete: true);
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{fresh}'");
        Assert.Empty(ItemIds(await UnusedOk(admin, "?days=1")));
        Assert.Empty(ItemIds(await UnusedOk(admin, "?days=3650")));
    }

    [Fact]
    public async Task Unused_assets_by_before_lists_what_has_not_been_used_since_that_date()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var old = await Aged(admin, brand, 200, "Old");
        var mid = await Aged(admin, brand, 100, "Mid");
        var recent = await Aged(admin, brand, 10, "Recent");

        var body = await UnusedOk(admin, $"?before={Day(50)}");
        Assert.Equal([old, mid], ItemIds(body));
        Assert.Equal(DateTimeOffset.Parse(Day(50) + "T00:00:00Z", CultureInfo.InvariantCulture), body.GetProperty("cutoff").GetDateTimeOffset());

        Assert.Equal([old], ItemIds(await UnusedOk(admin, $"?before={Day(150)}T00:00:00Z")));
        Assert.Equal([old, mid, recent], ItemIds(await UnusedOk(admin, $"?before={Day(-1)}")));
        Assert.Empty(ItemIds(await UnusedOk(admin, $"?before={Day(300)}")));
    }

    [Fact]
    public async Task Days_and_before_cannot_be_combined_and_bad_values_are_refused()
    {
        var (_, admin, _) = await TenantWithPacks();
        foreach (var query in new[] { "?days=30&before=2026-01-01", "?days=0", "?days=3651", "?days=-3", "?before=sometime", "?status=live", "?type=gif", "?cursor=zzz" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, Unused + query, admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Unused + "?days=3650", admin)).Status);
    }

    [Fact]
    public async Task Using_an_asset_makes_it_drop_out_of_the_list()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 200, "A");
        var b = await Aged(admin, brand, 200, "B");
        Assert.Equal(2, (await UnusedOk(admin, "?days=30")).GetProperty("total").GetInt32());

        await Record(admin, Use(a, "download", channel: "console"));

        var after = await UnusedOk(admin, "?days=30");
        Assert.Equal([b], ItemIds(after));
        Assert.Equal(1, after.GetProperty("total").GetInt32());
        Assert.DoesNotContain(a, ItemIds(await UnusedOk(admin, "?days=1")));   // used just now: not idle even for the shortest window
    }

    // ---- unused assets: filters ------------------------------------------------------------------------------

    [Fact]
    public async Task Status_type_and_folder_filters_narrow_the_list()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var trail = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { parentId = brand, name = "Trail" })).Body, "folder", "id");
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Other" })).Body, "folder", "id");

        var publishedImage = await Aged(admin, brand, 100, "Published image");
        var draftImage = await Aged(admin, brand, 110, "Draft image", status: "draft");
        var archivedDoc = await Aged(admin, trail, 120, "Archived doc", status: "archived", type: "document");
        var deleted = await Aged(admin, brand, 130, "Deleted", status: "deleted");
        var blocked = await Aged(admin, brand, 140, "Blocked", status: "blocked");
        var inOther = await Aged(admin, other, 150, "In other");
        var unfiled = await Aged(admin, null, 160, "Unfiled");

        // The default status is published.
        Assert.Equal([unfiled, inOther, publishedImage], ItemIds(await UnusedOk(admin)));
        Assert.Equal([draftImage], ItemIds(await UnusedOk(admin, "?status=draft")));
        Assert.Equal([archivedDoc], ItemIds(await UnusedOk(admin, "?status=archived")));
        Assert.Equal([deleted], ItemIds(await UnusedOk(admin, "?status=deleted")));   // asked for by name
        Assert.Equal([blocked], ItemIds(await UnusedOk(admin, "?status=blocked")));
        // "all" is every status except deleted and blocked.
        Assert.Equal([unfiled, inOther, archivedDoc, draftImage, publishedImage], ItemIds(await UnusedOk(admin, "?status=all")));

        Assert.Equal([archivedDoc], ItemIds(await UnusedOk(admin, "?status=all&type=document")));
        Assert.Equal([unfiled, inOther, draftImage, publishedImage], ItemIds(await UnusedOk(admin, "?status=all&type=image")));

        // Folder filters include subfolders by default.
        Assert.Equal([archivedDoc, draftImage, publishedImage], ItemIds(await UnusedOk(admin, $"?status=all&folderId={brand}")));
        Assert.Equal([draftImage, publishedImage], ItemIds(await UnusedOk(admin, $"?status=all&folderId={brand}&includeSubfolders=false")));
        Assert.Equal([archivedDoc], ItemIds(await UnusedOk(admin, $"?status=all&folderId={trail}")));
        Assert.Equal([inOther], ItemIds(await UnusedOk(admin, $"?folderId={other}")));
        var nested = (await UnusedOk(admin, $"?status=archived&folderId={brand}")).GetProperty("items")[0];
        Assert.Equal("Brand / Trail", nested.GetProperty("folderPath").GetString());
        Assert.Equal(JsonValueKind.Null, (await UnusedOk(admin)).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == unfiled).GetProperty("folderPath").ValueKind);
    }

    [Fact]
    public async Task Paging_walks_the_whole_list_in_order_without_repeats_or_gaps()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var expected = new List<Guid>();
        foreach (var days in new[] { 500, 400, 300, 200, 100 }) expected.Add(await Aged(admin, brand, days, $"Age {days}"));
        var reporter = await ScopedReporter(t, admin, "brand");

        foreach (var token in new[] { admin, reporter })   // the database pages for the admin; the scope scan pages for the reporter
        {
            var seen = new List<Guid>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var body = await UnusedOk(token, $"?days=30&limit=2{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
                Assert.Equal(5, body.GetProperty("total").GetInt32());
                seen.AddRange(ItemIds(body));
                cursor = body.GetProperty("nextCursor").GetString();
                pages++;
            } while (cursor is not null && pages < 10);
            Assert.Equal(3, pages);
            Assert.Equal(expected, seen);
        }
    }

    [Fact]
    public async Task Limit_is_clamped_and_assets_with_the_same_idle_time_are_ordered_by_id_across_pages()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await Aged(admin, brand, 100, $"Twin {i}"));
        Sql($"UPDATE assets SET created_at = now() - interval '100 days' WHERE tenant_id = '{t}'"); // now() is fixed within the statement: identical timestamps
        var reporter = await ScopedReporter(t, admin, "brand");

        foreach (var token in new[] { admin, reporter })
        {
            var seen = new List<Guid>();
            string? cursor = null;
            do
            {
                var body = await UnusedOk(token, $"?limit=4{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
                seen.AddRange(ItemIds(body));
                cursor = body.GetProperty("nextCursor").GetString();
            } while (cursor is not null);
            Assert.Equal(6, seen.Count);
            Assert.Equal(6, seen.Distinct().Count());
            Assert.Equal(ids.Order(), seen.Order());
            Assert.Equal(Scalar($"SELECT string_agg(id::text, ',' ORDER BY id) FROM assets WHERE tenant_id = '{t}'"), string.Join(',', seen));   // the database's own uuid order
        }
        Assert.Single(ItemIds(await UnusedOk(admin, "?limit=0")));
        Assert.Equal(6, ItemIds(await UnusedOk(admin, "?limit=1000")).Count);
    }

    [Fact]
    public async Task The_size_of_the_current_version_is_reported()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var withFile = await Aged(admin, brand, 100, "With file");
        var shell = await Aged(admin, brand, 110, "Shell");
        AddVersion(withFile, 2_500_000);

        var items = (await UnusedOk(admin)).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal(2_500_000, items[withFile].GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, items[shell].GetProperty("sizeBytes").ValueKind);
    }

    private string Scalar(string sql)
    {
        using var c = new Npgsql.NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return (string)cmd.ExecuteScalar()!;
    }

    /// <summary>Gives an asset a first version without running the upload pipeline.</summary>
    private void AddVersion(Guid asset, long size) => Sql(
        "WITH v AS (INSERT INTO asset_versions (id, tenant_id, asset_id, version_no, bucket, storage_key, size_bytes, mime, sha256, technical_metadata, change_note, upload_id, created_at) "
        + $"SELECT gen_random_uuid(), tenant_id, id, (SELECT coalesce(max(version_no), 0) + 1 FROM asset_versions WHERE asset_id = '{asset}'), 'dam-originals', gen_random_uuid()::text, {size}, 'image/png', repeat('a', 64), '{{}}'::jsonb, '', gen_random_uuid(), now() "
        + $"FROM assets WHERE id = '{asset}' RETURNING id, asset_id) UPDATE assets SET current_version_id = v.id FROM v WHERE assets.id = v.asset_id");

    // ---- unused assets: scope and permission -----------------------------------------------------------------

    [Fact]
    public async Task A_scoped_user_sees_only_assets_in_their_scope_and_learns_nothing_about_the_rest()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Other" })).Body, "folder", "id");
        var mine1 = await Aged(admin, brand, 100, "Mine 1");
        var mine2 = await Aged(admin, brand, 200, "Mine 2");
        var mineDraft = await Aged(admin, brand, 150, "Mine draft", status: "draft");
        var theirs1 = await Aged(admin, other, 300, "Theirs 1");
        var theirs2 = await Aged(admin, other, 400, "Theirs 2");
        var reporter = await ScopedReporter(t, admin, "brand");

        var body = await UnusedOk(reporter);
        Assert.Equal([mine2, mine1], ItemIds(body));
        Assert.Equal(2, body.GetProperty("total").GetInt32());   // the count never includes what they cannot read
        Assert.True(body.GetProperty("totalIsExact").GetBoolean());

        var asAdmin = await UnusedOk(admin);
        Assert.Equal(4, asAdmin.GetProperty("total").GetInt32());

        // Asking for the other folder, or for drafts (assets.read without unpublished access), gives nothing - not an error, not a count.
        var elsewhere = await UnusedOk(reporter, $"?folderId={other}");
        Assert.Empty(ItemIds(elsewhere));
        Assert.Equal(0, elsewhere.GetProperty("total").GetInt32());
        var drafts = await UnusedOk(reporter, "?status=draft");
        Assert.Empty(ItemIds(drafts));
        Assert.Equal(0, drafts.GetProperty("total").GetInt32());
        Assert.Equal([mine2, mine1], ItemIds(await UnusedOk(reporter, "?status=all")));
        Assert.Equal([theirs2, theirs1, mine2, mineDraft, mine1], ItemIds(await UnusedOk(admin, "?status=all")));
    }

    [Fact]
    public async Task A_user_who_can_read_everything_published_gets_exact_database_counts_but_still_no_drafts()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var pub = await Aged(admin, brand, 100, "Published");
        await Aged(admin, brand, 110, "Draft", status: "draft");
        await Aged(admin, brand, 120, "In review", status: "in_review");

        // Viewer (published only) plus a role that carries analytics.read.
        var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Viewer"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        var role = Id((await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Reporter", permissions = new[] { "analytics.read" } })).Body, "id");
        await Call(HttpMethod.Put, $"/api/v1/users/{userId}/roles", admin, new { ids = new[] { role } });

        var published = await UnusedOk(token);
        Assert.Equal([pub], ItemIds(published));
        Assert.Equal(1, published.GetProperty("total").GetInt32());
        Assert.Equal([pub], ItemIds(await UnusedOk(token, "?status=all")));
        Assert.Empty(ItemIds(await UnusedOk(token, "?status=draft")));
        Assert.Equal(3, (await UnusedOk(admin, "?status=all")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_scope_scan_is_capped_and_says_when_the_total_is_only_what_was_found_so_far()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Other" })).Body, "folder", "id");
        var mine = await Aged(admin, brand, 100, "Mine");
        // 5,200 older assets the reporter cannot read stand between the start of the list and their own asset.
        var seed = await Aged(admin, other, 500, "Seed");
        Sql("INSERT INTO assets (id, tenant_id, folder_id, type, status, title, metadata, metadata_schema_version, taxonomy_terms, tags, attributes, legal_hold, status_reason, created_at, updated_at) "
            + "SELECT gen_random_uuid(), tenant_id, folder_id, type, status, 'Bulk ' || n, '{}'::jsonb, 0, '{}', '{}', '{}'::jsonb, false, '', created_at - n * interval '1 second', created_at "
            + $"FROM assets, generate_series(1, 5200) AS n WHERE id = '{seed}'");
        var reporter = await ScopedReporter(t, admin, "brand");

        var body = await UnusedOk(reporter);
        Assert.Empty(ItemIds(body));                       // their asset sorts after the scan limit
        Assert.False(body.GetProperty("totalIsExact").GetBoolean());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        // The admin's count is exact whatever the size, and a narrower query brings the reporter's asset within reach.
        var all = await UnusedOk(admin);
        Assert.Equal(5202, all.GetProperty("total").GetInt32());
        Assert.True(all.GetProperty("totalIsExact").GetBoolean());
        var narrowed = await UnusedOk(reporter, $"?folderId={brand}");
        Assert.Equal([mine], ItemIds(narrowed));
        Assert.True(narrowed.GetProperty("totalIsExact").GetBoolean());
    }

    [Fact]
    public async Task Analytics_need_the_analytics_read_permission()
    {
        var (t, admin, brand) = await TenantWithPacks();
        await Aged(admin, brand, 100);

        foreach (var role in new[] { "Viewer", "Contributor", "Dealer", "ApiClient" })
        {
            var token = role is "Dealer" or "ApiClient"
                ? ApiTestHost.TokenFor(t, Guid.NewGuid(), [role], attributes: new() { ["region"] = ["north"], ["channel"] = ["signage"] })
                : As(t, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, Unused, token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, Overview, token)).Status);
        }
        foreach (var role in new[] { "Admin", "Librarian", "Editor", "Approver" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Unused, As(t, role))).Status);
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Overview, As(t, role))).Status);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, Unused, "nope")).Status);
    }

    // ---- overview --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_overview_has_a_zero_filled_daily_series_totals_a_trend_and_breakdowns()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100, "Alpha");
        var b = await Aged(admin, brand, 100, "Beta");
        var c = await Aged(admin, brand, 100, "Gamma");
        await Record(admin,
            Use(a, "download", 0, "console"), Use(a, "download", 0, "console"), Use(a, "view", 2, "console"), Use(a, "play", 2, "signage"),
            Use(b, "play", 2, "signage"), Use(b, "play", 5, "signage"), Use(c, "share", 9),
            Use(a, "download", 31, "console"), Use(b, "download", 40), Use(b, "download", 59), Use(c, "download", 70));  // before the period / two periods back

        var r = await Call(HttpMethod.Get, Overview, admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var body = r.Body;
        Assert.Equal(30, body.GetProperty("periodDays").GetInt32());
        Assert.InRange((DateTimeOffset.UtcNow - body.GetProperty("generatedAt").GetDateTimeOffset()).TotalMinutes, -1, 2);

        var usage = body.GetProperty("usage");
        Assert.Equal(7, usage.GetProperty("total").GetInt64());
        Assert.Equal(3, usage.GetProperty("previousTotal").GetInt64());   // 31, 40 and 59 days ago; 70 is two periods back

        var byDay = usage.GetProperty("byDay").EnumerateArray().ToList();
        Assert.Equal(30, byDay.Count);
        Assert.Equal(Day(29), byDay[0].GetProperty("date").GetString());
        Assert.Equal(Day(0), byDay[^1].GetProperty("date").GetString());
        Assert.Equal(Enumerable.Range(0, 30).Select(i => Day(29 - i)), byDay.Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(7, byDay.Sum(d => d.GetProperty("count").GetInt64()));   // the series adds up to the total
        var counts = byDay.ToDictionary(d => d.GetProperty("date").GetString()!, d => d.GetProperty("count").GetInt64());
        Assert.Equal(2, counts[Day(0)]);
        Assert.Equal(3, counts[Day(2)]);
        Assert.Equal(1, counts[Day(5)]);
        Assert.Equal(1, counts[Day(9)]);
        Assert.Equal(0, counts[Day(1)]);                                       // empty days are present, as zero
        Assert.Equal(26, counts.Values.Count(v => v == 0));

        var byType = usage.GetProperty("byType").EnumerateArray().ToDictionary(x => x.GetProperty("eventType").GetString()!, x => x.GetProperty("count").GetInt64());
        Assert.Equal(new Dictionary<string, long> { ["download"] = 2, ["view"] = 1, ["play"] = 3, ["share"] = 1 }, byType);

        var byChannel = usage.GetProperty("byChannel").EnumerateArray().Select(x => (x.GetProperty("channel").GetString(), x.GetProperty("count").GetInt64())).ToList();
        Assert.Equal([("console", 3L), ("signage", 3L), ("unspecified", 1L)], byChannel);   // busiest first, ties by name; no channel is "unspecified"

        var top = body.GetProperty("topAssets").EnumerateArray().ToList();
        Assert.Equal([a, b, c], top.Select(x => x.GetProperty("assetId").GetGuid()));
        Assert.Equal([4L, 2L, 1L], top.Select(x => x.GetProperty("count").GetInt64()));
        Assert.Equal("Alpha", top[0].GetProperty("title").GetString());
        Assert.Equal("image", top[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_overview_period_can_be_changed_and_is_validated()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);
        await Record(admin, Use(a, daysAgo: 3), Use(a, daysAgo: 12));

        var week = (await Call(HttpMethod.Get, Overview + "?days=7", admin)).Body;
        Assert.Equal(7, week.GetProperty("periodDays").GetInt32());
        Assert.Equal(7, week.GetProperty("usage").GetProperty("byDay").GetArrayLength());
        Assert.Equal(1, week.GetProperty("usage").GetProperty("total").GetInt64());
        Assert.Equal(1, week.GetProperty("usage").GetProperty("previousTotal").GetInt64());
        Assert.Equal(365, (await Call(HttpMethod.Get, Overview + "?days=365", admin)).Body.GetProperty("usage").GetProperty("byDay").GetArrayLength());
        Assert.Equal(1, (await Call(HttpMethod.Get, Overview + "?days=1", admin)).Body.GetProperty("usage").GetProperty("byDay").GetArrayLength());
        foreach (var bad in new[] { "0", "366", "-1" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, Overview + "?days=" + bad, admin)).Status);
    }

    [Fact]
    public async Task An_empty_library_still_gives_a_complete_overview()
    {
        var t = await NewTenantAsync("core"); var admin = Admin(t);
        var body = (await Call(HttpMethod.Get, Overview, admin)).Body;
        var usage = body.GetProperty("usage");
        Assert.Equal(0, usage.GetProperty("total").GetInt64());
        Assert.Equal(0, usage.GetProperty("previousTotal").GetInt64());
        Assert.Equal(30, usage.GetProperty("byDay").GetArrayLength());
        Assert.Equal(0, usage.GetProperty("byType").GetArrayLength());
        Assert.Equal(0, usage.GetProperty("byChannel").GetArrayLength());
        Assert.Equal(0, body.GetProperty("topAssets").GetArrayLength());
        Assert.Equal(12, body.GetProperty("uploads").GetArrayLength());
        Assert.All(body.GetProperty("uploads").EnumerateArray(), w => Assert.Equal(0, w.GetProperty("count").GetInt64()));
        Assert.Equal(0, body.GetProperty("storage").GetProperty("totalBytes").GetInt64());
        Assert.Equal(0, body.GetProperty("storage").GetProperty("byType").GetArrayLength());
        Assert.Empty(ItemIds(await UnusedOk(admin)));
    }

    [Fact]
    public async Task Channels_are_limited_to_the_six_busiest()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100);
        var events = new List<object>();
        for (var ch = 1; ch <= 8; ch++) for (var n = 0; n < ch; n++) events.Add(Use(a, "view", channel: $"channel-{ch}"));
        await Record(admin, events.ToArray());

        var channels = (await Call(HttpMethod.Get, Overview, admin)).Body.GetProperty("usage").GetProperty("byChannel").EnumerateArray().ToList();
        Assert.Equal(6, channels.Count);
        Assert.Equal(["channel-8", "channel-7", "channel-6", "channel-5", "channel-4", "channel-3"], channels.Select(x => x.GetProperty("channel").GetString()));
        Assert.Equal([8L, 7L, 6L, 5L, 4L, 3L], channels.Select(x => x.GetProperty("count").GetInt64()));
    }

    [Fact]
    public async Task Uploads_are_counted_per_week_for_twelve_weeks_excluding_deleted_assets()
    {
        var (_, admin, brand) = await TenantWithPacks();
        await Aged(admin, brand, 0, "This week");
        await Aged(admin, brand, 0, "This week 2");
        var lastWeek = await Aged(admin, brand, 7, "Last week");
        await Aged(admin, brand, 7 * 5 + 1, "Five weeks back");
        await Aged(admin, brand, 7 * 20, "Too old");
        await Aged(admin, brand, 0, "Trashed", status: "deleted");
        Sql($"UPDATE assets SET created_at = date_trunc('week', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' - interval '7 days' + interval '1 second' WHERE id = '{lastWeek}'"); // Monday 00:00:01 UTC last week

        var weeks = (await Call(HttpMethod.Get, Overview, admin)).Body.GetProperty("uploads").EnumerateArray().ToList();
        Assert.Equal(12, weeks.Count);
        var thisMonday = DateTime.UtcNow.Date.AddDays(-(((int)DateTime.UtcNow.DayOfWeek + 6) % 7));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => thisMonday.AddDays(-7 * (11 - i)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), weeks.Select(w => w.GetProperty("weekStart").GetString()));
        Assert.All(weeks, w => Assert.Equal(DayOfWeek.Monday, DateTime.Parse(w.GetProperty("weekStart").GetString()!, CultureInfo.InvariantCulture).DayOfWeek));
        var counts = weeks.Select(w => w.GetProperty("count").GetInt64()).ToList();
        Assert.Equal(2, counts[^1]);   // this week; the deleted asset is not counted
        Assert.Equal(1, counts[^2]);   // last week (pinned to its Monday)
        Assert.Equal(4, counts.Sum()); // plus the one five-ish weeks back; the 20-week-old asset is outside the window
    }

    [Fact]
    public async Task Storage_sums_the_current_version_of_each_live_asset_by_type()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var img1 = await Aged(admin, brand, 10, "Img 1");
        var img2 = await Aged(admin, brand, 10, "Img 2");
        var doc = await Aged(admin, brand, 10, "Doc", type: "document");
        await Aged(admin, brand, 10, "Image shell");
        var gone = await Aged(admin, brand, 10, "Gone", status: "deleted");
        AddVersion(img1, 1_000);
        AddVersion(img2, 2_000);
        AddVersion(doc, 50_000);
        AddVersion(gone, 9_000_000);
        AddVersion(img1, 7);   // a newer version becomes current: the 1,000-byte one no longer counts

        var storage = (await Call(HttpMethod.Get, Overview, admin)).Body.GetProperty("storage");
        Assert.Equal(50_000 + 2_000 + 7, storage.GetProperty("totalBytes").GetInt64());
        var byType = storage.GetProperty("byType").EnumerateArray().ToDictionary(x => x.GetProperty("type").GetString()!);
        Assert.Equal((3L, 2_007L), (byType["image"].GetProperty("count").GetInt64(), byType["image"].GetProperty("bytes").GetInt64()));
        Assert.Equal((1L, 50_000L), (byType["document"].GetProperty("count").GetInt64(), byType["document"].GetProperty("bytes").GetInt64()));
        Assert.Equal(["document", "image"], storage.GetProperty("byType").EnumerateArray().Select(x => x.GetProperty("type").GetString()));   // biggest first
    }

    [Fact]
    public async Task Aggregates_are_tenant_wide_but_top_assets_are_limited_to_what_the_caller_can_read()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Other" })).Body, "folder", "id");
        var mine = await Aged(admin, brand, 100, "Mine");
        var theirs = await Aged(admin, other, 100, "Theirs");
        await Record(admin, Use(mine, channel: "console"), Use(theirs, channel: "console"), Use(theirs, channel: "console"), Use(theirs, channel: "console"));
        var reporter = await ScopedReporter(t, admin, "brand");

        var asAdmin = (await Call(HttpMethod.Get, Overview, admin)).Body;
        Assert.Equal([theirs, mine], asAdmin.GetProperty("topAssets").EnumerateArray().Select(x => x.GetProperty("assetId").GetGuid()));

        var asReporter = (await Call(HttpMethod.Get, Overview, reporter)).Body;
        Assert.Equal([mine], asReporter.GetProperty("topAssets").EnumerateArray().Select(x => x.GetProperty("assetId").GetGuid()));
        Assert.Equal(4, asReporter.GetProperty("usage").GetProperty("total").GetInt64());   // the totals are the library's, not the viewer's
        Assert.Equal(asAdmin.GetProperty("storage").ToString(), asReporter.GetProperty("storage").ToString());
    }

    // ---- tenants ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Usage_is_private_to_its_tenant()
    {
        var (a, adminA, brandA) = await TenantWithPacks();
        var (b, adminB, brandB) = await TenantWithPacks();
        var inA = await Aged(adminA, brandA, 100, "A's");
        var inB = await Aged(adminB, brandB, 100, "B's");
        var idleA = await Aged(adminA, brandA, 100, "A's idle");
        var idleB = await Aged(adminB, brandB, 100, "B's idle");
        await Record(adminA, Use(inA, clientEventId: "same-id"));

        // Tenant B cannot record against A's asset; B and A may reuse a client event id independently.
        var cross = await Record(adminB, Use(inA), Use(inB, clientEventId: "same-id"));
        Assert.Equal((1, 0, 1), (cross.Body.GetProperty("accepted").GetInt32(), cross.Body.GetProperty("duplicates").GetInt32(), cross.Body.GetProperty("rejected").GetInt32()));

        Assert.Equal(1, Count(a, "SELECT count(*) FROM usage_events"));
        Assert.Equal(1, Count(b, "SELECT count(*) FROM usage_events"));
        Assert.Equal(1, Count(a, "SELECT count(*) FROM asset_usage"));
        Assert.Equal(1, Count(b, "SELECT count(*) FROM asset_usage"));
        Assert.Equal(0, Count(b, $"SELECT count(*) FROM usage_events WHERE asset_id = '{inA}'"));   // RLS: B's session cannot even see A's rows

        Assert.Equal(1, (await Call(HttpMethod.Get, Overview, adminA)).Body.GetProperty("usage").GetProperty("total").GetInt64());
        Assert.Equal(1, (await Call(HttpMethod.Get, Overview, adminB)).Body.GetProperty("usage").GetProperty("total").GetInt64());
        Assert.Equal([idleB], ItemIds(await UnusedOk(adminB)));   // each tenant's list holds only its own idle asset
        Assert.Equal([idleA], ItemIds(await UnusedOk(adminA)));
    }

    [Fact]
    public async Task The_app_role_cannot_see_or_write_another_tenants_usage_rows_directly()
    {
        var (a, adminA, brandA) = await TenantWithPacks();
        var (b, _, _) = await TenantWithPacks();
        var asset = await Aged(adminA, brandA, 100);
        await Record(adminA, Use(asset));

        await using var conn = Pg.RawAppConnection(b);
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT (SELECT count(*) FROM usage_events), (SELECT count(*) FROM asset_usage)", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal((0L, 0L), (reader.GetInt64(0), reader.GetInt64(1)));
        await reader.CloseAsync();

        await using var insert = new Npgsql.NpgsqlCommand(
            $"INSERT INTO asset_usage (asset_id, tenant_id, last_used_at, first_used_at, use_count) VALUES ('{asset}', '{a}', now(), now(), 1)", conn);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => insert.ExecuteNonQueryAsync());   // WITH CHECK refuses another tenant's id
        Assert.Equal(1, Count(a, "SELECT count(*) FROM asset_usage"));
    }
}
