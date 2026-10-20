using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Knowledge;
using Dam.Shared.Identity;
using Dam.Shared.Knowledge;

namespace Dam.Application.Knowledge;

// ------------------------------------------------------------------------------------------------------------- list

/// <summary>
/// The knowledge documents whose assets the caller may read (the same access clauses the asset listing uses), newest first, paged by document id. Filters: the
/// asset (its mapping), the type, model and variant codes, the language, a model year (documents for that year or for no year in particular) and <paramref name="Q"/>, which keeps
/// documents whose title, model or variant contains the text.
/// </summary>
public sealed record ListKnowledgeDocumentsQuery(string? Type, string? Model, string? Variant, int? Year, string? Lang, string? Q, string? AssetId, string? Cursor, int? Limit)
    : IQuery<Page<KnowledgeDocumentDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ListKnowledgeDocumentsHandler(
    IRepository<KnowledgeDocument> documents, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListKnowledgeDocumentsQuery, Page<KnowledgeDocumentDto>>
{
    private const int MaxRowsScanned = 5000;

    private sealed class Row
    {
        public required KnowledgeDocument D { get; init; }
        public required Asset A { get; init; }
    }

    public async Task<Result<Page<KnowledgeDocumentDto>>> Handle(ListKnowledgeDocumentsQuery r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Type is { Length: > 0 } && !KnowledgeDocTypes.IsKnown(r.Type)) errors["type"] = [$"type must be one of: {string.Join(", ", KnowledgeDocTypes.All)}."];
        var model = r.Model is { Length: > 0 } ? KnowledgeCodes.Normalize(r.Model) : null;
        if (r.Model is { Length: > 0 } && model is null) errors["model"] = [KnowledgeCodes.Hint("model")];
        var variant = r.Variant is { Length: > 0 } ? KnowledgeCodes.Normalize(r.Variant) : null;
        if (r.Variant is { Length: > 0 } && variant is null) errors["variant"] = [KnowledgeCodes.Hint("variant")];
        Guid? onlyAsset = null;
        if (r.AssetId is { Length: > 0 })
        {
            if (Guid.TryParse(r.AssetId, out var parsed)) onlyAsset = parsed;
            else errors["assetId"] = ["assetId must be an asset id (a GUID)."];
        }
        if (r.Cursor is { Length: > 0 } && Paging.ParseCursor(r.Cursor) is null) errors["cursor"] = ["Not a valid cursor."];
        if (errors.Count > 0) return Error.Validation(errors);

        var limit = Paging.Clamp(r.Limit);
        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return new Page<KnowledgeDocumentDto>([], null);

        var pathById = (await q.ToListAsync(folders.Query().Select(f => new { f.Id, f.Path }), ct)).ToDictionary(f => f.Id, f => f.Path);

        var query =
            from d in documents.Query()
            join a in assets.Query() on d.AssetId equals a.Id
            where a.Status != AssetStatuses.Deleted
            select new Row { D = d, A = a };
        if (onlyAsset is { } assetFilter) query = query.Where(x => x.D.AssetId == assetFilter);
        if (r.Type is { Length: > 0 }) query = query.Where(x => x.D.DocType == r.Type);
        if (model is not null) query = query.Where(x => x.D.Model == model);
        if (variant is not null) query = query.Where(x => x.D.Variant == variant);
        if (r.Lang is { Length: > 0 }) { var lang = r.Lang.Trim().ToLowerInvariant(); query = query.Where(x => x.D.Language == lang); }
        if (r.Year is { } y) query = query.Where(x => x.D.ModelYearFrom == null || (x.D.ModelYearFrom <= y && x.D.ModelYearTo >= y));
        if (r.Q is { Length: > 0 } text)
        {
            var lower = text.Trim().ToLowerInvariant();
            query = query.Where(x => x.A.Title.ToLower().Contains(lower) || x.D.Model.Contains(lower) || (x.D.Variant != null && x.D.Variant.Contains(lower)));
        }

        var cursor = Paging.ParseCursor(r.Cursor);
        var page = new List<Row>();
        var scanned = 0;
        var more = false;
        while (page.Count <= limit && scanned < MaxRowsScanned)
        {
            var chunk = cursor is { } after ? query.Where(x => x.D.Id.CompareTo(after) < 0) : query;
            var rows = await q.ToListAsync(chunk.OrderByDescending(x => x.D.Id).Take(limit * 4), ct);
            if (rows.Count == 0) break;
            foreach (var x in rows)
            {
                cursor = x.D.Id;
                scanned++;
                var path = x.A.FolderId is { } f && pathById.TryGetValue(f, out var p) ? p : null;
                if (!clauses.Any(c => c.Matches(x.A.ToResource(path), profile.UserId))) continue;
                page.Add(x);
                if (page.Count > limit) { more = true; break; }
            }
            if (more || rows.Count < limit * 4) break;
        }
        if (page.Count > limit) page.RemoveAt(limit);
        return new Page<KnowledgeDocumentDto>(page.Select(x => KnowledgeMapping.ToDto(x.D, x.A)).ToList(), more ? page[^1].D.Id.ToString() : null);
    }
}

// ------------------------------------------------------------------------------------------------------------- read

/// <summary>A document whose asset the caller may not read is reported as not found, so its existence is not revealed.</summary>
public sealed record GetKnowledgeDocumentQuery(Guid Id) : IQuery<KnowledgeDocumentDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetKnowledgeDocumentHandler(KnowledgeReader reader) : IRequestHandler<GetKnowledgeDocumentQuery, KnowledgeDocumentDetailDto>
{
    public async Task<Result<KnowledgeDocumentDetailDto>> Handle(GetKnowledgeDocumentQuery r, CancellationToken ct) =>
        await reader.LoadReadableAsync(r.Id, ct) is { } x ? KnowledgeMapping.ToDetail(x.Doc, x.Asset) : Error.NotFound("Knowledge document not found.");
}

public sealed record GetKnowledgeChaptersQuery(Guid Id) : IQuery<ChaptersDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetKnowledgeChaptersHandler(KnowledgeReader reader) : IRequestHandler<GetKnowledgeChaptersQuery, ChaptersDto>
{
    public async Task<Result<ChaptersDto>> Handle(GetKnowledgeChaptersQuery r, CancellationToken ct) =>
        await reader.LoadReadableAsync(r.Id, ct) is { } x
            ? new ChaptersDto(x.Doc.ChaptersSource, x.Doc.ChaptersIndexedAt, x.Doc.Chapters.Select(KnowledgeMapping.ToDto).ToList())
            : Error.NotFound("Knowledge document not found.");
}

// ----------------------------------------------------------------------------------------------------------- create

/// <summary>Maps an asset (a manual, document or html5 asset the caller may update) as a knowledge document. One document per asset.</summary>
public sealed record CreateKnowledgeDocumentCommand(CreateKnowledgeDocumentInput Input) : ICommand<KnowledgeDocumentDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class CreateKnowledgeDocumentHandler(
    IRepository<KnowledgeDocument> documents, IQueryExecutor q, IAuthorizationService authz, KnowledgeReader reader, ITenantContext tenant, ICurrentUser user, IClock clock)
    : IRequestHandler<CreateKnowledgeDocumentCommand, KnowledgeDocumentDetailDto>
{
    public async Task<Result<KnowledgeDocumentDetailDto>> Handle(CreateKnowledgeDocumentCommand c, CancellationToken ct)
    {
        var i = c.Input;
        var languages = await reader.LanguagesAsync(ct);
        var errors = KnowledgeRules.ValidateMapping(i.DocType, i.Model, i.Variant, i.ModelYearFrom, i.ModelYearTo, i.Language, i.Theme, languages, out var language);
        if (i.AssetId is null || i.AssetId == Guid.Empty) errors["assetId"] = ["assetId is required."];
        if (errors.Count > 0) return Error.Validation(errors);

        var (asset, path) = await reader.LoadAssetAsync(i.AssetId!.Value, ct);
        // An asset the caller cannot read looks exactly like one that does not exist.
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.Validation(KnowledgeMapping.Fail("assetId", "Asset not found."));
        if (!KnowledgeAssetTypes.IsAllowed(asset.Type))
            return Error.Validation(KnowledgeMapping.Fail("assetId", $"A knowledge document sits on a {string.Join(", ", KnowledgeAssetTypes.Allowed)} asset; this one is a '{asset.Type}'."));
        if (!await authz.CanAsync(Permissions.AssetsUpdate, asset.ToResource(path), ct)) return Error.Forbidden("You may not edit this asset, so you cannot map it as a knowledge document.");
        if (asset.Status == AssetStatuses.Deleted) return Error.Conflict("The asset is deleted.");
        if (await q.AnyAsync(documents.Query().Where(d => d.AssetId == asset.Id), ct)) return Error.Conflict("This asset already has a knowledge document.");

        var doc = KnowledgeDocument.Create(tenant.TenantId, asset.Id, i.DocType!, KnowledgeCodes.Normalize(i.Model)!, KnowledgeCodes.Normalize(i.Variant), i.ModelYearFrom, i.ModelYearTo,
            language, KnowledgeCodes.Normalize(i.Theme), user.UserId, clock.UtcNow);
        documents.Add(doc);
        return KnowledgeMapping.ToDetail(doc, asset);
    }
}

// ----------------------------------------------------------------------------------------------------------- update

public sealed record UpdateKnowledgeDocumentCommand(Guid Id, UpdateKnowledgeDocumentInput Input) : ICommand<KnowledgeDocumentDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class UpdateKnowledgeDocumentHandler(KnowledgeReader reader, IAuthorizationService authz, IClock clock)
    : IRequestHandler<UpdateKnowledgeDocumentCommand, KnowledgeDocumentDetailDto>
{
    public async Task<Result<KnowledgeDocumentDetailDto>> Handle(UpdateKnowledgeDocumentCommand c, CancellationToken ct)
    {
        if (await reader.LoadReadableAsync(c.Id, ct) is not { } x) return Error.NotFound("Knowledge document not found.");
        if (!await authz.CanAsync(Permissions.AssetsUpdate, x.Asset.ToResource(x.Path), ct)) return Error.Forbidden("You may not edit this asset.");

        var i = c.Input;
        var errors = KnowledgeRules.ValidateMapping(i.DocType, i.Model, i.Variant, i.ModelYearFrom, i.ModelYearTo, i.Language ?? x.Doc.Language, i.Theme, await reader.LanguagesAsync(ct), out var language);
        if (errors.Count > 0) return Error.Validation(errors);

        x.Doc.Update(i.DocType!, KnowledgeCodes.Normalize(i.Model)!, KnowledgeCodes.Normalize(i.Variant), i.ModelYearFrom, i.ModelYearTo, language, KnowledgeCodes.Normalize(i.Theme), clock.UtcNow);
        return KnowledgeMapping.ToDetail(x.Doc, x.Asset);
    }
}

// ----------------------------------------------------------------------------------------------------------- delete

/// <summary>Removes the mapping. The asset, its versions and its delivery are untouched.</summary>
public sealed record DeleteKnowledgeDocumentCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class DeleteKnowledgeDocumentHandler(KnowledgeReader reader, IRepository<KnowledgeDocument> documents, IAuthorizationService authz)
    : IRequestHandler<DeleteKnowledgeDocumentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteKnowledgeDocumentCommand c, CancellationToken ct)
    {
        if (await reader.LoadReadableAsync(c.Id, ct) is not { } x) return Error.NotFound("Knowledge document not found.");
        if (!await authz.CanAsync(Permissions.AssetsUpdate, x.Asset.ToResource(x.Path), ct)) return Error.Forbidden("You may not edit this asset.");
        x.Doc.MarkDeleted();
        documents.Remove(x.Doc);
        return c.Id;
    }
}

// ---------------------------------------------------------------------------------------------------------- chapters

/// <summary>Enters or corrects the table of contents by hand (source <c>manual</c>). An empty list clears it and hands the document back to automatic indexing.</summary>
public sealed record SetKnowledgeChaptersCommand(Guid Id, ChaptersInput Input) : ICommand<ChaptersDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class SetKnowledgeChaptersHandler(KnowledgeReader reader, IClock clock) : IRequestHandler<SetKnowledgeChaptersCommand, ChaptersDto>
{
    public async Task<Result<ChaptersDto>> Handle(SetKnowledgeChaptersCommand c, CancellationToken ct)
    {
        if (await reader.LoadReadableAsync(c.Id, ct) is not { } x) return Error.NotFound("Knowledge document not found.");
        var errors = KnowledgeRules.ValidateChapters(c.Input.Chapters, out var chapters);
        if (errors.Count > 0) return Error.Validation(errors);
        x.Doc.SetManualChapters(chapters, x.Asset.CurrentVersionId, clock.UtcNow);
        return new ChaptersDto(x.Doc.ChaptersSource, x.Doc.ChaptersIndexedAt, x.Doc.Chapters.Select(KnowledgeMapping.ToDto).ToList());
    }
}

/// <summary>Asks the worker to read the chapters again. Manual chapters are kept unless <paramref name="Force"/>.</summary>
public sealed record ReindexKnowledgeDocumentCommand(Guid Id, bool Force) : ICommand<KnowledgeDocumentDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.KnowledgeManage;
}

public sealed class ReindexKnowledgeDocumentHandler(KnowledgeReader reader) : IRequestHandler<ReindexKnowledgeDocumentCommand, KnowledgeDocumentDetailDto>
{
    public async Task<Result<KnowledgeDocumentDetailDto>> Handle(ReindexKnowledgeDocumentCommand c, CancellationToken ct)
    {
        if (await reader.LoadReadableAsync(c.Id, ct) is not { } x) return Error.NotFound("Knowledge document not found.");
        x.Doc.RequestReindex(c.Force);
        return KnowledgeMapping.ToDetail(x.Doc, x.Asset);
    }
}
