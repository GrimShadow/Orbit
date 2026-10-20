using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dam.Domain.Common;

namespace Dam.Domain.Ai;

public static class TranslationJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public static readonly IReadOnlyList<string> All = [Pending, Running, Succeeded, Partial, Failed];
    public static bool IsFinished(string status) => status is Succeeded or Partial or Failed;
}

public static class TranslationItemStatuses
{
    public const string Ok = "ok";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>What happened to one field in one language. Stored as part of the job (jsonb).</summary>
public sealed class TranslationItem
{
    public string Field { get; set; } = "";
    public string Language { get; set; } = "";
    public string Status { get; set; } = TranslationItemStatuses.Ok;
    public string? Reason { get; set; }
}

/// <summary>
/// One request to translate some multilingual fields of an asset into some languages (ADR-0036), and what came of it. The worker's <c>ai-translate</c>
/// consumer works through it field by field and language by language and records an outcome for each; the translations themselves are
/// <see cref="AiSuggestion"/>s of kind <c>translation</c>, waiting for a person.
/// </summary>
public sealed class TranslationJob : TenantEntity
{
    private TranslationJob() { }

    public Guid AssetId { get; private set; }
    public string Status { get; private set; } = TranslationJobStatuses.Pending;
    public List<string> Fields { get; private set; } = [];
    public List<string> TargetLanguages { get; private set; } = [];
    /// <summary>The language the caller asked to translate from; null when it was left to be chosen per field.</summary>
    public string? SourceLanguage { get; private set; }
    public bool Overwrite { get; private set; }
    public Guid? RequestedBy { get; private set; }
    public List<TranslationItem> Items { get; private set; } = [];
    /// <summary>Why the whole job failed (no item was attempted), when it did.</summary>
    public string Error { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public static TranslationJob Create(
        Guid tenantId, Guid assetId, IEnumerable<string> fields, IEnumerable<string> targetLanguages, string? sourceLanguage, bool overwrite, Guid? requestedBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, AssetId = assetId, Fields = fields.ToList(), TargetLanguages = targetLanguages.ToList(), SourceLanguage = sourceLanguage,
            Overwrite = overwrite, RequestedBy = requestedBy, CreatedAt = now,
        };

    public bool IsFinished => TranslationJobStatuses.IsFinished(Status);

    public void Start(DateTimeOffset now)
    {
        Status = TranslationJobStatuses.Running;
        StartedAt ??= now;
    }

    public void Finish(IReadOnlyList<TranslationItem> items, DateTimeOffset now)
    {
        Items = [.. items];
        Status = TranslationRules.StatusFor(items);
        Error = "";
        FinishedAt = now;
    }

    /// <summary>Nothing could be attempted (AI switched off meanwhile, the asset gone, the provider gave up).</summary>
    public void Fail(string reason, DateTimeOffset now)
    {
        Status = TranslationJobStatuses.Failed;
        Error = reason.Length > 1000 ? reason[..1000] : reason;
        FinishedAt = now;
    }

    public int Count(string itemStatus) => Items.Count(i => i.Status == itemStatus);
}

/// <summary>Asks the worker to run a translation job (internal; audit trail of who asked for what).</summary>
public sealed record AssetTranslationRequested(Guid AssetId, Guid JobId, IReadOnlyList<string> Fields, IReadOnlyList<string> TargetLanguages, bool Overwrite) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.translation.requested";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>A translation job has finished (or could not run): there is something to review, or something to know. Public (webhooks, chat, the requester's
/// notification); carries counts and ids, never any text. Written to the outbox by the worker directly, like <see cref="AssetAiEnriched"/>.</summary>
public sealed record AssetTranslationReady(Guid AssetId, Guid JobId, Guid? RequestedBy, string Status, int Translated, int Skipped, int Failed) : DomainEvent, IEntityEvent
{
    public const string EventName = "asset.translation.ready";
    public override string Name => EventName;
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>What a translation request may ask for, and how a job's outcome is worked out. Pure.</summary>
public static class TranslationRules
{
    public const int MaxFields = 20;
    public const int MaxLanguages = 10;
    /// <summary>The longest source text a request accepts when the field states no limit of its own.</summary>
    public const int DefaultMaxChars = 20_000;
    public const int MaxGlossaryTerms = 5_000;

    /// <summary>The longest source text of a field that is sent for translation: the field's own limit, and never more than <see cref="DefaultMaxChars"/>.</summary>
    public static int SourceLimit(int? fieldMaxLength) => Math.Min(fieldMaxLength ?? DefaultMaxChars, DefaultMaxChars);

    /// <summary>
    /// The job's status from what happened to its items. Skipped items (nothing to translate, text already there) are not failures; all items skipped is a
    /// job that succeeded in doing nothing. At least one translation made and at least one failure is <c>partial</c>; failures and nothing made is <c>failed</c>.
    /// No items at all is <c>failed</c> (there was nothing to attempt).
    /// </summary>
    public static string StatusFor(IReadOnlyList<TranslationItem> items)
    {
        if (items.Count == 0) return TranslationJobStatuses.Failed;
        var ok = items.Count(i => i.Status == TranslationItemStatuses.Ok);
        var failed = items.Count(i => i.Status == TranslationItemStatuses.Failed);
        if (failed == 0) return TranslationJobStatuses.Succeeded;
        return ok > 0 ? TranslationJobStatuses.Partial : TranslationJobStatuses.Failed;
    }

    /// <summary>A language code as multilingual metadata keys it: lower case, 2 to 12 characters, a letter first, then letters, digits or hyphens. Null when it is not one.</summary>
    public static string? NormalizeLanguage(string? code)
    {
        if (code is null) return null;
        var c = code.Trim().ToLowerInvariant();
        return c.Length is >= 2 and <= 12 && char.IsAsciiLetter(c[0]) && c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') ? c : null;
    }

    /// <summary>SHA-256 (hex) of a source text, as stored with a translation to tell later whether the source has changed.</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The text a multilingual field holds in a language, or null when it has none (or the field is not an object).</summary>
    public static string? TextOf(JsonElement? fieldValue, string language)
    {
        if (fieldValue is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, language, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                return p.Value.GetString();
        return null;
    }

    /// <summary>
    /// The language to translate a field from. The caller's choice when given; otherwise the tenant's default language, then English, then whichever other
    /// language has text, first in the field's own order. A language that is a target is never chosen (it would be translated into itself).
    /// Null when there is no usable source.
    /// </summary>
    public static (string Language, string Text)? ChooseSource(JsonElement? fieldValue, string? requested, string? tenantDefault, string target)
    {
        if (fieldValue is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (requested is not null)
            return requested != target && TextOf(fieldValue, requested) is { } t ? (requested, t) : null;

        var order = new List<string>();
        if (tenantDefault is not null) order.Add(tenantDefault);
        order.Add("en");
        order.AddRange(obj.EnumerateObject().Select(p => p.Name.ToLowerInvariant()));
        foreach (var language in order.Distinct())
            if (language != target && TextOf(fieldValue, language) is { } text) return (language, text);
        return null;
    }
}

/// <summary>One thing the worker will do (or skip) for a field and target language.</summary>
/// <param name="SkipReason">Why nothing will be translated here; null when the text is to be translated.</param>
/// <param name="NoSource">Skipped because the field has no text to translate from (as opposed to the language already having text).</param>
public sealed record TranslationWork(string Field, string Language, string? SourceLanguage, string? SourceText, string? SkipReason, bool NoSource = false)
{
    public bool ShouldTranslate => SkipReason is null;
}

public static class TranslationPlanner
{
    /// <summary>
    /// Works out, for every requested field and language, whether there is anything to translate and from what. Skipped, with a plain reason: the field has no text
    /// to translate from, the language is the source, or the language already has text and <paramref name="overwrite"/> is false.
    /// </summary>
    /// <param name="valueOf">The current value of a multilingual field (null: the asset has none).</param>
    public static IReadOnlyList<TranslationWork> Plan(
        IReadOnlyList<string> fields, IReadOnlyList<string> languages, string? requestedSource, string? tenantDefault, bool overwrite, Func<string, JsonElement?> valueOf)
    {
        var work = new List<TranslationWork>();
        foreach (var field in fields)
        {
            var value = valueOf(field);
            foreach (var language in languages)
            {
                if (requestedSource is not null && requestedSource == language)
                {
                    work.Add(new TranslationWork(field, language, requestedSource, null, $"'{language}' is the source language."));
                    continue;
                }
                if (!overwrite && TranslationRules.TextOf(value, language) is not null)
                {
                    work.Add(new TranslationWork(field, language, null, null, $"Already has text in '{language}'; ask to overwrite to replace it."));
                    continue;
                }
                var source = TranslationRules.ChooseSource(value, requestedSource, tenantDefault, language);
                work.Add(source is { } s
                    ? new TranslationWork(field, language, s.Language, s.Text, null)
                    : new TranslationWork(field, language, requestedSource, null,
                        requestedSource is null ? "There is no text in another language to translate from." : $"There is no text in '{requestedSource}' to translate from.",
                        NoSource: true));
            }
        }
        return work;
    }
}
