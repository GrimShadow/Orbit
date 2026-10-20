using Dam.Application.Ai;
using Dam.Application.Messaging;
using Dam.Shared.Ai;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <param name="AcknowledgeOffSite">Required (true) the first time AI is enabled with a provider that sends pictures and audio outside this installation.</param>
public sealed record AiSettingsRequest(bool Enabled, string? Provider, AiFeaturesDto? Features, string? OcrLanguage, bool AcknowledgeOffSite = false);

/// <summary>AI enrichment: what was found for an asset, accepting and rejecting suggestions, translation and summaries, and the administrator's controls (ADR-0035, ADR-0036).</summary>
public static class AiEndpoints
{
    public static void MapAiEndpoints(this RouteGroupBuilder v1)
    {
        // ---- per asset
        v1.MapGet("/assets/{id:guid}/ai", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetAssetAiQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("GetAssetAi")
            .WithSummary("What the AI found for the asset's current version: status, the run and its steps, suggestions (tags and language for the current version; every translation of the asset), text read from the picture or pages (clipped at 20,000 characters), the transcript, the summary and whether the semantic-search vectors exist. Needs assets.read")
            .Produces<AssetAiDto>();
        v1.MapPost("/assets/{id:guid}/ai/enrich", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new EnrichAssetCommand(id), v => Task.FromResult(Results.Accepted($"/api/v1/assets/{id}/ai", v)), ct))
            .WithTags("AI").WithName("EnrichAsset")
            .WithSummary("Runs the AI again for the asset's current version (202; poll GET /assets/{id}/ai). Decided suggestions stay decided. 409 when AI is off for the tenant, the asset has no file, or a run is in progress. Needs assets.update")
            .Produces<AssetAiDto>(StatusCodes.Status202Accepted);
        v1.MapPost("/assets/{id:guid}/ai/suggestions/{suggestionId:guid}/accept", (Guid id, Guid suggestionId, AcceptSuggestionInput? body, IDispatcher d, CancellationToken ct) =>
                Run(d, new AcceptSuggestionCommand(id, suggestionId, body?.Text, body?.Replace ?? false), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("AcceptAiSuggestion")
            .WithSummary("Accepts a suggestion. A tag is added to the asset's tags through the normal update path (audited, re-indexed); a language is written to the asset's 'language' metadata field when its schema has one that accepts it, otherwise only the acceptance is recorded (see notes). A translation is merged into its multilingual field under its language, leaving the other languages untouched, through the normal metadata validation, audit and re-index; the optional body carries the reviewer's edit (text) and replace (allow overwriting text the language already has: otherwise 409). Needs assets.update")
            .Produces<AiDecisionDto>();
        v1.MapPost("/assets/{id:guid}/ai/suggestions/{suggestionId:guid}/reject", (Guid id, Guid suggestionId, IDispatcher d, CancellationToken ct) =>
                Run(d, new RejectSuggestionCommand(id, suggestionId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("RejectAiSuggestion")
            .WithSummary("Rejects a suggestion; it is not proposed again. Needs assets.update")
            .Produces<AiDecisionDto>();
        v1.MapPost("/assets/{id:guid}/ai/suggestions/accept-all", (Guid id, string? kind, IDispatcher d, CancellationToken ct) =>
                Run(d, new AcceptAllSuggestionsCommand(id, kind), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("AcceptAllAiSuggestions")
            .WithSummary("Accepts every pending suggestion of a kind (query parameter kind: tag, the default, language or translation) for the current version. Tags beyond the asset's 50-tag limit, and translations whose language already has text, stay pending (see notes). Needs assets.update")
            .Produces<AiDecisionDto>();
        v1.MapPost("/assets/{id:guid}/ai/summarise", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new SummariseAssetCommand(id), v => Task.FromResult(Results.Accepted($"/api/v1/assets/{id}/ai", v)), ct))
            .WithTags("AI").WithName("SummariseAsset")
            .WithSummary("Writes (or rewrites) the summary of the asset's current version from the text already extracted from it (202; poll GET /assets/{id}/ai for the summary). 409 when AI is off, summaries are switched off, the provider cannot summarise, the asset has no file, or no text has been extracted from it yet. Needs assets.update")
            .Produces<AssetAiDto>(StatusCodes.Status202Accepted);

        // ---- translation (ADR-0036)
        v1.MapPost("/assets/{id:guid}/translate", (Guid id, TranslateInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new TranslateAssetCommand(id, r), v => Task.FromResult(Results.Accepted($"/api/v1/assets/{id}/translations", v)), ct))
            .WithTags("AI").WithName("TranslateAsset")
            .WithSummary("Asks for translations of multilingual (ml_text) fields into the target languages (202). The worker translates around the tenant's glossary terms and stores each result as a pending suggestion (GET /assets/{id}/ai) for a person to accept; the job (GET /assets/{id}/translations) records what happened to every field and language, and the requester is notified when it is done. At most 20 fields and 10 languages; sourceLanguage defaults per field; languages that already have text are skipped unless overwrite is true. 409 when AI is off or the provider cannot translate; 422 for fields that are not ml_text fields, languages the tenant does not use, limits, or nothing to translate from. Needs assets.update")
            .Produces<TranslationJobDto>(StatusCodes.Status202Accepted);
        v1.MapGet("/assets/{id:guid}/translations", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new ListTranslationsQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("ListAssetTranslations")
            .WithSummary("The latest ten translation jobs of the asset, newest first, with the outcome (ok, skipped with a reason, failed with a reason) of every field and language. The translations themselves are the suggestions in GET /assets/{id}/ai. Needs assets.read")
            .Produces<AssetTranslationsDto>();

        // ---- administration
        var ai = v1.MapGroup("/ai").WithTags("AI");
        ai.MapGet("/capabilities", (IDispatcher d, CancellationToken ct) => Run(d, new GetAiCapabilitiesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAiCapabilities")
            .WithSummary("What AI can do for the caller's tenant right now: enabled, semanticSearch (also requires a ready vector index), enrichment, textVectors, imageVectors, translate, summarise, provider kind and whether data goes off-site. For any signed-in user; no URLs, keys or models")
            .Produces<AiCapabilitiesDto>();
        ai.MapGet("/reviews", (string? kind, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                Run(d, new ListAiReviewsQuery(kind, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAiReviews")
            .WithSummary("The review queue: assets with AI suggestions waiting for a person (kind: translation, the only one), oldest first, with how many wait and in which fields and languages. Only assets the caller can edit (assets.update through role and access rules) are listed. For any signed-in user")
            .Produces<Dam.Shared.Identity.Page<AiReviewItemDto>>();
        ai.MapGet("/reviews/summary", (IDispatcher d, CancellationToken ct) => Run(d, new GetAiReviewSummaryQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAiReviewSummary")
            .WithSummary("How many assets have translations waiting for the caller to review (only assets the caller can edit count): the number for the Tasks badge. For any signed-in user")
            .Produces<AiReviewSummaryDto>();
        ai.MapGet("/settings", (IDispatcher d, CancellationToken ct) => Run(d, new GetAiSettingsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAiSettings")
            .WithSummary("The tenant's AI settings (off until saved), the deployment's default provider and which providers are configured. Needs ai.manage")
            .Produces<AiSettingsDto>();
        ai.MapPut("/settings", (AiSettingsRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateAiSettingsCommand(new AiSettingsInput(r.Enabled, r.Provider, r.Features, r.OcrLanguage), r.AcknowledgeOffSite), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateAiSettings")
            .WithSummary("Replaces the tenant's AI settings: on/off, provider (empty = deployment default), features (tags, ocr, transcribe, embeddings, summaries) and OCR language. Enabling needs a configured provider (422 on provider) and, for one that sends data off-site, acknowledgeOffSite true (422). Needs ai.manage")
            .Produces<AiSettingsDto>();
        ai.MapGet("/status", (IDispatcher d, CancellationToken ct) => Run(d, new GetAiStatusQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAiStatus")
            .WithSummary("Provider health (probed live), models, capabilities, counts of assets enriched, waiting, failed and not started, the queue depth, and whether the search index needs a reindex for the configured vector sizes. Needs ai.manage")
            .Produces<AiStatusDto>();
        ai.MapPost("/backfill", (AiBackfillInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new AiBackfillCommand(r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("AiBackfill")
            .WithSummary("Queues AI for existing assets (all, or by type and folder), at most 5,000 per call (default 500); the worker processes them at its own pace. Repeat to continue. 409 when AI is off. Needs ai.manage")
            .Produces<AiBackfillResultDto>();
        ai.MapPost("/test", (IDispatcher d, CancellationToken ct) => Run(d, new AiTestQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("AiTest")
            .WithSummary("Proves the provider works: health, then an embedding of a tiny built-in picture and of a phrase, with timings and vector sizes. Sends nothing of the tenant's, so it works before AI is enabled. Needs ai.manage")
            .Produces<AiTestResultDto>();
        ai.MapPost("/reindex", (IDispatcher d, CancellationToken ct) => Run(d, new RequestReindexCommand(), v => Task.FromResult(Results.Accepted("/api/v1/ai/status", v)), ct))
            .WithName("AiReindex")
            .WithSummary("Asks the worker to rebuild the tenant's search index in the background (search keeps working; needed after the vector sizes changed). Needs ai.manage")
            .Produces<AiReindexDto>(StatusCodes.Status202Accepted);
    }
}
