using System.Net;
using System.Text.Json;
using Dam.UnitTests.Support;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class LifecycleTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly Dictionary<string, string?> NoScan = new() { ["DAM_SCAN_MODE"] = "off" };   // scanning has its own tests

    private async Task<(HttpStatusCode Status, JsonElement Body)> Transition(string token, Guid asset, string action, string? reason = null) =>
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", token, new { reason });

    private async Task<string> StatusOf(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", token)).Body.GetProperty("status").GetString()!;

    /// <summary>Grants a scoped power (spec ADR-0002: approve etc. are never in a role, only in an access rule) to a fresh user
    /// in the given folder subtree, and returns that user's token.</summary>
    private async Task<string> ScopedUser(Guid tenant, string admin, string folderLabel, params string[] permissions)
    {
        var subject = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(tenant, subject, ["Viewer"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
        {
            principalType = "user", principalId = userId, scopeType = "folder", scopeValue = folderLabel, permissions,
        })).Status);
        return token;
    }

    // ---- the happy path -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_asset_walks_draft_to_published_with_a_permission_check_and_an_audit_row_at_every_step()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Assert.Equal("draft", await StatusOf(admin, asset));

        var editor = As(t, "Editor");
        Assert.Equal(HttpStatusCode.OK, (await Transition(editor, asset, "submit")).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));

        // Editor may not approve their own submission: approve is scoped-only, granted to nobody by default.
        Assert.Equal(HttpStatusCode.Forbidden, (await Transition(editor, asset, "approve")).Status);
        var approver = await ScopedUser(t, admin, "brand", "assets.approve", "assets.read", "assets.read.in_review");
        Assert.Equal(HttpStatusCode.OK, (await Transition(approver, asset, "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));

        var librarian = As(t, "Librarian");   // publishing (like approving) is not an Editor power
        Assert.Equal(HttpStatusCode.OK, (await Transition(librarian, asset, "publish")).Status);
        Assert.Equal("published", await StatusOf(admin, asset));

        Assert.Equal(3, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed' AND entity_id = '{asset}'"));   // submit, approve, publish (the forbidden attempt left no trace)
    }

    [Fact]
    public async Task Rejecting_sends_an_asset_back_to_draft_with_the_reviewers_reason_and_needs_a_reason()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        await Transition(As(t, "Editor"), asset, "submit");
        var approver = await ScopedUser(t, admin, "brand", "assets.approve", "assets.read", "assets.read.in_review");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transition(approver, asset, "reject")).Status);   // no reason given
        var rejected = await Transition(approver, asset, "reject", "Wrong resolution, please re-export.");
        Assert.Equal(HttpStatusCode.OK, rejected.Status);
        Assert.Equal("draft", rejected.Body.GetProperty("status").GetString());
        Assert.Equal("Wrong resolution, please re-export.", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("statusReason").GetString());
    }

    // ---- guards ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Submitting_or_publishing_with_incomplete_required_metadata_is_refused()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var schema = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray()
            .Select(f => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText())!).ToList();
        foreach (var f in schema.Where(f => f["name"].GetString() == "description")) f["required"] = JsonDocument.Parse("true").RootElement;
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = schema })).Status);

        var asset = await NewAsset(admin, brand, metadata: new { }, allowIncomplete: true);
        var r = await Transition(admin, asset, "submit");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Equal("This field is required.", r.Body.GetProperty("errors").GetProperty("metadata.description")[0].GetString());
        Assert.Equal("draft", await StatusOf(admin, asset));   // never moved
    }

    [Fact]
    public async Task Illegal_transitions_are_refused_with_a_clear_reason_and_change_nothing()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        foreach (var action in new[] { "approve", "reject", "publish", "expire", "archive" })
        {
            var r = await Transition(admin, asset, action, reason: "irrelevant to a state that cannot be reached");
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Contains("draft", r.Body.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transition(admin, asset, "not-a-real-action")).Status);
        Assert.Equal("draft", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task Withdraw_returns_a_published_asset_to_draft_and_it_must_be_resubmitted_from_scratch()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        var editor = As(t, "Editor");
        await Transition(editor, asset, "submit");
        var approver = await ScopedUser(t, admin, "brand", "assets.approve", "assets.read", "assets.read.in_review");
        await Transition(approver, asset, "approve");
        var librarian = As(t, "Librarian");
        await Transition(librarian, asset, "publish");

        Assert.Equal(HttpStatusCode.OK, (await Transition(librarian, asset, "withdraw")).Status);
        Assert.Equal("draft", await StatusOf(admin, asset));
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(librarian, asset, "publish")).Status);   // straight back to published is not legal
        Assert.Equal(HttpStatusCode.OK, (await Transition(editor, asset, "submit")).Status);              // the normal path still works
    }

    [Fact]
    public async Task Archiving_is_retention_scoped_not_a_normal_publishing_power()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Sql($"UPDATE assets SET status = 'expired' WHERE id = '{asset}'");

        Assert.Equal(HttpStatusCode.Forbidden, (await Transition(As(t, "Librarian"), asset, "archive")).Status);   // publish/withdraw powers, but not retention
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "archive")).Status);
        Assert.Equal("archived", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task A_hidden_asset_looks_absent_rather_than_forbidden()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Assert.Equal(HttpStatusCode.NotFound, (await Transition(As(t, "Viewer"), asset, "submit")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Transition(admin, Guid.NewGuid(), "submit")).Status);
    }

    // ---- restore --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restoring_an_old_version_copies_its_bytes_into_a_brand_new_version_and_makes_it_current()
    {
        var (t, admin, brand) = await TenantWithPacks();
        using var worker = StartWorker(NoScan, renditions: false); await worker.StartAsync();
        var asset = await NewAsset(admin, brand);

        var v1Bytes = TestImages.Sized(400, 300);
        Assert.Equal("ingested", (await Ingest(admin, asset, v1Bytes, "a.jpg")).GetProperty("status").GetString());
        var v2Bytes = TestImages.Sized(1600, 1200);
        Assert.Equal("ingested", (await Ingest(admin, asset, v2Bytes, "b.jpg")).GetProperty("status").GetString());
        Assert.Equal(2, (await Versions(admin, asset)).GetArrayLength());

        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/versions/1/restore", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(3, r.Body.GetProperty("versionNo").GetInt32());
        Assert.True(r.Body.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(v1Bytes.Length, r.Body.GetProperty("sizeBytes").GetInt64());
        Assert.Contains("Restored from version 1", r.Body.GetProperty("changeNote").GetString());

        Assert.Equal(3, (await Versions(admin, asset)).GetArrayLength());
        Assert.Equal(3, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.version.added' AND entity_id = '{asset}'"));   // two ingests plus the restore

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/versions/3/restore", admin)).Status);   // already current
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/versions/99/restore", admin)).Status);
    }

    // ---- compare --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Comparing_two_versions_reports_only_what_differs()
    {
        var (_, admin, brand) = await TenantWithPacks();
        using var worker = StartWorker(NoScan, renditions: false); await worker.StartAsync();
        var asset = await NewAsset(admin, brand);
        await Ingest(admin, asset, TestImages.Sized(400, 300), "a.jpg");
        await Ingest(admin, asset, TestImages.Sized(1600, 1200), "b.jpg");

        var diff = await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions/1/compare/2", admin);
        Assert.Equal(HttpStatusCode.OK, diff.Status);
        var fields = diff.Body.GetProperty("fields");
        Assert.True(fields.TryGetProperty("sizeBytes", out var size));
        Assert.True(size.GetProperty("before").GetInt64() > 0 && size.GetProperty("after").GetInt64() > 0);
        Assert.NotEqual(size.GetProperty("before").GetInt64(), size.GetProperty("after").GetInt64());
        Assert.True(fields.TryGetProperty("sha256", out _));
        Assert.False(fields.TryGetProperty("mime", out _));   // both are image/jpeg: not reported as a difference

        var sameVersion = await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions/1/compare/1", admin);
        Assert.Empty(sameVersion.Body.GetProperty("fields").EnumerateObject());

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions/1/compare/99", admin)).Status);
    }
}
