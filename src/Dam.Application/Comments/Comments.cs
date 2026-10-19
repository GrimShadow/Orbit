using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Comments;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Comments;
using FluentValidation;

namespace Dam.Application.Comments;

// ------------------------------------------------------------------------------------------------------ list

/// <summary>
/// The comment threads of an asset, oldest thread first (reading order), each with its replies oldest first. Needs <c>assets.read</c> on the
/// asset (an asset the caller cannot see is 404). The counts describe the version asked about regardless of the state and kind filters.
/// </summary>
/// <param name="State">open, resolved or all (default).</param>
/// <param name="Kind">annotation (threads with an anchor), comment (threads without) or all (default).</param>
public sealed record ListCommentsQuery(Guid AssetId, Guid? VersionId, string? State, string? Kind) : IQuery<CommentThreadsDto>;

public sealed class ListCommentsValidator : AbstractValidator<ListCommentsQuery>
{
    public ListCommentsValidator()
    {
        RuleFor(x => x.State).Must(s => s is null or "all" or "open" or "resolved").WithMessage("Use open, resolved or all.").OverridePropertyName("state");
        RuleFor(x => x.Kind).Must(k => k is null or "all" or "annotation" or "comment").WithMessage("Use annotation, comment or all.").OverridePropertyName("kind");
    }
}

public sealed class ListCommentsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<Comment> comments, IAuthorizationService authz, CommentMapper mapper, IQueryExecutor q)
    : IRequestHandler<ListCommentsQuery, CommentThreadsDto>
{
    public async Task<Result<CommentThreadsDto>> Handle(ListCommentsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");

        var roots = comments.Query().Where(c => c.AssetId == asset.Id && c.ParentId == null);
        if (r.VersionId is { } version) roots = roots.Where(c => c.VersionId == version);
        var rootList = await q.ToListAsync(roots.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id), ct);
        var rootIds = rootList.Select(c => c.Id).ToList();
        var replyList = rootIds.Count == 0 ? [] : await q.ToListAsync(
            comments.Query().Where(c => c.AssetId == asset.Id && c.ParentId != null && c.DeletedAt == null && rootIds.Contains(c.ParentId!.Value))
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id), ct);
        var repliesByRoot = replyList.GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        // A deleted first comment stays as a placeholder only while live replies hang from it.
        var visible = rootList.Where(c => !c.IsDeleted || repliesByRoot.ContainsKey(c.Id)).ToList();
        var counts = new CommentCountsDto(visible.Count(c => !c.Resolved), visible.Count(c => c.Resolved), visible.Count(c => !c.IsDeleted && c.AnchorType != null));

        IEnumerable<Comment> shown = visible;
        if (r.State == "open") shown = shown.Where(c => !c.Resolved);
        else if (r.State == "resolved") shown = shown.Where(c => c.Resolved);
        if (r.Kind == "annotation") shown = shown.Where(c => !c.IsDeleted && c.AnchorType != null);
        else if (r.Kind == "comment") shown = shown.Where(c => c.IsDeleted || c.AnchorType == null);
        var threads = shown.ToList();

        var everything = threads.Concat(threads.SelectMany(t => repliesByRoot.GetValueOrDefault(t.Id) ?? [])).ToList();
        var dtos = (await mapper.ToDtosAsync(everything, threads.ToDictionary(t => t.Id), null, ct)).ToDictionary(d => d.Id);
        return new CommentThreadsDto(
            threads.Select(t => new CommentThreadDto(dtos[t.Id], (repliesByRoot.GetValueOrDefault(t.Id) ?? []).Select(x => dtos[x.Id]).ToList())).ToList(), counts);
    }
}

// ------------------------------------------------------------------------------------------------------- add

/// <summary>
/// Adds a comment, or a reply when <paramref name="ParentId"/> is set. Needs <c>comments.write</c> and <c>assets.read</c> on the asset. A reply can
/// only target the first comment of a thread and carries no anchor of its own; a first comment may carry one. Mentions are kept only for
/// people who can see the asset.
/// </summary>
public sealed record AddCommentCommand(Guid AssetId, string? Body, Guid? VersionId, Guid? ParentId, AnchorDto? Anchor) : ICommand<CommentDto>;

public sealed class AddCommentValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentValidator() => RuleFor(x => x.Body).CommentBody();
}

internal static class CommentRulesExtensions
{
    /// <summary>The text rules, reported under <c>body</c>: something to say, at most 4,000 characters, no more than 20 mentions, no NUL.</summary>
    public static IRuleBuilderOptions<T, string?> CommentBody<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("Write something first.")
            .Must(b => b is null || b.Trim().Length <= CommentText.MaxLength).WithMessage($"At most {CommentText.MaxLength} characters.")
            .Must(b => b is null || !b.Contains('\0', StringComparison.Ordinal)).WithMessage("Contains characters that cannot be stored.")
            .Must(b => b is null || CommentText.MentionedIds(b).Count <= CommentText.MaxMentions).WithMessage($"At most {CommentText.MaxMentions} people can be mentioned in one comment.")
            .OverridePropertyName("body");
}

public sealed class AddCommentHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<Comment> comments, IRepository<CommentMention> mentions, IRepository<AssetVersion> versions,
    IAuthorizationService authz, AssetReaders readers, CommentMapper mapper, ITenantContext tenant, IClock clock, IQueryExecutor q)
    : IRequestHandler<AddCommentCommand, CommentDto>
{
    public async Task<Result<CommentDto>> Handle(AddCommentCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        var resource = asset?.ToResource(path);
        if (asset is null || resource is null || !await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.CommentsWrite, resource, ct)) return Error.Forbidden("Commenting needs the comments.write permission.");
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return Error.Conflict($"The asset is {asset.Status}.");
        var me = (await authz.GetProfileAsync(ct)).UserId;

        Comment? parent = null;
        if (c.ParentId is { } parentId)
        {
            parent = await q.FirstOrDefaultAsync(comments.Query().Where(x => x.Id == parentId && x.AssetId == asset.Id), ct);
            if (parent is null) return Error.Validation(ContentMapping.Fail("parentId", "Comment not found on this asset."));
            if (!parent.IsRoot) return Error.Validation(ContentMapping.Fail("parentId", "Reply to the first comment of the thread, not to a reply."));
            if (parent.IsDeleted) return Error.Validation(ContentMapping.Fail("parentId", "That comment was deleted."));
            if (c.Anchor is not null) return Error.Validation(ContentMapping.Fail("anchor", "A reply cannot carry an anchor."));
        }

        Guid? versionId;
        if (parent is not null)
        {
            if (c.VersionId is { } given && given != parent.VersionId) return Error.Validation(ContentMapping.Fail("versionId", "A reply belongs to the version of its thread."));
            versionId = parent.VersionId;
        }
        else if (c.VersionId is { } requested)
        {
            if (!await q.AnyAsync(versions.Query().Where(v => v.Id == requested && v.AssetId == asset.Id), ct))
                return Error.Validation(ContentMapping.Fail("versionId", "That version does not belong to this asset."));
            versionId = requested;
        }
        else versionId = asset.CurrentVersionId;

        var anchor = CommentMapper.ToAnchor(c.Anchor);
        if (anchor is not null)
        {
            var problems = anchor.Validate(asset.Type);
            if (problems.Count > 0) return Error.Validation(problems.ToDictionary(p => p.Key, p => p.Value));
        }

        var (body, mentioned) = await NormalizeAsync(asset, path, c.Body!.Trim(), ct);
        var comment = Comment.Create(tenant.TenantId, asset.Id, versionId, parent?.Id, me, body, anchor, mentioned, clock.UtcNow);
        comments.Add(comment);
        mentions.AddRange(mentioned.Select(u => new CommentMention { CommentId = comment.Id, UserId = u, TenantId = tenant.TenantId }));

        var roots = parent is null ? new Dictionary<Guid, Comment>() : new Dictionary<Guid, Comment> { [parent.Id] = parent };
        return (await mapper.ToDtosAsync([comment], roots, new Dictionary<Guid, IReadOnlyList<Guid>> { [comment.Id] = mentioned }, ct))[0];
    }

    private async Task<(string Body, IReadOnlyList<Guid> Mentioned)> NormalizeAsync(Asset asset, string? path, string body, CancellationToken ct)
    {
        var named = CommentText.MentionedIds(body);
        if (named.Count == 0) return (body, []);
        var eligible = await readers.ReadersAsync(asset, path, named, ct);
        return (CommentText.Normalize(body, eligible), named.Where(eligible.ContainsKey).ToList());
    }
}

// ----------------------------------------------------------------------------------------------------- update

/// <summary>
/// Edits the text (the author only) and/or resolves or reopens the thread (the first comment only). A null field is left alone. Both need
/// <c>comments.write</c>; people newly mentioned by an edit are notified, people already mentioned are not told again.
/// </summary>
public sealed record UpdateCommentCommand(Guid CommentId, string? Body, bool? Resolved) : ICommand<CommentDto>;

public sealed class UpdateCommentValidator : AbstractValidator<UpdateCommentCommand>
{
    public UpdateCommentValidator()
    {
        When(x => x.Body is not null, () => RuleFor(x => x.Body).CommentBody());
        RuleFor(x => x).Must(x => x.Body is not null || x.Resolved is not null).WithMessage("Send a new body and/or resolved.").OverridePropertyName("body");
    }
}

public sealed class UpdateCommentHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<Comment> comments, IRepository<CommentMention> mentions,
    IAuthorizationService authz, AssetReaders readers, CommentMapper mapper, ITenantContext tenant, IClock clock, IQueryExecutor q)
    : IRequestHandler<UpdateCommentCommand, CommentDto>
{
    public async Task<Result<CommentDto>> Handle(UpdateCommentCommand c, CancellationToken ct)
    {
        var comment = await q.FirstOrDefaultAsync(comments.Query().Where(x => x.Id == c.CommentId), ct);
        if (comment is null || comment.IsDeleted) return Error.NotFound("Comment not found.");
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, comment.AssetId, ct);
        var resource = asset?.ToResource(path);
        if (asset is null || resource is null || !await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Comment not found.");
        if (!await authz.CanAsync(Permissions.CommentsWrite, resource, ct)) return Error.Forbidden("Commenting needs the comments.write permission.");
        var me = (await authz.GetProfileAsync(ct)).UserId;
        if (c.Body is not null && comment.AuthorId != me) return Error.Forbidden("Only the author can edit a comment.");
        if (c.Resolved is not null && !comment.IsRoot) return Error.Validation(ContentMapping.Fail("resolved", "Only the first comment of a thread can be resolved or reopened."));
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return Error.Conflict($"The asset is {asset.Status}.");

        var now = clock.UtcNow;
        var current = (await q.ToListAsync(mentions.Query().Where(m => m.CommentId == comment.Id), ct));
        IReadOnlyList<Guid> mentionIds = current.Select(m => m.UserId).ToList();
        if (c.Body is not null)
        {
            var named = CommentText.MentionedIds(c.Body.Trim());
            var eligible = named.Count == 0 ? [] : await readers.ReadersAsync(asset, path, named, ct);
            var body = CommentText.Normalize(c.Body.Trim(), eligible);
            var nowMentioned = named.Where(eligible.ContainsKey).ToList();
            var added = nowMentioned.Where(u => !mentionIds.Contains(u)).ToList();
            if (comment.Edit(body, added, now))
            {
                mentions.RemoveRange(current.Where(m => !nowMentioned.Contains(m.UserId)));
                mentions.AddRange(added.Select(u => new CommentMention { CommentId = comment.Id, UserId = u, TenantId = tenant.TenantId }));
                mentionIds = nowMentioned;
            }
        }
        if (c.Resolved is { } resolved)
        {
            if (resolved) comment.Resolve(me, now); else comment.Reopen(me);
        }

        var roots = await mapper.RootsOfAsync([comment], ct);
        return (await mapper.ToDtosAsync([comment], roots, new Dictionary<Guid, IReadOnlyList<Guid>> { [comment.Id] = mentionIds }, ct))[0];
    }
}

// ----------------------------------------------------------------------------------------------------- delete

/// <summary>
/// Deletes a comment: its author, or anyone who may update the asset. The text is erased and the comment hidden; a first comment that still
/// has replies stays as a "[deleted]" placeholder so the thread keeps its shape.
/// </summary>
public sealed record DeleteCommentCommand(Guid CommentId) : ICommand<Guid>;

public sealed class DeleteCommentHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<Comment> comments, IRepository<CommentMention> mentions,
    IAuthorizationService authz, IClock clock, IQueryExecutor q) : IRequestHandler<DeleteCommentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteCommentCommand c, CancellationToken ct)
    {
        var comment = await q.FirstOrDefaultAsync(comments.Query().Where(x => x.Id == c.CommentId), ct);
        if (comment is null || comment.IsDeleted) return Error.NotFound("Comment not found.");
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, comment.AssetId, ct);
        var resource = asset?.ToResource(path);
        if (asset is null || resource is null || !await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Comment not found.");
        var me = (await authz.GetProfileAsync(ct)).UserId;
        if (comment.AuthorId != me && !await authz.CanAsync(Permissions.AssetsUpdate, resource, ct))
            return Error.Forbidden("Only the author, or someone who can update the asset, can delete a comment.");
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return Error.Conflict($"The asset is {asset.Status}.");

        comment.Delete(me, clock.UtcNow);
        mentions.RemoveRange(await q.ToListAsync(mentions.Query().Where(m => m.CommentId == comment.Id), ct));
        return comment.Id;
    }
}
