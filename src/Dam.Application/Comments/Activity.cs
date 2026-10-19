using System.Globalization;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Comments;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Shared.Comments;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Comments;

/// <summary>What one audit entry says in the activity feed (ADR-0032). A whitelist: an action that is not here never appears, and only the fields below are ever read from its payload.</summary>
public sealed record ActivitySummary(string Kind, string Text, string? Detail = null, int? VersionNo = null);

public static class ActivitySummaries
{
    public static readonly IReadOnlyList<string> Actions =
    [
        "asset.created", "asset.version.added", "asset.status.changed", "asset.metadata.updated", "asset.legal_hold.changed",
        "workflow.task.completed", "workflow.sla.breached", "comment.resolved", "comment.reopened",
    ];

    /// <summary>The friendly kind and phrase for an audit action, or null when the action is not part of the feed.</summary>
    public static ActivitySummary? For(string action, JsonElement? payload) => action switch
    {
        "asset.created" => new("created", "created the asset"),
        "asset.version.added" => Int(payload, "VersionNo") is { } n ? new("version_added", $"uploaded version {n}", null, n) : new("version_added", "uploaded a new version"),
        "asset.status.changed" => Text(payload, "To") is { } to
            ? new("status_changed", $"changed the status to {Label(to)}", Text(payload, "From") is { } from ? $"from {Label(from)}" : null)
            : new("status_changed", "changed the status"),
        "asset.metadata.updated" => new("metadata_updated", "updated the details"),
        "asset.legal_hold.changed" => Bool(payload, "Hold") == false ? new("legal_hold", "released the legal hold") : new("legal_hold", "placed a legal hold"),
        "workflow.task.completed" => new("workflow_decision",
            Text(payload, "Decision") == "reject" ? "rejected a workflow step" : "approved a workflow step", Clip(Text(payload, "Comment"), 200)),
        "workflow.sla.breached" => new("workflow_overdue", "flagged a workflow step as overdue"),
        "comment.resolved" => new("resolved", "resolved a comment thread"),
        "comment.reopened" => new("reopened", "reopened a comment thread"),
        _ => null,
    };

    private static string Label(string status) => status.Replace('_', ' ');

    private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..(max - 1)].TrimEnd() + "…";

    private static string? Text(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool? Bool(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>The paging cursor: the time and id of the last item returned. Opaque to clients.</summary>
    public static string EncodeCursor(DateTimeOffset at, Guid id) => $"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{id:N}";

    public static (DateTimeOffset At, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        var parts = cursor.Split('.');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.Ticks && Guid.TryParseExact(parts[1], "N", out var id)
            ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
            : null;
    }
}

// ----------------------------------------------------------------------------------------------------- feed

/// <summary>
/// A curated, merged, newest-first stream for everyone who may read the asset: its comments and replies, thread resolutions, and a whitelist of
/// lifecycle events. It is not the audit log (that needs <c>audit.read</c>): no before/after payloads, no IP addresses, only friendly phrases.
/// </summary>
public sealed record ListActivityQuery(Guid AssetId, string? Cursor, int? Limit) : IQuery<Page<ActivityItemDto>>;

public sealed class ListActivityValidator : AbstractValidator<ListActivityQuery>
{
    public ListActivityValidator() =>
        RuleFor(x => x.Cursor).Must(c => string.IsNullOrWhiteSpace(c) || ActivitySummaries.DecodeCursor(c) is not null).WithMessage("Not a cursor from this feed.").OverridePropertyName("cursor");
}

public sealed class ListActivityHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<Comment> comments, IRepository<AssetVersion> versions, IRepository<User> users,
    IAuthorizationService authz, IAssetActivityReader activity, IQueryExecutor q) : IRequestHandler<ListActivityQuery, Page<ActivityItemDto>>
{
    public async Task<Result<Page<ActivityItemDto>>> Handle(ListActivityQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        var limit = Paging.Clamp(r.Limit);
        var before = ActivitySummaries.DecodeCursor(r.Cursor);

        var commentQuery = comments.Query().Where(c => c.AssetId == asset.Id && c.DeletedAt == null);
        if (before is { } b) commentQuery = commentQuery.Where(c => c.CreatedAt < b.At || (c.CreatedAt == b.At && c.Id.CompareTo(b.Id) < 0));
        var commentRows = await q.ToListAsync(commentQuery.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(limit + 1), ct);
        var auditRows = await activity.ReadAsync(asset.Id, ActivitySummaries.Actions, before, limit + 1, ct);

        // The audit trail also remembers threads that were deleted since; those do not belong in the feed.
        var deleted = (await q.ToListAsync(comments.Query().Where(c => c.AssetId == asset.Id && c.DeletedAt != null).Select(c => c.Id), ct)).ToHashSet();
        var versionRows = await q.ToListAsync(versions.Query().Where(v => v.AssetId == asset.Id).Select(v => new { v.Id, v.VersionNo, v.CreatedBy }), ct);
        var versionNos = versionRows.ToDictionary(v => v.Id, v => v.VersionNo);
        var uploaders = versionRows.Where(v => v.CreatedBy != null).ToDictionary(v => v.VersionNo, v => v.CreatedBy!.Value);

        var peopleIds = commentRows.Select(c => c.AuthorId).Concat(uploaders.Values).Distinct().ToList();
        var names = (await q.ToListAsync(users.Query().Where(u => peopleIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Email }), ct))
            .ToDictionary(u => u.Id, u => CommentMapper.NameOf(u.DisplayName, u.Email));

        var items = new List<ActivityItemDto>();
        foreach (var c in commentRows)
        {
            var kind = c.IsRoot ? "comment" : "reply";
            var text = c.IsRoot ? (c.AnchorType is null ? "added a comment" : "added an annotation") : "replied to a comment";
            items.Add(new ActivityItemDto(c.Id, kind, c.CreatedAt, c.AuthorId, names.GetValueOrDefault(c.AuthorId) ?? "Unknown user", text, c.Id,
                c.VersionId is { } v && versionNos.TryGetValue(v, out var no) ? no : null, CommentText.Excerpt(c.Body, 160)));
        }
        foreach (var a in auditRows)
        {
            if (ActivitySummaries.For(a.Action, a.Payload) is not { } s) continue;
            if (a.Action.StartsWith("comment.", StringComparison.Ordinal) && a.EntityId is { } cid && deleted.Contains(cid)) continue;
            Guid? actorId = a.ActorUserId;
            var actorName = a.ActorName;
            if (actorId is null && s.Kind == "version_added" && s.VersionNo is { } n && uploaders.TryGetValue(n, out var uploader))
            {
                actorId = uploader;
                actorName = names.GetValueOrDefault(uploader);
            }
            actorName ??= Guid.TryParse(a.Actor, out _) ? "Unknown user" : "System";
            items.Add(new ActivityItemDto(a.Id, s.Kind, a.At, actorId, actorName, s.Text, a.Action.StartsWith("comment.", StringComparison.Ordinal) ? a.EntityId : null, s.VersionNo, s.Detail));
        }

        var ordered = items.OrderByDescending(i => i.OccurredAt).ThenByDescending(i => i.Id).ToList();
        var more = ordered.Count > limit || commentRows.Count > limit || auditRows.Count > limit;
        var page = ordered.Take(limit).ToList();
        return new Page<ActivityItemDto>(page, more && page.Count > 0 ? ActivitySummaries.EncodeCursor(page[^1].OccurredAt, page[^1].Id) : null);
    }
}

// ------------------------------------------------------------------------------------------------- mentionable

/// <summary>
/// People the caller can @mention on this asset: active people in the tenant who can read it, matched by name or email (prefix matches first),
/// at most 10, never the caller. Needs <c>comments.write</c> and read access to the asset, so the picker cannot be used to list a tenant's people.
/// </summary>
public sealed record ListMentionableQuery(Guid AssetId, string? Query) : IQuery<IReadOnlyList<MentionableUserDto>>;

public sealed class ListMentionableHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<User> users, IAuthorizationService authz, AssetReaders readers, IQueryExecutor q)
    : IRequestHandler<ListMentionableQuery, IReadOnlyList<MentionableUserDto>>
{
    public const int MaxResults = 10;
    private const int PageSize = 100, MaxPages = 10, MaxQueryLength = 100;

    public async Task<Result<IReadOnlyList<MentionableUserDto>>> Handle(ListMentionableQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        var resource = asset?.ToResource(path);
        if (asset is null || resource is null || !await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.CommentsWrite, resource, ct)) return Error.Forbidden("Commenting needs the comments.write permission.");
        var me = (await authz.GetProfileAsync(ct)).UserId;
        var needle = (r.Query ?? "").Trim().ToLowerInvariant();
        if (needle.Length > MaxQueryLength) needle = needle[..MaxQueryLength];

        IQueryable<User> query = users.Query().Where(u => u.Status == UserStatus.Active && u.Id != me);
        if (needle.Length > 0) query = query.Where(u => u.DisplayName.ToLower().Contains(needle) || u.Email.ToLower().Contains(needle));
        var ordered = query.OrderBy(u => u.DisplayName.ToLower().StartsWith(needle) || u.Email.ToLower().StartsWith(needle) ? 0 : 1).ThenBy(u => u.DisplayName).ThenBy(u => u.Id);

        var found = new List<MentionableUserDto>();
        for (var page = 0; page < MaxPages && found.Count < MaxResults; page++)
        {
            var candidates = await q.ToListAsync(ordered.Skip(page * PageSize).Take(PageSize).Select(u => new { u.Id, u.DisplayName, u.Email }), ct);
            if (candidates.Count == 0) break;
            var readable = await readers.ReadersAsync(asset, path, candidates.Select(c => c.Id), ct);
            foreach (var c in candidates)
            {
                if (!readable.ContainsKey(c.Id)) continue;
                found.Add(new MentionableUserDto(c.Id, CommentMapper.NameOf(c.DisplayName, c.Email), c.Email));
                if (found.Count == MaxResults) break;
            }
            if (candidates.Count < PageSize) break;
        }
        return found;
    }
}
