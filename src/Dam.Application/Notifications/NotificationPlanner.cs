using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Workflow;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Identity;
using Dam.Domain.Workflow;

namespace Dam.Application.Notifications;

/// <summary>What one domain event means for people: the message, and who should get it.</summary>
public sealed record NotificationPlan(NotificationContent Content, IReadOnlyList<Guid> Recipients);

/// <summary>Builds <see cref="AccessProfile"/>s for users other than the caller, from what is stored, so background work can ask
/// "could this person do that?" with the same policy engine requests use.</summary>
public sealed class UserProfiles(
    IRepository<User> users, IRepository<Role> roles, IRepository<UserRole> userRoles, IRepository<UserGroup> userGroups,
    IRepository<GroupRole> groupRoles, IRepository<AccessRule> rules, IRepository<Tenant> tenants, ITenantContext tenant, IQueryExecutor q)
{
    /// <summary>Profiles of the given users (active or not: the profile says). Users that do not exist are absent.</summary>
    public async Task<Dictionary<Guid, AccessProfile>> LoadAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var found = await q.ToListAsync(users.Query().Where(u => ids.Contains(u.Id)), ct);
        var tenantActive = (await q.FirstOrDefaultAsync(tenants.Query(), ct)) is { Status: TenantStatus.Active };
        var allRoles = await q.ToListAsync(roles.Query(), ct);
        var groupLinks = await q.ToListAsync(userGroups.Query().Where(x => ids.Contains(x.UserId)), ct);
        var directLinks = await q.ToListAsync(userRoles.Query().Where(x => ids.Contains(x.UserId)), ct);
        var groupIds = groupLinks.Select(g => g.GroupId).Distinct().ToList();
        var viaGroups = groupIds.Count == 0 ? [] : await q.ToListAsync(groupRoles.Query().Where(x => groupIds.Contains(x.GroupId)), ct);
        var principalIds = ids.Concat(groupIds).Concat(allRoles.Select(r => r.Id)).Distinct().ToList();
        var allRules = await q.ToListAsync(rules.Query().Where(r => principalIds.Contains(r.PrincipalId)), ct);

        var result = new Dictionary<Guid, AccessProfile>();
        foreach (var u in found)
        {
            var myGroups = groupLinks.Where(g => g.UserId == u.Id).Select(g => g.GroupId).ToHashSet();
            var roleIds = directLinks.Where(l => l.UserId == u.Id).Select(l => l.RoleId)
                .Concat(viaGroups.Where(l => myGroups.Contains(l.GroupId)).Select(l => l.RoleId)).ToHashSet();
            var idp = u.IdpRoles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mine = allRoles.Where(r => roleIds.Contains(r.Id) || idp.Contains(r.Name)).ToList();
            var mineIds = mine.Select(r => r.Id).ToHashSet();
            var myRules = allRules.Where(r =>
                (r.PrincipalType == PrincipalType.User && r.PrincipalId == u.Id)
                || (r.PrincipalType == PrincipalType.Group && myGroups.Contains(r.PrincipalId))
                || (r.PrincipalType == PrincipalType.Role && mineIds.Contains(r.PrincipalId))).ToList();
            result[u.Id] = new AccessProfile(tenant.TenantId, u.Id, u.Status == UserStatus.Active && tenantActive, u.Attributes,
                mine.Select(r => new RoleGrant(r.Name, r.Permissions, r.AttributeRestricted, r.RequiredAttributes)).ToList(),
                myRules.Select(r => r.ToGrant()).ToList());
        }
        return result;
    }

    /// <summary>Active users who hold any of the roles: by direct assignment, through a group, or from the identity provider's token.</summary>
    public async Task<List<Guid>> HoldersOfRolesAsync(IReadOnlyCollection<string> roleNames, CancellationToken ct)
    {
        if (roleNames.Count == 0) return [];
        var wanted = roleNames.Select(n => n.ToLowerInvariant()).ToHashSet();
        var roleIds = (await q.ToListAsync(roles.Query(), ct)).Where(r => wanted.Contains(r.Name.ToLowerInvariant())).Select(r => r.Id).ToList();
        var names = wanted.ToList();

        var direct = roleIds.Count == 0 ? [] : await q.ToListAsync(userRoles.Query().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.UserId), ct);
        var groupIds = roleIds.Count == 0 ? [] : await q.ToListAsync(groupRoles.Query().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.GroupId), ct);
        var viaGroup = groupIds.Count == 0 ? [] : await q.ToListAsync(userGroups.Query().Where(x => groupIds.Contains(x.GroupId)).Select(x => x.UserId), ct);
        var viaIdp = await q.ToListAsync(users.Query().Where(u => u.IdpRoles.Any(r => names.Contains(r))).Select(u => u.Id), ct);

        var candidates = direct.Concat(viaGroup).Concat(viaIdp).Distinct().ToList();
        if (candidates.Count == 0) return [];
        return await q.ToListAsync(users.Query().Where(u => candidates.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id), ct);
    }
}

/// <summary>
/// Decides, for a domain event, which notification to raise and for whom (ADR-0031):
/// <list type="bullet">
/// <item><c>workflow.task.created</c> and <c>workflow.sla.breached</c>: the people who can decide the task right now - named directly or
/// holding an assignee role, and passing exactly the checks the task inbox applies (<c>workflow.tasks.decide</c> and <c>assets.approve</c> on the
/// asset, not barred by the distinct-approvers rule). Nobody who could not see or decide it is told about it.</item>
/// <item><c>workflow.task.completed</c>: whoever submitted the asset and the asset's owner, except the person who decided.</item>
/// <item><c>asset.status.changed</c> to expired or archived: the asset's owner.</item>
/// </list>
/// </summary>
public sealed class NotificationPlanner(
    IRepository<WorkflowTask> tasks, IRepository<WorkflowInstance> instances, IRepository<Asset> assets, IRepository<User> users,
    WorkflowTaskAccess access, UserProfiles profiles, IQueryExecutor q)
{
    public async Task<NotificationPlan?> PlanAsync(string eventName, JsonElement payload, CancellationToken ct) => eventName switch
    {
        "workflow.task.created" => await TaskEventAsync(payload, breached: false, ct),
        "workflow.sla.breached" => await TaskEventAsync(payload, breached: true, ct),
        "workflow.task.completed" => await CompletedAsync(payload, ct),
        "asset.status.changed" => await LifecycleAsync(payload, ct),
        _ => null,
    };

    private async Task<NotificationPlan?> TaskEventAsync(JsonElement payload, bool breached, CancellationToken ct)
    {
        if (!TryGuid(payload, "TaskId", out var taskId)) return null;
        var task = await q.FirstOrDefaultAsync(tasks.Query().Where(t => t.Id == taskId), ct);
        if (task is not { IsPending: true }) return null;   // already decided or cancelled: nothing to ask of anyone
        var instance = await q.FirstOrDefaultAsync(instances.Query().Where(i => i.Id == task.InstanceId), ct);
        var loaded = await access.LoadAssetsAsync([task.AssetId], ct);
        if (instance is null || !loaded.TryGetValue(task.AssetId, out var a)) return null;

        var candidates = task.AssigneeUserIds.Concat(task.EscalatedUserIds)
            .Concat(await profiles.HoldersOfRolesAsync(task.AssigneeRoles.Concat(task.EscalatedRoles).ToList(), ct)).Distinct().ToList();
        var loadedProfiles = await profiles.LoadAsync(candidates, ct);

        var distinct = instance.DistinctApprovers;
        var recipients = new List<Guid>();
        foreach (var (id, profile) in loadedProfiles)
        {
            if (!WorkflowTaskAccess.CanDecide(profile, task, a.Asset, a.Path)) continue;
            if (distinct)
            {
                var prior = (await access.ApprovalsByAsync(id, [instance.Id], ct)).GetValueOrDefault(instance.Id) ?? [];
                if (WorkflowEngine.ViolatesDistinctApprovers(true, task.StepId, task.Round, prior)) continue;
            }
            recipients.Add(id);
        }
        if (recipients.Count == 0) return null;
        var content = breached
            ? NotificationRules.SlaBreached(task.AssetId, a.Asset.Title, task.StepName, task.DueAt ?? DateTimeOffset.UtcNow)
            : NotificationRules.TaskCreated(task.AssetId, a.Asset.Title, task.StepName, instance.DefinitionName, task.DueAt);
        return new NotificationPlan(content, recipients);
    }

    private async Task<NotificationPlan?> CompletedAsync(JsonElement payload, CancellationToken ct)
    {
        if (!TryGuid(payload, "TaskId", out var taskId)) return null;
        var task = await q.FirstOrDefaultAsync(tasks.Query().Where(t => t.Id == taskId), ct);
        if (task is null || !TaskStatuses.IsDecision(task.Status)) return null;
        var instance = await q.FirstOrDefaultAsync(instances.Query().Where(i => i.Id == task.InstanceId), ct);
        var asset = await q.FirstOrDefaultAsync(assets.Query().Where(a => a.Id == task.AssetId), ct);
        if (instance is null || asset is null) return null;

        var people = new List<Guid>();
        if (instance.StartedBy is { } starter) people.Add(starter);
        if (asset.OwnerId is { } owner) people.Add(owner);
        var recipients = await ActiveAsync(people.Where(p => p != task.DecidedBy).Distinct().ToList(), ct);
        if (recipients.Count == 0) return null;

        string? decider = null;
        if (task.DecidedBy is { } by)
        {
            var u = await q.FirstOrDefaultAsync(users.Query().Where(x => x.Id == by).Select(x => new { x.DisplayName, x.Email }), ct);
            decider = u is null ? null : string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName;
        }
        var decision = task.Status == TaskStatuses.Approved ? "approve" : "reject";
        return new NotificationPlan(NotificationRules.TaskCompleted(asset.Id, asset.Title, task.StepName, decision, instance.Status, task.Comment, decider), recipients);
    }

    private async Task<NotificationPlan?> LifecycleAsync(JsonElement payload, CancellationToken ct)
    {
        if (!TryGuid(payload, "AssetId", out var assetId) || !payload.TryGetProperty("To", out var to) || to.GetString() is not { } status) return null;
        if (status is not ("expired" or "archived")) return null;
        var asset = await q.FirstOrDefaultAsync(assets.Query().Where(a => a.Id == assetId), ct);
        if (asset?.OwnerId is not { } owner) return null;
        var content = NotificationRules.AssetLifecycle(asset.Id, asset.Title, status, asset.StatusReason);
        var recipients = await ActiveAsync([owner], ct);
        return content is null || recipients.Count == 0 ? null : new NotificationPlan(content, recipients);
    }

    /// <summary>What to say about any catalogue event in a chat channel, which is not addressed to a person.</summary>
    public async Task<(string Title, string Body, string LinkPath)> DescribeAsync(string eventName, JsonElement payload, CancellationToken ct)
    {
        Guid? assetId = TryGuid(payload, "AssetId", out var a) ? a : null;
        if (assetId is null && TryGuid(payload, "TaskId", out var taskId))
            assetId = await q.FirstOrDefaultAsync(tasks.Query().Where(t => t.Id == taskId).Select(t => (Guid?)t.AssetId), ct);
        var title = assetId is { } id ? await q.FirstOrDefaultAsync(assets.Query().Where(x => x.Id == id).Select(x => x.Title), ct) : null;

        string? detail = null;
        if (eventName.StartsWith("asset.", StringComparison.Ordinal) && payload.TryGetProperty("From", out var from) && payload.TryGetProperty("To", out var to))
            detail = $"{from.GetString()} -> {to.GetString()}";
        else if (eventName == "asset.version.added" && payload.TryGetProperty("VersionNo", out var no)) detail = $"Version {no}";
        else if (eventName == "rendition.ready" && payload.TryGetProperty("Profile", out var profile)) detail = $"Profile {profile.GetString()}";
        else if (eventName == "workflow.task.created" && payload.TryGetProperty("StepName", out var step)) detail = $"Step: {step.GetString()}";
        else if (eventName == "workflow.task.completed" && payload.TryGetProperty("Decision", out var decision))
            detail = decision.GetString() == "approve" ? "Approved" : $"Rejected{(payload.TryGetProperty("Comment", out var c) && c.GetString() is { Length: > 0 } text ? ": " + text : "")}";
        var (t, body) = NotificationRules.Describe(eventName, title, detail);
        return (t, body, assetId is { } aid ? NotificationRules.AssetPath(aid) : "/");
    }

    private Task<List<Guid>> ActiveAsync(List<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.FromResult(new List<Guid>()) : q.ToListAsync(users.Query().Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id), ct);

    private static bool TryGuid(JsonElement e, string name, out Guid id)
    {
        id = Guid.Empty;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.TryGetGuid(out id);
    }
}
