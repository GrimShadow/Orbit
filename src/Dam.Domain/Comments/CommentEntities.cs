using Dam.Domain.Common;

namespace Dam.Domain.Comments;

/// <param name="ParentId">Set for a reply.</param>
/// <param name="AnchorType">region, point or timecode; null for a plain comment or a reply.</param>
/// <param name="Mentions">The people named who could see the asset (ADR-0032). The text is not part of any event.</param>
public sealed record CommentAdded(
    Guid CommentId, Guid AssetId, Guid? VersionId, Guid? ParentId, Guid AuthorId, string? AnchorType, IReadOnlyList<Guid> Mentions, bool Resolved)
    : DomainEvent, IEntityEvent
{
    public override string Name => "comment.added";
    public string EntityType => "comment";
    public Guid EntityId => CommentId;
}

public sealed record CommentResolved(Guid CommentId, Guid AssetId, Guid? VersionId, Guid AuthorId, Guid ResolvedBy) : DomainEvent, IEntityEvent
{
    public override string Name => "comment.resolved";
    public string EntityType => "comment";
    public Guid EntityId => CommentId;
}

public sealed record CommentReopened(Guid CommentId, Guid AssetId, Guid? VersionId, Guid AuthorId, Guid ReopenedBy) : DomainEvent, IEntityEvent
{
    public override string Name => "comment.reopened";
    public string EntityType => "comment";
    public Guid EntityId => CommentId;
}

/// <summary>Internal (audit trail and notifications for people newly mentioned; not delivered to webhooks).</summary>
public sealed record CommentEdited(Guid CommentId, Guid AssetId, Guid AuthorId, IReadOnlyList<Guid> AddedMentions) : DomainEvent, IEntityEvent
{
    public const string EventName = "comment.edited";
    public override string Name => EventName;
    public string EntityType => "comment";
    public Guid EntityId => CommentId;
}

/// <summary>Internal (audit trail only).</summary>
public sealed record CommentDeleted(Guid CommentId, Guid AssetId, Guid DeletedBy) : DomainEvent, IEntityEvent
{
    public override string Name => "comment.deleted";
    public string EntityType => "comment";
    public Guid EntityId => CommentId;
}

/// <summary>
/// A note on an asset, written against the version that was current at the time (ADR-0032). The first comment of a thread may carry an
/// anchor (a region of an image or page, a pin, or a timecode); replies attach to that first comment and never to each other. Resolving
/// belongs to the thread, so it lives on the first comment. Deleting keeps the row (for the thread's shape and the audit trail) but erases
/// the text.
/// </summary>
public sealed class Comment : AggregateRoot
{
    private Comment() { }

    public Guid AssetId { get; private set; }
    public Guid? VersionId { get; private set; }
    public Guid? ParentId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = "";
    /// <summary>The anchor as stored (jsonb); see <see cref="CommentAnchor.FromJson"/>.</summary>
    public string? AnchorJson { get; private set; }
    public string? AnchorType { get; private set; }
    public bool Resolved { get; private set; }
    public Guid? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? EditedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsRoot => ParentId is null;
    public bool IsDeleted => DeletedAt is not null;

    public static Comment Create(Guid tenantId, Guid assetId, Guid? versionId, Guid? parentId, Guid authorId, string body, CommentAnchor? anchor,
        IReadOnlyList<Guid> mentions, DateTimeOffset now)
    {
        var c = new Comment
        {
            TenantId = tenantId, AssetId = assetId, VersionId = versionId, ParentId = parentId, AuthorId = authorId, Body = body,
            AnchorJson = anchor?.ToJson(), AnchorType = anchor?.Type, CreatedAt = now,
        };
        c.Raise(new CommentAdded(c.Id, assetId, versionId, parentId, authorId, c.AnchorType, mentions, Resolved: false));
        return c;
    }

    /// <returns>False when the text is the same as before (nothing recorded).</returns>
    public bool Edit(string body, IReadOnlyList<Guid> addedMentions, DateTimeOffset now)
    {
        if (body == Body) return false;
        Body = body;
        EditedAt = now;
        Raise(new CommentEdited(Id, AssetId, AuthorId, addedMentions));
        return true;
    }

    /// <returns>False when the thread was already resolved.</returns>
    public bool Resolve(Guid by, DateTimeOffset now)
    {
        if (Resolved) return false;
        Resolved = true;
        ResolvedBy = by;
        ResolvedAt = now;
        Raise(new CommentResolved(Id, AssetId, VersionId, AuthorId, by));
        return true;
    }

    /// <returns>False when the thread was not resolved.</returns>
    public bool Reopen(Guid by)
    {
        if (!Resolved) return false;
        Resolved = false;
        ResolvedBy = null;
        ResolvedAt = null;
        Raise(new CommentReopened(Id, AssetId, VersionId, AuthorId, by));
        return true;
    }

    public void Delete(Guid by, DateTimeOffset now)
    {
        if (IsDeleted) return;
        DeletedAt = now;
        Body = "";
        Raise(new CommentDeleted(Id, AssetId, by));
    }
}

/// <summary>A person named in a comment who could see the asset when it was written. Tenant-scoped like every other table.</summary>
public sealed class CommentMention : ITenantScoped
{
    public Guid CommentId { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
}
