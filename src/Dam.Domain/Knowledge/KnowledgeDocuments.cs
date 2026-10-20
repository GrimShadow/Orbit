using Dam.Domain.Common;

namespace Dam.Domain.Knowledge;

public static class KnowledgeDocTypes
{
    public const string OwnerManual = "owner_manual";
    public const string Faq = "faq";
    public const string Service = "service";
    public const string Training = "training";
    public const string ProductInfo = "product_info";

    public static readonly IReadOnlyList<string> All = [OwnerManual, Faq, Service, Training, ProductInfo];

    public static bool IsKnown(string? type) => type is not null && All.Contains(type);
}

/// <summary>Where a document's chapters came from.</summary>
public static class ChapterSources
{
    /// <summary>No chapters: the file has no bookmarks or headings, or has not been read yet.</summary>
    public const string None = "none";
    public const string Pdf = "pdf";
    public const string Html = "html";
    /// <summary>Entered or corrected by a person; a re-index leaves them alone unless forced.</summary>
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All = [None, Pdf, Html, Manual];
}

/// <summary>The limits of the knowledge module, in one place so the API, the worker and the tests agree.</summary>
public static class KnowledgeLimits
{
    public const int MaxRulesPerTenant = 5000;
    public const int MaxChapters = 500;
    public const int MaxChapterTitle = 300;
    public const int MaxAnchor = 200;
    public const int MaxChapterLevel = 10;
    public const int MaxCodeLength = 100;
    public const int MaxRuleName = 200;
    public const int MaxImportBytes = 2 * 1024 * 1024;
    public const int MinPriority = -10_000;
    public const int MaxPriority = 10_000;
}

/// <summary>Model, variant and theme are codes: lower-case letters, digits, dot, underscore and hyphen, so they are stable between systems (ADR-0039).</summary>
public static class KnowledgeCodes
{
    /// <summary>Trims and lower-cases <paramref name="text"/>; null when it is not a code.</summary>
    public static string? Normalize(string? text)
    {
        var t = text?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(t) && t.Length <= KnowledgeLimits.MaxCodeLength && t.All(IsCodeChar) ? t : null;
    }

    public static bool IsCodeChar(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-';

    /// <summary>The words for a code a person typed wrongly.</summary>
    public static string Hint(string field) =>
        $"{field} must be 1 to {KnowledgeLimits.MaxCodeLength} characters: letters, digits, '.', '_' and '-' (it is stored in lower case).";
}

/// <summary>One entry of a document's table of contents. HTML chapters have an anchor; PDF chapters have pages.</summary>
public sealed record Chapter(string Title, string? Anchor, int Level, int? PageFrom, int? PageTo);

/// <summary>Pure helpers for chapter lists.</summary>
public static class ChapterRules
{
    /// <summary>
    /// Fills in each chapter's last page: the page before the next chapter of the same or a higher level, or the last page of the file. A chapter with no
    /// first page keeps none; a last page is never before its first.
    /// </summary>
    public static List<Chapter> WithPageRanges(IReadOnlyList<Chapter> chapters, int lastPage)
    {
        var result = new List<Chapter>(chapters.Count);
        for (var i = 0; i < chapters.Count; i++)
        {
            var c = chapters[i];
            if (c.PageFrom is not { } from || from < 1 || from > lastPage) { result.Add(c with { PageFrom = null, PageTo = null }); continue; }
            var to = lastPage;
            for (var j = i + 1; j < chapters.Count; j++)
            {
                if (chapters[j].Level > c.Level || chapters[j].PageFrom is not { } next || next < 1 || next > lastPage) continue;
                to = Math.Max(from, next - 1);
                break;
            }
            result.Add(c with { PageFrom = from, PageTo = to });
        }
        return result;
    }

    /// <summary>Trims titles, drops the empty ones and keeps at most <see cref="KnowledgeLimits.MaxChapters"/>.</summary>
    public static List<Chapter> Tidy(IEnumerable<Chapter> chapters, out bool truncated)
    {
        var list = new List<Chapter>();
        truncated = false;
        foreach (var c in chapters)
        {
            var title = CleanTitle(c.Title);
            if (title.Length == 0) continue;
            if (list.Count == KnowledgeLimits.MaxChapters) { truncated = true; break; }
            var anchor = c.Anchor?.Trim();
            list.Add(c with
            {
                Title = title,
                Anchor = string.IsNullOrEmpty(anchor) ? null : anchor.Length > KnowledgeLimits.MaxAnchor ? anchor[..KnowledgeLimits.MaxAnchor] : anchor,
                Level = Math.Clamp(c.Level, 1, KnowledgeLimits.MaxChapterLevel),
            });
        }
        return list;
    }

    /// <summary>A title with control characters removed and white space collapsed, at most <see cref="KnowledgeLimits.MaxChapterTitle"/> characters.</summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var sb = new System.Text.StringBuilder(title.Length);
        var space = false;
        foreach (var ch in title)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        var t = sb.ToString();
        return t.Length > KnowledgeLimits.MaxChapterTitle ? t[..KnowledgeLimits.MaxChapterTitle].TrimEnd() : t;
    }
}

public sealed record KnowledgeDocumentCreated(Guid DocumentId, Guid AssetId, string DocType, string Model, string? Variant, string Language) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.document.created";
    public override string Name => EventName;
    public string EntityType => "knowledge_document";
    public Guid EntityId => DocumentId;
}

public sealed record KnowledgeDocumentUpdated(Guid DocumentId, Guid AssetId, string DocType, string Model, string? Variant, string Language) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.document.updated";
    public override string Name => EventName;
    public string EntityType => "knowledge_document";
    public Guid EntityId => DocumentId;
}

public sealed record KnowledgeDocumentDeleted(Guid DocumentId, Guid AssetId) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.document.deleted";
    public override string Name => EventName;
    public string EntityType => "knowledge_document";
    public Guid EntityId => DocumentId;
}

/// <summary>Someone asked for the chapters to be read again (the worker's <c>knowledge-chapters</c> consumer acts on it).</summary>
public sealed record KnowledgeReindexRequested(Guid DocumentId, Guid AssetId, bool Force) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.document.reindex_requested";
    public override string Name => EventName;
    public string EntityType => "knowledge_document";
    public Guid EntityId => DocumentId;
}

public sealed record KnowledgeChaptersIndexed(Guid DocumentId, Guid AssetId, Guid? VersionId, string Source, int ChapterCount) : DomainEvent, IEntityEvent
{
    public const string EventName = "knowledge.chapters.indexed";
    public override string Name => EventName;
    public string EntityType => "knowledge_document";
    public Guid EntityId => DocumentId;
}

/// <summary>
/// A knowledge document (ADR-0039): the mapping that makes an asset (a manual, a FAQ, a service document) findable by vehicle. The asset keeps everything else;
/// deleting the mapping never touches it.
/// </summary>
public sealed class KnowledgeDocument : AggregateRoot
{
    private KnowledgeDocument() { }

    public Guid AssetId { get; private set; }
    public string DocType { get; private set; } = KnowledgeDocTypes.OwnerManual;
    public string Model { get; private set; } = "";
    /// <summary>Null means the document fits every variant of the model.</summary>
    public string? Variant { get; private set; }
    public int? ModelYearFrom { get; private set; }
    public int? ModelYearTo { get; private set; }
    public string Language { get; private set; } = "en";
    public string? Theme { get; private set; }
    public List<Chapter> Chapters { get; private set; } = [];
    public string ChaptersSource { get; private set; } = ChapterSources.None;
    /// <summary>The asset version the chapters were read from; null while they have not been indexed (or were cleared to be read again).</summary>
    public Guid? ChaptersIndexedVersionId { get; private set; }
    public DateTimeOffset? ChaptersIndexedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static KnowledgeDocument Create(Guid tenantId, Guid assetId, string docType, string model, string? variant, int? yearFrom, int? yearTo, string language,
        string? theme, Guid? createdBy, DateTimeOffset now)
    {
        var d = new KnowledgeDocument { TenantId = tenantId, AssetId = assetId, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now };
        d.Apply(docType, model, variant, yearFrom, yearTo, language, theme);
        d.Raise(new KnowledgeDocumentCreated(d.Id, assetId, d.DocType, d.Model, d.Variant, d.Language));
        return d;
    }

    public void Update(string docType, string model, string? variant, int? yearFrom, int? yearTo, string language, string? theme, DateTimeOffset now)
    {
        Apply(docType, model, variant, yearFrom, yearTo, language, theme);
        UpdatedAt = now;
        Raise(new KnowledgeDocumentUpdated(Id, AssetId, DocType, Model, Variant, Language));
    }

    public void MarkDeleted() => Raise(new KnowledgeDocumentDeleted(Id, AssetId));

    private void Apply(string docType, string model, string? variant, int? yearFrom, int? yearTo, string language, string? theme)
    {
        DocType = docType;
        Model = model;
        Variant = variant;
        ModelYearFrom = yearFrom;
        ModelYearTo = yearTo;
        Language = language;
        Theme = theme;
    }

    /// <summary>Asks the worker to read the chapters again; manual chapters are replaced only when <paramref name="force"/> is true.</summary>
    public void RequestReindex(bool force) => Raise(new KnowledgeReindexRequested(Id, AssetId, force));

    /// <summary>A person's own table of contents (source <c>manual</c>). An empty list clears the chapters and hands the document back to automatic indexing.</summary>
    public void SetManualChapters(IReadOnlyList<Chapter> chapters, Guid? versionId, DateTimeOffset now)
    {
        UpdatedAt = now;
        if (chapters.Count == 0)
        {
            Chapters = [];
            ChaptersSource = ChapterSources.None;
            ChaptersIndexedVersionId = null;
            ChaptersIndexedAt = null;
            Raise(new KnowledgeChaptersIndexed(Id, AssetId, versionId, ChapterSources.None, 0));
            RequestReindex(false);
            return;
        }
        Chapters = chapters.ToList();
        ChaptersSource = ChapterSources.Manual;
        ChaptersIndexedVersionId = versionId;
        ChaptersIndexedAt = now;
        Raise(new KnowledgeChaptersIndexed(Id, AssetId, versionId, ChapterSources.Manual, Chapters.Count));
    }

    /// <summary>The worker read the file: replaces the chapters (automatic or forced) and remembers which version they came from.</summary>
    public void ApplyIndexed(Guid versionId, string source, IReadOnlyList<Chapter> chapters, DateTimeOffset now)
    {
        Chapters = chapters.ToList();
        ChaptersSource = chapters.Count == 0 ? ChapterSources.None : source;
        ChaptersIndexedVersionId = versionId;
        ChaptersIndexedAt = now;
        UpdatedAt = now;
        Raise(new KnowledgeChaptersIndexed(Id, AssetId, versionId, ChaptersSource, Chapters.Count));
    }

    /// <summary>A new version arrived but the chapters are the person's own: they are kept, and this version is noted as seen so it is not read again.</summary>
    public void KeepManualFor(Guid versionId, DateTimeOffset now)
    {
        ChaptersIndexedVersionId = versionId;
        ChaptersIndexedAt = now;
    }
}
