using Dam.Application.Abstractions;
using Dam.Application.Ai;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Domain.Knowledge;
using Dam.Shared.Knowledge;

namespace Dam.Application.Knowledge;

/// <summary>Asset types a knowledge document can sit on (ADR-0039 decision 1).</summary>
public static class KnowledgeAssetTypes
{
    public static readonly IReadOnlyList<string> Allowed = ["manual", "document", "html5"];
    public static bool IsAllowed(string type) => Allowed.Contains(type);
}

/// <summary>The input rules of the knowledge module, pure so they can be tested without a database. Keys are the request's field names.</summary>
public static class KnowledgeRules
{
    /// <summary>Checks a document's mapping. <paramref name="languages"/> is the tenant's language setting; <paramref name="language"/> is returned normalised.</summary>
    public static Dictionary<string, string[]> ValidateMapping(
        string? docType, string? model, string? variant, int? yearFrom, int? yearTo, string? language, string? theme, TenantLanguages languages,
        out string normalizedLanguage)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var l)) errors[key] = l = []; l.Add(message); }

        if (!KnowledgeDocTypes.IsKnown(docType)) Add("docType", $"docType must be one of: {string.Join(", ", KnowledgeDocTypes.All)}.");
        if (KnowledgeCodes.Normalize(model) is null) Add("model", KnowledgeCodes.Hint("model"));
        if (!string.IsNullOrWhiteSpace(variant) && KnowledgeCodes.Normalize(variant) is null) Add("variant", KnowledgeCodes.Hint("variant"));
        if (!string.IsNullOrWhiteSpace(theme) && KnowledgeCodes.Normalize(theme) is null) Add("theme", KnowledgeCodes.Hint("theme"));

        if (yearFrom is null != yearTo is null) Add(yearFrom is null ? "modelYearFrom" : "modelYearTo", "Give both modelYearFrom and modelYearTo, or neither.");
        else if (yearFrom is { } from && yearTo is { } to)
        {
            if (from < ModelYears.MinYear || from > ModelYears.MaxYear) Add("modelYearFrom", $"A model year is between {ModelYears.MinYear} and {ModelYears.MaxYear}.");
            if (to < ModelYears.MinYear || to > ModelYears.MaxYear) Add("modelYearTo", $"A model year is between {ModelYears.MinYear} and {ModelYears.MaxYear}.");
            if (from > to) Add("modelYearTo", "modelYearTo cannot be before modelYearFrom.");
        }

        var lang = TranslationRules.NormalizeLanguage(string.IsNullOrWhiteSpace(language) ? languages.Default ?? "en" : language);
        normalizedLanguage = lang ?? "en";
        if (lang is null) Add("language", $"'{language}' is not a language code.");
        else if (!languages.Allows(lang)) Add("language", $"Language '{lang}' is not enabled for this tenant.");
        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>Checks a rule's input and, when it is fine, returns the facts to store (<paramref name="facts"/>) and the trimmed name.</summary>
    public static Dictionary<string, string[]> ValidateRule(VinRuleInput i, out RuleFacts? facts, out string name)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var l)) errors[key] = l = []; l.Add(message); }
        facts = null;

        name = i.Name?.Trim() ?? "";
        if (name.Length == 0) Add("name", "A name is required.");
        else if (name.Length > KnowledgeLimits.MaxRuleName) Add("name", $"A name is at most {KnowledgeLimits.MaxRuleName} characters.");
        else if (name.Any(char.IsControl)) Add("name", "A name cannot contain control characters.");

        VinPattern? pattern = null;
        if (!VinPattern.TryParse(i.Pattern, out var parsed, out var patternError)) Add("pattern", patternError);
        else pattern = parsed;

        var model = KnowledgeCodes.Normalize(i.Model);
        if (model is null) Add("model", KnowledgeCodes.Hint("model"));
        string? variant = null, theme = null;
        if (!string.IsNullOrWhiteSpace(i.Variant)) { variant = KnowledgeCodes.Normalize(i.Variant); if (variant is null) Add("variant", KnowledgeCodes.Hint("variant")); }
        if (!string.IsNullOrWhiteSpace(i.Theme)) { theme = KnowledgeCodes.Normalize(i.Theme); if (theme is null) Add("theme", KnowledgeCodes.Hint("theme")); }

        if (i.ModelYear is { } y && (y < ModelYears.MinYear || y > ModelYears.MaxYear)) Add("modelYear", $"A model year is between {ModelYears.MinYear} and {ModelYears.MaxYear}.");
        if (i.ModelYear is not null && i.YearFromVin == true) Add("yearFromVin", "Give a fixed modelYear or read the year from the VIN, not both.");
        var priority = i.Priority ?? 0;
        if (priority < KnowledgeLimits.MinPriority || priority > KnowledgeLimits.MaxPriority)
            Add("priority", $"Priority is between {KnowledgeLimits.MinPriority} and {KnowledgeLimits.MaxPriority}.");

        if (errors.Count == 0)
            facts = new RuleFacts(pattern!.Text, model!, variant, i.ModelYear, i.ModelYear is null && (i.YearFromVin ?? true), theme, priority, i.Enabled ?? true);
        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>Checks a table of contents entered by hand.</summary>
    public static Dictionary<string, string[]> ValidateChapters(IReadOnlyList<ChapterDto>? chapters, out List<Chapter> tidy)
    {
        tidy = [];
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var l)) errors[key] = l = []; l.Add(message); }
        if (chapters is null) { Add("chapters", "chapters is required (an empty list clears them)."); return Done(); }
        if (chapters.Count > KnowledgeLimits.MaxChapters) { Add("chapters", $"At most {KnowledgeLimits.MaxChapters} chapters."); return Done(); }

        for (var n = 0; n < chapters.Count; n++)
        {
            var c = chapters[n];
            var at = $"chapters[{n}]";
            var title = ChapterRules.CleanTitle(c.Title);
            if (title.Length == 0) Add($"{at}.title", "A chapter needs a title.");
            else if ((c.Title?.Trim().Length ?? 0) > KnowledgeLimits.MaxChapterTitle) Add($"{at}.title", $"A title is at most {KnowledgeLimits.MaxChapterTitle} characters.");
            if (c.Level < 1 || c.Level > KnowledgeLimits.MaxChapterLevel) Add($"{at}.level", $"Level is between 1 and {KnowledgeLimits.MaxChapterLevel}.");
            if (c.Anchor is { } a && a.Trim().Length > KnowledgeLimits.MaxAnchor) Add($"{at}.anchor", $"An anchor is at most {KnowledgeLimits.MaxAnchor} characters.");
            if (c.PageFrom is < 1) Add($"{at}.pageFrom", "Pages start at 1.");
            if (c.PageTo is < 1) Add($"{at}.pageTo", "Pages start at 1.");
            if (c.PageFrom is { } f && c.PageTo is { } t && t < f) Add($"{at}.pageTo", "pageTo cannot be before pageFrom.");
            if (c.PageFrom is null && c.PageTo is not null) Add($"{at}.pageTo", "pageTo needs a pageFrom.");
        }
        if (errors.Count == 0) tidy = ChapterRules.Tidy(chapters.Select(c => new Chapter(c.Title ?? "", c.Anchor, c.Level, c.PageFrom, c.PageTo)), out _);
        return Done();

        Dictionary<string, string[]> Done() => errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }
}

internal static class KnowledgeMapping
{
    public static ChapterDto ToDto(Chapter c) => new(c.Title, c.Anchor, c.Level, c.PageFrom, c.PageTo);

    public static KnowledgeDocumentDto ToDto(KnowledgeDocument d, Asset a) =>
        new(d.Id, d.AssetId, a.Title, a.Type, a.Status, d.DocType, d.Model, d.Variant, d.ModelYearFrom, d.ModelYearTo, d.Language, d.Theme, d.Chapters.Count,
            d.ChaptersSource, d.ChaptersIndexedAt, a.CurrentVersionId, d.CreatedAt, d.UpdatedAt);

    public static KnowledgeDocumentDetailDto ToDetail(KnowledgeDocument d, Asset a) =>
        new(d.Id, d.AssetId, a.Title, a.Type, a.Status, d.DocType, d.Model, d.Variant, d.ModelYearFrom, d.ModelYearTo, d.Language, d.Theme, d.Chapters.Count,
            d.ChaptersSource, d.ChaptersIndexedAt, a.CurrentVersionId, d.CreatedAt, d.UpdatedAt, d.Chapters.Select(ToDto).ToList());

    public static ResolvedDocumentDto ToResolved(KnowledgeDocument d, Asset a, int? versionNumber) =>
        new(d.Id, d.AssetId, a.Title, a.Type, a.Status, d.DocType, d.Model, d.Variant, d.ModelYearFrom, d.ModelYearTo, d.Language, d.Theme, d.Chapters.Count,
            d.ChaptersSource, d.ChaptersIndexedAt, a.CurrentVersionId, d.CreatedAt, d.UpdatedAt, d.Chapters.Select(ToDto).ToList(), a.CurrentVersionId, versionNumber);

    public static VinRuleDto ToDto(VinRule r) =>
        new(r.Id, r.Name, r.Pattern, r.Model, r.Variant, r.ModelYear, r.YearFromVin, r.Theme, r.Priority, r.Enabled, r.CreatedAt, r.UpdatedAt);

    public static Dictionary<string, string[]> Fail(string field, string message) => new() { [field] = [message] };
}

/// <summary>Loads documents with their asset and checks that the caller may read the asset, with exactly the rules assets themselves use (RBAC, ABAC, status).</summary>
public sealed class KnowledgeReader(
    IRepository<KnowledgeDocument> documents, IRepository<Asset> assets, IRepository<Folder> folders, IRepository<Tenant> tenants, IQueryExecutor q, IAuthorizationService authz)
{
    /// <summary>The document, its asset and the asset's folder path, or null for a document that does not exist or whose asset the caller may not read.</summary>
    public async Task<(KnowledgeDocument Doc, Asset Asset, string? Path)?> LoadReadableAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await q.FirstOrDefaultAsync(documents.Query().Where(d => d.Id == documentId), ct);
        if (doc is null) return null;
        var (asset, path) = await LoadAssetAsync(doc.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return null;
        return (doc, asset, path);
    }

    public async Task<(Asset? Asset, string? Path)> LoadAssetAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await q.FirstOrDefaultAsync(assets.Query().Where(a => a.Id == assetId), ct);
        if (asset is null) return (null, null);
        var path = asset.FolderId is { } fid ? (await q.FirstOrDefaultAsync(folders.Query().Where(f => f.Id == fid), ct))?.Path : null;
        return (asset, path);
    }

    public async Task<TenantLanguages> LanguagesAsync(CancellationToken ct) =>
        TenantLanguages.Parse((await q.FirstOrDefaultAsync(tenants.Query(), ct))?.SettingsJson);
}
