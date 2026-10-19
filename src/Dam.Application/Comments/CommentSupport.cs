using Dam.Application.Abstractions;
using Dam.Application.Notifications;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Comments;
using Dam.Domain.Identity;
using Dam.Shared.Comments;

namespace Dam.Application.Comments;

/// <summary>
/// Who could see an asset, for people other than the caller (ADR-0032). Mentions, comment notifications and the mention picker all ask the same
/// question the same way the notification planner does: build the person's access profile from what is stored and run the policy engine
/// against the asset, so a person an access rule keeps away from the asset is never offered, mentioned or notified.
/// </summary>
public sealed class AssetReaders(UserProfiles profiles, IRepository<User> users, IQueryExecutor q)
{
    /// <summary>The active people among <paramref name="userIds"/> who may read the asset, with their display names.</summary>
    public async Task<Dictionary<Guid, string>> ReadersAsync(Asset asset, string? folderPath, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var resource = asset.ToResource(folderPath);
        var loaded = await profiles.LoadAsync(ids, ct);
        var allowed = loaded.Where(p => PolicyEngine.Can(p.Value, Permissions.AssetsRead, resource)).Select(p => p.Key).ToList();
        if (allowed.Count == 0) return [];
        var rows = await q.ToListAsync(users.Query().Where(u => allowed.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Email }), ct);
        return rows.ToDictionary(u => u.Id, u => CommentMapper.NameOf(u.DisplayName, u.Email));
    }
}

/// <summary>Turns comments into <see cref="CommentDto"/>s: the names, version numbers and mentions they show, and a thread's state shared by its replies.</summary>
public sealed class CommentMapper(IRepository<User> users, IRepository<AssetVersion> versions, IRepository<CommentMention> mentions, IRepository<Comment> comments, IQueryExecutor q)
{
    public const string DeletedBody = "[deleted]";

    public static string NameOf(string? displayName, string? email) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName : !string.IsNullOrWhiteSpace(email) ? email : "Unknown user";

    public static AnchorDto? ToDto(CommentAnchor? a) => a is null ? null : new AnchorDto(a.Type, a.X, a.Y, a.W, a.H, a.Page, a.StartMs, a.EndMs);
    public static CommentAnchor? ToAnchor(AnchorDto? a) => a is null ? null : new CommentAnchor(a.Type?.Trim().ToLowerInvariant() ?? "", a.X, a.Y, a.W, a.H, a.Page, a.StartMs, a.EndMs);

    /// <param name="roots">The first comment of every thread the list's replies belong to (a reply's thread state is its root's).</param>
    /// <param name="knownMentions">Mentions not yet saved (a comment being created or edited in this request); everything else is read.</param>
    public async Task<List<CommentDto>> ToDtosAsync(
        IReadOnlyList<Comment> list, IReadOnlyDictionary<Guid, Comment> roots, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? knownMentions, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var ids = list.Select(c => c.Id).ToList();
        var stored = new Dictionary<Guid, List<Guid>>();
        var toRead = ids.Where(id => knownMentions is null || !knownMentions.ContainsKey(id)).ToList();
        if (toRead.Count > 0)
            foreach (var m in await q.ToListAsync(mentions.Query().Where(m => toRead.Contains(m.CommentId)), ct))
                (stored.TryGetValue(m.CommentId, out var l) ? l : stored[m.CommentId] = []).Add(m.UserId);
        IReadOnlyList<Guid> MentionsOf(Comment c) => knownMentions is not null && knownMentions.TryGetValue(c.Id, out var k) ? k : stored.GetValueOrDefault(c.Id) ?? [];

        var people = new HashSet<Guid>(list.Select(c => c.AuthorId));
        foreach (var c in list)
        {
            foreach (var m in MentionsOf(c)) people.Add(m);
            if (RootOf(c, roots).ResolvedBy is { } by) people.Add(by);
        }
        var peopleIds = people.ToList();
        var names = (await q.ToListAsync(users.Query().Where(u => peopleIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Email }), ct))
            .ToDictionary(u => u.Id, u => NameOf(u.DisplayName, u.Email));

        var versionIds = list.Where(c => c.VersionId != null).Select(c => c.VersionId!.Value).Distinct().ToList();
        var versionNos = versionIds.Count == 0 ? [] : (await q.ToListAsync(versions.Query().Where(v => versionIds.Contains(v.Id)).Select(v => new { v.Id, v.VersionNo }), ct))
            .ToDictionary(v => v.Id, v => v.VersionNo);

        return list.Select(c =>
        {
            var root = RootOf(c, roots);
            var deleted = c.IsDeleted;
            return new CommentDto(
                c.Id, c.AssetId, c.VersionId, c.VersionId is { } v && versionNos.TryGetValue(v, out var no) ? no : null, c.ParentId, c.AuthorId,
                names.GetValueOrDefault(c.AuthorId) ?? "Unknown user", deleted ? DeletedBody : c.Body,
                deleted ? [] : MentionsOf(c).Where(names.ContainsKey).Select(id => new MentionDto(id, names[id])).ToList(),
                deleted ? null : ToDto(CommentAnchor.FromJson(c.AnchorJson)),
                root.Resolved, root.ResolvedBy, root.ResolvedBy is { } rb ? names.GetValueOrDefault(rb) : null, root.ResolvedAt,
                !deleted && c.EditedAt is not null, deleted ? null : c.EditedAt, deleted, c.CreatedAt);
        }).ToList();
    }

    private static Comment RootOf(Comment c, IReadOnlyDictionary<Guid, Comment> roots) =>
        c.ParentId is { } p && roots.TryGetValue(p, out var r) ? r : c;

    /// <summary>Loads the first comments for a set of comments (themselves when they are first comments).</summary>
    public async Task<Dictionary<Guid, Comment>> RootsOfAsync(IEnumerable<Comment> list, CancellationToken ct)
    {
        var all = list.ToList();
        var result = all.Where(c => c.IsRoot).ToDictionary(c => c.Id);
        var missing = all.Where(c => c.ParentId is { } p && !result.ContainsKey(p)).Select(c => c.ParentId!.Value).Distinct().ToList();
        if (missing.Count > 0)
            foreach (var r in await q.ToListAsync(comments.Query().Where(c => missing.Contains(c.Id)), ct)) result[r.Id] = r;
        return result;
    }
}
