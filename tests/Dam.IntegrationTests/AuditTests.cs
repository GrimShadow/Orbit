using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>The audit log viewer, export and chain verification (ADR-0031) against a real Postgres and its hash-chain trigger.</summary>
[Collection("pg")]
public sealed class AuditTests(PostgresFixture pg) : AssetTestBase(pg)
{
    private const string Audit = "/api/v1/audit";
    private static readonly DateTimeOffset Day = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    // ---- helpers -----------------------------------------------------------------------------------------------

    private void InsertAudit(Guid tenant, DateTimeOffset ts, string actor, string action, string? entityType = null, Guid? entityId = null, string? after = null, string? ip = null)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand("""
            INSERT INTO audit_log (id, tenant_id, ts, actor, action, entity_type, entity_id, after, ip)
            VALUES (@id, @t, @ts, @actor, @action, @et, @eid, @after::jsonb, @ip)
            """, c);
        cmd.Parameters.AddWithValue("id", Dam.Domain.Common.Uuid7.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("ts", ts.ToUniversalTime());
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("et", (object?)entityType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("eid", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("after", (object?)after ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Changes a row the way only someone with the database owner's rights could: the append-only trigger is switched off for the moment.</summary>
    private void Tamper(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        foreach (var statement in new[] { "ALTER TABLE audit_log DISABLE TRIGGER audit_log_no_update", sql, "ALTER TABLE audit_log ENABLE TRIGGER audit_log_no_update" })
        {
            using var cmd = new NpgsqlCommand(statement, c);
            cmd.ExecuteNonQuery();
        }
    }

    private async Task<(HttpStatusCode Status, HttpResponseMessage Response, string Text)> Raw(string path, string token)
    {
        using var client = Api.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        var res = await client.SendAsync(req);
        return (res.StatusCode, res, await res.Content.ReadAsStringAsync());
    }

    private async Task<List<JsonElement>> AllEntries(string admin, string query = "")
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await Call(HttpMethod.Get, $"{Audit}?limit=200{query}" + (cursor is null ? "" : $"&cursor={cursor}"), admin);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            all.AddRange(page.Body.GetProperty("items").EnumerateArray());
            cursor = page.Body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.Body.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        return all;
    }

    /// <summary>A tenant whose log holds real events written by real commands.</summary>
    private async Task<(Guid Tenant, string Admin, Guid Subject, Guid UserId)> Busy()
    {
        var t = await NewTenantAsync("core", "automotive-sample"); var subject = Guid.NewGuid();
        var admin = ApiTestHost.TokenFor(t, subject, ["Admin"], name: "Ada Admin");
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", admin)).Body.GetProperty("userId").GetGuid();
        var folder = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Brand" })).Body, "folder", "id");
        var a = await NewAsset(admin, folder, title: "First");
        await NewAsset(admin, folder, title: "Second");
        await Call(HttpMethod.Post, $"/api/v1/assets/{a}/transitions/submit", admin, new { reason = (string?)null });
        return (t, admin, subject, userId);
    }

    // ---- viewer ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Entries_come_newest_first_with_the_actor_resolved_and_each_linked_to_the_one_before()
    {
        var (t, admin, subject, userId) = await Busy();
        var all = await AllEntries(admin);
        Assert.Equal(Count(t, "SELECT count(*) FROM audit_log"), all.Count);
        Assert.Equal(all.Select(e => e.GetProperty("chainNo").GetInt64()).OrderByDescending(x => x), all.Select(e => e.GetProperty("chainNo").GetInt64()));

        var created = all.First(e => e.GetProperty("action").GetString() == "asset.created");
        Assert.Equal(subject.ToString(), created.GetProperty("actorId").GetString());
        Assert.Equal(userId, created.GetProperty("actorUserId").GetGuid());
        Assert.Equal("Ada Admin", created.GetProperty("actorName").GetString());
        Assert.Equal("asset", created.GetProperty("entityType").GetString());
        Assert.NotEqual(Guid.Empty, created.GetProperty("entityId").GetGuid());
        Assert.Equal("image", created.GetProperty("after").GetProperty("AssetType").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("before").ValueKind);
        Assert.Equal(64, created.GetProperty("hash").GetString()!.Length);
        Assert.NotEqual(Guid.Empty, created.GetProperty("id").GetGuid());
        Assert.True(Math.Abs((created.GetProperty("occurredAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalMinutes) < 5);

        for (var i = 0; i < all.Count - 1; i++) Assert.Equal(all[i + 1].GetProperty("hash").GetString(), all[i].GetProperty("prevHash").GetString());   // each row names its predecessor
        Assert.Equal("", all[^1].GetProperty("prevHash").GetString());
    }

    [Fact]
    public async Task Paging_by_cursor_walks_every_entry_once()
    {
        var (t, admin, _, _) = await Busy();
        var total = Count(t, "SELECT count(*) FROM audit_log");
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await Call(HttpMethod.Get, $"{Audit}?limit=4" + (cursor is null ? "" : $"&cursor={cursor}"), admin)).Body;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null);
        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());
        Assert.True(pages > 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Audit}?cursor=abc", admin)).Status);
        Assert.Equal(200, (await Call(HttpMethod.Get, $"{Audit}?limit=100000", admin)).Body.GetProperty("items").GetArrayLength() > 200 ? 0 : 200);   // the limit is capped at 200
    }

    [Fact]
    public async Task Entries_can_be_filtered_by_time_actor_action_entity_and_free_text()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var asset1 = Guid.NewGuid(); var asset2 = Guid.NewGuid();
        InsertAudit(t, Day, "subject-a", "asset.created", "asset", asset1, """{"Note":"hero banner"}""", "10.0.0.1");
        InsertAudit(t, Day.AddHours(1), "subject-b", "asset.status.changed", "asset", asset1, """{"To":"published"}""");
        InsertAudit(t, Day.AddHours(2), "subject-a", "role.created", "role", asset2);
        InsertAudit(t, Day.AddDays(1), "worker", "rendition.ready", "asset", asset2);
        InsertAudit(t, Day.AddDays(2), "system", "workflow.sla.breached", "workflow_task", asset2);
        Task<List<JsonElement>> Q(string q) => AllEntries(admin, q);
        static string[] Actions(List<JsonElement> l) => l.Select(e => e.GetProperty("action").GetString()!).Order().ToArray();
        string Iso(DateTimeOffset d) => "&from=" + Uri.EscapeDataString(d.ToString("O"));

        Assert.Equal(["asset.created", "asset.status.changed"], Actions(await Q("&action=asset.")).Take(2).ToArray()[..2]);
        Assert.Equal(["asset.created", "asset.status.changed", "rendition.ready"], Actions(await Q("&entityType=asset")));
        Assert.Equal(["asset.created", "asset.status.changed"], Actions(await Q($"&entityId={asset1}")));
        Assert.Equal(["asset.created", "role.created"], Actions(await Q("&actor=subject-a")));
        Assert.Equal(["role.created"], Actions(await Q("&actor=subject-a&action=role")));
        Assert.Equal(["asset.status.changed"], Actions(await Q("&q=status")));                          // free text covers action, entity and actor - not the payload
        Assert.Empty(await Q("&q=hero"));
        Assert.Equal(["worker"], (await Q("&q=worker")).Select(e => e.GetProperty("actorId").GetString()!).ToArray());
        Assert.Equal(["workflow.sla.breached"], Actions(await Q("&q=sla")));
        Assert.Equal(["role.created"], Actions(await Q($"&q={asset2.ToString()[..8]}&action=role")));
        Assert.Equal(["rendition.ready", "workflow.sla.breached"], Actions(await Q(Iso(Day.AddDays(1).AddMinutes(-1)) + "&to=" + Uri.EscapeDataString(Day.AddDays(3).ToString("O")))));
        Assert.Equal(["asset.created", "asset.status.changed"], Actions(await Q($"&from={Uri.EscapeDataString(Day.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(Day.AddHours(1).AddMinutes(1).ToString("O"))}")));
        Assert.Empty(await Q("&action=nothing.like.this"));
        Assert.Empty(await Q("&q=no-such-text"));
        // LIKE wildcards in the input are literal.
        Assert.Empty(await Q("&action=%25"));
        Assert.Empty(await Q("&action=asset_"));          // would match "asset." if _ were a wildcard
        Assert.Empty(await Q("&q=asse_.created"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Audit}?from={Uri.EscapeDataString(Day.AddDays(1).ToString("O"))}&to={Uri.EscapeDataString(Day.ToString("O"))}", admin)).Status);

        var worker = (await Q("&actor=worker"))[0];
        Assert.Equal("worker", worker.GetProperty("actorName").GetString());   // automated actors are shown as they are
        Assert.Equal(JsonValueKind.Null, worker.GetProperty("actorUserId").ValueKind);
        Assert.Equal("10.0.0.1", (await Q("&action=asset.created"))[0].GetProperty("ip").GetString());
    }

    [Fact]
    public async Task The_actor_filter_accepts_the_orbit_user_id_as_well_as_the_identity_subject_and_the_action_list_feeds_a_picker()
    {
        var (_, admin, subject, userId) = await Busy();
        var bySubject = await AllEntries(admin, $"&actor={subject}");
        var byUser = await AllEntries(admin, $"&actor={userId}");
        Assert.NotEmpty(bySubject);
        Assert.Equal(bySubject.Select(e => e.GetProperty("id").GetGuid()), byUser.Select(e => e.GetProperty("id").GetGuid()));
        Assert.Empty(await AllEntries(admin, $"&actor={Guid.NewGuid()}"));

        var actions = (await Call(HttpMethod.Get, $"{Audit}/actions", admin)).Body.EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Contains("asset.created", actions);
        Assert.Contains("folder.created", actions);
        Assert.Equal(actions.Order(), actions);
        Assert.Equal(actions.Length, actions.Distinct().Count());
    }

    [Fact]
    public async Task Only_holders_of_audit_read_can_see_export_or_verify_the_log()
    {
        var (t, _, _, _) = await Busy();
        foreach (var role in new[] { "Librarian", "Editor", "Approver", "Contributor", "Viewer", "Dealer", "ApiClient" })
            foreach (var path in new[] { Audit, $"{Audit}/actions", $"{Audit}/verify", $"{Audit}/export", $"{Audit}/export?format=cef" })
                Assert.True(HttpStatusCode.Forbidden == (await Call(HttpMethod.Get, path, As(t, role))).Status, $"{role} {path}");
        foreach (var path in new[] { Audit, $"{Audit}/verify", $"{Audit}/export" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, path, "")).Status);
    }

    [Fact]
    public async Task One_tenant_never_sees_another_tenants_audit_log()
    {
        var a = await Busy(); var b = await Busy();
        var onlyB = await AllEntries(b.Admin);
        Assert.Equal(Count(b.Tenant, "SELECT count(*) FROM audit_log"), onlyB.Count);
        Assert.DoesNotContain(onlyB, e => e.GetProperty("actorId").GetString() == a.Subject.ToString());
        Assert.Empty(await AllEntries(b.Admin, $"&actor={a.Subject}"));
        var (_, _, text) = await Raw($"{Audit}/export", b.Admin);
        Assert.DoesNotContain(a.Subject.ToString(), text);
        Assert.Contains($"# tenant: {b.Tenant}", text);
        using var none = Pg.RawAppConnection(null);
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_log", none);
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    // ---- verification ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_untouched_chain_verifies_over_any_range()
    {
        var (t, admin, _, _) = await Busy();
        var total = Count(t, "SELECT count(*) FROM audit_log");
        var all = await AllEntries(admin);

        var r = await Call(HttpMethod.Get, $"{Audit}/verify", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.Body.GetProperty("verified").GetBoolean());
        Assert.Equal(total, r.Body.GetProperty("checked").GetInt64());
        Assert.Equal(all[^1].GetProperty("id").GetGuid(), r.Body.GetProperty("firstId").GetGuid());
        Assert.Equal(all[0].GetProperty("id").GetGuid(), r.Body.GetProperty("lastId").GetGuid());
        Assert.Equal(JsonValueKind.Null, r.Body.GetProperty("brokenAtId").ValueKind);
        Assert.Equal(all[0].GetProperty("hash").GetString(), r.Body.GetProperty("lastHash").GetString());

        // A window in the middle is checked against the row before it.
        var mid = all[all.Count / 2].GetProperty("occurredAt").GetDateTimeOffset();
        var window = await Call(HttpMethod.Get, $"{Audit}/verify?from={Uri.EscapeDataString(mid.ToString("O"))}", admin);
        Assert.True(window.Body.GetProperty("verified").GetBoolean());
        Assert.InRange(window.Body.GetProperty("checked").GetInt64(), 1, total);

        // A range with nothing in it is trivially intact.
        var none = await Call(HttpMethod.Get, $"{Audit}/verify?from=2001-01-01T00:00:00Z&to=2001-01-02T00:00:00Z", admin);
        Assert.True(none.Body.GetProperty("verified").GetBoolean());
        Assert.Equal(0, none.Body.GetProperty("checked").GetInt64());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Audit}/verify?from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z", admin)).Status);
    }

    private Guid RowWith(Guid tenant, string where)
    {
        using var c = Pg.RawAppConnection(tenant);
        using var cmd = new NpgsqlCommand($"SELECT id FROM audit_log WHERE {where}", c);
        return (Guid)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task A_row_changed_by_someone_with_database_rights_is_found()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        for (var i = 0; i < 8; i++) InsertAudit(t, Day.AddMinutes(i), "subject-a", "asset.created", "asset", Guid.NewGuid(), $$"""{"n":{{i}}}""");
        Assert.True((await Call(HttpMethod.Get, $"{Audit}/verify", admin)).Body.GetProperty("verified").GetBoolean());
        var target = RowWith(t, "after->>'n' = '4'");
        var targetNo = Count(t, "SELECT chain_no FROM audit_log WHERE after->>'n' = '4'");
        var before = Count(t, "SELECT count(*) FROM audit_log WHERE chain_no <= " + targetNo);

        Tamper($"UPDATE audit_log SET after = '{{\"n\":999}}'::jsonb WHERE id = '{target}'");

        var r = await Call(HttpMethod.Get, $"{Audit}/verify", admin);
        Assert.False(r.Body.GetProperty("verified").GetBoolean());
        Assert.Equal(target, r.Body.GetProperty("brokenAtId").GetGuid());
        Assert.Equal(targetNo, r.Body.GetProperty("brokenAtChainNo").GetInt64());
        Assert.Equal("hash_mismatch", r.Body.GetProperty("reason").GetString());
        Assert.Equal(before, r.Body.GetProperty("checked").GetInt64());   // it stopped at the first bad row

        // A window that stops short of the changed row still verifies; one that includes it does not.
        Assert.True((await Call(HttpMethod.Get, $"{Audit}/verify?to={Uri.EscapeDataString(Day.AddMinutes(3).AddSeconds(30).ToString("O"))}", admin)).Body.GetProperty("verified").GetBoolean());
        Assert.False((await Call(HttpMethod.Get, $"{Audit}/verify?from={Uri.EscapeDataString(Day.AddMinutes(4).ToString("O"))}&to={Uri.EscapeDataString(Day.AddMinutes(6).ToString("O"))}", admin)).Body.GetProperty("verified").GetBoolean());
        Assert.True((await Call(HttpMethod.Get, $"{Audit}/verify?from={Uri.EscapeDataString(Day.AddMinutes(5).ToString("O"))}&to={Uri.EscapeDataString(Day.AddMinutes(7).ToString("O"))}", admin)).Body.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task A_changed_field_of_any_kind_a_removed_row_and_a_recomputed_hash_are_each_detected()
    {
        async Task<(Guid Tenant, string Admin, List<Guid> Rows)> Fresh()
        {
            var t = await NewTenantAsync(); var admin = Admin(t);
            for (var i = 0; i < 6; i++) InsertAudit(t, Day.AddMinutes(i), "subject-a", "asset.created", "asset", Guid.NewGuid(), $$"""{"n":{{i}}}""", "10.0.0.1");
            // Only the six rows written here, oldest first (the tenant's own start-up rows come before them).
            var rows = (await AllEntries(admin, "&action=asset.created")).OrderBy(e => e.GetProperty("chainNo").GetInt64()).Select(e => e.GetProperty("id").GetGuid()).ToList();
            return (t, admin, rows);
        }

        foreach (var set in new[] { "actor = 'someone-else'", "action = 'asset.deleted'", "ip = '6.6.6.6'", "ts = ts + interval '1 second'", "entity_type = 'role'", "before = '{}'::jsonb" })
        {
            var (t, admin, rows) = await Fresh();
            Tamper($"UPDATE audit_log SET {set} WHERE id = '{rows[2]}'");
            var r = (await Call(HttpMethod.Get, $"{Audit}/verify", admin)).Body;
            Assert.False(r.GetProperty("verified").GetBoolean(), set);
            Assert.Equal(rows[2], r.GetProperty("brokenAtId").GetGuid());
            _ = t;
        }

        // A missing row in the middle.
        var (_, adminGap, gapRows) = await Fresh();
        Tamper($"DELETE FROM audit_log WHERE id = '{gapRows[3]}'");
        var gap = (await Call(HttpMethod.Get, $"{Audit}/verify", adminGap)).Body;
        Assert.False(gap.GetProperty("verified").GetBoolean());
        Assert.Equal("gap", gap.GetProperty("reason").GetString());
        Assert.Equal(gapRows[4], gap.GetProperty("brokenAtId").GetGuid());

        // Someone who also recomputes the changed row's hash is caught at the next row, which still names the old one.
        var (_, adminRehash, rehashRows) = await Fresh();
        Tamper($"UPDATE audit_log SET after = '{{\"n\":999}}'::jsonb WHERE id = '{rehashRows[2]}'");
        Tamper($"UPDATE audit_log a SET hash = audit_row_hash(a.prev_hash, a) WHERE a.id = '{rehashRows[2]}'");
        var rehash = (await Call(HttpMethod.Get, $"{Audit}/verify", adminRehash)).Body;
        Assert.False(rehash.GetProperty("verified").GetBoolean());
        Assert.Equal(rehashRows[3], rehash.GetProperty("brokenAtId").GetGuid());
        Assert.Equal("prev_hash_mismatch", rehash.GetProperty("reason").GetString());
    }

    // ---- export ------------------------------------------------------------------------------------------------

    private static (List<string> Comments, List<string> Lines) Split(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        return (lines.Where(l => l.StartsWith('#')).ToList(), lines.Where(l => !l.StartsWith('#')).ToList());
    }

    [Fact]
    public async Task A_csv_export_begins_and_ends_with_a_chain_verification_summary_and_holds_one_line_per_entry()
    {
        var (t, admin, _, _) = await Busy();
        var all = await AllEntries(admin);
        var (status, response, text) = await Raw($"{Audit}/export?format=csv", admin);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString());
        Assert.StartsWith("attachment; filename=\"orbit-audit-", response.Content.Headers.ContentDisposition!.ToString());
        Assert.EndsWith(".csv\"", response.Content.Headers.ContentDisposition.ToString());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# Orbit audit export (CSV)", lines[0]);
        Assert.StartsWith("# end of export:", lines[^1]);
        var (comments, rows) = Split(text);
        Assert.Contains($"# tenant: {t}", comments);
        Assert.Contains("# chain-verification: VERIFIED", comments);
        Assert.Contains($"# chain-rows-checked: {all.Count} (chain 1..{all.Count})", comments);
        Assert.Contains($"# rows-in-export: {all.Count}", comments);
        Assert.Contains($"# chain-last-hash: {all[0].GetProperty("hash").GetString()}", comments);
        Assert.Contains($"# chain-first-hash: {all[^1].GetProperty("hash").GetString()}", comments);
        Assert.Contains(comments, c => c.StartsWith("# filters: none", StringComparison.Ordinal));

        Assert.StartsWith("id,chain_no,occurred_at,actor_id,actor_user_id,actor_name,action,", rows[0]);
        Assert.Equal(all.Count, rows.Count - 1);
        // Oldest first, ending on the chain's last hash, which the footer repeats.
        Assert.Contains(all[^1].GetProperty("id").GetGuid().ToString(), rows[1]);
        Assert.EndsWith(all[0].GetProperty("hash").GetString()!, rows[^1]);
        Assert.Equal($"# end of export: rows={all.Count} first-hash={all[^1].GetProperty("hash").GetString()} last-hash={all[0].GetProperty("hash").GetString()} chain-verification=VERIFIED", lines[^1]);
    }

    [Fact]
    public async Task An_export_applies_the_filters_but_still_verifies_the_whole_chain_in_range()
    {
        var (_, admin, _, _) = await Busy();
        var created = await AllEntries(admin, "&action=asset.created");
        var (_, _, text) = await Raw($"{Audit}/export?format=csv&action=asset.created", admin);
        var (comments, rows) = Split(text);
        Assert.Equal(created.Count, rows.Count - 1);
        Assert.All(rows.Skip(1), r => Assert.Contains(",asset.created,", r));
        Assert.Contains($"# rows-in-export: {created.Count}", comments);
        Assert.Contains("# filters: action=asset.created*", comments);
        Assert.Contains("# chain-verification: VERIFIED", comments);
        Assert.Contains(comments, c => c.StartsWith("# chain-rows-checked:", StringComparison.Ordinal) && !c.Contains($" {created.Count} ", StringComparison.Ordinal));   // the whole chain was checked, not just the filtered rows
    }

    [Fact]
    public async Task A_cef_export_has_one_cef_event_per_line_with_the_same_summary_and_proper_escaping()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var asset = Guid.NewGuid();
        InsertAudit(t, Day, "subject-a", "asset.created", "asset", asset, """{"Note":"a=b|c\\d"}""", "10.0.0.7");
        InsertAudit(t, Day.AddMinutes(1), "sub|ject=b", "weird|action\nwith newline", "asset", asset);
        var (status, response, text) = await Raw($"{Audit}/export?format=cef", admin);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        Assert.EndsWith(".cef\"", response.Content.Headers.ContentDisposition!.ToString());
        var (comments, events) = Split(text);
        Assert.StartsWith("# Orbit audit export (CEF)", comments[0]);
        Assert.Contains("# chain-verification: VERIFIED", comments);
        var total = Count(t, "SELECT count(*) FROM audit_log");
        Assert.Contains($"# rows-in-export: {total}", comments);
        Assert.Equal(total, events.Count);
        Assert.All(events, e => Assert.StartsWith("CEF:0|Orbit|DAM|", e));

        var created = events.Single(e => e.Contains("|asset.created|", StringComparison.Ordinal));
        Assert.Contains("|asset.created|Asset created|3|", created);
        Assert.Contains("src=10.0.0.7", created);
        Assert.Contains($"cs2Label=entityId cs2={asset}", created);
        Assert.Contains(@"msg={""Note"": ""a\=b|c\\\\d""}", created);   // '=' escaped, each backslash doubled, a pipe is fine in the extension
        var weird = events.Single(e => e.Contains("with newline", StringComparison.Ordinal));
        Assert.Contains(@"|weird\|action\nwith newline|", weird);
        Assert.Contains(@"suid=sub|ject\=b", weird);
        Assert.DoesNotContain(events, e => e.Contains('\r'));
        Assert.StartsWith($"# end of export: rows={total}", comments[^1]);
    }

    [Fact]
    public async Task A_broken_chain_is_reported_in_both_the_header_and_the_footer_of_an_export()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        for (var i = 0; i < 5; i++) InsertAudit(t, Day.AddMinutes(i), "subject-a", "asset.created", "asset", Guid.NewGuid(), $$"""{"n":{{i}}}""");
        var rows = (await AllEntries(admin)).OrderBy(e => e.GetProperty("chainNo").GetInt64()).Select(e => e.GetProperty("id").GetGuid()).ToList();
        var target = RowWith(t, "chain_no = 3");
        Tamper($"UPDATE audit_log SET actor = 'forged' WHERE id = '{target}'");

        foreach (var format in new[] { "csv", "cef" })
        {
            var (status, _, text) = await Raw($"{Audit}/export?format={format}", admin);
            Assert.Equal(HttpStatusCode.OK, status);   // the export still happens: the point is to tell the reader
            var (comments, lines) = Split(text);
            Assert.Contains("# chain-verification: FAILED", comments);
            Assert.Contains($"# chain-broken-at: chain 3 id {target} reason hash_mismatch", comments);
            Assert.EndsWith("chain-verification=FAILED", comments[^1]);
            Assert.Equal(rows.Count, lines.Count - (format == "csv" ? 1 : 0));
        }
    }

    [Fact]
    public async Task A_large_export_is_streamed_complete_and_verified()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        using (var c = new NpgsqlConnection(Pg.OwnerConnection))
        {
            c.Open();
            using var cmd = new NpgsqlCommand("""
                INSERT INTO audit_log (id, tenant_id, ts, actor, action, entity_type, entity_id, after)
                SELECT gen_random_uuid(), @t, @ts + g * interval '1 second', 'bulk-actor', 'asset.metadata.updated', 'asset', gen_random_uuid(), jsonb_build_object('n', g, 'text', repeat('x', 200))
                FROM generate_series(1, 3000) g
                """, c);
            cmd.Parameters.AddWithValue("t", t);
            cmd.Parameters.AddWithValue("ts", Day.ToUniversalTime());
            cmd.ExecuteNonQuery();
        }
        var (status, _, text) = await Raw($"{Audit}/export?format=csv", admin);
        Assert.Equal(HttpStatusCode.OK, status);
        var (comments, rows) = Split(text);
        var total = Count(t, "SELECT count(*) FROM audit_log");
        Assert.True(total >= 3000);
        Assert.Equal(total, rows.Count - 1);
        Assert.Contains("# chain-verification: VERIFIED", comments);
        Assert.Contains($"# rows-in-export: {total}", comments);
        Assert.Equal(total, rows.Skip(1).Select(r => r.Split(',')[0]).Distinct().Count());
        Assert.Equal(total, (await Call(HttpMethod.Get, $"{Audit}/verify", admin)).Body.GetProperty("checked").GetInt64());
    }

    [Fact]
    public async Task A_spreadsheet_formula_in_a_name_is_defused_in_csv_and_an_unknown_format_is_refused()
    {
        var t = await NewTenantAsync();
        var admin = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Admin"], name: "=HYPERLINK(\"http://evil.test\",\"click\")");
        await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Brand" });
        var (_, _, text) = await Raw($"{Audit}/export", admin);   // csv is the default
        Assert.Contains("'=HYPERLINK", text);
        Assert.DoesNotContain(",=HYPERLINK", text);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Audit}/export?format=xml", admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Audit}/export?format=csv&to=2020-01-01T00:00:00Z&from=2021-01-01T00:00:00Z", admin)).Status);
    }
}
