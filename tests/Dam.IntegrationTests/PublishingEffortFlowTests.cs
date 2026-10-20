using System.Net;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.IntegrationTests;

/// <summary>
/// Publishing effort (ADR-0037) through the REAL publish paths rather than hand-built rows: a person publishing, a workflow's automatic publish (after a rejection and straight away)
/// and a scheduled publish all record the first publication, and the first-pass rule sees the rejection.
/// </summary>
[Collection("pg")]
public sealed class PublishingEffortFlowTests(PostgresFixture pg) : AnalyticsTestBase(pg)
{
    private async Task<(string Token, Guid UserId)> Reviewer(Guid tenant, string admin, string folderPath)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), ["Approver"], name: "Reviewer");
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
        {
            principalType = "user", principalId = userId, scopeType = "folder", scopeValue = folderPath,
            permissions = new[] { "assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review" },
        })).Status);
        return (token, userId);
    }


    private async Task<Guid> TaskFor(string token, Guid asset)
    {
        var inbox = (await Call(HttpMethod.Get, "/api/v1/tasks", token)).Body.GetProperty("items").EnumerateArray();
        return inbox.Single(t => t.GetProperty("assetId").GetGuid() == asset).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Every_way_of_publishing_records_the_first_publication_and_a_rejection_before_it_costs_the_first_pass()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var path = (await Call(HttpMethod.Get, $"/api/v1/folders/{brand}", admin)).Body.GetProperty("folder").GetProperty("path").GetString()!;
        var reviewer = await Reviewer(t, admin, path);

        // 1. a person: submit, approve, publish (no workflow in this folder yet)
        var manual = await NewAsset(admin, brand, title: "Manual");
        foreach (var action in new[] { "submit", "approve", "publish" })
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{manual}/transitions/{action}", admin, new { })).Status);
        var first = Text($"SELECT first_published_at::text FROM assets WHERE id = '{manual}'");
        Assert.NotEqual("", first);
        // withdrawn and published again: the first time stands
        foreach (var action in new[] { "withdraw", "submit", "approve", "publish" })
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{manual}/transitions/{action}", admin, new { })).Status);
        Assert.Equal(first, Text($"SELECT first_published_at::text FROM assets WHERE id = '{manual}'"));

        // 2. a workflow that publishes automatically: one asset rejected once, one approved at once
        var step = new Dictionary<string, object?>
        {
            ["id"] = "review", ["name"] = "Review", ["assignees"] = new { roles = new[] { "Approver" }, users = Array.Empty<Guid>() }, ["mode"] = "any",
            ["slaHours"] = null, ["escalation"] = null, ["onApprove"] = "complete", ["onReject"] = "reject",
        };
        var wf = await Call(HttpMethod.Post, "/api/v1/workflows", admin, new { name = "Auto review", description = "", definition = new { version = 1, start = "review", autoPublish = true, distinctApprovers = false, steps = new[] { step } } });
        Assert.Equal(HttpStatusCode.Created, wf.Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/folders/{brand}", admin, new { defaultWorkflowId = Id(wf.Body, "id") })).Status);

        var smooth = await NewAsset(admin, brand, title: "Smooth");
        await Call(HttpMethod.Post, $"/api/v1/assets/{smooth}/transitions/submit", admin, new { });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/tasks/{await TaskFor(reviewer.Token, smooth)}/decision", reviewer.Token, new { decision = "approve" })).Status);

        var bumpy = await NewAsset(admin, brand, title: "Bumpy");
        await Call(HttpMethod.Post, $"/api/v1/assets/{bumpy}/transitions/submit", admin, new { });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/tasks/{await TaskFor(reviewer.Token, bumpy)}/decision", reviewer.Token, new { decision = "reject", comment = "Needs a better crop" })).Status);
        await Call(HttpMethod.Post, $"/api/v1/assets/{bumpy}/transitions/submit", admin, new { });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/tasks/{await TaskFor(reviewer.Token, bumpy)}/decision", reviewer.Token, new { decision = "approve" })).Status);

        foreach (var asset in new[] { smooth, bumpy })
        {
            Assert.Equal("published", Text($"SELECT status FROM assets WHERE id = '{asset}'"));
            Assert.NotEqual("", Text($"SELECT coalesce(first_published_at::text, '') FROM assets WHERE id = '{asset}'"));
        }

        // 3. a scheduled publish: an approved asset whose publish_at has come
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/folders/{brand}", admin, new { clearDefaultWorkflow = true })).Status);
        var scheduled = await NewAsset(admin, brand, title: "Scheduled");
        foreach (var action in new[] { "submit", "approve" })
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{scheduled}/transitions/{action}", admin, new { })).Status);
        Sql($"UPDATE assets SET publish_at = now() - interval '1 minute' WHERE id = '{scheduled}'");
        using (var scheduler = NotifyHosts.Scheduler(Pg, new FakeClock(DateTimeOffset.UtcNow), configure: svc => svc.AddScheduledJob<PublishExpireJob>()))
            await NotifyHosts.RunJobAsync(scheduler, PublishExpireJob.JobKey);
        Assert.Equal("published", Text($"SELECT status FROM assets WHERE id = '{scheduled}'"));
        Assert.NotEqual("", Text($"SELECT coalesce(first_published_at::text, '') FROM assets WHERE id = '{scheduled}'"));

        // the KPI sees all four (the person's, the two workflow assets and the scheduled one); only the rejected-once asset did not pass first time
        var p = (await GetOk(KpisUrl, admin)).GetProperty("publishingEffort");
        Assert.Equal(4, p.GetProperty("publishedCount").GetInt64());
        Assert.Equal(0.75, p.GetProperty("firstPassRate").GetDouble());
        Assert.True(p.GetProperty("medianHours").GetDouble() >= 0);
    }
}
