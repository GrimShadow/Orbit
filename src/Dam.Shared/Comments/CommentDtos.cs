namespace Dam.Shared.Comments;

/// <summary>
/// Where a comment points, normalised so it survives any rendering size (ADR-0032). Which fields are used depends on <paramref name="Type"/>:
/// <c>region</c> uses x, y, w, h (and optionally page), <c>point</c> uses x, y (and optionally page), <c>timecode</c> uses startMs and optionally endMs.
/// Unused fields are null.
/// </summary>
/// <param name="Type">region, point or timecode.</param>
/// <param name="X">Left edge, 0 to 1 of the rendered width.</param>
/// <param name="Y">Top edge, 0 to 1 of the rendered height.</param>
/// <param name="W">Width, greater than 0, with x + w at most 1.</param>
/// <param name="H">Height, greater than 0, with y + h at most 1.</param>
/// <param name="Page">1-based page of a multi-page document (regions and points only).</param>
/// <param name="StartMs">Start of the moment or range in a video or audio file, in milliseconds.</param>
/// <param name="EndMs">End of the range, greater than startMs; null for a single moment.</param>
public sealed record AnchorDto(string Type, double? X = null, double? Y = null, double? W = null, double? H = null, int? Page = null, long? StartMs = null, long? EndMs = null);

/// <summary>A person named in a comment, with their current name.</summary>
public sealed record MentionDto(Guid UserId, string DisplayName);

/// <param name="VersionId">The asset version the comment is about; null for an asset that had no file yet. A reply has its thread's version.</param>
/// <param name="VersionNo">That version's number.</param>
/// <param name="ParentId">The first comment of the thread a reply belongs to; null for the first comment itself.</param>
/// <param name="Body">Plain text as stored: mentions appear as <c>@[Display Name](user:id)</c> tokens. A deleted comment shows <c>[deleted]</c>.</param>
/// <param name="Mentions">The people the comment really mentions (those who could see the asset), with their current names.</param>
/// <param name="Anchor">Only on the first comment of a thread; null for a plain comment, a reply or a deleted comment.</param>
/// <param name="Resolved">Whether the thread is resolved. A reply shows its thread's state and who resolved it.</param>
/// <param name="Edited">True once the text was changed after posting.</param>
/// <param name="Deleted">True for the placeholder of a deleted first comment that still has replies.</param>
public sealed record CommentDto(
    Guid Id, Guid AssetId, Guid? VersionId, int? VersionNo, Guid? ParentId, Guid AuthorId, string AuthorName, string Body,
    IReadOnlyList<MentionDto> Mentions, AnchorDto? Anchor, bool Resolved, Guid? ResolvedBy, string? ResolvedByName, DateTimeOffset? ResolvedAt,
    bool Edited, DateTimeOffset? EditedAt, bool Deleted, DateTimeOffset CreatedAt);

/// <param name="Root">The first comment of the thread.</param>
/// <param name="Replies">Replies, oldest first.</param>
public sealed record CommentThreadDto(CommentDto Root, IReadOnlyList<CommentDto> Replies);

/// <summary>Thread counts for the version the list was asked about (or all versions), regardless of the state and kind filters.</summary>
/// <param name="Open">Unresolved threads.</param>
/// <param name="Resolved">Resolved threads.</param>
/// <param name="Annotations">Threads that carry an anchor (open or resolved).</param>
public sealed record CommentCountsDto(int Open, int Resolved, int Annotations);

/// <param name="Threads">Oldest thread first (reading order).</param>
public sealed record CommentThreadsDto(IReadOnlyList<CommentThreadDto> Threads, CommentCountsDto Counts);

/// <param name="Body">Plain text, up to 4,000 characters; mentions as <c>@[Display Name](user:id)</c>.</param>
/// <param name="VersionId">Defaults to the asset's current version.</param>
/// <param name="ParentId">Set to reply; must be the first comment of a thread.</param>
/// <param name="Anchor">Only on a first comment.</param>
public sealed record AddCommentInput(string? Body, Guid? VersionId, Guid? ParentId, AnchorDto? Anchor);

/// <param name="Body">New text (author only); null leaves it.</param>
/// <param name="Resolved">true resolves, false reopens a thread (first comments only); null leaves it.</param>
public sealed record UpdateCommentInput(string? Body, bool? Resolved);

/// <param name="Id">The audit entry or comment this item stands for.</param>
/// <param name="Kind">comment, reply, resolved, reopened, created, version_added, status_changed, metadata_updated, legal_hold, workflow_decision or workflow_overdue.</param>
/// <param name="ActorId">The Orbit user; null for the system.</param>
/// <param name="Summary">A short phrase that follows the actor's name, for example "uploaded version 3".</param>
/// <param name="Detail">A little more: the comment excerpt, "draft to in review", the reviewer's comment. Never a before/after payload.</param>
public sealed record ActivityItemDto(
    Guid Id, string Kind, DateTimeOffset OccurredAt, Guid? ActorId, string ActorName, string Summary, Guid? CommentId, int? VersionNo, string? Detail);

public sealed record MentionableUserDto(Guid Id, string DisplayName, string Email);
