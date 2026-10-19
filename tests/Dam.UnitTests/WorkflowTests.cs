using System.Text.Json;
using Dam.Application.Content;
using Dam.Domain.Workflow;

namespace Dam.UnitTests;

/// <summary>The pure parts of the workflow engine (ADR-0030): definition validation, step resolution, tasks, the publish gate.</summary>
public sealed class WorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid Asset = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Alice = Guid.Parse("0197a000-0000-7000-8000-0000000000aa");
    private static readonly Guid Bob = Guid.Parse("0197a000-0000-7000-8000-0000000000bb");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static object Step(string id, string onApprove = "complete", string onReject = "reject", string mode = "any", object? sla = null, object? escalation = null, object? assignees = null, string? name = null) =>
        new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = name ?? id.ToUpperInvariant(),
            ["assignees"] = assignees ?? new { roles = new[] { "Approver" }, users = Array.Empty<Guid>() },
            ["mode"] = mode,
            ["slaHours"] = sla,
            ["escalation"] = escalation,
            ["onApprove"] = onApprove,
            ["onReject"] = onReject,
        };

    private static JsonElement Doc(string start, params object[] steps) =>
        JsonSerializer.SerializeToElement(new { version = 1, start, autoPublish = false, distinctApprovers = true, steps });

    private static WorkflowParseResult Parse(JsonElement e) => WorkflowDefinitionParser.Parse(e);

    private static WorkflowDef Valid(params object[] steps) =>
        Parse(Doc(((Dictionary<string, object?>)steps[0])["id"]!.ToString()!, steps)).Definition ?? throw new InvalidOperationException("expected a valid definition");

    private static string[] PathsOf(WorkflowParseResult r) => r.Errors.Select(e => e.Path).ToArray();

    // ---- validator: the happy path and defaults ---------------------------------------------------------------

    [Fact]
    public void A_two_step_definition_parses_with_every_field()
    {
        var user = Guid.NewGuid();
        var r = Parse(Doc("review",
            Step("review", onApprove: "final", sla: 48, escalation: new { to = new { roles = new[] { "Admin" }, users = new[] { user } } }),
            Step("final", mode: "all", assignees: new { roles = new[] { "Librarian", "Editor" }, users = new[] { user } })));
        Assert.True(r.IsValid);
        var def = r.Definition!;
        Assert.Equal("review", def.Start);
        Assert.False(def.AutoPublish);
        Assert.True(def.DistinctApprovers);
        Assert.Equal(2, def.Steps.Count);
        Assert.Equal(48, def.Steps[0].SlaHours);
        Assert.Equal(["Admin"], def.Steps[0].Escalation!.To.Roles);
        Assert.Equal([user], def.Steps[0].Escalation!.To.Users);
        Assert.Equal("all", def.Steps[1].Mode);
        Assert.Null(def.Steps[1].SlaHours);
        Assert.Null(def.Steps[1].Escalation);
        Assert.Equal(3, def.Steps[1].Assignees.Count);
    }

    [Fact]
    public void Optional_fields_default_and_unknown_properties_are_ignored()
    {
        var r = Parse(Json("""
            { "version": 1, "start": "a", "layout": { "a": { "x": 1, "y": 2 } },
              "steps": [ { "id": "a", "name": "A", "assignees": { "roles": ["Approver"] }, "onApprove": "complete", "onReject": "reject", "ui": 5 } ] }
            """));
        Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => $"{e.Path}: {e.Message}")));
        Assert.Equal("any", r.Definition!.Steps[0].Mode);
        Assert.True(r.Definition.DistinctApprovers);   // segregation of duties is on unless switched off
        Assert.False(r.Definition.AutoPublish);
        Assert.Null(r.Definition.Steps[0].SlaHours);
        Assert.Empty(r.Definition.Steps[0].Assignees.Users);
    }

    [Fact]
    public void A_single_step_that_completes_or_rejects_directly_is_valid()
    {
        Assert.True(Parse(Doc("only", Step("only"))).IsValid);
    }

    // ---- validator: structure --------------------------------------------------------------------------------

    [Fact]
    public void The_document_must_be_an_object_with_a_supported_version_a_start_and_steps()
    {
        Assert.Equal([""], PathsOf(WorkflowDefinitionParser.Parse(null)));
        Assert.Equal([""], PathsOf(Parse(Json("[]"))));
        var r = Parse(Json("{}"));
        Assert.Equal(["version", "start", "steps"], PathsOf(r));
        Assert.Equal(["version"], PathsOf(Parse(Json("""{"version":2,"start":"a","steps":[]}"""))).Take(1));
        Assert.Contains("steps", PathsOf(Parse(Json("""{"version":1,"start":"a","steps":[]}"""))));
        Assert.Contains("steps", PathsOf(Parse(Json("""{"version":1,"start":"a","steps":"nope"}"""))));
    }

    [Fact]
    public void A_workflow_has_between_one_and_twenty_steps()
    {
        var many = Enumerable.Range(0, 21).Select(i => Step($"s{i}", onApprove: i < 20 ? $"s{i + 1}" : "complete")).ToArray();
        var r = Parse(Doc("s0", many));
        Assert.Contains("steps", PathsOf(r));
        var twenty = Enumerable.Range(0, 20).Select(i => Step($"s{i}", onApprove: i < 19 ? $"s{i + 1}" : "complete")).ToArray();
        Assert.True(Parse(Doc("s0", twenty)).IsValid);
    }

    [Fact]
    public void Start_must_name_an_existing_step()
    {
        var r = Parse(Doc("nope", Step("a")));
        Assert.Equal(["start"], PathsOf(r));
        Assert.Contains("nope", r.Errors[0].Message);
    }

    [Theory]
    [InlineData("A")]            // upper case
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("way-too-long-way-too-long-way-too-long-way-too-long")]
    [InlineData("dots.not.allowed")]
    public void Step_ids_use_one_to_forty_lowercase_letters_digits_hyphens_and_underscores(string id)
    {
        var r = Parse(Doc("x", Step("x"), Step(id)));
        Assert.Contains("steps[1].id", PathsOf(r));
    }

    [Fact]
    public void Step_ids_are_unique_and_never_a_reserved_word()
    {
        Assert.Contains("steps[1].id", PathsOf(Parse(Doc("a", Step("a"), Step("a")))));
        Assert.Contains("steps[0].id", PathsOf(Parse(Doc("complete", Step("complete")))));
        Assert.Contains("steps[0].id", PathsOf(Parse(Doc("reject", Step("reject")))));
    }

    [Fact]
    public void A_step_needs_a_name_and_a_reasonable_one()
    {
        var noName = Json("""{"version":1,"start":"a","steps":[{"id":"a","assignees":{"roles":["Approver"]},"onApprove":"complete","onReject":"reject"}]}""");
        Assert.Equal(["steps[0].name"], PathsOf(Parse(noName)));
        Assert.Contains("steps[0].name", PathsOf(Parse(Doc("a", Step("a", name: new string('x', 101))))));
        Assert.Contains("steps[0]", PathsOf(Parse(Json("""{"version":1,"start":"a","steps":["a"]}"""))));
    }

    // ---- validator: assignees, mode, SLA, escalation ---------------------------------------------------------

    [Fact]
    public void Every_step_needs_at_least_one_assignee()
    {
        Assert.Equal(["steps[0].assignees"], PathsOf(Parse(Doc("a", Step("a", assignees: new { roles = Array.Empty<string>(), users = Array.Empty<Guid>() })))));
        Assert.Equal(["steps[0].assignees"], PathsOf(Parse(Doc("a", Step("a", assignees: new { })))));
        var missing = Json("""{"version":1,"start":"a","steps":[{"id":"a","name":"A","onApprove":"complete","onReject":"reject"}]}""");
        Assert.Equal(["steps[0].assignees"], PathsOf(Parse(missing)));
    }

    [Fact]
    public void Assignees_are_role_names_and_user_guids_without_repeats()
    {
        var r = Parse(Doc("a", Step("a", assignees: new { roles = new[] { "Approver", "Approver", "" }, users = new[] { "not-a-guid", Guid.Empty.ToString() } })));
        Assert.Equal(["steps[0].assignees.roles[1]", "steps[0].assignees.roles[2]", "steps[0].assignees.users[0]", "steps[0].assignees.users[1]"], PathsOf(r));
        var dup = Guid.NewGuid();
        Assert.Equal(["steps[0].assignees.users[1]"], PathsOf(Parse(Doc("a", Step("a", assignees: new { users = new[] { dup, dup } })))));
        Assert.Equal(["steps[0].assignees.roles"], PathsOf(Parse(Doc("a", Step("a", assignees: new { roles = "Approver" })))));
    }

    [Theory]
    [InlineData("any", true)]
    [InlineData("all", true)]
    [InlineData("both", false)]
    [InlineData("ALL", false)]
    public void Mode_is_any_or_all(string mode, bool valid) =>
        Assert.Equal(valid, Parse(Doc("a", Step("a", mode: mode))).IsValid);

    [Theory]
    [InlineData(1, true)]
    [InlineData(8760, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(8761, false)]
    public void Sla_hours_are_one_to_8760(int hours, bool valid) =>
        Assert.Equal(valid, Parse(Doc("a", Step("a", sla: hours))).IsValid);

    [Fact]
    public void Sla_hours_may_be_null_but_not_fractional_or_text()
    {
        Assert.True(Parse(Doc("a", Step("a", sla: null))).IsValid);
        Assert.Equal(["steps[0].slaHours"], PathsOf(Parse(Doc("a", Step("a", sla: 1.5)))));
        Assert.Equal(["steps[0].slaHours"], PathsOf(Parse(Doc("a", Step("a", sla: "soon")))));
    }

    [Fact]
    public void An_escalation_needs_at_least_one_target()
    {
        Assert.Equal(["steps[0].escalation.to"], PathsOf(Parse(Doc("a", Step("a", escalation: new { to = new { roles = Array.Empty<string>() } })))));
        Assert.Equal(["steps[0].escalation.to"], PathsOf(Parse(Doc("a", Step("a", escalation: new { })))));
        Assert.Equal(["steps[0].escalation.to.roles[0]"], PathsOf(Parse(Doc("a", Step("a", escalation: new { to = new { roles = new[] { "" } } })))));
        Assert.True(Parse(Doc("a", Step("a", escalation: new { to = new { users = new[] { Alice } } }))).IsValid);
    }

    [Fact]
    public void Flags_must_be_booleans()
    {
        var r = Parse(Json("""{"version":1,"start":"a","autoPublish":"yes","distinctApprovers":1,"steps":[{"id":"a","name":"A","assignees":{"roles":["Approver"]},"onApprove":"complete","onReject":"reject"}]}"""));
        Assert.Equal(["autoPublish", "distinctApprovers"], PathsOf(r));
    }

    // ---- validator: routing graph ----------------------------------------------------------------------------

    [Fact]
    public void Routing_targets_must_be_a_step_or_their_reserved_word()
    {
        var r = Parse(Doc("a", Step("a", onApprove: "ghost", onReject: "ghost")));
        Assert.Equal(["steps[0].onApprove", "steps[0].onReject"], PathsOf(r));
        // The reserved word for one side is not valid on the other.
        Assert.Equal(["steps[0].onApprove"], PathsOf(Parse(Doc("a", Step("a", onApprove: "reject")))));
        Assert.Equal(["steps[0].onReject"], PathsOf(Parse(Doc("a", Step("a", onReject: "complete")))));
        var missing = Json("""{"version":1,"start":"a","steps":[{"id":"a","name":"A","assignees":{"roles":["Approver"]}}]}""");
        Assert.Equal(["steps[0].onApprove", "steps[0].onReject"], PathsOf(Parse(missing)));
    }

    [Fact]
    public void Every_step_must_be_reachable_from_the_start()
    {
        var r = Parse(Doc("a", Step("a"), Step("orphan")));
        Assert.Equal(["steps[1].id"], PathsOf(r));
        Assert.Contains("orphan", r.Errors[0].Message);
    }

    [Fact]
    public void A_step_reachable_only_by_rejection_is_reachable()
    {
        Assert.True(Parse(Doc("a", Step("a", onReject: "rework"), Step("rework", onApprove: "complete", onReject: "reject"))).IsValid);
    }

    [Fact]
    public void Approvals_must_not_loop_but_rejections_may()
    {
        var self = Parse(Doc("a", Step("a", onApprove: "a")));
        Assert.Equal(["steps[0].onApprove"], PathsOf(self));
        var two = Parse(Doc("a", Step("a", onApprove: "b"), Step("b", onApprove: "a")));
        Assert.Single(two.Errors);
        Assert.Contains("a -> b -> a", two.Errors[0].Message);
        // a rejection that sends the work back to an earlier step is the normal rework loop
        Assert.True(Parse(Doc("a", Step("a", onApprove: "b"), Step("b", onReject: "a"))).IsValid);
        // a cycle that the start step merely leads into is still a cycle
        Assert.Single(Parse(Doc("a", Step("a", onApprove: "b"), Step("b", onApprove: "c"), Step("c", onApprove: "b"))).Errors);
    }

    // ---- engine: tasks a step creates -----------------------------------------------------------------------

    [Fact]
    public void An_any_step_offers_one_task_to_every_assignee_and_an_all_step_one_per_assignee()
    {
        var assignees = new { roles = new[] { "Approver", "Librarian" }, users = new[] { Alice } };
        var any = Valid(Step("a", assignees: assignees)).Steps[0];
        var slot = Assert.Single(WorkflowEngine.Slots(any));
        Assert.Equal(["Approver", "Librarian"], slot.Roles);
        Assert.Equal([Alice], slot.Users);

        var all = Valid(Step("a", mode: "all", assignees: assignees)).Steps[0];
        var slots = WorkflowEngine.Slots(all);
        Assert.Equal(3, slots.Count);
        Assert.Equal([["Approver"], ["Librarian"], []], slots.Select(s => s.Roles.ToArray()));
        Assert.Equal(Alice, slots[2].Users.Single());
    }

    [Fact]
    public void Due_time_is_the_creation_time_plus_the_sla()
    {
        Assert.Equal(Now.AddHours(48), WorkflowEngine.DueAt(Valid(Step("a", sla: 48)).Steps[0], Now));
        Assert.Null(WorkflowEngine.DueAt(Valid(Step("a")).Steps[0], Now));
    }

    // ---- engine: what a step's decisions add up to ---------------------------------------------------------

    private static StepDef OneStep(string mode, string onApprove = "complete", string onReject = "reject") =>
        new("s", "S", new AssigneeSet(["Approver"], []), mode, null, null, onApprove, onReject);

    [Fact]
    public void An_any_step_is_approved_by_its_first_approval_and_rejected_by_any_rejection()
    {
        var step = OneStep("any");
        Assert.Equal(AdvanceKind.Wait, WorkflowEngine.Resolve(step, [TaskStatuses.Pending]).Kind);
        Assert.Equal(AdvanceKind.Complete, WorkflowEngine.Resolve(step, [TaskStatuses.Approved]).Kind);
        Assert.Equal(AdvanceKind.Reject, WorkflowEngine.Resolve(step, [TaskStatuses.Rejected]).Kind);
    }

    [Fact]
    public void An_all_step_waits_for_every_task_and_any_rejection_rejects_it_at_once()
    {
        var step = OneStep("all");
        Assert.Equal(AdvanceKind.Wait, WorkflowEngine.Resolve(step, [TaskStatuses.Approved, TaskStatuses.Pending]).Kind);
        Assert.Equal(AdvanceKind.Complete, WorkflowEngine.Resolve(step, [TaskStatuses.Approved, TaskStatuses.Approved]).Kind);
        Assert.Equal(AdvanceKind.Reject, WorkflowEngine.Resolve(step, [TaskStatuses.Approved, TaskStatuses.Rejected, TaskStatuses.Pending]).Kind);
        Assert.Equal(AdvanceKind.Reject, WorkflowEngine.Resolve(step, [TaskStatuses.Pending, TaskStatuses.Rejected]).Kind);
    }

    [Fact]
    public void Approving_or_rejecting_routes_to_another_step_when_the_definition_says_so()
    {
        var step = OneStep("any", onApprove: "next", onReject: "next");
        Assert.Equal(new Advance(AdvanceKind.Goto, "next"), WorkflowEngine.Resolve(step, [TaskStatuses.Approved]));
        Assert.Equal(new Advance(AdvanceKind.Goto, "next"), WorkflowEngine.Resolve(step, [TaskStatuses.Rejected]));
    }

    [Fact]
    public void A_step_with_no_tasks_has_nothing_to_decide()
    {
        Assert.Equal(AdvanceKind.Wait, WorkflowEngine.Resolve(OneStep("all"), []).Kind);
        Assert.Equal(AdvanceKind.Wait, WorkflowEngine.Resolve(OneStep("any"), []).Kind);
    }

    // ---- engine: segregation of duties ------------------------------------------------------------------------

    [Fact]
    public void With_distinct_approvers_one_person_cannot_approve_two_steps_or_two_parallel_tasks()
    {
        Assert.False(WorkflowEngine.ViolatesDistinctApprovers(true, "final", 2, []));
        Assert.True(WorkflowEngine.ViolatesDistinctApprovers(true, "final", 2, [("review", 1)]));                  // an earlier step
        Assert.True(WorkflowEngine.ViolatesDistinctApprovers(true, "review", 1, [("review", 1)]));                 // a sibling task in the same activation
        Assert.False(WorkflowEngine.ViolatesDistinctApprovers(true, "review", 3, [("review", 1)]));                // the same step again after a rejection loop
        Assert.False(WorkflowEngine.ViolatesDistinctApprovers(false, "final", 2, [("review", 1)]));                // switched off
    }

    // ---- publish gate ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, true)]                          // no workflow ever applied
    [InlineData(WorkflowStatuses.Approved, true)]
    [InlineData(WorkflowStatuses.Running, false)]
    [InlineData(WorkflowStatuses.Rejected, true)]     // back to draft; a fresh submit is judged by its own outcome
    [InlineData(WorkflowStatuses.Cancelled, true)]
    public void The_gate_holds_publishing_back_only_while_the_latest_workflow_is_running(string? latest, bool allowed) =>
        Assert.Equal(allowed, WorkflowEngine.AllowsPublish(latest));

    // ---- flow order --------------------------------------------------------------------------------------------

    [Fact]
    public void Flow_order_follows_the_approve_path_then_lists_rework_only_steps()
    {
        var def = Parse(Doc("review", Step("rework", onApprove: "complete"), Step("review", onApprove: "final", onReject: "rework"), Step("final"))).Definition!;
        Assert.Equal(["rework", "review", "final"], def.Steps.Select(s => s.Id));   // definition order
        Assert.Equal(["review", "final", "rework"], def.FlowOrder().Select(s => s.Id));
    }

    // ---- tasks and instances ---------------------------------------------------------------------------------

    private static (WorkflowInstance Instance, StepDef Step) NewInstance(string mode = "any", int? sla = 24)
    {
        var def = Valid(Step("review", mode: mode, sla: sla, escalation: new { to = new { roles = new[] { "Admin" }, users = new[] { Bob } } }));
        var json = JsonSerializer.Serialize(Doc("review", Step("review", mode: mode, sla: sla)));
        var definition = WorkflowDefinition.Create(Tenant, "Test flow", null, json, null, Now);
        return (WorkflowInstance.Start(Tenant, Asset, definition, true, Alice, Now), def.Steps[0]);
    }

    [Fact]
    public void A_new_task_is_pending_raises_task_created_and_is_due_after_the_sla()
    {
        var (instance, step) = NewInstance(sla: 24);
        var task = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], [Alice]), instance.BeginRound(), Now);
        Assert.Equal(TaskStatuses.Pending, task.Status);
        Assert.Equal(Now.AddHours(24), task.DueAt);
        Assert.Equal(1, task.Round);
        var created = Assert.IsType<WorkflowTaskCreated>(Assert.Single(task.DomainEvents));
        Assert.Equal("workflow.task.created", created.Name);
        Assert.Equal(task.Id, created.EntityId);
        Assert.Equal(["Approver"], created.AssigneeRoles);
    }

    [Fact]
    public void A_task_is_assigned_by_user_id_by_role_and_after_escalation_by_the_escalation_targets()
    {
        var (instance, step) = NewInstance();
        var task = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], [Alice]), 1, Now);
        Assert.True(task.IsAssignedTo(Alice, []));
        Assert.True(task.IsAssignedTo(Guid.NewGuid(), ["Approver"]));
        Assert.False(task.IsAssignedTo(Bob, ["Editor"]));

        Assert.True(task.MarkSlaBreached(Now.AddHours(25), ["Admin"], [Bob]));
        Assert.True(task.IsAssignedTo(Bob, []));
        Assert.True(task.IsAssignedTo(Guid.NewGuid(), ["Admin"]));
        Assert.True(task.IsEscalated);
    }

    [Fact]
    public void An_sla_breach_is_recorded_once_and_only_for_an_open_task_with_a_due_time()
    {
        var (instance, step) = NewInstance();
        var task = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], []), 1, Now);
        task.PullEvents();
        Assert.False(task.IsOverdue(Now.AddHours(23)));
        Assert.True(task.IsOverdue(Now.AddHours(24)));

        Assert.True(task.MarkSlaBreached(Now.AddHours(25), ["Approver", "Admin"], []));
        var breached = Assert.IsType<WorkflowSlaBreached>(Assert.Single(task.PullEvents()));
        Assert.Equal("workflow.sla.breached", breached.Name);
        Assert.Equal(Now.AddHours(24), breached.DueAt);
        Assert.Equal(["Admin"], task.EscalatedRoles);           // a target that is already an assignee is not added again
        Assert.False(task.MarkSlaBreached(Now.AddHours(26), ["Admin"], []));   // already breached

        var (i2, noSla) = NewInstance(sla: null);
        var t2 = WorkflowTask.Create(i2, noSla, new TaskSlot(["Approver"], []), 1, Now);
        Assert.False(t2.MarkSlaBreached(Now.AddYears(1), [], []));   // no due time, nothing to breach
        Assert.False(t2.IsOverdue(Now.AddYears(1)));
    }

    [Fact]
    public void Deciding_a_task_records_who_when_and_whether_it_was_on_time()
    {
        var (instance, step) = NewInstance();
        var onTime = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], []), 1, Now);
        onTime.PullEvents();
        onTime.Decide(Alice, approve: true, "  Looks good  ", Now.AddHours(3));
        Assert.Equal(TaskStatuses.Approved, onTime.Status);
        Assert.Equal(Alice, onTime.DecidedBy);
        Assert.Equal("Looks good", onTime.Comment);
        var done = Assert.IsType<WorkflowTaskCompleted>(Assert.Single(onTime.PullEvents()));
        Assert.Equal(("workflow.task.completed", "approve", true), (done.Name, done.Decision, done.OnTime));

        var late = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], []), 1, Now);
        late.PullEvents();
        late.Decide(Bob, approve: false, "No", Now.AddHours(30));
        Assert.Equal(TaskStatuses.Rejected, late.Status);
        var lateEvent = Assert.IsType<WorkflowTaskCompleted>(Assert.Single(late.PullEvents()));
        Assert.False(lateEvent.OnTime);
        Assert.Equal("reject", lateEvent.Decision);
    }

    [Fact]
    public void Only_an_open_task_can_be_cancelled()
    {
        var (instance, step) = NewInstance();
        var open = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], []), 1, Now);
        open.Cancel();
        Assert.Equal(TaskStatuses.Cancelled, open.Status);

        var decided = WorkflowTask.Create(instance, step, new TaskSlot(["Approver"], []), 1, Now);
        decided.Decide(Alice, true, null, Now);
        decided.Cancel();
        Assert.Equal(TaskStatuses.Approved, decided.Status);
    }

    [Fact]
    public void An_instance_freezes_the_definition_it_started_with()
    {
        var json = JsonSerializer.Serialize(Doc("review", Step("review", sla: 24)));
        var definition = WorkflowDefinition.Create(Tenant, " Flow ", " about ", json, Alice, Now);
        var instance = WorkflowInstance.Start(Tenant, Asset, definition, true, Alice, Now);
        definition.Update("Flow v2", "changed", JsonSerializer.Serialize(Doc("review", Step("review", sla: 1))), Now.AddDays(1));

        Assert.Equal(json, instance.DefinitionJson);
        Assert.Equal("Flow", instance.DefinitionName);
        Assert.Equal(definition.Id, instance.DefinitionId);
        Assert.Equal(24, Parse(JsonDocument.Parse(instance.DefinitionJson).RootElement).Definition!.Steps[0].SlaHours);
    }

    [Fact]
    public void Finishing_an_instance_records_the_outcome_and_caps_the_reason()
    {
        var (instance, _) = NewInstance();
        Assert.True(instance.IsRunning);
        instance.Finish(WorkflowStatuses.Rejected, "draft", new string('x', 800), Now.AddDays(1));
        Assert.False(instance.IsRunning);
        Assert.Equal(500, instance.OutcomeReason!.Length);
        Assert.Equal("draft", instance.OutcomeStatus);
        Assert.Equal(Now.AddDays(1), instance.CompletedAt);
    }

    // ---- the shipped template ---------------------------------------------------------------------------------

    [Fact]
    public void Every_workflow_a_template_pack_ships_is_valid_uses_built_in_roles_and_names_no_customer()
    {
        var workflows = TemplateCatalog.All.Values.SelectMany(p => p.Workflows ?? []).ToList();
        Assert.Contains(workflows, w => w.Name == "Two-level approval");
        var builtIn = Dam.Domain.Authorization.BuiltInRoles.All.Select(r => r.Name).ToHashSet();
        foreach (var w in workflows)
        {
            var r = Parse(w.Definition);
            Assert.True(r.IsValid, $"{w.Name}: " + string.Join("; ", r.Errors.Select(e => $"{e.Path}: {e.Message}")));
            Assert.All(r.Definition!.Steps.SelectMany(s => s.Assignees.Roles.Concat(s.Escalation?.To.Roles ?? [])), role => Assert.Contains(role, builtIn));
            Assert.DoesNotContain("mahindra", JsonSerializer.Serialize(w), StringComparison.OrdinalIgnoreCase);
        }
        var two = Parse(workflows.Single(w => w.Name == "Two-level approval").Definition).Definition!;
        Assert.Equal(["review", "final-approval"], two.FlowOrder().Select(s => s.Id));
        Assert.False(two.AutoPublish);
        Assert.Equal([48, 24], two.Steps.Select(s => s.SlaHours));
    }
}
