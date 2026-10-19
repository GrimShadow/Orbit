using Dam.Application.Licensing;
using Dam.Application.Messaging;
using Dam.Shared.Licensing;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>Rights and licences: records, assignment to assets, release documents, and the channel/region check (ADR-0033).</summary>
public static class RightsEndpoints
{
    public static void MapRightsEndpoints(this RouteGroupBuilder v1)
    {
        var rights = v1.MapGroup("/rights").WithTags("Rights");

        rights.MapGet("", (IDispatcher d, CancellationToken ct, string? status, string? q) => Run(d, new ListRightsQuery(status, q), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListRights")
            .WithSummary("Every rights record with its status today (valid, expiring, expired, not_yet_valid) and its asset and document counts, by name. status narrows; q matches names containing the text. Needs rights.manage")
            .Produces<IReadOnlyList<RightsDto>>();
        rights.MapPost("", (RightsInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateRightsCommand(r), v => Task.FromResult(Results.Created($"/api/v1/rights/{v.Id}", v)), ct))
            .WithName("CreateRights")
            .WithSummary("Creates a rights record: licence type, channels and regions it covers (empty = all), validity dates (the end date is inclusive), watermark requirement. Channel and region values must be codes of the tenant's vocabularies. Names are unique per tenant, ignoring case (422 on name). Needs rights.manage")
            .Produces<RightsDto>(StatusCodes.Status201Created);
        rights.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetRightsQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetRights").Produces<RightsDto>();
        rights.MapPut("/{id:guid}", (Guid id, RightsInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateRightsCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateRights")
            .WithSummary("Replaces a rights record. If its channels or regions change, the access attributes of up to 500 assigned assets are re-derived in the same request (and re-indexed); the rest are reported in pendingAssets and caught up within the hour")
            .Produces<RightsDto>();
        rights.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new DeleteRightsCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteRights").WithSummary("Deletes a rights record and its documents. 409 (with assetCount) while any asset still uses it");
        rights.MapPost("/{id:guid}/assign", (Guid id, AssignRightsInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new AssignRightsCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("AssignRights")
            .WithSummary("Assigns the record to up to 500 assets. Every asset gets its own result: assigned, skipped (already assigned, has other rights and replace is false, deleted/blocked, or the licence is not in force for a published asset) or failed (not found, not permitted)")
            .Produces<AssignRightsResultDto>();

        rights.MapGet("/{id:guid}/documents", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new ListRightsDocumentsQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListRightsDocuments").WithSummary("Release forms and licence files attached to the record, oldest first. Needs rights.manage").Produces<IReadOnlyList<RightsDocumentDto>>();
        rights.MapPost("/{id:guid}/documents", (Guid id, RightsDocumentUploadInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new StartRightsDocumentQuery(id, r), v => Task.FromResult(Results.Created($"/api/v1/rights/{id}/documents/{v.DocumentId}", v)), ct))
            .WithName("StartRightsDocumentUpload")
            .WithSummary("Step 1 of attaching a document: validates the file name, type (pdf, png, jpeg, docx or txt, by declared type and extension) and size (up to 25 MB) and returns a presigned PUT address valid for 15 minutes. Nothing is recorded until complete. Documents are not virus-scanned")
            .Produces<RightsDocumentUploadDto>(StatusCodes.Status201Created);
        rights.MapPost("/{id:guid}/documents/{documentId:guid}/complete", (Guid id, Guid documentId, RightsDocumentCompleteInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CompleteRightsDocumentCommand(id, documentId, r), v => Task.FromResult(Results.Created($"/api/v1/rights/{id}/documents/{documentId}", v)), ct))
            .WithName("CompleteRightsDocumentUpload")
            .WithSummary("Step 2: finds the uploaded object, checks it has the declared size and looks like its declared type, and records the document. A file that fails the check is deleted from storage")
            .Produces<RightsDocumentDto>(StatusCodes.Status201Created);
        rights.MapGet("/{id:guid}/documents/{documentId:guid}/link", (Guid id, Guid documentId, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetRightsDocumentLinkQuery(id, documentId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetRightsDocumentLink").WithSummary("A download address valid for 5 minutes; the response is a forced attachment of type application/octet-stream").Produces<RightsDocumentLinkDto>();
        rights.MapDelete("/{id:guid}/documents/{documentId:guid}", (Guid id, Guid documentId, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteRightsDocumentCommand(id, documentId), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteRightsDocument");

        // The asset side: the rights an asset is under, and whether it may be used somewhere.
        v1.MapGet("/assets/{id:guid}/rights", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetAssetRightsQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Rights").WithName("GetAssetRights")
            .WithSummary("The rights assigned to an asset (rights is null when none), for anyone who can read the asset. licensor and notes are filled only for callers with rights.manage")
            .Produces<AssetRightsDto>();
        v1.MapPut("/assets/{id:guid}/rights", (Guid id, SetAssetRightsInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new SetAssetRightsCommand(id, r.RightsId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Rights").WithName("SetAssetRights")
            .WithSummary("Assigns the asset's rights, or clears them with rightsId null. Re-derives the asset's access attributes (its channel and region narrowed by the licence) and re-indexes it. Needs rights.manage on the asset; a published asset cannot be put under a licence that is not in force (409)")
            .Produces<AssetRightsDto>();
        v1.MapGet("/assets/{id:guid}/rights/check", (Guid id, IDispatcher d, CancellationToken ct, string? channel, string? region) =>
                Run(d, new CheckAssetRightsQuery(id, channel, region), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Rights").WithName("CheckAssetRights")
            .WithSummary("May the asset be used on this channel in this region? Checks the licence's validity today, the licence's channels and regions, and the asset's effective access attributes. Both parameters are optional. Needs assets.read")
            .Produces<RightsCheckDto>();
    }
}
