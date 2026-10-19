using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dam.Domain.Workflow;

/// <summary>Who may act on a step: anyone holding one of the roles, or one of the named users (DAM user ids).</summary>
public sealed record AssigneeSet(IReadOnlyList<string> Roles, IReadOnlyList<Guid> Users)
{
    public int Count => Roles.Count + Users.Count;
    public static AssigneeSet Empty { get; } = new([], []);
}

public sealed record EscalationDef(AssigneeSet To);

/// <summary>One approval step of a workflow definition (ADR-0030).</summary>
/// <param name="Mode"><c>any</c>: one task offered to every assignee, the first decision wins. <c>all</c>: one task per listed
/// role or user, every one must approve, any rejection rejects the step.</param>
/// <param name="OnApprove">A step id, or <see cref="WorkflowDef.Complete"/>.</param>
/// <param name="OnReject">A step id, or <see cref="WorkflowDef.Reject"/>.</param>
public sealed record StepDef(
    string Id, string Name, AssigneeSet Assignees, string Mode, int? SlaHours, EscalationDef? Escalation, string OnApprove, string OnReject);

/// <summary>A parsed, validated workflow definition (version 1). Immutable; see <see cref="WorkflowDefinitionParser"/>.</summary>
public sealed record WorkflowDef(int Version, string Start, bool AutoPublish, bool DistinctApprovers, IReadOnlyList<StepDef> Steps)
{
    public const string Complete = "complete";
    public const string Reject = "reject";
    public const string ModeAny = "any";
    public const string ModeAll = "all";

    public StepDef? Find(string id) => Steps.FirstOrDefault(s => s.Id == id);

    /// <summary>Steps in the order they are normally visited: from the start step along the approve path, then any step only
    /// reachable by a rejection route, in definition order. Used to present a running instance as a timeline.</summary>
    public IReadOnlyList<StepDef> FlowOrder()
    {
        var ordered = new List<StepDef>();
        var seen = new HashSet<string>();
        for (var step = Find(Start); step is not null && seen.Add(step.Id); step = Find(step.OnApprove)) ordered.Add(step);
        ordered.AddRange(Steps.Where(s => seen.Add(s.Id)));
        return ordered;
    }
}

/// <summary>A problem with a definition, addressed by the path of the offending JSON value (e.g. <c>steps[1].assignees.roles[0]</c>).</summary>
public sealed record WorkflowDefError(string Path, string Message);

public sealed record WorkflowParseResult(WorkflowDef? Definition, IReadOnlyList<WorkflowDefError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reads and validates a workflow definition JSON document. Pure: no I/O, so every rule is unit-tested directly, and the
/// same code validates on save, on the designer's live "validate" call and when a running instance re-reads its frozen
/// snapshot. Unknown properties are ignored (the designer may keep layout data in the document). Rules (ADR-0030):
/// <list type="bullet">
/// <item>version 1; 1 to 20 steps; step ids unique, <c>[a-z0-9_-]{1,40}</c>, never the reserved words complete/reject;</item>
/// <item><c>start</c> names a step; every <c>onApprove</c>/<c>onReject</c> is a step id or its reserved word;</item>
/// <item>every step is reachable from <c>start</c>; the <c>onApprove</c> edges contain no cycle (<c>onReject</c> may loop back);</item>
/// <item>every step has at least one assignee; <c>slaHours</c> is 1 to 8760 or null; an escalation names at least one target.</item>
/// </list>
/// </summary>
public static partial class WorkflowDefinitionParser
{
    public const int MaxSteps = 20;
    public const int MaxSlaHours = 8760;
    public const int MaxNameLength = 100;
    public const int MaxRoleLength = 64;
    public const int MaxAssignees = 50;

    [GeneratedRegex("^[a-z0-9_-]{1,40}$")]
    private static partial Regex IdPattern();

    public static WorkflowParseResult Parse(JsonElement? json)
    {
        var errors = new List<WorkflowDefError>();
        void Err(string path, string message) => errors.Add(new WorkflowDefError(path, message));

        if (json is not { ValueKind: JsonValueKind.Object } root)
        {
            Err("", "The definition must be a JSON object.");
            return new WorkflowParseResult(null, errors);
        }

        var version = 0;
        if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version)) Err("version", "Required. Use 1.");
        else if (version != 1) Err("version", "Unsupported version. Use 1.");

        var autoPublish = OptionalBool(root, "autoPublish", false, Err);
        var distinct = OptionalBool(root, "distinctApprovers", true, Err);

        string? start = null;
        if (!root.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(s.GetString())) Err("start", "Required: the id of the first step.");
        else start = s.GetString();

        var steps = new List<StepDef>();
        var ids = new HashSet<string>();
        if (!root.TryGetProperty("steps", out var arr) || arr.ValueKind != JsonValueKind.Array) Err("steps", "Required: a list of steps.");
        else
        {
            var count = arr.GetArrayLength();
            if (count is < 1 or > MaxSteps) Err("steps", $"A workflow has between 1 and {MaxSteps} steps.");
            // First pass: collect ids, so targets can be checked against all steps whatever their order.
            var i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { } id)
                    ids.Add(id);
                i++;
            }
            i = 0;
            var seen = new HashSet<string>();
            foreach (var el in arr.EnumerateArray().Take(MaxSteps + 1))
            {
                var step = ParseStep(el, i, ids, seen, Err);
                if (step is not null) steps.Add(step);
                i++;
            }
        }

        if (start is not null && !ids.Contains(start)) Err("start", $"No step has the id '{start}'.");

        // Graph rules only make sense once every step parsed on its own.
        if (errors.Count == 0 && start is not null)
        {
            CheckReachable(steps, start, Err);
            CheckApproveCycle(steps, Err);
        }

        return errors.Count == 0
            ? new WorkflowParseResult(new WorkflowDef(version, start!, autoPublish, distinct, steps), errors)
            : new WorkflowParseResult(null, errors);
    }

    private static StepDef? ParseStep(JsonElement el, int index, HashSet<string> ids, HashSet<string> seen, Action<string, string> err)
    {
        var p = $"steps[{index}]";
        if (el.ValueKind != JsonValueKind.Object) { err(p, "A step must be an object."); return null; }
        var failed = false;
        void E(string path, string message) { failed = true; err(path, message); }

        string? id = null;
        if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) E($"{p}.id", "Required.");
        else
        {
            id = idEl.GetString()!;
            if (!IdPattern().IsMatch(id)) E($"{p}.id", "Use 1-40 lowercase letters, digits, hyphens or underscores.");
            else if (id is WorkflowDef.Complete or WorkflowDef.Reject) E($"{p}.id", $"'{id}' is reserved.");
            else if (!seen.Add(id)) E($"{p}.id", $"Another step already uses the id '{id}'.");
        }

        string? name = null;
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameEl.GetString())) E($"{p}.name", "Required.");
        else if (nameEl.GetString()!.Trim().Length > MaxNameLength) E($"{p}.name", $"At most {MaxNameLength} characters.");
        else name = nameEl.GetString()!.Trim();

        var assignees = AssigneeSet.Empty;
        if (!el.TryGetProperty("assignees", out var aEl) || aEl.ValueKind != JsonValueKind.Object) E($"{p}.assignees", "Required: roles and/or users.");
        else
        {
            assignees = ParseAssignees(aEl, $"{p}.assignees", E, out var assigneeProblem);
            if (assignees.Count == 0 && !assigneeProblem) E($"{p}.assignees", "Name at least one role or user.");
        }

        var mode = WorkflowDef.ModeAny;
        if (el.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind != JsonValueKind.Null)
        {
            if (modeEl.ValueKind != JsonValueKind.String || modeEl.GetString() is not (WorkflowDef.ModeAny or WorkflowDef.ModeAll)) E($"{p}.mode", "Use 'any' or 'all'.");
            else mode = modeEl.GetString()!;
        }

        int? sla = null;
        if (el.TryGetProperty("slaHours", out var slaEl) && slaEl.ValueKind != JsonValueKind.Null)
        {
            if (slaEl.ValueKind != JsonValueKind.Number || !slaEl.TryGetInt32(out var hours) || hours is < 1 or > MaxSlaHours) E($"{p}.slaHours", $"Use a whole number of hours from 1 to {MaxSlaHours}, or null.");
            else sla = hours;
        }

        EscalationDef? escalation = null;
        if (el.TryGetProperty("escalation", out var escEl) && escEl.ValueKind != JsonValueKind.Null)
        {
            if (escEl.ValueKind != JsonValueKind.Object || !escEl.TryGetProperty("to", out var toEl) || toEl.ValueKind != JsonValueKind.Object) E($"{p}.escalation.to", "Required: roles and/or users to escalate to.");
            else
            {
                var to = ParseAssignees(toEl, $"{p}.escalation.to", E, out var escalationProblem);
                if (to.Count == 0 && !escalationProblem) E($"{p}.escalation.to", "Name at least one role or user to escalate to, or set escalation to null.");
                else if (to.Count > 0) escalation = new EscalationDef(to);
            }
        }

        string? onApprove = null, onReject = null;
        if (!el.TryGetProperty("onApprove", out var oaEl) || oaEl.ValueKind != JsonValueKind.String) E($"{p}.onApprove", "Required: a step id or 'complete'.");
        else if (oaEl.GetString() is { } approveTarget && approveTarget != WorkflowDef.Complete && !ids.Contains(approveTarget)) E($"{p}.onApprove", $"No step has the id '{approveTarget}'.");
        else onApprove = oaEl.GetString();
        if (!el.TryGetProperty("onReject", out var orEl) || orEl.ValueKind != JsonValueKind.String) E($"{p}.onReject", "Required: a step id or 'reject'.");
        else if (orEl.GetString() is { } rejectTarget && rejectTarget != WorkflowDef.Reject && !ids.Contains(rejectTarget)) E($"{p}.onReject", $"No step has the id '{rejectTarget}'.");
        else onReject = orEl.GetString();

        return failed || id is null || name is null || onApprove is null || onReject is null
            ? null
            : new StepDef(id, name, assignees, mode, sla, escalation, onApprove, onReject);
    }

    private static AssigneeSet ParseAssignees(JsonElement el, string path, Action<string, string> err, out bool failed)
    {
        failed = false;
        var roles = new List<string>();
        var users = new List<Guid>();
        if (el.TryGetProperty("roles", out var rEl) && rEl.ValueKind != JsonValueKind.Null)
        {
            if (rEl.ValueKind != JsonValueKind.Array) { err($"{path}.roles", "Must be a list of role names."); failed = true; }
            else
            {
                var i = 0;
                foreach (var r in rEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(r.GetString()) || r.GetString()!.Length > MaxRoleLength)
                    { err($"{path}.roles[{i}]", $"Must be a role name of 1-{MaxRoleLength} characters."); failed = true; }
                    else if (roles.Contains(r.GetString()!)) { err($"{path}.roles[{i}]", $"'{r.GetString()}' is listed twice."); failed = true; }
                    else roles.Add(r.GetString()!);
                    i++;
                }
            }
        }
        if (el.TryGetProperty("users", out var uEl) && uEl.ValueKind != JsonValueKind.Null)
        {
            if (uEl.ValueKind != JsonValueKind.Array) { err($"{path}.users", "Must be a list of user ids."); failed = true; }
            else
            {
                var i = 0;
                foreach (var u in uEl.EnumerateArray())
                {
                    if (u.ValueKind != JsonValueKind.String || !Guid.TryParse(u.GetString(), out var g) || g == Guid.Empty)
                    { err($"{path}.users[{i}]", "Must be a user id (a GUID)."); failed = true; }
                    else if (users.Contains(g)) { err($"{path}.users[{i}]", "This user is listed twice."); failed = true; }
                    else users.Add(g);
                    i++;
                }
            }
        }
        if (roles.Count + users.Count > MaxAssignees) { err(path, $"At most {MaxAssignees} roles and users."); failed = true; }
        return new AssigneeSet(roles, users);
    }

    private static bool OptionalBool(JsonElement root, string name, bool fallback, Action<string, string> err)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return fallback;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        err(name, "Must be true or false.");
        return fallback;
    }

    private static void CheckReachable(List<StepDef> steps, string start, Action<string, string> err)
    {
        var byId = steps.ToDictionary(s => s.Id);
        var reached = new HashSet<string>();
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!reached.Add(id)) continue;
            var step = byId[id];
            foreach (var next in new[] { step.OnApprove, step.OnReject })
                if (byId.ContainsKey(next)) queue.Enqueue(next);
        }
        for (var i = 0; i < steps.Count; i++)
            if (!reached.Contains(steps[i].Id)) err($"steps[{i}].id", $"Step '{steps[i].Id}' cannot be reached from the start step.");
    }

    /// <summary>Every step has at most one approve edge, so a cycle is found by following each chain until it ends or repeats.</summary>
    private static void CheckApproveCycle(List<StepDef> steps, Action<string, string> err)
    {
        var byId = steps.ToDictionary(s => s.Id);
        var reported = new HashSet<string>();
        foreach (var first in steps)
        {
            var chain = new List<string>();
            var current = first.Id;
            while (byId.TryGetValue(current, out var step))
            {
                if (chain.Contains(current))
                {
                    var loop = chain.Skip(chain.IndexOf(current)).Append(current).ToList();
                    var anchor = loop[0];
                    if (reported.Add(string.Join('>', loop.Distinct().Order())))
                        err($"steps[{steps.FindIndex(s => s.Id == anchor)}].onApprove",
                            $"Approving leads back to the same step: {string.Join(" -> ", loop)}. Approvals must move forward; only rejections may loop back.");
                    break;
                }
                chain.Add(current);
                current = step.OnApprove;
            }
        }
    }
}
