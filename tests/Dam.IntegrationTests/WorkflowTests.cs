using System.Net;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Scheduling;
using Dam.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>The workflow engine (ADR-0030) against a real Postgres: definitions, binding, tasks, scope, SLA, isolation.</summary>
[Collection("pg")]
public sealed class WorkflowTests(PostgresFixture pg) : AssetTestBase(pg)
{
    private const string Workflows = "/api/v1/workflows";
    private const string Tasks = "/api/v1/tasks";

    // ---- builders -----------------------------------------------------------------------------------------------

    private static Dictionary<string, object?> S(string id, string[]? roles = null, string onApprove = "complete", string onReject = "reject",
        string mode = "any", int? sla = null, string[]? escalate = null, Guid[]? users = null) =>
        new()
        {
            ["id"] = id,
            ["name"] = char.ToUpperInvariant(id[0]) + id[1..],
            ["assignees"] = new { roles = roles ?? ["Approver"], users = users ?? [] },
            ["mode"] = mode,
            ["slaHours"] = sla,
            ["escalation"] = escalate is null ? null : new { to = new { roles = escalate, users = Array.Empty<Guid>() } },
            ["onApprove"] = onApprove,
            ["onReject"] = onReject,
        };

    private static object Def(string start, object[] steps, bool autoPublish = false, bool distinct = true) =>
        new { version = 1, start, autoPublish, distinctApprovers = distinct, steps };

    /// <summary>Review (Approver) then Final approval (Librarian).</summary>
    private static object TwoLevel(bool autoPublish = false, bool distinct = true) =>
        Def("review", [S("review", ["Approver"], onApprove: "final", sla: 48, escalate: ["Admin"]), S("final", ["Librarian"], sla: 24)], autoPublish, distinct);

    private static object OneLevel(string role = "Approver", int? sla = null, string[]? escalate = null) => Def("review", [S("review", [role], sla: sla, escalate: escalate)]);

    private async Task<Guid> NewWorkflow(string admin, string name, object definition)
    {
        var r = await Call(HttpMethod.Post, Workflows, admin, new { name, description = "for tests", definition });
        Assert.True(r.Status == HttpStatusCode.Created, r.Body.ToString());
        return Id(r.Body, "id");
    }

    private async Task<Guid> NewFolder(string admin, string name, Guid? parent = null) =>
        Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name, parentId = parent })).Body, "folder", "id");

    private async Task SetDefault(string admin, Guid folder, Guid? workflow)
    {
        var r = await Call(HttpMethod.Patch, $"/api/v1/folders/{folder}", admin, workflow is null ? new { clearDefaultWorkflow = true } : (object)new { defaultWorkflowId = workflow });
        Assert.Equal(HttpStatusCode.OK, r.Status);
    }

    private async Task<string> PathOf(string admin, Guid folder) =>
        (await Call(HttpMethod.Get, $"/api/v1/folders/{folder}", admin)).Body.GetProperty("folder").GetProperty("path").GetString()!;

    /// <summary>A user holding <paramref name="roles"/> who may approve and decide workflow tasks in <paramref name="scopePath"/> (and read what is in review there).</summary>
    private async Task<(string Token, Guid UserId)> Reviewer(Guid tenant, string admin, string[] roles, string? scopePath, string[]? permissions = null)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), roles, name: "Reviewer " + Guid.NewGuid().ToString("N")[..4]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        if (scopePath is not null)
            Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
            {
                principalType = "user",
                principalId = userId,
                scopeType = "folder",
                scopeValue = scopePath,
                permissions = permissions ?? ["assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review"],
            })).Status);
        return (token, userId);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Transition(string token, Guid asset, string action, string? reason = null) =>
        Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", token, new { reason });

    private Task<(HttpStatusCode Status, JsonElement Body)> Decide(string token, Guid task, string decision, string? comment = null) =>
        Call(HttpMethod.Post, $"{Tasks}/{task}/decision", token, new { decision, comment });

    private async Task<List<JsonElement>> Inbox(string token, string query = "")
    {
        var r = await Call(HttpMethod.Get, Tasks + query, token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<Guid> TaskFor(string token, Guid asset, string? step = null) =>
        (await Inbox(token)).Single(t => t.GetProperty("assetId").GetGuid() == asset && (step is null || t.GetProperty("stepId").GetString() == step)).GetProperty("id").GetGuid();

    private async Task<string> StatusOf(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", token)).Body.GetProperty("status").GetString()!;

    private async Task<JsonElement> View(string token, Guid asset)
    {
        var r = await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/workflow", token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("instance");
    }

    private static string[] StepStates(JsonElement instance) => instance.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("state").GetString()!).ToArray();

    /// <summary>A tenant with a "Brand" folder whose default workflow is <paramref name="definition"/>, and one draft asset in it.</summary>
    private async Task<(Guid Tenant, string Admin, Guid Folder, string Path, Guid Workflow, Guid Asset)> Scene(object definition)
    {
        var (t, admin, brand) = await TenantWithPacks();
        var workflow = await NewWorkflow(admin, "Test flow", definition);
        await SetDefault(admin, brand, workflow);
        return (t, admin, brand, await PathOf(admin, brand), workflow, await NewAsset(admin, brand));
    }

    // ---- definitions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_workflow_can_be_created_read_listed_updated_and_deleted_by_someone_with_workflow_manage()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = await NewWorkflow(admin, "Quick review", OneLevel(sla: 12));

        var got = await Call(HttpMethod.Get, $"{Workflows}/{id}", admin);
        Assert.Equal(HttpStatusCode.OK, got.Status);
        Assert.Equal("Quick review", got.Body.GetProperty("name").GetString());
        Assert.Equal(12, got.Body.GetProperty("definition").GetProperty("steps")[0].GetProperty("slaHours").GetInt32());
        Assert.Equal((0, 0, 0), (got.Body.GetProperty("usedByFolders").GetInt32(), got.Body.GetProperty("bindings").GetInt32(), got.Body.GetProperty("runningInstances").GetInt32()));

        var list = await Call(HttpMethod.Get, Workflows, admin);
        Assert.Contains(list.Body.EnumerateArray(), w => w.GetProperty("id").GetGuid() == id);

        var put = await Call(HttpMethod.Put, $"{Workflows}/{id}", admin, new { name = "Quick review v2", description = "changed", definition = OneLevel(sla: 6) });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("Quick review v2", put.Body.GetProperty("name").GetString());
        Assert.Equal(6, put.Body.GetProperty("definition").GetProperty("steps")[0].GetProperty("slaHours").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Workflows}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Workflows}/{id}", admin)).Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.created'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.deleted'"));
    }

    [Fact]
    public async Task Workflow_management_needs_workflow_manage_and_reading_also_goes_to_folder_managers()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = await NewWorkflow(admin, "Locked", OneLevel());
        foreach (var role in new[] { "Librarian", "Editor", "Approver", "Viewer" })
        {
            var token = As(t, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, Workflows, token, new { name = "x", definition = OneLevel() })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"{Workflows}/{id}", token, new { name = "x", definition = OneLevel() })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"{Workflows}/{id}", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"{Workflows}/validate", token, new { definition = OneLevel() })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"{Workflows}/bindings", token, new { bindings = Array.Empty<object>() })).Status);
        }
        // Librarians manage folders, so they can read the list to pick a folder's default; nobody else but managers can.
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Workflows, As(t, "Librarian"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"{Workflows}/bindings", As(t, "Librarian"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, Workflows, As(t, "Editor"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, Workflows, "")).Status);
    }

    [Fact]
    public async Task An_invalid_definition_is_422_with_errors_addressed_by_path_and_validate_always_answers_200()
    {
        var (_, admin, _) = await TenantWithPacks();
        var bad = Def("a", [S("a", ["Approver"], onApprove: "ghost")]);

        var create = await Call(HttpMethod.Post, Workflows, admin, new { name = "Bad", definition = bad });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.Status);
        Assert.True(create.Body.GetProperty("errors").TryGetProperty("definition.steps[0].onApprove", out _), create.Body.ToString());

        var validate = await Call(HttpMethod.Post, $"{Workflows}/validate", admin, new { definition = bad });
        Assert.Equal(HttpStatusCode.OK, validate.Status);
        Assert.False(validate.Body.GetProperty("valid").GetBoolean());
        var error = validate.Body.GetProperty("errors")[0];
        Assert.Equal("steps[0].onApprove", error.GetProperty("path").GetString());
        Assert.Contains("ghost", error.GetProperty("message").GetString());

        var ok = await Call(HttpMethod.Post, $"{Workflows}/validate", admin, new { definition = OneLevel() });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.True(ok.Body.GetProperty("valid").GetBoolean());
        Assert.Equal(0, ok.Body.GetProperty("errors").GetArrayLength());

        // Not even a definition: still a 200 saying so, never a 4xx.
        var none = await Call(HttpMethod.Post, $"{Workflows}/validate", admin, new { });
        Assert.Equal(HttpStatusCode.OK, none.Status);
        Assert.False(none.Body.GetProperty("valid").GetBoolean());
        var notObject = await Call(HttpMethod.Post, $"{Workflows}/validate", admin, new { definition = "text" });
        Assert.Equal(HttpStatusCode.OK, notObject.Status);
        Assert.False(notObject.Body.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Roles_and_users_named_in_a_definition_must_exist_in_the_tenant()
    {
        var (_, admin, _) = await TenantWithPacks();
        var r = await Call(HttpMethod.Post, $"{Workflows}/validate", admin, new
        {
            definition = Def("a", [S("a", ["Approver", "Typo Role"], escalate: ["Nobody"], users: [Guid.NewGuid()])]),
        });
        var paths = r.Body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(["steps[0].assignees.roles[1]", "steps[0].assignees.users[0]", "steps[0].escalation.to.roles[0]"], paths);
    }

    [Fact]
    public async Task Workflow_names_are_unique_per_tenant_ignoring_case()
    {
        var (_, admin, _) = await TenantWithPacks();
        await NewWorkflow(admin, "Signoff", OneLevel());
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, Workflows, admin, new { name = "SIGNOFF", definition = OneLevel() })).Status);
        var other = await NewWorkflow(admin, "Other", OneLevel());
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"{Workflows}/{other}", admin, new { name = "signoff", definition = OneLevel() })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"{Workflows}/{other}", admin, new { name = "Other", definition = OneLevel() })).Status);   // keeping its own name is fine
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, Workflows, admin, new { name = "", definition = OneLevel() })).Status);
    }

    [Fact]
    public async Task Applying_the_core_template_adds_a_generic_two_level_workflow_that_is_not_bound_to_anything()
    {
        var t = await NewTenantAsync("core");
        var admin = Admin(t);
        var list = (await Call(HttpMethod.Get, Workflows, admin)).Body.EnumerateArray().ToList();
        var two = Assert.Single(list, w => w.GetProperty("name").GetString() == "Two-level approval");
        Assert.Equal(2, two.GetProperty("definition").GetProperty("steps").GetArrayLength());
        Assert.Equal((0, 0), (two.GetProperty("usedByFolders").GetInt32(), two.GetProperty("bindings").GetInt32()));
        Assert.Equal(0, (await Call(HttpMethod.Get, $"{Workflows}/bindings", admin)).Body.GetArrayLength());

        // Additive and repeatable: applying again, or after the tenant renamed it, creates nothing new.
        var again = await Call(HttpMethod.Post, "/api/v1/templates/core/apply", admin);
        Assert.Equal(0, again.Body.GetProperty("workflowsCreated").GetInt32());
        Assert.Equal(1, (await Call(HttpMethod.Get, Workflows, admin)).Body.EnumerateArray().Count(w => w.GetProperty("name").GetString() == "Two-level approval"));
        var catalog = (await Call(HttpMethod.Get, "/api/v1/templates", admin)).Body.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "core");
        Assert.Equal(1, catalog.GetProperty("workflows").GetInt32());
    }

    // ---- folder defaults and bindings -------------------------------------------------------------------------

    [Fact]
    public async Task A_folders_default_workflow_must_exist()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var r = await Call(HttpMethod.Patch, $"/api/v1/folders/{brand}", admin, new { defaultWorkflowId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
    }

    [Fact]
    public async Task Bindings_are_replaced_as_a_whole_validated_and_keep_the_ids_of_unchanged_rows()
    {
        var (_, admin, _) = await TenantWithPacks();
        var a = await NewWorkflow(admin, "A", OneLevel());
        var b = await NewWorkflow(admin, "B", OneLevel());

        var put = await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new
        {
            bindings = new object[] { new { workflowId = a, assetType = "image", region = (string?)null }, new { workflowId = b, assetType = (string?)null, region = "north" } },
        });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        var rows = put.Body.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var imageRow = rows.Single(r => r.GetProperty("assetType").GetString() == "image");
        Assert.Equal("A", imageRow.GetProperty("workflowName").GetString());

        var swap = await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new
        {
            bindings = new object[] { new { workflowId = b, assetType = "image", region = (string?)null } },   // same key, new workflow; the region row is gone
        });
        var after = Assert.Single(swap.Body.EnumerateArray());
        Assert.Equal(imageRow.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        Assert.Equal("B", after.GetProperty("workflowName").GetString());
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Workflows}/bindings", admin)).Body.GetArrayLength());

        // Validation: nothing to match on, unknown type, unknown workflow, duplicates.
        async Task<HttpStatusCode> Put(params object[] bindings) => (await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new { bindings })).Status;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Put(new { workflowId = a, assetType = (string?)null, region = (string?)null }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Put(new { workflowId = a, assetType = "spaceship", region = (string?)null }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Put(new { workflowId = Guid.NewGuid(), assetType = "image", region = (string?)null }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Put(new { workflowId = a, assetType = "image", region = (string?)null }, new { workflowId = b, assetType = "image", region = (string?)null }));
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Workflows}/bindings", admin)).Body.GetArrayLength());   // a refused replacement changed nothing

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new { bindings = Array.Empty<object>() })).Status);
        Assert.Equal(0, (await Call(HttpMethod.Get, $"{Workflows}/bindings", admin)).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_workflow_in_use_cannot_be_deleted_and_the_counts_say_why()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var wf = await NewWorkflow(admin, "In use", OneLevel());

        // folder default
        await SetDefault(admin, brand, wf);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"{Workflows}/{wf}", admin)).Status);
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Workflows}/{wf}", admin)).Body.GetProperty("usedByFolders").GetInt32());
        await SetDefault(admin, brand, null);

        // binding
        await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new { bindings = new object[] { new { workflowId = wf, assetType = "video", region = (string?)null } } });
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"{Workflows}/{wf}", admin)).Status);
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Workflows}/{wf}", admin)).Body.GetProperty("bindings").GetInt32());
        await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new { bindings = Array.Empty<object>() });

        // a running instance
        await SetDefault(admin, brand, wf);
        var asset = await NewAsset(admin, brand);
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "submit")).Status);
        await SetDefault(admin, brand, null);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"{Workflows}/{wf}", admin)).Status);
        var listed = (await Call(HttpMethod.Get, Workflows, admin)).Body.EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == wf);
        Assert.Equal(1, listed.GetProperty("runningInstances").GetInt32());

        // once it has finished, the workflow can go, and the finished instance keeps its snapshot
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "withdraw")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Workflows}/{wf}", admin)).Status);
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM workflow_instances WHERE asset_id = '{asset}' AND definition_id IS NULL AND definition_name = 'In use' AND status = 'cancelled'"));
        var view = await View(admin, asset);
        Assert.Equal("In use", view.GetProperty("workflowName").GetString());
        Assert.Equal("cancelled", view.GetProperty("status").GetString());
    }

    // ---- which workflow applies -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_folder_default_applies_to_assets_in_it_and_in_its_subfolders_and_the_nearest_folder_wins()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var outer = await NewWorkflow(admin, "Outer", OneLevel());
        var inner = await NewWorkflow(admin, "Inner", OneLevel());
        var cars = await NewFolder(admin, "Cars", brand);
        var suv = await NewFolder(admin, "Suv", cars);
        var other = await NewFolder(admin, "Elsewhere");

        await SetDefault(admin, brand, outer);
        async Task<string?> WorkflowOf(Guid folder)
        {
            var asset = await NewAsset(admin, folder);
            Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "submit")).Status);
            return (await View(admin, asset)) is { ValueKind: JsonValueKind.Object } v ? v.GetProperty("workflowName").GetString() : null;
        }
        Assert.Equal("Outer", await WorkflowOf(brand));
        Assert.Equal("Outer", await WorkflowOf(suv));           // inherited by a grandchild
        Assert.Null(await WorkflowOf(other));                   // a folder outside the tree has no workflow

        await SetDefault(admin, cars, inner);
        Assert.Equal("Inner", await WorkflowOf(suv));           // the nearest ancestor with a default wins
        Assert.Equal("Inner", await WorkflowOf(cars));
        Assert.Equal("Outer", await WorkflowOf(brand));
        Assert.Equal(5, Count(t, "SELECT count(*) FROM workflow_instances"));   // the none case starts no instance
    }

    [Fact]
    public async Task Without_a_folder_default_bindings_pick_by_type_and_region_most_specific_first_and_a_folder_beats_them_all()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var exact = await NewWorkflow(admin, "Type and region", OneLevel());
        var typeOnly = await NewWorkflow(admin, "Type only", OneLevel());
        var regionOnly = await NewWorkflow(admin, "Region only", OneLevel());
        var folderFlow = await NewWorkflow(admin, "Folder flow", OneLevel());
        await Call(HttpMethod.Put, $"{Workflows}/bindings", admin, new
        {
            bindings = new object[]
            {
                new { workflowId = exact, assetType = "image", region = "north" },
                new { workflowId = typeOnly, assetType = "image", region = (string?)null },
                new { workflowId = regionOnly, assetType = (string?)null, region = "south" },
            },
        });
        var loose = await NewFolder(admin, "Loose");   // no folder default anywhere above it

        async Task<string?> Submit(string type, string[]? regions, Guid? folder = null)
        {
            var asset = await NewAsset(admin, folder ?? loose, type, metadata: new { description = new { en = "x" }, regions }, allowIncomplete: true);
            var r = await Transition(admin, asset, "submit");
            Assert.True(r.Status == HttpStatusCode.OK, r.Body.ToString());
            return (await View(admin, asset)) is { ValueKind: JsonValueKind.Object } v ? v.GetProperty("workflowName").GetString() : null;
        }

        Assert.Equal("Type and region", await Submit("image", ["north"]));
        Assert.Equal("Type only", await Submit("image", ["south"]));              // type matches; the region binding is for a different type scope
        Assert.Equal("Type only", await Submit("image", null));
        Assert.Equal("Region only", await Submit("video", ["south"]));
        Assert.Null(await Submit("video", ["north"]));                            // nothing matches: no workflow
        Assert.Null(await Submit("document", null));

        await SetDefault(admin, brand, folderFlow);
        Assert.Equal("Folder flow", await Submit("image", ["north"], brand));     // folder default outranks every binding
        Assert.Equal(7, Count(t, "SELECT count(*) FROM assets WHERE status = 'in_review'"));
    }

    [Fact]
    public async Task With_no_workflow_the_direct_approve_and_reject_transitions_work_as_before()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "submit")).Status);
        Assert.Equal(JsonValueKind.Null, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/workflow", admin)).Body.GetProperty("instance").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "publish")).Status);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM workflow_instances"));
    }

    // ---- the two-level approval --------------------------------------------------------------------------------

    [Fact]
    public async Task Two_level_approval_blocks_publishing_until_both_levels_approve_and_the_second_level_does_not_exist_before_the_first_finishes()
    {
        var (t, admin, brand, path, _, asset) = await Scene(TwoLevel());
        var level1 = await Reviewer(t, admin, ["Approver"], path);
        var level2 = await Reviewer(t, admin, ["Librarian"], path);

        var submit = await Transition(As(t, "Editor"), asset, "submit");
        Assert.Equal(HttpStatusCode.OK, submit.Status);
        Assert.Equal("in_review", submit.Body.GetProperty("status").GetString());
        Assert.Equal("Submitted for review (workflow 'Test flow').", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("statusReason").GetString());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.task.created'"));

        // Only the first level's task exists, and only the first level's people see it.
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{asset}'"));
        Assert.Empty(await Inbox(level2.Token));
        var first = Assert.Single(await Inbox(level1.Token));
        Assert.Equal("review", first.GetProperty("stepId").GetString());
        Assert.Equal("pending", first.GetProperty("status").GetString());
        Assert.Equal("Test flow", first.GetProperty("workflowName").GetString());
        Assert.Equal(["Approver"], Strings(first.GetProperty("assigneeRoles")));
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("dueAt").ValueKind);
        Assert.False(first.GetProperty("slaBreached").GetBoolean());

        // Publishing is impossible while the workflow runs.
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(As(t, "Librarian"), asset, "publish")).Status);

        // First level approves: the asset stays in review and the second level's task now exists.
        var d1 = await Decide(level1.Token, first.GetProperty("id").GetGuid(), "approve", "Brand looks right.");
        Assert.Equal(HttpStatusCode.OK, d1.Status);
        Assert.Equal("approved", d1.Body.GetProperty("status").GetString());
        Assert.Equal("approve", d1.Body.GetProperty("decision").GetString());
        Assert.Equal(level1.UserId, d1.Body.GetProperty("decidedBy").GetGuid());
        Assert.Equal("Brand looks right.", d1.Body.GetProperty("comment").GetString());
        Assert.Equal("in_review", await StatusOf(admin, asset));
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(As(t, "Librarian"), asset, "publish")).Status);
        Assert.Empty(await Inbox(level1.Token));                         // nothing left for them
        var second = Assert.Single(await Inbox(level2.Token));
        Assert.Equal("final", second.GetProperty("stepId").GetString());

        // Second level approves: now the asset is approved and can be published.
        var d2 = await Decide(level2.Token, second.GetProperty("id").GetGuid(), "approve");
        Assert.Equal(HttpStatusCode.OK, d2.Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
        var reason = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("statusReason").GetString();
        Assert.Equal("Approved by workflow 'Test flow' (step 'Final').", reason);
        Assert.Equal(HttpStatusCode.OK, (await Transition(As(t, "Librarian"), asset, "publish")).Status);
        Assert.Equal("published", await StatusOf(admin, asset));

        var view = await View(admin, asset);
        Assert.Equal("approved", view.GetProperty("status").GetString());
        Assert.Equal("approved", view.GetProperty("outcomeStatus").GetString());
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("completedAt").ValueKind);
        Assert.Equal(["done", "done"], StepStates(view));

        // Everything was recorded: tasks created and completed, status changes audited.
        Assert.Equal(2, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.task.created'"));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.task.completed'"));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'workflow.task.created'"));
        Assert.Equal(3, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed' AND entity_id = '{asset}'"));   // submitted, approved, published
    }

    [Fact]
    public async Task Rejecting_returns_the_asset_to_draft_with_the_reviewers_comment_and_a_comment_is_required()
    {
        var (t, admin, _, path, _, asset) = await Scene(TwoLevel());
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");
        var task = await TaskFor(reviewer.Token, asset);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Decide(reviewer.Token, task, "reject")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Decide(reviewer.Token, task, "reject", "   ")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Decide(reviewer.Token, task, "maybe", "hmm")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Decide(reviewer.Token, task, "approve", new string('x', 1001))).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));

        var rejected = await Decide(reviewer.Token, task, "reject", "Wrong crop, please re-export.");
        Assert.Equal(HttpStatusCode.OK, rejected.Status);
        Assert.Equal("rejected", rejected.Body.GetProperty("status").GetString());
        Assert.Equal("draft", await StatusOf(admin, asset));
        Assert.Equal("Wrong crop, please re-export.", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("statusReason").GetString());

        var view = await View(admin, asset);
        Assert.Equal("rejected", view.GetProperty("status").GetString());
        Assert.Equal("Wrong crop, please re-export.", view.GetProperty("outcomeReason").GetString());
        Assert.Equal("draft", view.GetProperty("outcomeStatus").GetString());
        Assert.Equal(["done", "skipped"], StepStates(view));
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{asset}' AND status = 'pending'"));

        // It can be resubmitted, which starts a fresh instance with fresh tasks.
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "submit")).Status);
        Assert.Equal(2, Count(t, $"SELECT count(*) FROM workflow_instances WHERE asset_id = '{asset}'"));
        Assert.Equal("running", (await View(admin, asset)).GetProperty("status").GetString());
        Assert.NotEqual(task, await TaskFor(reviewer.Token, asset));
    }

    [Fact]
    public async Task While_a_workflow_runs_direct_approve_and_reject_are_409_and_withdraw_cancels_the_instance_and_its_tasks()
    {
        var (t, admin, _, path, _, asset) = await Scene(TwoLevel());
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");

        var approve = await Transition(admin, asset, "approve");   // even an Admin must use the task
        Assert.Equal(HttpStatusCode.Conflict, approve.Status);
        Assert.Contains("workflow", approve.Body.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, (await Transition(admin, asset, "reject", "no")).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));

        var task = await TaskFor(reviewer.Token, asset);
        Assert.Equal(HttpStatusCode.OK, (await Transition(As(t, "Editor"), asset, "withdraw", "Needs another pass")).Status);
        Assert.Equal("draft", await StatusOf(admin, asset));
        var view = await View(admin, asset);
        Assert.Equal("cancelled", view.GetProperty("status").GetString());
        Assert.Equal("draft", view.GetProperty("outcomeStatus").GetString());
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{asset}' AND status = 'pending'"));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{asset}' AND status = 'cancelled'"));

        // The cancelled task can no longer be decided and leaves the inbox.
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(reviewer.Token, task, "approve")).Status);
        Assert.Empty(await Inbox(reviewer.Token));

        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "submit")).Status);
        Assert.Equal(2, Count(t, $"SELECT count(*) FROM workflow_instances WHERE asset_id = '{asset}'"));
        Assert.Single(await Inbox(reviewer.Token));
    }

    [Fact]
    public async Task The_publish_gate_refuses_an_asset_whose_workflow_is_still_running_even_if_its_status_was_forced_to_approved()
    {
        var (t, admin, _, _, _, asset) = await Scene(OneLevel());
        await Transition(admin, asset, "submit");
        Sql($"UPDATE assets SET status = 'approved' WHERE id = '{asset}'");   // simulates a status that skipped the workflow

        var publish = await Transition(As(t, "Librarian"), asset, "publish");
        Assert.Equal(HttpStatusCode.Conflict, publish.Status);
        Assert.Contains("approval workflow", publish.Body.GetProperty("title").GetString());
        Assert.Equal("approved", await StatusOf(admin, asset));

        // Once its workflow is no longer running, the same publish goes through.
        Sql($"UPDATE workflow_instances SET status = 'approved' WHERE asset_id = '{asset}'");
        Assert.Equal(HttpStatusCode.OK, (await Transition(As(t, "Librarian"), asset, "publish")).Status);
    }

    [Fact]
    public async Task An_asset_can_have_only_one_running_workflow_at_a_time()
    {
        var (t, admin, _, _, _, asset) = await Scene(OneLevel());
        await Transition(admin, asset, "submit");
        var e = Assert.Throws<PostgresException>(() => Sql(
            "INSERT INTO workflow_instances (id, tenant_id, asset_id, definition_name, definition_json, distinct_approvers, status, started_at, rounds) " +
            $"VALUES (gen_random_uuid(), '{t}', '{asset}', 'Second', '{{}}', true, 'running', now(), 0)"));
        Assert.Equal("23505", e.SqlState);
    }

    [Fact]
    public async Task Deleting_an_asset_under_review_cancels_its_workflow()
    {
        var (t, admin, _, path, _, asset) = await Scene(OneLevel());
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");
        Assert.Single(await Inbox(reviewer.Token));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/assets/{asset}", admin)).Status);
        Assert.Empty(await Inbox(reviewer.Token));
        Assert.Equal("cancelled", (await View(admin, asset)).GetProperty("status").GetString());
    }

    // ---- who may decide -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_is_scoped_an_approver_without_an_access_rule_for_the_folder_sees_no_task_and_cannot_decide()
    {
        var (t, admin, brand, path, _, asset) = await Scene(OneLevel());
        var elsewhere = await NewFolder(admin, "Other region");
        var inScope = await Reviewer(t, admin, ["Approver"], path);
        var wrongFolder = await Reviewer(t, admin, ["Approver"], await PathOf(admin, elsewhere));
        var noRule = await Reviewer(t, admin, ["Approver"], null);
        await Transition(admin, asset, "submit");

        var task = await TaskFor(inScope.Token, asset);
        Assert.Empty(await Inbox(wrongFolder.Token));
        Assert.Empty(await Inbox(noRule.Token));
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(wrongFolder.Token, task, "approve")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(noRule.Token, task, "approve")).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));
        var summary = (await Call(HttpMethod.Get, $"{Tasks}/summary", wrongFolder.Token)).Body;
        Assert.Equal(0, summary.GetProperty("pendingForMe").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Decide(inScope.Token, task, "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task Regional_review_works_through_the_attribute_limits_of_an_access_rule()
    {
        var (t, admin, brand, path, _, _) = await Scene(OneLevel());
        var north = await NewAsset(admin, brand, metadata: new { description = new { en = "n" }, regions = new[] { "north" } });
        var south = await NewAsset(admin, brand, metadata: new { description = new { en = "s" }, regions = new[] { "south" } });
        var northReviewer = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Approver"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", northReviewer)).Body.GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
        {
            principalType = "user",
            principalId = userId,
            scopeType = "folder",
            scopeValue = path,
            attributes = new { region = new[] { "north" } },
            permissions = new[] { "assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review" },
        })).Status);
        await Transition(admin, north, "submit");
        await Transition(admin, south, "submit");

        var items = await Inbox(northReviewer);
        Assert.Equal([north], items.Select(i => i.GetProperty("assetId").GetGuid()));
        var southTask = Guid.Parse(Call(t, $"SELECT id FROM workflow_tasks WHERE asset_id = '{south}'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(northReviewer, southTask, "approve")).Status);
    }

    private string Call(Guid tenant, string sql)
    {
        using var c = Pg.RawAppConnection(tenant);
        using var cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar()!.ToString()!;
    }

    [Fact]
    public async Task A_task_is_decided_only_by_its_assignees_by_role_or_by_name_and_deciding_needs_the_decide_permission()
    {
        var (t, admin, _, _, _, _) = await Scene(OneLevel());
        var folder = await NewFolder(admin, "Named");
        var path = await PathOf(admin, folder);
        string[] full = ["assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review"];
        var named = await Reviewer(t, admin, ["Viewer"], path, full);               // holds only Viewer, but is named in the workflow
        var otherNamed = await Reviewer(t, admin, ["Viewer"], path, full);          // named in nothing
        var byName = await NewWorkflow(admin, "By name", Def("review", [S("review", ["Approver"], users: [named.UserId])]));
        await SetDefault(admin, folder, byName);

        var asset = await NewAsset(admin, folder);
        await Transition(admin, asset, "submit");
        var editorWithRule = await Reviewer(t, admin, ["Editor"], path, full);      // scoped, but neither named nor in the Approver role
        var approverByRole = await Reviewer(t, admin, ["Approver"], path, full);    // assigned through the role
        var approverNoDecide = await Reviewer(t, admin, ["Approver"], path, ["assets.approve", "assets.read", "assets.read.in_review"]);   // may approve here but lacks workflow.tasks.decide

        var task = Guid.Parse(Call(t, $"SELECT id FROM workflow_tasks WHERE asset_id = '{asset}'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(editorWithRule.Token, task, "approve")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(otherNamed.Token, task, "approve")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(approverNoDecide.Token, task, "approve")).Status);
        Assert.Empty(await Inbox(editorWithRule.Token));
        Assert.Empty(await Inbox(otherNamed.Token));
        Assert.Empty(await Inbox(approverNoDecide.Token));                         // assigned and in scope, but not allowed to decide: not shown
        Assert.Single(await Inbox(approverByRole.Token));                          // by role
        Assert.Single(await Inbox(named.Token));                                   // by name, though the user holds only Viewer
        Assert.Equal("in_review", await StatusOf(admin, asset));

        var done = await Decide(named.Token, task, "approve");
        Assert.Equal(HttpStatusCode.OK, done.Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
        Assert.Equal([named.UserId], done.Body.GetProperty("assigneeUserIds").EnumerateArray().Select(x => x.GetGuid()));
    }

    [Fact]
    public async Task A_missing_task_is_404_and_deciding_requires_authentication()
    {
        var (_, admin, _) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(admin, Guid.NewGuid(), "approve")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Decide("", Guid.NewGuid(), "approve")).Status);
    }

    [Fact]
    public async Task A_decided_task_cannot_be_decided_again()
    {
        var (t, admin, _, path, _, asset) = await Scene(TwoLevel());
        var one = await Reviewer(t, admin, ["Approver"], path);
        var two = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");
        var task = await TaskFor(one.Token, asset);
        Assert.Equal(HttpStatusCode.OK, (await Decide(one.Token, task, "approve")).Status);
        var again = await Decide(two.Token, task, "reject", "too late");
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Contains("already", again.Body.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(one.Token, task, "approve")).Status);
    }

    // ---- parallel approvals and routing -------------------------------------------------------------------------

    [Fact]
    public async Task An_all_step_needs_every_task_to_approve_and_any_rejection_rejects_the_step_at_once()
    {
        var def = Def("review", [S("review", ["Approver", "Librarian"], mode: "all")]);
        var (t, admin, _, path, _, asset) = await Scene(def);
        var approver = await Reviewer(t, admin, ["Approver"], path);
        var librarian = await Reviewer(t, admin, ["Librarian"], path);
        await Transition(admin, asset, "submit");

        Assert.Equal(2, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{asset}' AND status = 'pending'"));
        var view = await View(admin, asset);
        Assert.Equal(2, view.GetProperty("steps")[0].GetProperty("tasks").GetArrayLength());
        Assert.Equal("any", "any");
        var mine = Assert.Single(await Inbox(approver.Token));
        Assert.Equal(["Approver"], Strings(mine.GetProperty("assigneeRoles")));   // each parallel task is addressed to one assignee

        Assert.Equal(HttpStatusCode.OK, (await Decide(approver.Token, mine.GetProperty("id").GetGuid(), "approve")).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));                   // one of two is not enough
        Assert.Equal("active", StepStates(await View(admin, asset))[0]);

        Assert.Equal(HttpStatusCode.OK, (await Decide(librarian.Token, await TaskFor(librarian.Token, asset), "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));

        // A rejection in an all step settles it immediately and cancels the other open task.
        var second = await NewAsset(admin, Id((await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body, "folderId"));
        await Transition(admin, second, "submit");
        var approverTask = await TaskFor(approver.Token, second);
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver.Token, approverTask, "reject", "No.")).Status);
        Assert.Equal("draft", await StatusOf(admin, second));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{second}' AND status = 'cancelled'"));
        Assert.Empty(await Inbox(librarian.Token, "?status=pending"));
    }

    [Fact]
    public async Task Concurrent_decisions_on_one_any_task_have_one_winner_and_one_conflict()
    {
        var (t, admin, _, path, _, asset) = await Scene(OneLevel());
        var one = await Reviewer(t, admin, ["Approver"], path);
        var two = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");
        var task = await TaskFor(one.Token, asset);

        var results = await Task.WhenAll(Decide(one.Token, task, "approve"), Decide(two.Token, task, "approve", "me too"));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.Status).Order());
        Assert.Equal("approved", await StatusOf(admin, asset));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{asset}' AND status = 'approved'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.task.completed'"));
    }

    [Fact]
    public async Task The_last_two_parallel_approvals_arriving_together_both_succeed_and_the_step_completes_exactly_once()
    {
        var def = Def("review", [S("review", ["Approver", "Librarian"], mode: "all")]);
        var (t, admin, brand, path, _, _) = await Scene(def);
        var approver = await Reviewer(t, admin, ["Approver"], path);
        var librarian = await Reviewer(t, admin, ["Librarian"], path);
        var assets = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var a = await NewAsset(admin, brand, title: $"Race {i}");
            await Transition(admin, a, "submit");
            assets.Add(a);
        }
        var pairs = new List<(Guid A, Guid B)>();
        foreach (var a in assets) pairs.Add((await TaskFor(approver.Token, a), await TaskFor(librarian.Token, a)));

        var all = await Task.WhenAll(pairs.SelectMany(p => new[] { Decide(approver.Token, p.A, "approve"), Decide(librarian.Token, p.B, "approve") }));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        foreach (var a in assets) Assert.Equal("approved", await StatusOf(admin, a));
        Assert.Equal(6, Count(t, "SELECT count(*) FROM workflow_instances WHERE status = 'approved'"));
    }

    [Fact]
    public async Task A_rejection_can_send_the_asset_back_to_an_earlier_step_which_gets_new_tasks()
    {
        var def = Def("draft-review", [S("draft-review", ["Approver"], onApprove: "legal", onReject: "reject"), S("legal", ["Librarian"], onApprove: "complete", onReject: "draft-review")]);
        var (t, admin, _, path, _, asset) = await Scene(def);
        var approver = await Reviewer(t, admin, ["Approver"], path);
        var librarian = await Reviewer(t, admin, ["Librarian"], path);
        await Transition(admin, asset, "submit");

        Assert.Equal(HttpStatusCode.OK, (await Decide(approver.Token, await TaskFor(approver.Token, asset), "approve")).Status);
        var legalTask = await TaskFor(librarian.Token, asset);
        var bounced = await Decide(librarian.Token, legalTask, "reject", "Needs the disclaimer.");
        Assert.Equal(HttpStatusCode.OK, bounced.Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));       // still under review, back at step one

        var view = await View(admin, asset);
        Assert.Equal("running", view.GetProperty("status").GetString());
        Assert.Equal(["active", "done"], StepStates(view));
        var firstStep = view.GetProperty("steps")[0].GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(2, firstStep.Count);                                // the original (approved) and the new round's task
        Assert.Equal(["approved", "pending"], firstStep.Select(x => x.GetProperty("status").GetString()!).Order());
        Assert.Equal(3, Count(t, $"SELECT max(round) FROM workflow_tasks WHERE asset_id = '{asset}' AND step_id = 'draft-review'"));   // activations are numbered per instance: review, legal, review again

        // The same approver may approve the step again (it is the same step), and it runs on to the end.
        var again = await TaskFor(approver.Token, asset);
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver.Token, again, "approve")).Status);
        var finalTask = (await Inbox(librarian.Token)).Single(x => x.GetProperty("stepId").GetString() == "legal");
        Assert.Equal(HttpStatusCode.OK, (await Decide(librarian.Token, finalTask.GetProperty("id").GetGuid(), "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task With_distinct_approvers_one_person_cannot_approve_two_steps_and_without_it_they_can()
    {
        async Task<(HttpStatusCode First, HttpStatusCode Second, int Visible)> Run(bool distinct)
        {
            var (t, admin, _, path, _, asset) = await Scene(TwoLevel(distinct: distinct));
            var both = await Reviewer(t, admin, ["Approver", "Librarian"], path);
            await Transition(admin, asset, "submit");
            var first = await Decide(both.Token, await TaskFor(both.Token, asset), "approve");
            var visible = (await Inbox(both.Token)).Count;   // the second step's task, if this person may still see it
            var pending = Guid.Parse(Call(t, $"SELECT id FROM workflow_tasks WHERE asset_id = '{asset}' AND status = 'pending'"));
            var second = await Decide(both.Token, pending, "approve");
            return (first.Status, second.Status, visible);
        }

        var strict = await Run(distinct: true);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Forbidden, 0), strict);   // forbidden to decide, and the task is hidden from their inbox

        var relaxed = await Run(distinct: false);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, 1), relaxed);
    }

    [Fact]
    public async Task One_person_holding_both_roles_cannot_stand_in_for_both_parallel_tasks_of_an_all_step()
    {
        var (t, admin, _, path, _, asset) = await Scene(Def("review", [S("review", ["Approver", "Librarian"], mode: "all")]));
        var both = await Reviewer(t, admin, ["Approver", "Librarian"], path);
        await Transition(admin, asset, "submit");
        var tasks = await Inbox(both.Token);
        Assert.Equal(2, tasks.Count);
        Assert.Equal(HttpStatusCode.OK, (await Decide(both.Token, tasks[0].GetProperty("id").GetGuid(), "approve")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(both.Token, tasks[1].GetProperty("id").GetGuid(), "approve")).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));
    }

    // ---- auto publish --------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_auto_publish_the_final_approval_also_publishes()
    {
        var (t, admin, _, path, _, asset) = await Scene(Def("review", [S("review")], autoPublish: true));
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");
        Assert.Equal(HttpStatusCode.OK, (await Decide(reviewer.Token, await TaskFor(reviewer.Token, asset), "approve")).Status);
        Assert.Equal("published", await StatusOf(admin, asset));
        var view = await View(admin, asset);
        Assert.Equal("approved", view.GetProperty("status").GetString());
        Assert.Equal("published", view.GetProperty("outcomeStatus").GetString());
        Assert.True(view.GetProperty("autoPublish").GetBoolean());
        Assert.Equal("Published automatically by workflow 'Test flow'.", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("statusReason").GetString());
    }

    [Fact]
    public async Task Auto_publish_that_fails_a_publish_guard_leaves_the_asset_approved_and_says_why()
    {
        var (t, admin, _, path, _, asset) = await Scene(Def("review", [S("review")], autoPublish: true));
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");

        // A field becomes required after submission, and this asset does not have it.
        var fields = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray()
            .Select(f => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText())!).ToList();
        foreach (var f in fields.Where(f => f["name"].GetString() == "channels")) f["required"] = JsonDocument.Parse("true").RootElement;
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields })).Status);

        Assert.Equal(HttpStatusCode.OK, (await Decide(reviewer.Token, await TaskFor(reviewer.Token, asset), "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
        var view = await View(admin, asset);
        Assert.Equal("approved", view.GetProperty("status").GetString());
        Assert.Equal("approved", view.GetProperty("outcomeStatus").GetString());
        Assert.Contains("not published automatically", view.GetProperty("outcomeReason").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transition(admin, asset, "publish")).Status);   // the guard still holds for a manual publish
    }

    // ---- snapshot ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_workflow_never_changes_an_instance_that_is_already_running()
    {
        var (t, admin, _, path, wf, asset) = await Scene(TwoLevel());
        var approver = await Reviewer(t, admin, ["Approver"], path);
        var librarian = await Reviewer(t, admin, ["Librarian"], path);
        await Transition(admin, asset, "submit");

        // Rewrite the workflow: one step, completing straight away, assigned to someone else, with a different name.
        var edit = await Call(HttpMethod.Put, $"{Workflows}/{wf}", admin, new { name = "Renamed flow", definition = Def("only", [S("only", ["Editor"])]) });
        Assert.Equal(HttpStatusCode.OK, edit.Status);

        var view = await View(admin, asset);
        Assert.Equal("Test flow", view.GetProperty("workflowName").GetString());
        Assert.Equal(["review", "final"], view.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!));
        Assert.Equal("Test flow", (await Inbox(approver.Token)).Single().GetProperty("workflowName").GetString());

        // It still runs by the old rules: two levels, approvers then librarians.
        Assert.Equal(HttpStatusCode.OK, (await Decide(approver.Token, await TaskFor(approver.Token, asset), "approve")).Status);
        Assert.Equal("in_review", await StatusOf(admin, asset));
        Assert.Equal(HttpStatusCode.OK, (await Decide(librarian.Token, await TaskFor(librarian.Token, asset), "approve")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));

        // A later submission uses the edited definition.
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "withdraw")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Transition(admin, asset, "submit")).Status);
        var next = await View(admin, asset);
        Assert.Equal("Renamed flow", next.GetProperty("workflowName").GetString());
        Assert.Equal(["only"], next.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM workflow_instances WHERE asset_id = '{asset}' AND definition_json::text NOT LIKE '%final%'"));
    }

    // ---- inbox, summary, per-asset view ------------------------------------------------------------------------

    [Fact]
    public async Task The_asset_view_shows_each_step_waiting_active_or_done_with_its_tasks()
    {
        var (t, admin, _, path, _, asset) = await Scene(TwoLevel());
        var approver = await Reviewer(t, admin, ["Approver"], path);
        var librarian = await Reviewer(t, admin, ["Librarian"], path);
        Assert.Equal(JsonValueKind.Null, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/workflow", admin)).Body.GetProperty("instance").ValueKind);

        await Transition(As(t, "Editor"), asset, "submit");
        var view = await View(admin, asset);
        Assert.Equal("Test flow", view.GetProperty("workflowName").GetString());
        Assert.Equal("running", view.GetProperty("status").GetString());
        Assert.False(view.GetProperty("startedBy").ValueKind == JsonValueKind.Null);
        Assert.Equal(["active", "waiting"], StepStates(view));
        var steps = view.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["review", "final"], steps.Select(s => s.GetProperty("id").GetString()!));
        Assert.Equal(["Review", "Final"], steps.Select(s => s.GetProperty("name").GetString()!));
        Assert.Equal([48, 24], steps.Select(s => s.GetProperty("slaHours").GetInt32()));
        Assert.Equal("any", steps[0].GetProperty("mode").GetString());
        Assert.Equal(1, steps[0].GetProperty("tasks").GetArrayLength());
        Assert.Equal(0, steps[1].GetProperty("tasks").GetArrayLength());

        await Decide(approver.Token, await TaskFor(approver.Token, asset), "approve", "ok");
        view = await View(admin, asset);
        Assert.Equal(["done", "active"], StepStates(view));
        var done = view.GetProperty("steps")[0].GetProperty("tasks")[0];
        Assert.Equal("approve", done.GetProperty("decision").GetString());
        Assert.Equal("ok", done.GetProperty("comment").GetString());
        Assert.StartsWith("Reviewer", done.GetProperty("decidedByName").GetString());

        // Reading the view needs read access to the asset.
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/workflow", As(t, "Viewer"))).Status);   // a Viewer cannot see an asset in review
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{Guid.NewGuid()}/workflow", admin)).Status);
    }

    [Fact]
    public async Task The_inbox_lists_open_and_decided_tasks_filters_by_status_pages_and_restricts_the_all_view_to_managers()
    {
        var (t, admin, brand, path, _, _) = await Scene(OneLevel());
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        var assets = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var a = await NewAsset(admin, brand, title: $"Item {i}");
            await Transition(admin, a, "submit");
            assets.Add(a);
        }

        var open = await Inbox(reviewer.Token);
        Assert.Equal(5, open.Count);
        Assert.Equal(assets.AsEnumerable().Reverse(), open.Select(x => x.GetProperty("assetId").GetGuid()));   // newest first
        Assert.Equal("Item 4", open[0].GetProperty("assetTitle").GetString());
        Assert.Equal("image", open[0].GetProperty("assetType").GetString());

        var page1 = (await Call(HttpMethod.Get, $"{Tasks}?limit=2", reviewer.Token)).Body;
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        var page2 = (await Call(HttpMethod.Get, $"{Tasks}?limit=2&cursor={page1.GetProperty("nextCursor").GetString()}", reviewer.Token)).Body;
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        var page3 = (await Call(HttpMethod.Get, $"{Tasks}?limit=2&cursor={page2.GetProperty("nextCursor").GetString()}", reviewer.Token)).Body;
        Assert.Equal(1, page3.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page3.GetProperty("nextCursor").ValueKind);

        await Decide(reviewer.Token, open[0].GetProperty("id").GetGuid(), "approve");
        await Decide(reviewer.Token, open[1].GetProperty("id").GetGuid(), "reject", "No");
        Assert.Equal(3, (await Inbox(reviewer.Token)).Count);
        Assert.Equal(3, (await Inbox(reviewer.Token, "?status=pending")).Count);
        var decided = await Inbox(reviewer.Token, "?status=decided");
        Assert.Equal(2, decided.Count);
        Assert.Equal(new[] { "approve", "reject" }, decided.Select(x => x.GetProperty("decision").GetString()!).Order());
        Assert.Equal(5, (await Inbox(reviewer.Token, "?status=all")).Count);

        // Everyone's tasks: managers only.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"{Tasks}?assignee=all", reviewer.Token)).Status);
        Assert.Equal(5, (await Inbox(admin, "?assignee=all&status=all")).Count);
        Assert.Empty(await Inbox(admin));                       // the Admin is not an assignee, so their own inbox is empty
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Tasks}?status=bogus", admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Tasks}?assignee=everyone", admin)).Status);
    }

    [Fact]
    public async Task The_summary_counts_open_and_overdue_tasks_for_me_and_gives_tenant_totals_and_sla_attainment_to_managers_only()
    {
        var (t, admin, brand, path, _, _) = await Scene(OneLevel(sla: 24));
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        var viewer = As(t, "Viewer");

        var empty = (await Call(HttpMethod.Get, $"{Tasks}/summary", admin)).Body;
        Assert.Equal(0, empty.GetProperty("pendingTotal").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("slaAttainmentPercent").ValueKind);   // nothing completed yet

        var assets = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var a = await NewAsset(admin, brand, title: $"S{i}");
            await Transition(admin, a, "submit");
            assets.Add(a);
        }
        // One task is already past its due time (the SLA job has not run, but the due time is what counts).
        Sql($"UPDATE workflow_tasks SET due_at = now() - interval '2 hours' WHERE asset_id = '{assets[0]}'");

        var mine = (await Call(HttpMethod.Get, $"{Tasks}/summary", reviewer.Token)).Body;
        Assert.Equal((4, 1), (mine.GetProperty("pendingForMe").GetInt32(), mine.GetProperty("overdueForMe").GetInt32()));
        Assert.Equal(4, mine.GetProperty("pendingTotal").GetInt32());                            // the Approver role holds analytics.read
        var none = (await Call(HttpMethod.Get, $"{Tasks}/summary", viewer)).Body;
        Assert.Equal((0, 0), (none.GetProperty("pendingForMe").GetInt32(), none.GetProperty("overdueForMe").GetInt32()));
        foreach (var field in new[] { "pendingTotal", "overdueTotal", "completed30d", "completedOnTime30d", "slaAttainmentPercent" })
            Assert.Equal(JsonValueKind.Null, none.GetProperty(field).ValueKind);                 // tenant figures are not for everyone

        var all = (await Call(HttpMethod.Get, $"{Tasks}/summary", admin)).Body;
        Assert.Equal((4, 1), (all.GetProperty("pendingTotal").GetInt32(), all.GetProperty("overdueTotal").GetInt32()));
        Assert.Equal((0, 0), (all.GetProperty("pendingForMe").GetInt32(), all.GetProperty("overdueForMe").GetInt32()));
        Assert.Equal(0, all.GetProperty("completed30d").GetInt32());

        // Two decisions: one on time, one late (decided after its due time passed).
        await Decide(reviewer.Token, await TaskFor(reviewer.Token, assets[1]), "approve");
        await Decide(reviewer.Token, await TaskFor(reviewer.Token, assets[0]), "approve");
        var after = (await Call(HttpMethod.Get, $"{Tasks}/summary", admin)).Body;
        Assert.Equal((2, 0), (after.GetProperty("pendingTotal").GetInt32(), after.GetProperty("overdueTotal").GetInt32()));
        Assert.Equal((2, 1), (after.GetProperty("completed30d").GetInt32(), after.GetProperty("completedOnTime30d").GetInt32()));
        Assert.Equal(50.0, after.GetProperty("slaAttainmentPercent").GetDouble());
        var librarian = (await Call(HttpMethod.Get, $"{Tasks}/summary", As(t, "Librarian"))).Body;   // analytics.read holders see the totals too
        Assert.Equal(2, librarian.GetProperty("pendingTotal").GetInt32());
    }

    // ---- SLA ---------------------------------------------------------------------------------------------------

    private sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }

    private IHost SchedulerHost(FakeClock clock)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DAM_S3_ENDPOINT"] = Pg.S3Endpoint,
            ["DAM_S3_ACCESS_KEY"] = Pg.S3AccessKey,
            ["DAM_S3_SECRET_KEY"] = Pg.S3SecretKey,
        }).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(Pg.AppConnection);
        b.Services.AddDamStorage(cfg);
        b.Services.AddDamRetentionScheduling(Pg.SystemConnection);
        b.Services.AddSingleton<IClock>(clock);   // the last registration wins: the job sees the test's time
        return b.Build();
    }

    private static async Task RunSlaJobAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetServices<IScheduledJob>().First(j => j.Key == WorkflowSlaJob.JobKey);
        await job.RunAsync(new Dictionary<string, string>(), default);
    }

    [Fact]
    public async Task The_sla_job_marks_an_overdue_task_once_adds_the_escalation_targets_and_raises_an_event()
    {
        var (t, admin, _, path, _, asset) = await Scene(OneLevel(sla: 2, escalate: ["Admin"]));
        var reviewer = await Reviewer(t, admin, ["Approver"], path);
        await Transition(admin, asset, "submit");
        var task = await TaskFor(reviewer.Token, asset);
        var due = (await Inbox(reviewer.Token)).Single().GetProperty("dueAt").GetDateTimeOffset();
        var clock = new FakeClock(due.AddMinutes(-30));
        using var host = SchedulerHost(clock);

        // Before the due time nothing happens.
        await RunSlaJobAsync(host);
        Assert.False((await Inbox(reviewer.Token)).Single().GetProperty("slaBreached").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(admin, task, "approve")).Status);   // an Admin is not an assignee... yet

        // Just after: the breach is recorded and visible through the API.
        clock.UtcNow = due.AddSeconds(5);
        await RunSlaJobAsync(host);
        var item = (await Inbox(reviewer.Token)).Single();
        Assert.True(item.GetProperty("slaBreached").GetBoolean());
        Assert.Equal(clock.UtcNow, item.GetProperty("slaBreachedAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Equal(["Approver", "Admin"], Strings(item.GetProperty("assigneeRoles")));
        Assert.True(item.GetProperty("escalated").GetBoolean());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.sla.breached'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'workflow.sla.breached'"));
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Tasks}/summary", reviewer.Token)).Body.GetProperty("overdueForMe").GetInt32());
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Tasks}/summary", admin)).Body.GetProperty("overdueTotal").GetInt32());

        // Running again changes nothing: a breach is recorded once.
        clock.UtcNow = due.AddHours(1);
        await RunSlaJobAsync(host);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'workflow.sla.breached'"));

        // The escalation target can now see and decide the task.
        Assert.Single(await Inbox(admin));
        Assert.Equal(HttpStatusCode.OK, (await Decide(admin, task, "approve", "Escalated and approved.")).Status);
        Assert.Equal("approved", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task The_sla_job_covers_every_tenant_and_ignores_decided_tasks_and_steps_without_an_sla()
    {
        var one = await Scene(OneLevel(sla: 1));
        var two = await Scene(OneLevel(sla: 1));
        var noSla = await Scene(OneLevel(sla: null));
        var decided = await NewAsset(one.Admin, one.Folder, title: "Decided before the deadline");
        var r1 = await Reviewer(one.Tenant, one.Admin, ["Approver"], one.Path);
        foreach (var s in new[] { one, two, noSla }) await Transition(s.Admin, s.Asset, "submit");
        await Transition(one.Admin, decided, "submit");
        await Decide(r1.Token, await TaskFor(r1.Token, decided), "approve");

        var clock = new FakeClock(DateTimeOffset.UtcNow.AddHours(3));
        using var host = SchedulerHost(clock);
        await RunSlaJobAsync(host);

        Assert.Equal(1, Count(one.Tenant, "SELECT count(*) FROM workflow_tasks WHERE sla_breached_at IS NOT NULL"));
        Assert.Equal(1, Count(two.Tenant, "SELECT count(*) FROM workflow_tasks WHERE sla_breached_at IS NOT NULL"));
        Assert.Equal(0, Count(noSla.Tenant, "SELECT count(*) FROM workflow_tasks WHERE sla_breached_at IS NOT NULL"));
        Assert.Equal(0, Count(one.Tenant, $"SELECT count(*) FROM workflow_tasks WHERE asset_id = '{decided}' AND sla_breached_at IS NOT NULL"));
    }

    // ---- isolation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Workflow_data_is_isolated_per_tenant_by_the_api_and_by_row_level_security()
    {
        var a = await Scene(OneLevel());
        var b = await Scene(OneLevel());
        var reviewerA = await Reviewer(a.Tenant, a.Admin, ["Approver"], a.Path);
        await Transition(a.Admin, a.Asset, "submit");
        var taskA = await TaskFor(reviewerA.Token, a.Asset);

        // Through the API, tenant B cannot see or touch tenant A's workflow, task or asset view.
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Workflows}/{a.Workflow}", b.Admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(b.Admin, taskA, "approve")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{a.Asset}/workflow", b.Admin)).Status);
        Assert.DoesNotContain((await Call(HttpMethod.Get, Workflows, b.Admin)).Body.EnumerateArray(), w => w.GetProperty("id").GetGuid() == a.Workflow);
        Assert.Empty(await Inbox(b.Admin, "?assignee=all&status=all"));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Workflows}/{a.Workflow}", b.Admin)).Status);

        // Underneath, Postgres itself hides the rows: the app role sees only its own tenant's, and none with no tenant set.
        foreach (var table in new[] { "workflow_definitions", "workflow_instances", "workflow_tasks" })
        {
            Assert.True(Count(a.Tenant, $"SELECT count(*) FROM {table}") > 0, table);
            Assert.Equal(0, Count(b.Tenant, $"SELECT count(*) FROM {table} WHERE tenant_id = '{a.Tenant}'"));
            using var none = Pg.RawAppConnection(null);
            using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", none);
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        await Call(HttpMethod.Put, $"{Workflows}/bindings", a.Admin, new { bindings = new object[] { new { workflowId = a.Workflow, assetType = "image", region = (string?)null } } });
        Assert.Equal(0, Count(b.Tenant, $"SELECT count(*) FROM workflow_bindings WHERE tenant_id = '{a.Tenant}'"));
        Assert.Equal(1, Count(a.Tenant, "SELECT count(*) FROM workflow_bindings"));

        // A tenant cannot write another tenant's rows either.
        using var conn = Pg.RawAppConnection(b.Tenant);
        using var insert = new NpgsqlCommand($"INSERT INTO workflow_bindings (id, workflow_id, asset_type, tenant_id) VALUES (gen_random_uuid(), '{a.Workflow}', 'video', '{a.Tenant}')", conn);
        Assert.Throws<PostgresException>(() => insert.ExecuteNonQuery());
    }

    [Fact]
    public async Task The_system_role_can_only_read_tasks_which_is_all_the_sla_job_needs()
    {
        var s = await Scene(OneLevel(sla: 1));
        await Transition(s.Admin, s.Asset, "submit");
        await using var conn = new NpgsqlConnection(Pg.SystemConnection);
        await conn.OpenAsync();
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM workflow_tasks", conn))
            Assert.True(Convert.ToInt64(await read.ExecuteScalarAsync()) >= 1);   // across tenants
        foreach (var sql in new[]
        {
            "SELECT count(*) FROM workflow_instances", "SELECT count(*) FROM workflow_definitions",
            "UPDATE workflow_tasks SET status = 'approved'", "DELETE FROM workflow_tasks",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", e.SqlState);   // insufficient_privilege
        }
    }

    [Fact]
    public async Task The_app_role_can_never_delete_instances_or_tasks()
    {
        var s = await Scene(OneLevel());
        await Transition(s.Admin, s.Asset, "submit");
        foreach (var sql in new[] { "DELETE FROM workflow_tasks", "DELETE FROM workflow_instances" })
        {
            using var c = Pg.RawAppConnection(s.Tenant);
            using var cmd = new NpgsqlCommand(sql, c);
            var e = Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery());
            Assert.Equal("42501", e.SqlState);
        }
    }
}
