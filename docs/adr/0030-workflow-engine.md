# ADR-0030: Workflow engine

## Status
Accepted (Step 2.3, backend)

## Context
Until now an asset moved from `in_review` to `approved` when somebody holding `assets.approve` for its folder pressed approve
(ADR-0009, ADR-0002). That is one person, one step, no deadline. Real organisations want a process: a regional reviewer, then a
brand or legal sign-off, with a time limit and an escalation when it is missed, and different processes for different content.
Decision D9 fixed the shape: our own state-machine engine, workflows defined as JSON, a visual designer (the console half of
this step). `Folder.DefaultWorkflowId` and the `IWorkflowGate` publish guard were left as seams for it. The engine must not
change what happens where no workflow is configured, and "regional review" (an Approver may decide only in the folders and
regions an access rule gives them) must keep working.

## Decisions

**Definition JSON v1.** A workflow is a document `{ version: 1, start, autoPublish, distinctApprovers, steps[] }`; a step is
`{ id, name, assignees{roles[], users[]}, mode, slaHours, escalation{to{roles[], users[]}}, onApprove, onReject }`.
`mode: "any"` creates ONE task offered to every assignee and the first decision wins. `mode: "all"` creates ONE task PER listed
role or user; every one must approve (parallel approvals) and any rejection rejects the step at once. `onApprove` is a step id
or `complete`; `onReject` is a step id or `reject`. `complete` ends the workflow approved (the asset moves `in_review` to
`approved`); `reject` returns the asset to `draft` with the reviewer's comment as the reason; a step id routes there with new
tasks, which is how "send back to the previous step" is expressed. `distinctApprovers` (default true) is segregation of
duties: one person cannot approve two different steps of an instance, nor stand in for two parallel tasks of an `all` step;
approving the same step again after a rejection loop is allowed. `autoPublish` (default false) publishes right after the final
approval, still subject to the guards a manual publish has (required metadata complete; rights are not implemented yet): if one
fails the asset stays `approved` and the instance records why, so nothing is published that a person could not have published.
Defaults when a field is omitted: `mode` any, `slaHours` null, `escalation` null, `autoPublish` false, `distinctApprovers` true.

**Validation is a pure function** (`WorkflowDefinitionParser`, Dam.Domain) that returns a parsed model or path-addressed errors
(`steps[1].assignees.roles[0]: ...`). The rules: 1 to 20 steps; ids unique, `[a-z0-9_-]{1,40}`, never the reserved words
`complete` or `reject`; `start` exists; every `onApprove`/`onReject` names a step or its own reserved word; every step reachable
from `start` (by either edge); the `onApprove` edges contain no cycle (rejections may loop back, approvals only move forward);
every step has at least one assignee, no repeats; `slaHours` is a whole number 1 to 8760 or null; an escalation names at least
one target. Unknown properties are ignored and the document is stored exactly as submitted, so the designer can keep node
positions in it. The same code validates on save, on `POST /workflows/validate` (the designer's live feedback; always 200,
`valid` + `errors[]`) and when an instance reads its snapshot. The API layer adds the one check that needs data: every role
and user named exists in the tenant (exact role name), so a typo cannot create a step nobody can ever decide.

**Tables** (migration `WorkflowEngine`, tenant RLS forced like every tenant table):
`workflow_definitions(name unique per tenant ignoring case, json jsonb)`; `workflow_bindings(workflow_id, asset_type?,
region?)` unique per (tenant, type, region) with nulls treated as equal; `workflow_instances` with a FROZEN
`definition_json` snapshot, `definition_name`, status `running|approved|rejected|cancelled`, `started_by/at`, `completed_at`,
`outcome_reason`, `outcome_status` (the asset's status when it ended), and a partial unique index allowing one running instance
per asset; `workflow_tasks` with `instance_id`, `step_id`, `round` (which activation of the step), `assignee_roles text[]`,
`assignee_user_ids uuid[]`, `escalated_roles/escalated_user_ids` (added when the SLA breaches), status, `due_at`,
`sla_breached_at`, `decided_by/at`, `comment`. Instances and tasks are history: the app role cannot delete them. A definition
can be deleted (the instance keeps its snapshot; `definition_id` becomes null). Indexes: inbox by (tenant, status), GIN on the
assignee arrays, decided-by; a partial index for the SLA sweep; per-asset lookups.

**Which workflow applies on `submit`** (first match wins): 1) the nearest folder, the asset's own and then each ancestor, with a
`default_workflow_id`; 2) a binding by type and region (the asset's `region` attribute): type+region, then type only, then
region only (for an asset with several regions, the alphabetically first matching binding); 3) none. With none, nothing changes:
`approve` and `reject` work directly exactly as before, no instance exists, and publishing is not gated. When one applies,
`submit` moves the asset to `in_review` and creates a `WorkflowInstance` plus the first step's tasks in the same transaction,
raising `workflow.task.created` through the outbox. The folder `PATCH` now refuses a `defaultWorkflowId` that does not exist.

**While an instance runs** the asset's direct `approve`/`reject` transitions return 409 ("decide via the workflow task"), for
everyone including Admin: a second route to the same decision would make the segregation and SLA rules advisory. `withdraw` (and
deleting the asset) cancels the instance and its open tasks; submitting again starts a new instance. The `IWorkflowGate`
implementation refuses publishing only while the asset's latest instance is still `running`. A rejected or cancelled latest
instance does not block: the asset went back to draft, and the only way to `approved` after that is a fresh submit, which
either starts a new instance (judged by its own outcome) or has no workflow and is approved directly. (Blocking on a
rejected/cancelled instance would wrongly hold back an asset that was resubmitted after its workflow binding was removed.)

**Deciding a task**: `POST /tasks/{id}/decision {decision: approve|reject, comment?}`; a comment is required to reject (at most
1000 characters). The caller must (a) be an assignee: listed by user id, or hold one of the listed roles, or, once the task
escalated, be an escalation target; (b) hold `workflow.tasks.decide` (in no built-in role except Admin, like `assets.approve`: ADR-0002);
(c) pass `assets.approve` on that asset's resource. (b) and (c) are the "regional" part: no role grants either, only an access
rule scoped to folders, types or attributes such as a region, so an Approver without a rule that lists both permissions for that
folder does not see the task and cannot decide it (403), exactly as they could not use the direct transition. An assignee who is
in scope for `assets.approve` but lacks `workflow.tasks.decide` is barred too, and the task is left out of their inbox. Approving a step does what the definition
says: wait (an `all` step with tasks still open), create the next step's tasks, or finish. Every decision raises
`workflow.task.completed` with whether it was on time. The asset status change goes through the ordinary
`Asset.ChangeStatus` (so `asset.status.changed` and the audit chain) with a reason such as "Approved by workflow 'X' (step
'Review')"; a rejection uses the reviewer's comment as the reason.

**Concurrency**: every decision (and a withdrawal) first takes `SELECT ... FOR UPDATE` on the instance row, inside the request's
transaction, and reads the tasks only afterwards. Two people deciding the same `any` task therefore serialise: the first wins,
the second sees a decided task and gets 409. It also closes the subtler race where the last two parallel approvals of an `all`
step each read the other as still pending and the step never completes. Integration tests run both races concurrently.

**SLA**: a task's `due_at` is its creation time plus the step's `slaHours`. `WorkflowSlaJob` in Dam.Scheduler runs every 30
seconds (`DAM_WORKFLOW_SLA_POLL_CRON`), finds overdue open tasks across all tenants through the read-only `dam_system`
connection (one extra `SELECT` grant and policy on `workflow_tasks`, like the retention jobs have on `assets`), then marks each
through an ordinary tenant-scoped scope: sets `sla_breached_at`, adds the step's escalation targets as further eligible
deciders, raises `workflow.sla.breached`. A breach is recorded once and is visible in the API within one poll of the due time.
The job reads the time from `IClock`, so tests drive it with a fake clock.

**Inbox and summary**. `GET /tasks` (`assignee=me` default, `status=pending|decided|all`, `limit` up to 200, `cursor`) returns
the tasks the caller could decide (assignee match and scope, as above) and the ones they decided themselves; `assignee=all`
needs `workflow.manage`. Newest first, by task id (UUIDv7), so paging is stable; the assignee match is done in SQL
(array overlap) and the scope check in memory with the same policy engine as everywhere else, scanning up to 5,000 rows. A
pending task the caller is barred from by `distinctApprovers` is left out of their inbox. `GET /tasks/summary` gives
`pendingForMe`, `overdueForMe` (due time passed or SLA breached) and, only to holders of `workflow.manage` or `analytics.read`
(the tenant-wide figures; null for everyone else), `pendingTotal`, `overdueTotal`, `completed30d`, `completedOnTime30d` and
`slaAttainmentPercent`. A completed task counts as on time when decided no later than its due time; a task whose step has no SLA
is never late, so it counts as on time. `GET /assets/{id}/workflow` (needs `assets.read` on the asset) returns the asset's latest
instance as a timeline: each step of the frozen snapshot, in flow order, with state `waiting|active|done|skipped` and its tasks
across every round.

**Authorization of the management API**: create, update, delete, validate and bindings need `workflow.manage` (Admin only; the
catalogue permission already existed). Reading definitions and bindings is also open to holders of `folders.manage`, because
choosing a folder's default needs the list. No built-in role changed.

**Template**: the `core` pack gains a workflow "Two-level approval" (Review by role Approver, 48 hours, escalating to Admin, then
Final approval by role Librarian, 24 hours; reject ends it; no auto publish). Applying a pack creates it if no workflow of that
name exists and never binds it. Customer processes are data in the same way (a pack file or the API), never code.

## Deliberately not included
- **Notifications** (email/Teams on task creation, SLA breach): the events are on the outbox; delivering them is Step 2.x
  notifications.
- **Conditional routing and expressions** (route by metadata value), sub-workflows, timers other than the step SLA, and
  reassignment/delegation of an open task. The definition is versioned (`version: 1`) so these can be added.
- **Per-user working calendars** for the SLA: hours are wall-clock hours.
- **Editing the assignees of a running instance.** A definition change applies to later submissions; to change a running one,
  withdraw and resubmit.
- **An index on `escalated_*`**: escalated tasks are a small subset of open tasks, which the inbox query already narrows by
  status and tenant.

## Consequences
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated: tag Workflow (`ListWorkflows`, `CreateWorkflow`, `GetWorkflow`,
  `UpdateWorkflow`, `DeleteWorkflow`, `ValidateWorkflow`, `ListWorkflowBindings`, `ReplaceWorkflowBindings`, `ListTasks`,
  `GetTaskSummary`, `DecideTask`, `GetAssetWorkflow`). `PUT /workflows/bindings` takes `{ bindings: [...] }`.
- Reviewers are set up as before, through an access rule; the rule now lists `workflow.tasks.decide` next to `assets.approve`
  (the policy matrix tests already assumed this pairing).
- `Dam.Scheduler` runs a third cross-tenant job; `TemplateDto` and `TemplateApplyResultDto` gain a workflow count.
- Tests: pure rules (`tests/Dam.UnitTests/WorkflowTests.cs`: every validator rule and its error path, step resolution for
  any/all/reject-to-step/complete, segregation of duties, tasks, the publish gate rule, the shipped template) and the API against
  a real Postgres (`tests/Dam.IntegrationTests/WorkflowTests.cs`): definition management and permissions, binding resolution
  order, the no-workflow path, the two-level flow end to end, rejection, direct approve 409, withdraw, scope and role and user
  assignment, parallel approval, both concurrency races, reject-to-step, distinct approvers, auto publish and its failure,
  snapshot immutability, inbox/summary/asset view, the SLA job with a fake clock across tenants, and RLS/grants on the new tables.

## Console
- **Tasks inbox** (`/tasks`): Pending / Decided and, for workflow managers, Mine / Everyone, kept in the URL; overdue first, then due-soonest,
  then no deadline. Approve is one click; Reject requires a comment (dialog). A Tasks count pill (danger when anything is overdue) sits on
  the nav item in the sidebar and the phone tab bar and refreshes every minute, so an SLA breach shows within about a minute.
- **Asset page**: a workflow panel (stepper, per-task decision/who/when/comment, outcome banner) with Approve/Reject for the viewer's own open
  task. While an instance runs the direct Approve/Reject lifecycle actions are not offered (the server would 409) and a note names the step.
  The library's card menu and bulk bar do not know per asset whether a workflow runs, so they may still offer them; the server refuses with
  an explanatory message. Hiding them needs a "workflow running" flag on the asset listing (not built).
- **Dashboard**: the Published card became "Open tasks" (your pending tasks, overdue count, tenant SLA attainment when available).
- **Designer** (`/admin/workflows`, `/admin/workflows/:id`, Admin only): a custom step-flow editor, not React Flow - our workflows are chains with
  a few back-edges, and the console avoids a large dependency for that. A vertical flow (Submitted -> step cards -> Approved) with "+" insert
  points, an inspector (assignees, any/all, SLA, escalation, on approve/reject), a problems panel (instant local checks mirroring the server
  rules, then the server's authoritative validation on Check/Save) and a JSON view that round-trips exactly; unknown JSON properties are kept
  so node positions can be added later. The list page also edits the asset-type/region bindings (replace-all) and per-folder default workflows.
  Browser Back is not guarded against unsaved edits (the router has no blocker); reload/close and link clicks are.
- Hindi strings for `tasks` and `workflows` are not translated yet and fall back to English.
