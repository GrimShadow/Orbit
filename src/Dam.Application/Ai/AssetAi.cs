using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Ai;
using FluentValidation;

namespace Dam.Application.Ai;

internal static class AiMapping
{
    public const int OcrTextLimit = 20_000;
    public const int MaxTags = 50;

    /// <summary>A suggestion as the caller sees it. A pending translation says whether the text it was made from has changed since (<c>sourceStale</c>).</summary>
    public static AiSuggestionDto ToDto(AiSuggestion s, Asset asset) =>
        new(s.Id, s.Kind, s.Value, s.Confidence, s.Model, s.Status, s.CreatedAt, s.DecidedBy, s.DecidedAt, s.Field, s.Language, s.SourceLanguage, IsStale(s, asset));

    private static bool IsStale(AiSuggestion s, Asset asset)
    {
        if (!s.IsTranslation || !s.IsPending || s.Field is null || s.SourceLanguage is null) return false;
        var now = asset.Metadata.TryGetValue(s.Field, out var value) ? TranslationRules.TextOf(value, s.SourceLanguage) : null;
        return now is null || TranslationRules.Hash(now) != s.SourceHash;
    }

    public static AiSummaryDto? SummaryOf(AssetExtract? x) =>
        x is { Summary: { Length: > 0 } text } ? new AiSummaryDto(text, x.SummaryLanguage, x.Model.GetValueOrDefault("summary") ?? "", x.SummaryAt ?? x.UpdatedAt) : null;

    public static AiRunDto ToDto(AiRun r) =>
        new(r.Id, r.VersionId, r.Status, r.Steps.Select(s => new AiStepDto(s.Name, s.Status, s.DurationMs, s.Model, s.Detail)).ToList(),
            r.Error.Length == 0 ? null : r.Error, r.Attempts, r.StartedAt, r.FinishedAt);

    public static AiFeaturesDto ToDto(AiFeatureFlags f) => new(f.Tags, f.Ocr, f.Transcribe, f.Embeddings, f.Summaries);

    public static AiProviderCapabilitiesDto ToDto(AiCapabilities c) =>
        new(c.Tags, c.Ocr, c.Transcribe, c.TextEmbeddings, c.ImageEmbeddings, c.DetectLanguage, c.Translate, c.Summarise);

    public static AiHealthDto ToDto(AiHealth h) => new(h.Status, h.Detail, h.LatencyMs, h.Models ?? new Dictionary<string, string>());
}

// ------------------------------------------------------------------------------------------------------------ read

/// <summary>
/// What the AI found for an asset's current version: its run, the suggestions (pending, accepted and rejected), the text read from the picture or
/// pages, the transcript. Needs <c>assets.read</c> on the asset (one the caller cannot see is 404).
/// </summary>
public sealed record GetAssetAiQuery(Guid AssetId) : IQuery<AssetAiDto>;

public sealed class GetAssetAiHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AiRun> runs, IRepository<AiSuggestion> suggestions,
    IRepository<AssetExtract> extracts, IRepository<AssetEmbedding> embeddings, IQueryExecutor q, IAuthorizationService authz, IAiResolver resolver)
    : IRequestHandler<GetAssetAiQuery, AssetAiDto>
{
    public async Task<Result<AssetAiDto>> Handle(GetAssetAiQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");

        // Tags, languages, text and vectors belong to a file; a translation belongs to the asset's metadata, so it is listed whichever file is current (or none yet).
        var hasFile = asset.CurrentVersionId is not null;
        var versionId = asset.CurrentVersionId ?? Guid.Empty;
        var run = await q.FirstOrDefaultAsync(runs.Query().Where(x => x.AssetId == asset.Id && x.VersionId == versionId), ct);
        var list = await q.ToListAsync(suggestions.Query().Where(x => x.AssetId == asset.Id && (x.VersionId == versionId || x.Kind == AiSuggestionKinds.Translation))
            .OrderByDescending(x => x.Confidence).ThenBy(x => x.Value), ct);
        var extract = await q.FirstOrDefaultAsync(extracts.Query().Where(x => x.AssetId == asset.Id && x.VersionId == versionId), ct);
        var hasVectors = await q.AnyAsync(embeddings.Query().Where(x => x.AssetId == asset.Id && x.VersionId == versionId), ct);

        var status = !hasFile ? await IdleStatusAsync(ct) : run?.Status switch
        {
            AiRunStatuses.Pending => "pending",
            AiRunStatuses.Running => "running",
            AiRunStatuses.Succeeded => "ready",
            AiRunStatuses.Failed => "failed",
            _ => await IdleStatusAsync(ct),
        };

        var ocr = extract?.OcrText;
        var truncated = ocr is { Length: > AiMapping.OcrTextLimit };
        if (truncated) ocr = ocr![..AiMapping.OcrTextLimit];
        var transcript = extract is { TranscriptSegments.Count: > 0 }
            ? new AiTranscriptDto(extract.TranscriptLanguage, extract.DurationMs, extract.Model.GetValueOrDefault("transcript"),
                extract.TranscriptSegments.Select(s => new TranscriptSegmentDto(s.StartMs, s.EndMs, s.Text)).ToList())
            : null;
        var language = list.Where(s => s.Kind == AiSuggestionKinds.Language && s.Status != AiSuggestionStatuses.Rejected).Select(s => s.Value).FirstOrDefault();

        return new AssetAiDto(status, run is null ? null : AiMapping.ToDto(run), list.Select(s => AiMapping.ToDto(s, asset)).ToList(), language, ocr, truncated,
            extract?.OcrLanguage, transcript, hasVectors, AiMapping.SummaryOf(extract));
    }

    private async Task<string> IdleStatusAsync(CancellationToken ct) => (await resolver.ResolveAsync(ct)).IsActive ? "none" : "disabled";
}

// --------------------------------------------------------------------------------------------------- decide

public enum AiDecision { Accept, Reject }

/// <summary>Applies a person's decision to suggestions: accepting a tag adds it to the asset the normal way (audited, re-indexed); accepting a language
/// sets the asset's <c>language</c> field when its schema has one and records the acceptance either way.</summary>
public sealed class AiSuggestionDecider(
    IAuthorizationService authz, AssetMetadataBuilder builder, IRepository<MetadataSchema> schemas, IQueryExecutor q, IEventCollector events, IClock clock,
    TranslationAcceptor translations)
{
    public async Task<AiDecisionDto> DecideAsync(Asset asset, IReadOnlyList<AiSuggestion> pending, AiDecision decision, CancellationToken ct)
    {
        var me = (await authz.GetProfileAsync(ct)).UserId;
        var now = clock.UtcNow;
        var notes = new List<string>();
        var changed = new List<AiSuggestion>();

        if (decision == AiDecision.Reject)
        {
            foreach (var s in pending.Where(s => s.IsPending))
            {
                s.Reject(me, now);
                events.Raise(new AiSuggestionDecided(asset.Id, s.Id, s.Kind, s.Value, "rejected"));
                changed.Add(s);
            }
            return Result(asset, pending, changed, notes);
        }

        var tags = pending.Where(s => s.IsPending && s.Kind == AiSuggestionKinds.Tag).ToList();
        if (tags.Count > 0)
        {
            var added = asset.AddTags(tags.Select(t => t.Value), AiMapping.MaxTags, now);
            var addedSet = added.ToHashSet(StringComparer.Ordinal);
            foreach (var s in tags)
            {
                var normalised = TagCandidates.Normalize(s.Value);
                // A tag the asset already has is simply accepted; one that did not fit under the limit stays pending.
                if (normalised is not null && !addedSet.Contains(normalised) && !asset.Tags.Contains(normalised)) continue;
                s.Accept(me, now);
                events.Raise(new AiSuggestionDecided(asset.Id, s.Id, s.Kind, s.Value, "accepted"));
                changed.Add(s);
            }
            if (tags.Count > changed.Count) notes.Add($"{tags.Count - changed.Count} tag(s) were left pending: an asset can have at most {AiMapping.MaxTags} tags.");
        }

        foreach (var s in pending.Where(s => s.IsPending && s.Kind == AiSuggestionKinds.Language))
        {
            var note = await ApplyLanguageAsync(asset, s.Value, now, ct);
            if (note is not null) notes.Add(note);
            s.Accept(me, now);
            events.Raise(new AiSuggestionDecided(asset.Id, s.Id, s.Kind, s.Value, "accepted"));
            changed.Add(s);
        }

        // Translations: each is merged into its field; one that cannot be (the language already has text, the field is gone) stays pending and says why.
        foreach (var s in pending.Where(s => s.IsPending && s.IsTranslation))
        {
            var problem = await translations.ApplyAsync(asset, s, null, replace: false, ct);
            if (problem is null) changed.Add(s);
            else notes.Add($"The {s.Language} translation of '{s.Field}' was left pending: {problem.Message}");
        }
        return Result(asset, pending, changed, notes);
    }

    private static AiDecisionDto Result(Asset asset, IReadOnlyList<AiSuggestion> all, List<AiSuggestion> changed, List<string> notes) =>
        new(all.Select(s => AiMapping.ToDto(s, asset)).ToList(), asset.Tags, changed.Count, notes);

    /// <summary>Sets the asset's language metadata when the schema has a field called <c>language</c> that accepts the value; otherwise explains why not.</summary>
    private async Task<string?> ApplyLanguageAsync(Asset asset, string code, DateTimeOffset now, CancellationToken ct)
    {
        var schema = await q.FirstOrDefaultAsync(schemas.Query().Where(s => s.AssetType == asset.Type && s.IsCurrent), ct);
        var field = schema?.Fields.FirstOrDefault(f => f.Name == "language");
        if (field is null) return $"Language '{code}' was recorded as accepted, but the {asset.Type} schema has no 'language' field to put it in.";
        if (field.Type is FieldType.MultilingualText or FieldType.Number or FieldType.Date or FieldType.Boolean or FieldType.Reference)
            return $"Language '{code}' was recorded as accepted; the schema's 'language' field is not a kind that can hold it.";

        var merged = new Dictionary<string, JsonElement>(asset.Metadata);
        var existing = merged.TryGetValue("language", out var e) ? e : (JsonElement?)null;
        if (field.Multi)
        {
            var values = existing is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
            if (!values.Contains(code, StringComparer.OrdinalIgnoreCase)) values.Add(code);
            merged["language"] = JsonSerializer.SerializeToElement(values);
        }
        else merged["language"] = JsonSerializer.SerializeToElement(code);

        var built = await builder.BuildAsync(asset.Type, null, JsonSerializer.SerializeToElement(merged), requireFields: false, ct);
        if (built.IsFailure) return $"Language '{code}' was recorded as accepted but not applied: the schema's language field does not allow that value.";
        asset.SetMetadata(built.Value.Metadata, built.Value.SchemaVersion, built.Value.Projection, now);
        return null;
    }
}

internal static class AiAssetAccess
{
    /// <summary>The asset for a change: visible, updatable, not deleted or blocked.</summary>
    public static async Task<(Asset? Asset, Error? Error)> LoadForUpdateAsync(
        IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, Guid id, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, id, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return (null, Error.NotFound("Asset not found."));
        if (!await authz.CanAsync(Permissions.AssetsUpdate, asset.ToResource(path), ct)) return (null, Error.Forbidden());
        if (asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return (null, Error.Conflict($"The asset is {asset.Status}."));
        return (asset, null);
    }
}

/// <summary>Accepts one suggestion. Needs <c>assets.update</c>. Only tag and language suggestions of the asset's current version can be decided. A translation is merged into
/// its field under its language (other languages untouched); <paramref name="Text"/> is the reviewer's edit of it, and <paramref name="Replace"/> allows replacing text
/// the language already has (otherwise 409). Both are ignored for tags and languages.</summary>
public sealed record AcceptSuggestionCommand(Guid AssetId, Guid SuggestionId, string? Text = null, bool Replace = false) : ICommand<AiDecisionDto>;

public sealed class AcceptSuggestionHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AiSuggestion> suggestions, IQueryExecutor q, IAuthorizationService authz, AiSuggestionDecider decider,
    TranslationAcceptor translations)
    : IRequestHandler<AcceptSuggestionCommand, AiDecisionDto>
{
    public async Task<Result<AiDecisionDto>> Handle(AcceptSuggestionCommand c, CancellationToken ct)
    {
        var (asset, error) = await AiAssetAccess.LoadForUpdateAsync(assets, folders, q, authz, c.AssetId, ct);
        if (error is not null) return error;
        var s = await q.FirstOrDefaultAsync(suggestions.Query().Where(x => x.Id == c.SuggestionId && x.AssetId == c.AssetId), ct);
        if (s is null) return Error.NotFound("Suggestion not found.");
        if (!s.IsPending) return Error.Conflict($"This suggestion was already {s.Status}.");

        if (s.IsTranslation)
        {
            var problem = await translations.ApplyAsync(asset!, s, c.Text, c.Replace, ct);
            if (problem is not null) return problem;
            return new AiDecisionDto(
                (await q.ToListAsync(suggestions.Query().Where(x => x.AssetId == asset!.Id && (x.VersionId == (asset.CurrentVersionId ?? Guid.Empty) || x.Kind == AiSuggestionKinds.Translation))
                    .OrderByDescending(x => x.Confidence).ThenBy(x => x.Value), ct)).Select(x => AiMapping.ToDto(x, asset!)).ToList(),
                asset!.Tags, 1, []);
        }
        if (s.VersionId != asset!.CurrentVersionId) return Error.Conflict("This suggestion belongs to an older version of the asset.");
        return await decider.DecideAsync(asset, [s], AiDecision.Accept, ct);
    }
}

public sealed record RejectSuggestionCommand(Guid AssetId, Guid SuggestionId) : ICommand<AiDecisionDto>;

public sealed class RejectSuggestionHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AiSuggestion> suggestions, IQueryExecutor q, IAuthorizationService authz, AiSuggestionDecider decider)
    : IRequestHandler<RejectSuggestionCommand, AiDecisionDto>
{
    public async Task<Result<AiDecisionDto>> Handle(RejectSuggestionCommand c, CancellationToken ct)
    {
        var (asset, error) = await AiAssetAccess.LoadForUpdateAsync(assets, folders, q, authz, c.AssetId, ct);
        if (error is not null) return error;
        var s = await q.FirstOrDefaultAsync(suggestions.Query().Where(x => x.Id == c.SuggestionId && x.AssetId == c.AssetId), ct);
        if (s is null) return Error.NotFound("Suggestion not found.");
        if (!s.IsPending) return Error.Conflict($"This suggestion was already {s.Status}.");
        return await decider.DecideAsync(asset!, [s], AiDecision.Reject, ct);
    }
}

/// <summary>Accepts every pending suggestion of a kind (default: tag) for the asset's current version (translations: every pending one). Tags that do not fit under the tag limit, and translations whose language already has text, stay pending.</summary>
public sealed record AcceptAllSuggestionsCommand(Guid AssetId, string? Kind) : ICommand<AiDecisionDto>;

public sealed class AcceptAllSuggestionsValidator : AbstractValidator<AcceptAllSuggestionsCommand>
{
    public AcceptAllSuggestionsValidator() =>
        RuleFor(x => x.Kind).Must(k => k is null || AiSuggestionKinds.IsKnown(k)).WithMessage($"Kind must be one of: {string.Join(", ", AiSuggestionKinds.All)}.").OverridePropertyName("kind");
}

public sealed class AcceptAllSuggestionsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AiSuggestion> suggestions, IQueryExecutor q, IAuthorizationService authz, AiSuggestionDecider decider)
    : IRequestHandler<AcceptAllSuggestionsCommand, AiDecisionDto>
{
    public async Task<Result<AiDecisionDto>> Handle(AcceptAllSuggestionsCommand c, CancellationToken ct)
    {
        var (asset, error) = await AiAssetAccess.LoadForUpdateAsync(assets, folders, q, authz, c.AssetId, ct);
        if (error is not null) return error;
        var kind = c.Kind ?? AiSuggestionKinds.Tag;
        var versionId = asset!.CurrentVersionId;
        // Translations are the asset's, whichever file is current; tags and languages are the current file's.
        var list = await q.ToListAsync(suggestions.Query().Where(x => x.AssetId == c.AssetId && x.Kind == kind && x.Status == AiSuggestionStatuses.Pending
                && (x.Kind == AiSuggestionKinds.Translation || x.VersionId == versionId))
            .OrderByDescending(x => x.Confidence), ct);
        return await decider.DecideAsync(asset, list, AiDecision.Accept, ct);
    }
}

// ------------------------------------------------------------------------------------------------------- enrich

/// <summary>Runs the AI again for the asset's current version (replaces its run; suggestions already decided stay decided). Needs <c>assets.update</c>.
/// 409 when AI is off for the tenant, the asset has no file yet, or a run is in progress.</summary>
public sealed record EnrichAssetCommand(Guid AssetId) : ICommand<AssetAiDto>;

public sealed class EnrichAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AiRun> runs, IQueryExecutor q, IAuthorizationService authz, IAiResolver resolver,
    IEventCollector events, ITenantContext tenant, IClock clock, IDispatcher dispatcher)
    : IRequestHandler<EnrichAssetCommand, AssetAiDto>
{
    public async Task<Result<AssetAiDto>> Handle(EnrichAssetCommand c, CancellationToken ct)
    {
        var (asset, error) = await AiAssetAccess.LoadForUpdateAsync(assets, folders, q, authz, c.AssetId, ct);
        if (error is not null) return error;
        if (asset!.CurrentVersionId is not { } versionId) return Error.Conflict("The asset has no file yet.");
        var ai = await resolver.ResolveAsync(ct);
        if (!ai.IsActive) return Error.Conflict(ai.DisabledReason ?? "AI is not enabled for this tenant.");

        var run = await q.FirstOrDefaultAsync(runs.Query().Where(x => x.AssetId == asset.Id && x.VersionId == versionId), ct);
        if (run is { Status: AiRunStatuses.Running }) return Error.Conflict("An enrichment is already running for this asset.");
        if (run is null) { run = AiRun.Create(tenant.TenantId, asset.Id, versionId, clock.UtcNow); runs.Add(run); }
        else run.Queue();
        events.Raise(new AssetAiRequested(asset.Id, versionId, Force: true));

        // The response is built here rather than read back: the run is not saved until the command commits.
        var current = (await dispatcher.Send(new GetAssetAiQuery(asset.Id), ct));
        return current.IsSuccess ? current.Value with { Status = "pending", Run = AiMapping.ToDto(run) } : current.Error;
    }
}
