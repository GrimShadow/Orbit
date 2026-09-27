using System.Text.Json;
using Dam.Application.Assets;
using Dam.Application.Messaging;
using Dam.Shared.Content;
using Dam.Shared.Identity;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

public sealed record CreateAssetRequest(Guid? FolderId, string Type, string Title, JsonElement? Metadata, IReadOnlyList<string>? Tags, bool AllowIncomplete = false);
public sealed record UpdateAssetRequest(string? Title, JsonElement? Metadata, IReadOnlyList<string>? Tags);
public sealed record MoveAssetRequest(Guid? FolderId);
public sealed record TransitionAssetRequest(string? Reason);
public sealed record ScheduleAssetRequest(DateTimeOffset? PublishAt, DateTimeOffset? ExpireAt);
public sealed record SetRetentionPolicyRequest(Guid? RetentionPolicyId);
public sealed record SetLegalHoldRequest(bool Hold);
public sealed record InitiateUploadRequest(string FileName, long Size, string? ContentType, bool AllowDuplicate = false, string? Sha256 = null);
public sealed record PartNumbersRequest(IReadOnlyList<int> PartNumbers);
public sealed record CompleteUploadRequest(IReadOnlyList<PartEtag>? Parts = null, string? Sha256 = null);
public sealed record BulkUploadRequest(Guid? FolderId, Guid? BatchId, JsonElement? Metadata, IReadOnlyList<string>? Tags, IReadOnlyList<BulkFileRequest> Files);

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this RouteGroupBuilder v1)
    {
        var assets = v1.MapGroup("/assets").WithTags("Assets");

        assets.MapGet("", (IDispatcher d, CancellationToken ct, Guid? folderId, bool? includeSubfolders, string? status, string? type, string? cursor, int? limit) =>
                Run(d, new ListAssetsQuery(folderId, includeSubfolders ?? true, status, type, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAssets").WithSummary("Assets the caller may read (interim listing; search arrives in Step 1.7)").Produces<Page<AssetDto>>();
        assets.MapPost("", (CreateAssetRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateAssetCommand(r.FolderId, r.Type, r.Title, r.Metadata, r.Tags, r.AllowIncomplete),
                    v => Task.FromResult(Results.Created($"/api/v1/assets/{v.Id}", v)), ct))
            .WithName("CreateAsset").WithSummary("Create an asset shell (draft). Metadata is validated against the type's schema").Produces<AssetDto>(StatusCodes.Status201Created);
        assets.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetAssetQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAsset").Produces<AssetDto>();
        assets.MapPatch("/{id:guid}", (Guid id, UpdateAssetRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateAssetCommand(id, r.Title, r.Metadata, r.Tags), aid => new GetAssetQuery(aid), ct))
            .WithName("UpdateAsset").Produces<AssetDto>();
        assets.MapPost("/{id:guid}/move", (Guid id, MoveAssetRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new MoveAssetCommand(id, r.FolderId), aid => new GetAssetQuery(aid), ct))
            .WithName("MoveAsset").Produces<AssetDto>();
        assets.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteAssetCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteAsset").WithSummary("Soft delete: the asset becomes 'deleted'");

        assets.MapGet("/{id:guid}/versions", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new ListVersionsQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAssetVersions").WithSummary("Every stored version, newest first, with the technical metadata read at ingest").Produces<IReadOnlyList<AssetVersionDto>>();
        assets.MapGet("/{id:guid}/versions/{versionNo:int}", (Guid id, int versionNo, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetVersionQuery(id, versionNo), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAssetVersion").Produces<AssetVersionDto>();
        assets.MapPost("/{id:guid}/versions/{versionNo:int}/restore", (Guid id, int versionNo, IDispatcher d, CancellationToken ct) =>
                Run(d, new RestoreVersionCommand(id, versionNo), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("RestoreAssetVersion").WithSummary("Makes an old version current again by copying it into a new version; history is never rewritten").Produces<AssetVersionDto>();
        assets.MapGet("/{id:guid}/versions/{versionNo:int}/compare/{otherVersionNo:int}", (Guid id, int versionNo, int otherVersionNo, IDispatcher d, CancellationToken ct) =>
                Run(d, new CompareVersionsQuery(id, versionNo, otherVersionNo), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("CompareAssetVersions").WithSummary("What differs between two versions: size, mime, checksum and technical metadata").Produces<VersionDiffDto>();
        assets.MapPost("/{id:guid}/transitions/{action}", (Guid id, string action, TransitionAssetRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new TransitionAssetCommand(id, action, r.Reason), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("TransitionAsset").WithSummary("Moves an asset along its lifecycle: submit, approve, reject, publish, withdraw, expire, archive, purge").Produces<AssetDto>();
        assets.MapPost("/{id:guid}/schedule", (Guid id, ScheduleAssetRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new ScheduleAssetCommand(id, r.PublishAt, r.ExpireAt), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ScheduleAsset").WithSummary("Sets or clears when the asset should publish or expire itself; the scheduler acts once it reaches the matching status").Produces<AssetDto>();
        assets.MapPost("/{id:guid}/retention-policy", (Guid id, SetRetentionPolicyRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new SetAssetRetentionPolicyCommand(id, r.RetentionPolicyId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("SetAssetRetentionPolicy").Produces<AssetDto>();
        assets.MapPost("/{id:guid}/legal-hold", (Guid id, SetLegalHoldRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new SetLegalHoldCommand(id, r.Hold), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("SetAssetLegalHold").WithSummary("Blocks (or unblocks) retention purge for this asset, regardless of its policy's schedule").Produces<AssetDto>();
        assets.MapGet("/{id:guid}/renditions", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new ListRenditionsQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAssetRenditions").WithSummary("Renditions already made for the current version").Produces<IReadOnlyList<RenditionDto>>();
        assets.MapPost("/{id:guid}/links", (Guid id, CreateLinkRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateDeliveryLinkCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("CreateDeliveryLink").WithSummary("Signed, expiring URLs to renditions or one transform, served by the delivery origin / CDN").Produces<DeliveryLinkDto>();
        assets.MapGet("/{id:guid}/similar", (Guid id, IDispatcher d, CancellationToken ct, string? by, int? maxDistance, int? limit) =>
                by is null or "phash"
                    ? Run(d, new FindSimilarQuery(id, maxDistance, limit), v => Task.FromResult(Results.Ok(v)), ct)
                    : Task.FromResult(Results.ValidationProblem(new Dictionary<string, string[]> { ["by"] = ["Only 'phash' is supported."] }, statusCode: 422)))
            .WithName("FindSimilarAssets").WithSummary("Near-duplicate images by perceptual hash (?by=phash&maxDistance=8)").Produces<IReadOnlyList<SimilarAssetDto>>();

        assets.MapPost("/{id:guid}/uploads", (Guid id, InitiateUploadRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new InitiateUploadCommand(id, r.FileName, r.Size, r.ContentType, r.AllowDuplicate, r.Sha256),
                    v => Task.FromResult(Results.Created($"/api/v1/uploads/{v.Upload.Id}", v)), ct))
            .WithName("InitiateUpload").WithSummary("Open a resumable multipart upload; returns presigned part URLs. File bytes go straight to storage")
            .Produces<UploadStartDto>(StatusCodes.Status201Created);
        assets.MapPost("/bulk-upload", (BulkUploadRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new BulkUploadCommand(r.FolderId, r.BatchId, r.Metadata, r.Tags, r.Files), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("BulkUpload").WithSummary("One draft asset and one upload per file, all sharing the batch metadata and tags. Failures are per file")
            .Produces<BulkUploadResultDto>();

        var uploads = v1.MapGroup("/uploads").WithTags("Uploads");
        uploads.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetUploadQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetUpload").WithSummary("State of a transfer. While open it lists the parts storage already has, which is where to resume from").Produces<UploadDto>();
        uploads.MapPost("/{id:guid}/parts", (Guid id, PartNumbersRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new PresignPartsQuery(id, r.PartNumbers), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("PresignParts").WithSummary("Fresh short-lived URLs for specific parts").Produces<IReadOnlyList<PartUrlDto>>();
        uploads.MapPost("/{id:guid}/complete", (Guid id, CompleteUploadRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CompleteUploadCommand(id, r.Parts, r.Sha256),
                    v => Task.FromResult(v.Status == "duplicate"
                        ? Results.Problem(statusCode: 409, title: "Identical content already exists.",
                            extensions: new Dictionary<string, object?> { ["duplicateOfAssetId"] = v.DuplicateOfAssetId?.ToString(), ["uploadId"] = v.Id })
                        : Results.Accepted($"/api/v1/uploads/{id}", v)), ct))
            .WithName("CompleteUpload").WithSummary("Assemble the parts. Returns 202; poll the upload for ready, duplicate or failed").Produces<UploadDto>(StatusCodes.Status202Accepted);
        uploads.MapPost("/{id:guid}/abort", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new AbortUploadCommand(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("AbortUpload").Produces<UploadDto>();
    }
}
