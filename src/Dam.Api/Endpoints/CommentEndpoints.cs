using Dam.Application.Comments;
using Dam.Application.Messaging;
using Dam.Shared.Comments;
using Dam.Shared.Identity;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>Threaded comments, annotations, the activity feed and the mention picker (ADR-0032).</summary>
public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/assets/{id:guid}/comments", (Guid id, IDispatcher d, CancellationToken ct, Guid? versionId, string? state, string? kind) =>
                Run(d, new ListCommentsQuery(id, versionId, state, kind), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Comments").WithName("ListComments")
            .WithSummary("Comment threads on an asset, oldest thread first, each with its replies oldest first, plus open/resolved/annotation counts for the version. versionId narrows to one version; state=open|resolved|all (default); kind=annotation|comment|all (default). Needs assets.read on the asset")
            .Produces<CommentThreadsDto>();
        v1.MapPost("/assets/{id:guid}/comments", (Guid id, AddCommentInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new AddCommentCommand(id, r.Body, r.VersionId, r.ParentId, r.Anchor), v => Task.FromResult(Results.Created($"/api/v1/assets/{id}/comments", v)), ct))
            .WithTags("Comments").WithName("AddComment")
            .WithSummary("Adds a comment (on the current version unless versionId is given) or, with parentId, a reply to the first comment of a thread. A first comment may carry an anchor: a normalised region or pin on an image or page, or a timecode on video or audio. Needs comments.write and assets.read. Problems are 422 keyed by field (body, parentId, versionId, anchor.x ...)")
            .Produces<CommentDto>(StatusCodes.Status201Created);
        v1.MapPatch("/comments/{id:guid}", (Guid id, UpdateCommentInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateCommentCommand(id, r.Body, r.Resolved), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Comments").WithName("UpdateComment")
            .WithSummary("Edits the text (author only) and/or resolves (resolved=true) or reopens (false) a thread; a null field is left alone, and only a thread's first comment can be resolved. Needs comments.write")
            .Produces<CommentDto>();
        v1.MapDelete("/comments/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteCommentCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithTags("Comments").WithName("DeleteComment")
            .WithSummary("Deletes a comment (its author, or anyone who may update the asset). The text is erased; a first comment that still has replies stays as a '[deleted]' placeholder");

        v1.MapGet("/assets/{id:guid}/activity", (Guid id, IDispatcher d, CancellationToken ct, string? cursor, int? limit) =>
                Run(d, new ListActivityQuery(id, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Comments").WithName("ListAssetActivity")
            .WithSummary("A curated, newest-first feed of what happened to an asset: comments, replies, thread resolutions, versions, status changes, edits, approvals. Not the audit log (no before/after, no IPs). Needs assets.read; limit up to 200, cursor from nextCursor")
            .Produces<Page<ActivityItemDto>>();
        v1.MapGet("/assets/{id:guid}/mentionable", (Guid id, IDispatcher d, CancellationToken ct, string? q) =>
                Run(d, new ListMentionableQuery(id, q), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Comments").WithName("ListMentionableUsers")
            .WithSummary("Up to 10 active people who can read the asset and match q by name or email, for the @mention picker. Needs comments.write and assets.read, so it cannot be used to list a tenant")
            .Produces<IReadOnlyList<MentionableUserDto>>();
    }
}
