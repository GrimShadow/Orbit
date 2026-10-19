using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Licensing;
using Dam.Shared.Licensing;

namespace Dam.Application.Licensing;

/// <summary>How long a presigned upload or download address works. Short: a fresh one is a request away.</summary>
public static class RightsDocumentSettings
{
    public static readonly TimeSpan UploadTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DownloadTtl = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Step one of attaching a release form or licence file: validates what is about to be sent and returns a presigned PUT address under
/// <c>rights/{tenant}/{rights}/{document}</c> in the originals bucket. Nothing is recorded yet; the document exists once <c>complete</c> has found the bytes.
/// Needs <c>rights.manage</c>. The file never passes through the API and is not virus-scanned (ADR-0033).
/// </summary>
public sealed record StartRightsDocumentQuery(Guid RightsId, RightsDocumentUploadInput Input) : IQuery<RightsDocumentUploadDto>;

public sealed class StartRightsDocumentHandler(
    IRepository<Rights> rights, IQueryExecutor q, IAuthorizationService authz, IObjectStore store, ITenantContext tenant, IClock clock)
    : IRequestHandler<StartRightsDocumentQuery, RightsDocumentUploadDto>
{
    public async Task<Result<RightsDocumentUploadDto>> Handle(StartRightsDocumentQuery c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        if (await RightsAccess.FindAsync(rights, q, c.RightsId, ct) is null) return Error.NotFound("Rights record not found.");
        var errors = RightsDocumentPolicy.Validate(c.Input.FileName, c.Input.ContentType, c.Input.Size);
        if (errors.Count > 0) return Error.Validation(errors);

        var id = Uuid7.NewGuid();
        var type = RightsDocumentPolicy.NormalizeContentType(c.Input.ContentType);
        var url = store.PresignPut(store.OriginalsBucket, RightsDocument.KeyFor(tenant.TenantId, c.RightsId, id), type, RightsDocumentSettings.UploadTtl);
        return new RightsDocumentUploadDto(id, url, "PUT", new Dictionary<string, string> { ["Content-Type"] = type }, clock.UtcNow + RightsDocumentSettings.UploadTtl, RightsDocumentPolicy.MaxBytes);
    }
}

/// <summary>
/// Step two: finds the uploaded object, checks it is what was declared (same size, within the limit, and its first bytes look like the declared type),
/// and records the document. A file that fails the check is deleted from storage.
/// </summary>
public sealed record CompleteRightsDocumentCommand(Guid RightsId, Guid DocumentId, RightsDocumentCompleteInput Input) : ICommand<RightsDocumentDto>;

public sealed class CompleteRightsDocumentHandler(
    IRepository<Rights> rights, IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz, IObjectStore store,
    ITenantContext tenant, ICurrentUser me, IClock clock) : IRequestHandler<CompleteRightsDocumentCommand, RightsDocumentDto>
{
    public async Task<Result<RightsDocumentDto>> Handle(CompleteRightsDocumentCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        if (await RightsAccess.FindAsync(rights, q, c.RightsId, ct) is null) return Error.NotFound("Rights record not found.");
        var errors = RightsDocumentPolicy.Validate(c.Input.FileName, c.Input.ContentType, c.Input.Size);
        if (errors.Count > 0) return Error.Validation(errors);
        if (await q.AnyAsync(documents.Query().Where(d => d.Id == c.DocumentId), ct)) return Error.Conflict("This document was already recorded.");

        var bucket = store.OriginalsBucket;
        var key = RightsDocument.KeyFor(tenant.TenantId, c.RightsId, c.DocumentId);
        var stored = await store.HeadAsync(bucket, key, ct);
        if (stored is null) return Error.Conflict("No file has been uploaded to that address (or it expired). Start again.");

        var type = RightsDocumentPolicy.NormalizeContentType(c.Input.ContentType);
        var problems = new Dictionary<string, string[]>();
        if (stored.Size > RightsDocumentPolicy.MaxBytes) problems["size"] = [$"The uploaded file is too large: at most {RightsDocumentPolicy.MaxBytes / (1024 * 1024)} MB."];
        else if (stored.Size != c.Input.Size) problems["size"] = [$"The uploaded file is {stored.Size} bytes, not the {c.Input.Size} declared."];
        else
        {
            var head = await store.ReadRangeAsync(bucket, key, 0, 4096, ct);
            if (!RightsDocumentPolicy.LooksLike(type, head)) problems["contentType"] = [$"The file's content does not look like '{type}'."];
        }
        if (problems.Count > 0)
        {
            await store.DeleteAsync(bucket, key, ct);
            return Error.Validation(problems);
        }

        var doc = RightsDocument.Create(c.DocumentId, tenant.TenantId, c.RightsId, RightsDocumentPolicy.CleanFileName(c.Input.FileName), type, stored.Size, bucket, me.UserId, clock.UtcNow);
        documents.Add(doc);
        return RightsMapping.ToDto(doc);
    }
}

public sealed record ListRightsDocumentsQuery(Guid RightsId) : IQuery<IReadOnlyList<RightsDocumentDto>>;

public sealed class ListRightsDocumentsHandler(IRepository<Rights> rights, IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListRightsDocumentsQuery, IReadOnlyList<RightsDocumentDto>>
{
    public async Task<Result<IReadOnlyList<RightsDocumentDto>>> Handle(ListRightsDocumentsQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        if (await RightsAccess.FindAsync(rights, q, r.RightsId, ct) is null) return Error.NotFound("Rights record not found.");
        var list = await q.ToListAsync(documents.Query().Where(d => d.RightsId == r.RightsId).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id), ct);
        return list.Select(RightsMapping.ToDto).ToList();
    }
}

/// <summary>A short-lived address to download a document from; the response is a forced attachment of neutral type, never rendered by the browser.</summary>
public sealed record GetRightsDocumentLinkQuery(Guid RightsId, Guid DocumentId) : IQuery<RightsDocumentLinkDto>;

public sealed class GetRightsDocumentLinkHandler(IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz, IObjectStore store, IClock clock)
    : IRequestHandler<GetRightsDocumentLinkQuery, RightsDocumentLinkDto>
{
    public async Task<Result<RightsDocumentLinkDto>> Handle(GetRightsDocumentLinkQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var doc = await q.FirstOrDefaultAsync(documents.Query().Where(d => d.Id == r.DocumentId && d.RightsId == r.RightsId), ct);
        if (doc is null) return Error.NotFound("Document not found.");
        return new RightsDocumentLinkDto(store.PresignDownload(doc.Bucket, doc.StorageKey, doc.Name, RightsDocumentSettings.DownloadTtl), clock.UtcNow + RightsDocumentSettings.DownloadTtl);
    }
}

public sealed record DeleteRightsDocumentCommand(Guid RightsId, Guid DocumentId) : ICommand<Guid>;

public sealed class DeleteRightsDocumentHandler(IRepository<RightsDocument> documents, IQueryExecutor q, IAuthorizationService authz, IObjectStore store)
    : IRequestHandler<DeleteRightsDocumentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteRightsDocumentCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.RightsManage, ct: ct)) return Error.Forbidden();
        var doc = await q.FirstOrDefaultAsync(documents.Query().Where(d => d.Id == c.DocumentId && d.RightsId == c.RightsId), ct);
        if (doc is null) return Error.NotFound("Document not found.");
        doc.MarkRemoved();
        documents.Remove(doc);
        await store.DeleteAsync(doc.Bucket, doc.StorageKey, ct);
        return doc.Id;
    }
}
