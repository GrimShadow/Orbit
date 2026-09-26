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
