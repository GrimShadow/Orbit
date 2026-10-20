using Dam.Domain.Common;

namespace Dam.Domain.Ai;

public static class AiProviderKinds
{
    public const string SelfHosted = "self-hosted";
    public const string OpenAiCompatible = "openai-compatible";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [SelfHosted, OpenAiCompatible, None];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>Reads a configured value leniently ("OpenAI", "selfhosted", blank) and returns a known kind, or <c>none</c>.</summary>
    public static string Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "self-hosted" or "selfhosted" or "self_hosted" => SelfHosted,
        "openai-compatible" or "openai" or "openai_compatible" or "azure" or "vllm" => OpenAiCompatible,
        _ => None,
    };
}

public static class AiSuggestionKinds
{
    public const string Tag = "tag";
    public const string Language = "language";
    /// <summary>A proposed rendering of one multilingual metadata field in another language (ADR-0036).</summary>
    public const string Translation = "translation";
    public static readonly IReadOnlyList<string> All = [Tag, Language, Translation];
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}

public static class AiSuggestionStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public static class AiRunStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

public static class AiStepStatuses
{
    public const string Ok = "ok";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public static class EmbeddingKinds
{
    public const string Image = "image";
    public const string Text = "text";
}

/// <summary>Which enrichment features a tenant has switched on. All on by default; the tenant's <c>enabled</c> flag still gates everything.</summary>
public sealed class AiFeatureFlags
{
    public bool Tags { get; set; } = true;
    public bool Ocr { get; set; } = true;
    public bool Transcribe { get; set; } = true;
    public bool Embeddings { get; set; } = true;
    /// <summary>Summaries of long documents (ADR-0036). On unless switched off; settings stored before Step 2.2 lack the key and read as on.</summary>
    public bool Summaries { get; set; } = true;

    public AiFeatureFlags Copy() => new() { Tags = Tags, Ocr = Ocr, Transcribe = Transcribe, Embeddings = Embeddings, Summaries = Summaries };
}

/// <summary>
/// A tenant's AI switchboard (ADR-0035): whether enrichment runs at all (<see cref="Enabled"/>, off until an administrator turns it on, because a
/// cloud provider means pictures and audio leave the building), which provider (null: the deployment's default), which features, and the OCR language
/// hint. One row per tenant; the tenant id is the key.
/// </summary>
public sealed class AiSettings : ITenantScoped
{
    private AiSettings() { }

    public Guid TenantId { get; private set; }
    public bool Enabled { get; private set; }
    /// <summary>One of <see cref="AiProviderKinds"/>, or null to use the deployment default (DAM_AI_PROVIDER).</summary>
    public string? Provider { get; private set; }
    public AiFeatureFlags Features { get; private set; } = new();
    public string? OcrLanguage { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AiSettings Create(Guid tenantId, DateTimeOffset now) => new() { TenantId = tenantId, UpdatedAt = now };

    public void Update(bool enabled, string? provider, AiFeatureFlags features, string? ocrLanguage, Guid? by, DateTimeOffset now)
    {
        Enabled = enabled;
        Provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
        Features = features.Copy();
        OcrLanguage = string.IsNullOrWhiteSpace(ocrLanguage) ? null : ocrLanguage.Trim().ToLowerInvariant();
        UpdatedBy = by;
        UpdatedAt = now;
    }
}

/// <summary>
/// Something the AI proposes and a person decides (a tag, the language of the content, a translation). Never written into the asset until accepted
/// (spec A11). Tags and languages are unique per (asset, version, kind, value), so a re-run never proposes the same thing twice and never resurrects a
/// rejection. A translation is the text itself in <see cref="Value"/>; there is at most one pending per (asset, field, language), and a new request
/// replaces it (ADR-0036).
/// </summary>
public sealed class AiSuggestion : TenantEntity
{
    private AiSuggestion() { }

    public Guid AssetId { get; private set; }
    public Guid VersionId { get; private set; }
    public string Kind { get; private set; } = "";
    public string Value { get; private set; } = "";
    public float Confidence { get; private set; }
    public string Model { get; private set; } = "";
    public string Status { get; private set; } = AiSuggestionStatuses.Pending;
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Translations only: the multilingual metadata field the text is for.</summary>
    public string? Field { get; private set; }
    /// <summary>Translations only: the target language.</summary>
    public string? Language { get; private set; }
    /// <summary>Translations only: the language that was translated from.</summary>
    public string? SourceLanguage { get; private set; }
    /// <summary>Translations only: hash of the source text, so the suggestion can say when the source has changed since.</summary>
    public string? SourceHash { get; private set; }

    public static AiSuggestion Create(Guid tenantId, Guid assetId, Guid versionId, string kind, string value, float confidence, string model, DateTimeOffset now) => new()
    {
        TenantId = tenantId, AssetId = assetId, VersionId = versionId, Kind = kind, Value = value,
        Confidence = float.IsFinite(confidence) ? Math.Clamp(confidence, 0f, 1f) : 0f,
        Model = model.Length > 100 ? model[..100] : model, CreatedAt = now,
    };

    /// <summary>A proposed translation. <paramref name="versionId"/> is the asset's current version (an empty id for an asset with no file yet): metadata does not
    /// belong to a version, so a translation stays valid when a new file arrives.</summary>
    public static AiSuggestion CreateTranslation(
        Guid tenantId, Guid assetId, Guid versionId, string field, string language, string sourceLanguage, string sourceHash, string text, string model, DateTimeOffset now) => new()
    {
        TenantId = tenantId, AssetId = assetId, VersionId = versionId, Kind = AiSuggestionKinds.Translation, Value = text, Confidence = 0f,
        Model = model.Length > 100 ? model[..100] : model, Field = field, Language = language, SourceLanguage = sourceLanguage, SourceHash = sourceHash, CreatedAt = now,
    };

    public bool IsPending => Status == AiSuggestionStatuses.Pending;
    public bool IsTranslation => Kind == AiSuggestionKinds.Translation;

    /// <summary>A newer request for the same field and language replaces a translation still waiting for review.</summary>
    public void Supersede(Guid versionId, string sourceLanguage, string sourceHash, string text, string model, DateTimeOffset now)
    {
        VersionId = versionId;
        SourceLanguage = sourceLanguage;
        SourceHash = sourceHash;
        Value = text;
        Model = model.Length > 100 ? model[..100] : model;
        CreatedAt = now;
    }

    /// <summary>Accepting a translation, optionally as the reviewer edited it: what is kept is the text that went into the asset.</summary>
    public void AcceptTranslation(string finalText, Guid? by, DateTimeOffset now)
    {
        Value = finalText;
        Accept(by, now);
    }

    public void Accept(Guid? by, DateTimeOffset now) { Status = AiSuggestionStatuses.Accepted; DecidedBy = by; DecidedAt = now; }
    public void Reject(Guid? by, DateTimeOffset now) { Status = AiSuggestionStatuses.Rejected; DecidedBy = by; DecidedAt = now; }
}

/// <summary>One timed line of a transcript. Times are milliseconds from the start of the media.</summary>
public sealed record TranscriptSegment(long StartMs, long EndMs, string Text);

/// <summary>
/// Text pulled out of a version: what OCR read from the picture or pages, what a transcript says. This is extraction, not editorial: it is
/// indexed and shown as it is (ADR-0035), unlike tags and languages, which wait for a person.
/// </summary>
public sealed class AssetExtract : ITenantScoped
{
    private AssetExtract() { }

    public Guid TenantId { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid VersionId { get; private set; }
    public string? OcrText { get; private set; }
    public string? OcrLanguage { get; private set; }
    public string? TranscriptLanguage { get; private set; }
    public List<TranscriptSegment> TranscriptSegments { get; private set; } = [];
    public string? TranscriptText { get; private set; }
    public long? DurationMs { get; private set; }
    /// <summary>A short summary of the text (ADR-0036). Derived data like the OCR text: indexed and shown as it is, labelled as AI-written in the console.</summary>
    public string? Summary { get; private set; }
    public string? SummaryLanguage { get; private set; }
    public DateTimeOffset? SummaryAt { get; private set; }
    /// <summary>The model that produced each part: keys ocr, transcript, summary.</summary>
    public Dictionary<string, string> Model { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AssetExtract Create(Guid tenantId, Guid assetId, Guid versionId, DateTimeOffset now) =>
        new() { TenantId = tenantId, AssetId = assetId, VersionId = versionId, CreatedAt = now, UpdatedAt = now };

    public void SetOcr(string? text, string? language, string model, DateTimeOffset now)
    {
        OcrText = string.IsNullOrWhiteSpace(text) ? null : text.Replace("\0", "", StringComparison.Ordinal);
        OcrLanguage = language;
        Model = new Dictionary<string, string>(Model) { ["ocr"] = model };
        UpdatedAt = now;
    }

    public void SetSummary(string? text, string? language, string model, DateTimeOffset now)
    {
        Summary = string.IsNullOrWhiteSpace(text) ? null : text.Replace("\0", "", StringComparison.Ordinal).Trim();
        SummaryLanguage = Summary is null ? null : language;
        SummaryAt = Summary is null ? null : now;
        Model = new Dictionary<string, string>(Model) { ["summary"] = model };
        UpdatedAt = now;
    }

    public void SetTranscript(IReadOnlyList<TranscriptSegment> segments, string? language, long durationMs, string model, DateTimeOffset now)
    {
        TranscriptSegments = segments.Select(s => s with { Text = s.Text.Replace("\0", "", StringComparison.Ordinal) }).ToList();
        TranscriptText = segments.Count == 0 ? null : string.Join(' ', segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        TranscriptLanguage = language;
        DurationMs = durationMs;
        Model = new Dictionary<string, string>(Model) { ["transcript"] = model };
        UpdatedAt = now;
    }
}

/// <summary>A vector kept so a reindex never has to ask the provider again (float32, little-endian; see <see cref="EmbeddingCodec"/>).</summary>
public sealed class AssetEmbedding : ITenantScoped
{
    private AssetEmbedding() { }

    public Guid TenantId { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid VersionId { get; private set; }
    /// <summary>One of <see cref="EmbeddingKinds"/>.</summary>
    public string Kind { get; private set; } = "";
    public int Dim { get; private set; }
    public string Model { get; private set; } = "";
    public byte[] Vector { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    public static AssetEmbedding Create(Guid tenantId, Guid assetId, Guid versionId, string kind, string model, float[] vector, DateTimeOffset now) => new()
    {
        TenantId = tenantId, AssetId = assetId, VersionId = versionId, Kind = kind, Dim = vector.Length,
        Model = model.Length > 100 ? model[..100] : model, Vector = EmbeddingCodec.ToBytes(vector), CreatedAt = now,
    };

    public float[] ToVector() => EmbeddingCodec.FromBytes(Vector);

    /// <summary>A re-run replaces the vector in place (the key is the row's identity).</summary>
    public void Replace(string model, float[] vector, DateTimeOffset now)
    {
        Dim = vector.Length;
        Model = model.Length > 100 ? model[..100] : model;
        Vector = EmbeddingCodec.ToBytes(vector);
        CreatedAt = now;
    }
}

/// <summary>What one step of an enrichment run did, for the "AI" panel and for operators.</summary>
public sealed class AiStep
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = AiStepStatuses.Ok;
    public int DurationMs { get; set; }
    public string? Model { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Published when a version's enrichment has been stored: the search indexer picks it up to index the vectors and the extracted text. Internal, and written to the
/// outbox directly rather than through the audit trail: it is a consequence of work the run record already documents, and keeping it out of the audit chain keeps the
/// enrichment's transaction away from the audit chain's per-tenant lock (which a long rendition job can hold for minutes).</summary>
public sealed record AssetAiEnriched(Guid AssetId, Guid VersionId, int Suggestions) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.ai.enriched";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>Asks the worker to enrich an asset (a re-run, or a backfill). <paramref name="VersionId"/> null means the current version.</summary>
public sealed record AssetAiRequested(Guid AssetId, Guid? VersionId, bool Force) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.ai.requested";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>Asks the worker to (re)write the summary of the asset's current version from the text already extracted from it (ADR-0036).</summary>
public sealed record AssetSummariseRequested(Guid AssetId, Guid VersionId) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.ai.summarise.requested";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>A person accepted or rejected a suggestion (audit trail).</summary>
public sealed record AiSuggestionDecided(Guid AssetId, Guid SuggestionId, string Kind, string Value, string Decision) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.ai.suggestion.decided";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>An administrator changed the tenant's AI settings (audit trail). Never carries anything secret.</summary>
public sealed record AiSettingsChanged(Guid TenantId, bool Enabled, string? Provider, bool Tags, bool Ocr, bool Transcribe, bool Embeddings, bool Summaries = true) : DomainEvent, IEntityEvent
{
    public const string EventName = "ai.settings.changed";
    public override string Name => EventName;
    public string EntityType => "tenant";
    public Guid EntityId => TenantId;
}

/// <summary>Asks the worker to rebuild the tenant's search index (needed after a vector dimension change).</summary>
public sealed record SearchReindexRequested(Guid TenantId) : DomainEvent, IEntityEvent
{
    public const string EventName = "search.reindex.requested";
    public override string Name => EventName;
    public string EntityType => "tenant";
    public Guid EntityId => TenantId;
}

/// <summary>The record of one enrichment of one version. A re-run reuses the row.</summary>
public sealed class AiRun : AggregateRoot
{
    private AiRun() { }

    public Guid AssetId { get; private set; }
    public Guid VersionId { get; private set; }
    public string Status { get; private set; } = AiRunStatuses.Pending;
    public List<AiStep> Steps { get; private set; } = [];
    public string Error { get; private set; } = "";
    public int Attempts { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AiRun Create(Guid tenantId, Guid assetId, Guid versionId, DateTimeOffset now) =>
        new() { TenantId = tenantId, AssetId = assetId, VersionId = versionId, CreatedAt = now };

    public bool IsActive => Status is AiRunStatuses.Pending or AiRunStatuses.Running;

    /// <summary>Queued again (a manual re-run, or a backfill).</summary>
    public void Queue()
    {
        Status = AiRunStatuses.Pending;
        Steps = [];
        Error = "";
        StartedAt = null;
        FinishedAt = null;
    }

    public void Start(DateTimeOffset now)
    {
        Status = AiRunStatuses.Running;
        Attempts++;
        StartedAt = now;
        FinishedAt = null;
        Error = "";
    }

    /// <summary>An attempt failed and the message will be tried again; the row stays visible as waiting, with the reason.</summary>
    public void NoteRetry(string error, DateTimeOffset now)
    {
        Status = AiRunStatuses.Pending;
        Error = Clip(error);
        FinishedAt = null;
        StartedAt ??= now;
    }

    public void Succeed(IReadOnlyList<AiStep> steps, DateTimeOffset now)
    {
        Status = AiRunStatuses.Succeeded;
        Steps = [.. steps];
        Error = "";
        FinishedAt = now;
    }

    public void Fail(IReadOnlyList<AiStep> steps, string error, DateTimeOffset now)
    {
        Status = AiRunStatuses.Failed;
        Steps = [.. steps];
        Error = Clip(error);
        FinishedAt = now;
    }

    /// <summary>Records what a single step did after the run finished (a manual summary), without changing the run's own status.</summary>
    public void UpsertStep(AiStep step)
    {
        var steps = Steps.Where(x => x.Name != step.Name).ToList();
        steps.Add(step);
        Steps = steps;
    }

    public void Skip(string reason, IReadOnlyList<AiStep> steps, DateTimeOffset now)
    {
        Status = AiRunStatuses.Skipped;
        Steps = [.. steps];
        Error = Clip(reason);
        FinishedAt = now;
    }

    private static string Clip(string s) => s.Length > 1000 ? s[..1000] : s;
}
