using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Content;

internal static class VocabularyLookup
{
    public static async Task<Vocabulary?> ByIdAsync(IRepository<Vocabulary> vocabularies, IQueryExecutor q, Guid id, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(vocabularies.Query().Where(v => v.Id == id), ct);
}

// ------------------------------------------------------------------------------------------------ vocabularies

public sealed record ListVocabulariesQuery : IQuery<IReadOnlyList<VocabularyDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ListVocabulariesHandler(IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IQueryExecutor q)
    : IRequestHandler<ListVocabulariesQuery, IReadOnlyList<VocabularyDto>>
{
    public async Task<Result<IReadOnlyList<VocabularyDto>>> Handle(ListVocabulariesQuery r, CancellationToken ct)
    {
        var list = await q.ToListAsync(vocabularies.Query().OrderBy(v => v.Key), ct);
        var counts = (await q.ToListAsync(terms.Query().GroupBy(t => t.VocabularyId).Select(g => new { g.Key, N = g.Count() }), ct)).ToDictionary(x => x.Key, x => x.N);
        return list.Select(v => new VocabularyDto(v.Id, v.Key, v.Name, v.Description, v.Hierarchical, v.Levels, counts.GetValueOrDefault(v.Id))).ToList();
    }
}

public sealed record GetVocabularyQuery(Guid Id) : IQuery<VocabularyDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetVocabularyHandler(IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IQueryExecutor q)
    : IRequestHandler<GetVocabularyQuery, VocabularyDto>
{
    public async Task<Result<VocabularyDto>> Handle(GetVocabularyQuery r, CancellationToken ct)
    {
        var v = await VocabularyLookup.ByIdAsync(vocabularies, q, r.Id, ct);
        if (v is null) return Error.NotFound("Vocabulary not found.");
        var n = await q.CountAsync(terms.Query().Where(t => t.VocabularyId == v.Id), ct);
        return new VocabularyDto(v.Id, v.Key, v.Name, v.Description, v.Hierarchical, v.Levels, n);
    }
}

public sealed record CreateVocabularyCommand(string Key, string Name, string? Description, bool Hierarchical, IReadOnlyList<string>? Levels)
    : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.TaxonomyManage;
}

public sealed class CreateVocabularyValidator : AbstractValidator<CreateVocabularyCommand>
{
    public CreateVocabularyValidator()
    {
        RuleFor(x => x.Key).Must(k => Paths.IsValidLabel(k) && char.IsAsciiLetterLower(k[0])).WithMessage("Key: lowercase letters, digits and underscores, starting with a letter (max 64).");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Levels).Must(l => l is null || (l.Count <= Paths.MaxDepth && l.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 100))).WithMessage("Up to 12 level names, each 1-100 characters.");
        RuleFor(x => x.Levels).Must((c, l) => l is null || l.Count == 0 || c.Hierarchical).WithMessage("Levels only apply to hierarchical vocabularies.");
    }
}

public sealed class CreateVocabularyHandler(IRepository<Vocabulary> vocabularies, IQueryExecutor q, ITenantContext tenant, IClock clock, IEventCollector events)
    : IRequestHandler<CreateVocabularyCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateVocabularyCommand c, CancellationToken ct)
    {
        if (await q.AnyAsync(vocabularies.Query().Where(v => v.Key == c.Key), ct)) return Error.Conflict($"A vocabulary with key '{c.Key}' already exists.");
        var v = Vocabulary.Create(tenant.TenantId, c.Key, c.Name, c.Description, c.Hierarchical, c.Levels, clock.UtcNow);
        vocabularies.Add(v);
        events.Raise(Events.Changed("vocabulary.created", "vocabulary", v.Id));
        return v.Id;
    }
}

/// <summary>The key and the hierarchical flag are fixed once created: schemas and terms depend on them.</summary>
public sealed record UpdateVocabularyCommand(Guid Id, string? Name, string? Description, IReadOnlyList<string>? Levels)
    : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.TaxonomyManage;
}

public sealed class UpdateVocabularyValidator : AbstractValidator<UpdateVocabularyCommand>
{
    public UpdateVocabularyValidator()
    {
        RuleFor(x => x.Name).MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Levels).Must(l => l is null || (l.Count <= Paths.MaxDepth && l.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 100))).WithMessage("Up to 12 level names, each 1-100 characters.");
    }
}

public sealed class UpdateVocabularyHandler(IRepository<Vocabulary> vocabularies, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<UpdateVocabularyCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateVocabularyCommand c, CancellationToken ct)
    {
        var v = await VocabularyLookup.ByIdAsync(vocabularies, q, c.Id, ct);
        if (v is null) return Error.NotFound("Vocabulary not found.");
        if (c.Levels is { Count: > 0 } && !v.Hierarchical) return Error.Validation(ContentMapping.Fail("levels", "Levels only apply to hierarchical vocabularies."));
        v.Update(c.Name, c.Description, c.Levels);
        events.Raise(Events.Changed("vocabulary.updated", "vocabulary", v.Id));
        return v.Id;
    }
}

public sealed record DeleteVocabularyCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.TaxonomyManage;
}

public sealed class DeleteVocabularyHandler(
    IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IRepository<MetadataSchema> schemas, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<DeleteVocabularyCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteVocabularyCommand c, CancellationToken ct)
    {
        var v = await VocabularyLookup.ByIdAsync(vocabularies, q, c.Id, ct);
        if (v is null) return Error.NotFound("Vocabulary not found.");
        var current = await q.ToListAsync(schemas.Query().Where(s => s.IsCurrent), ct);
        var users = current.Where(s => s.Fields.Any(f => f.Vocabulary == v.Key)).Select(s => s.AssetType).ToList();
        if (users.Count > 0) return Error.Conflict($"Vocabulary '{v.Key}' is used by the metadata schema for: {string.Join(", ", users)}. Remove those fields first.");
        terms.RemoveRange(await q.ToListAsync(terms.Query().Where(t => t.VocabularyId == v.Id), ct));
        vocabularies.Remove(v);
        events.Raise(Events.Changed("vocabulary.deleted", "vocabulary", v.Id));
        return v.Id;
    }
}

// ------------------------------------------------------------------------------------------------ terms

/// <summary>Terms of a vocabulary. With <paramref name="Tree"/> they come back nested; otherwise as a flat, path-ordered list.</summary>
public sealed record ListTermsQuery(Guid VocabularyId, bool Tree, bool IncludeDeprecated = true) : IQuery<TermListResult>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed record TermListResult(IReadOnlyList<TermDto>? Flat, IReadOnlyList<TermNodeDto>? Tree);

public sealed class ListTermsHandler(IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IQueryExecutor q)
    : IRequestHandler<ListTermsQuery, TermListResult>
{
    public async Task<Result<TermListResult>> Handle(ListTermsQuery r, CancellationToken ct)
    {
        if (await VocabularyLookup.ByIdAsync(vocabularies, q, r.VocabularyId, ct) is null) return Error.NotFound("Vocabulary not found.");
        var query = terms.Query().Where(t => t.VocabularyId == r.VocabularyId);
        if (!r.IncludeDeprecated) query = query.Where(t => t.Status == TermStatus.Active);
        var all = await q.ToListAsync(query, ct);
        if (!r.Tree) return new TermListResult(all.OrderBy(t => t.Path, StringComparer.Ordinal).Select(ContentMapping.ToDto).ToList(), null);
        var tree = TreeBuilder.Build<Term, TermNodeDto>(all, t => t.Id, t => t.ParentId, (t, kids) => new TermNodeDto(ContentMapping.ToDto(t), kids),
            (a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Code, b.Code));
        return new TermListResult(null, tree);
    }
}

public sealed record GetTermQuery(Guid Id) : IQuery<TermDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetTermHandler(IRepository<Term> terms, IQueryExecutor q) : IRequestHandler<GetTermQuery, TermDto>
{
    public async Task<Result<TermDto>> Handle(GetTermQuery r, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(terms.Query().Where(t => t.Id == r.Id), ct) is { } t ? ContentMapping.ToDto(t) : Error.NotFound("Term not found.");
}

public sealed record CreateTermCommand(Guid VocabularyId, Guid? ParentId, string Code, Dictionary<string, string>? Labels, int? SortOrder,
    JsonElement? Attributes) : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.TaxonomyManage;
}

public sealed class CreateTermValidator : AbstractValidator<CreateTermCommand>
{
    public CreateTermValidator()
    {
        RuleFor(x => x.Code).Must(Paths.IsValidLabel).WithMessage("Code: lowercase letters, digits and underscores (max 64). It is permanent.");
        RuleFor(x => x.Labels).Must(l => ContentMapping.LabelErrors(l) is null).WithMessage("Give 1-30 labels, keyed by language code, each 1-200 characters.");
    }
}

public sealed class CreateTermHandler(
    IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IQueryExecutor q, ITenantContext tenant, IClock clock, IEventCollector events)
    : IRequestHandler<CreateTermCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateTermCommand c, CancellationToken ct)
    {
        var v = await VocabularyLookup.ByIdAsync(vocabularies, q, c.VocabularyId, ct);
        if (v is null) return Error.NotFound("Vocabulary not found.");
        Term? parent = null;
        if (c.ParentId is { } pid)
        {
            if (!v.Hierarchical) return Error.Validation(ContentMapping.Fail("parentId", "This vocabulary is flat; terms cannot have a parent."));
            parent = await q.FirstOrDefaultAsync(terms.Query().Where(t => t.Id == pid && t.VocabularyId == v.Id), ct);
            if (parent is null) return Error.Validation(ContentMapping.Fail("parentId", "Parent term not found in this vocabulary."));
            if (Paths.Depth(parent.Path) >= Paths.MaxDepth) return Error.Validation(ContentMapping.Fail("parentId", $"Terms can be nested at most {Paths.MaxDepth} deep."));
        }
        if (await q.AnyAsync(terms.Query().Where(t => t.VocabularyId == v.Id && t.Code == c.Code), ct)) return Error.Conflict($"Code '{c.Code}' is already used in this vocabulary.");
        if (!ContentMapping.TryObject(c.Attributes, out var attrs, out var err)) return Error.Validation(ContentMapping.Fail("attributes", err!));

        var sort = c.SortOrder ?? await NextSortAsync(v.Id, c.ParentId, ct);
        var term = Term.Create(tenant.TenantId, v.Id, parent, c.Code, c.Labels!, sort, attrs, clock.UtcNow);
        terms.Add(term);
        events.Raise(Events.Changed("term.created", "term", term.Id));
        return term.Id;
    }

    private async Task<int> NextSortAsync(Guid vocabularyId, Guid? parentId, CancellationToken ct)
    {
        var siblings = await q.ToListAsync(terms.Query().Where(t => t.VocabularyId == vocabularyId && t.ParentId == parentId).Select(t => t.SortOrder), ct);
        return siblings.Count == 0 ? 0 : siblings.Max() + 1;
    }
}

/// <summary>Labels, order, status and attributes can change; the code (and so the path segment) cannot.</summary>
public sealed record UpdateTermCommand(Guid Id, Dictionary<string, string>? Labels, int? SortOrder, string? Status, JsonElement? Attributes)
    : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.TaxonomyManage;
}

public sealed class UpdateTermValidator : AbstractValidator<UpdateTermCommand>
{
    public UpdateTermValidator()
    {
        RuleFor(x => x.Labels).Must(l => l is null || ContentMapping.LabelErrors(l) is null).WithMessage("Give 1-30 labels, keyed by language code, each 1-200 characters.");
        RuleFor(x => x.Status).Must(s => s is null || Enum.TryParse<TermStatus>(s, true, out _)).WithMessage("Status must be 'active' or 'deprecated'.");
    }
}

public sealed class UpdateTermHandler(IRepository<Term> terms, IQueryExecutor q, IEventCollector events) : IRequestHandler<UpdateTermCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateTermCommand c, CancellationToken ct)
    {
        var t = await q.FirstOrDefaultAsync(terms.Query().Where(x => x.Id == c.Id), ct);
        if (t is null) return Error.NotFound("Term not found.");
        if (!ContentMapping.TryObject(c.Attributes, out var attrs, out var err)) return Error.Validation(ContentMapping.Fail("attributes", err!));
        t.Update(c.Labels, c.SortOrder, c.Status is null ? null : Enum.Parse<TermStatus>(c.Status, true), c.Attributes is null ? null : attrs);
        events.Raise(Events.Changed("term.updated", "term", t.Id));
        return t.Id;
    }
}

public sealed record MoveTermCommand(Guid Id, Guid? NewParentId) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.TaxonomyManage;
}

public sealed class MoveTermHandler(IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<MoveTermCommand, Guid>
{
    public async Task<Result<Guid>> Handle(MoveTermCommand c, CancellationToken ct)
    {
        var t = await q.FirstOrDefaultAsync(terms.Query().Where(x => x.Id == c.Id), ct);
        if (t is null) return Error.NotFound("Term not found.");
        var v = await VocabularyLookup.ByIdAsync(vocabularies, q, t.VocabularyId, ct);
        if (v is null || !v.Hierarchical) return Error.Conflict("Only terms in a hierarchical vocabulary can be moved.");

        Term? target = null;
        if (c.NewParentId is { } tid)
        {
            target = await q.FirstOrDefaultAsync(terms.Query().Where(x => x.Id == tid && x.VocabularyId == t.VocabularyId), ct);
            if (target is null) return Error.Validation(ContentMapping.Fail("newParentId", "Destination term not found in this vocabulary."));
            if (Paths.IsSelfOrDescendant(t.Path, target.Path)) return Error.Conflict("A term cannot be moved beneath itself.");
        }
        if (t.ParentId == c.NewParentId) return t.Id;

        var oldPrefix = t.Path;
        var descendants = await q.ToListAsync(terms.Query().Where(x => x.VocabularyId == t.VocabularyId && x.Path.StartsWith(oldPrefix + ".")), ct);
        t.MoveTo(target);
        var deepest = Paths.Depth(t.Path) + (descendants.Count == 0 ? 0 : descendants.Max(d => Paths.Depth(d.Path) - Paths.Depth(oldPrefix)));
        if (deepest > Paths.MaxDepth) return Error.Validation(ContentMapping.Fail("newParentId", $"The move would nest terms deeper than {Paths.MaxDepth} levels."));
        foreach (var d in descendants) d.RewritePath(oldPrefix, t.Path);
        events.Raise(Events.Changed("term.moved", "term", t.Id));
        return t.Id;
    }
}

/// <summary>Only a leaf can be deleted. To retire a term that assets may use, deprecate it instead.</summary>
public sealed record DeleteTermCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.TaxonomyManage;
}

public sealed class DeleteTermHandler(IRepository<Term> terms, IQueryExecutor q, IEventCollector events) : IRequestHandler<DeleteTermCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteTermCommand c, CancellationToken ct)
    {
        var t = await q.FirstOrDefaultAsync(terms.Query().Where(x => x.Id == c.Id), ct);
        if (t is null) return Error.NotFound("Term not found.");
        if (await q.AnyAsync(terms.Query().Where(x => x.ParentId == t.Id), ct)) return Error.Conflict("The term has child terms. Delete or move them first, or deprecate this term.");
        terms.Remove(t);
        events.Raise(Events.Changed("term.deleted", "term", t.Id));
        return t.Id;
    }
}
