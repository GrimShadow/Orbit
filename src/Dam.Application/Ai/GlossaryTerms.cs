using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Ai;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Shared.Ai;
using Dam.Shared.Identity;

namespace Dam.Application.Ai;

internal static class GlossaryMapping
{
    public const int MaxTermLength = 200;
    public const int MaxNoteLength = 500;

    public static GlossaryTermDto ToDto(GlossaryTerm t) =>
        new(t.Id, t.Term, t.Mode, t.Translations.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value), t.Note, t.CreatedAt, t.UpdatedAt);
}

/// <summary>The rules for a glossary term, pure so they can be tested without a database. Keys are the request's field names.</summary>
public static class GlossaryRules
{
    public static Dictionary<string, string[]> Validate(GlossaryTermInput i)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var l)) errors[key] = l = []; l.Add(message); }

        var term = i.Term?.Trim() ?? "";
        if (term.Length == 0) Add("term", "A term is required.");
        else if (term.Length > GlossaryMapping.MaxTermLength) Add("term", $"A term is at most {GlossaryMapping.MaxTermLength} characters.");
        else if (HasForbidden(term)) Add("term", "A term cannot contain line breaks, control characters or the placeholder brackets ⟦ ⟧.");

        if (i.Mode is null || !GlossaryModes.All.Contains(i.Mode)) Add("mode", $"Mode must be one of: {string.Join(", ", GlossaryModes.All)}.");
        if (i.Note is { Length: > GlossaryMapping.MaxNoteLength }) Add("note", $"A note is at most {GlossaryMapping.MaxNoteLength} characters.");

        if (i.Mode == GlossaryModes.Mapped)
        {
            if (i.Translations is not { Count: > 0 }) Add("translations", "A mapped term needs at least one translation.");
            else
            {
                if (i.Translations.Count > 30) Add("translations", "At most 30 languages.");
                var seen = new HashSet<string>();
                foreach (var (lang, text) in i.Translations)
                {
                    if (TranslationRules.NormalizeLanguage(lang) is not { } code) { Add("translations", $"'{lang}' is not a language code."); continue; }
                    if (!seen.Add(code)) Add("translations", $"Language '{code}' appears twice.");
                    var t = text?.Trim() ?? "";
                    if (t.Length == 0 || t.Length > GlossaryMapping.MaxTermLength) Add("translations", $"The rendering for '{code}' must be 1 to {GlossaryMapping.MaxTermLength} characters.");
                    else if (HasForbidden(t)) Add("translations", $"The rendering for '{code}' cannot contain line breaks, control characters or the placeholder brackets ⟦ ⟧.");
                }
            }
        }
        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static bool HasForbidden(string s) => s.Any(c => char.IsControl(c) || c is GlossaryProtector.Open or GlossaryProtector.Close);

    /// <summary>The translations that will be stored: language codes normalised, renderings trimmed; none for a locked term.</summary>
    public static Dictionary<string, string> Normalize(GlossaryTermInput i) =>
        i.Mode == GlossaryModes.Mapped && i.Translations is not null
            ? i.Translations.ToDictionary(kv => TranslationRules.NormalizeLanguage(kv.Key)!, kv => kv.Value.Trim())
            : [];
}

internal static class GlossaryAccess
{
    public static Task<GlossaryTerm?> FindAsync(IRepository<GlossaryTerm> terms, IQueryExecutor q, Guid id, CancellationToken ct) =>
        q.FirstOrDefaultAsync(terms.Query().Where(t => t.Id == id), ct);

    public static Task<bool> TermTakenAsync(IRepository<GlossaryTerm> terms, IQueryExecutor q, string term, Guid? except, CancellationToken ct) =>
        q.AnyAsync(terms.Query().Where(t => t.Term == term && (except == null || t.Id != except)), ct);
}

// ------------------------------------------------------------------------------------------------------------- list

/// <summary>The tenant's glossary, alphabetical, paged by the term itself. <paramref name="Q"/> keeps terms containing the text (ignoring case). Any reader of assets may see it.</summary>
public sealed record ListGlossaryQuery(string? Q, string? Cursor, int? Limit) : IQuery<Page<GlossaryTermDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ListGlossaryHandler(IRepository<GlossaryTerm> terms, IQueryExecutor q) : IRequestHandler<ListGlossaryQuery, Page<GlossaryTermDto>>
{
    public async Task<Result<Page<GlossaryTermDto>>> Handle(ListGlossaryQuery r, CancellationToken ct)
    {
        var limit = Paging.Clamp(r.Limit);
        IQueryable<GlossaryTerm> query = terms.Query();
        if (r.Q is { Length: > 0 } text) { var lower = text.Trim().ToLowerInvariant(); query = query.Where(t => t.Term.ToLower().Contains(lower)); }
        if (!string.IsNullOrEmpty(r.Cursor)) { var after = r.Cursor; query = query.Where(t => t.Term.CompareTo(after) > 0); }
        var rows = await q.ToListAsync(query.OrderBy(t => t.Term).Take(limit + 1), ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new Page<GlossaryTermDto>(rows.Select(GlossaryMapping.ToDto).ToList(), more ? rows[^1].Term : null);
    }
}

// ---------------------------------------------------------------------------------------------------------- create

public sealed record CreateGlossaryTermCommand(GlossaryTermInput Input) : ICommand<GlossaryTermDto>, IAuthorizedRequest
{
    public string Permission => Permissions.GlossaryManage;
}

public sealed class CreateGlossaryTermHandler(IRepository<GlossaryTerm> terms, IQueryExecutor q, ITenantContext tenant, IClock clock)
    : IRequestHandler<CreateGlossaryTermCommand, GlossaryTermDto>
{
    public async Task<Result<GlossaryTermDto>> Handle(CreateGlossaryTermCommand c, CancellationToken ct)
    {
        var errors = GlossaryRules.Validate(c.Input);
        var term = c.Input.Term?.Trim() ?? "";
        if (errors.Count == 0 && await GlossaryAccess.TermTakenAsync(terms, q, term, null, ct)) errors["term"] = ["This term is already in the glossary."];
        if (errors.Count == 0 && await q.CountAsync(terms.Query(), ct) >= TranslationRules.MaxGlossaryTerms)
            errors["term"] = [string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A glossary holds at most {TranslationRules.MaxGlossaryTerms:N0} terms.")];
        if (errors.Count > 0) return Error.Validation(errors);

        var created = GlossaryTerm.Create(tenant.TenantId, term, c.Input.Mode!, GlossaryRules.Normalize(c.Input), c.Input.Note, clock.UtcNow);
        terms.Add(created);
        return GlossaryMapping.ToDto(created);
    }
}

// ---------------------------------------------------------------------------------------------------------- update

public sealed record UpdateGlossaryTermCommand(Guid Id, GlossaryTermInput Input) : ICommand<GlossaryTermDto>, IAuthorizedRequest
{
    public string Permission => Permissions.GlossaryManage;
}

public sealed class UpdateGlossaryTermHandler(IRepository<GlossaryTerm> terms, IQueryExecutor q, IClock clock) : IRequestHandler<UpdateGlossaryTermCommand, GlossaryTermDto>
{
    public async Task<Result<GlossaryTermDto>> Handle(UpdateGlossaryTermCommand c, CancellationToken ct)
    {
        var found = await GlossaryAccess.FindAsync(terms, q, c.Id, ct);
        if (found is null) return Error.NotFound("Glossary term not found.");
        var errors = GlossaryRules.Validate(c.Input);
        var term = c.Input.Term?.Trim() ?? "";
        if (errors.Count == 0 && await GlossaryAccess.TermTakenAsync(terms, q, term, c.Id, ct)) errors["term"] = ["This term is already in the glossary."];
        if (errors.Count > 0) return Error.Validation(errors);

        found.Update(term, c.Input.Mode!, GlossaryRules.Normalize(c.Input), c.Input.Note, clock.UtcNow);
        return GlossaryMapping.ToDto(found);
    }
}

// ---------------------------------------------------------------------------------------------------------- delete

public sealed record DeleteGlossaryTermCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.GlossaryManage;
}

public sealed class DeleteGlossaryTermHandler(IRepository<GlossaryTerm> terms, IQueryExecutor q) : IRequestHandler<DeleteGlossaryTermCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteGlossaryTermCommand c, CancellationToken ct)
    {
        var found = await GlossaryAccess.FindAsync(terms, q, c.Id, ct);
        if (found is null) return Error.NotFound("Glossary term not found.");
        found.MarkDeleted();
        terms.Remove(found);
        return c.Id;
    }
}
