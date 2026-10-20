using Dam.Application.Ai;
using Dam.Application.Messaging;
using Dam.Shared.Ai;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <param name="AcknowledgeOffSite">Required (true) the first time AI is enabled with a provider that sends pictures and audio outside this installation.</param>
public sealed record AiSettingsRequest(bool Enabled, string? Provider, AiFeaturesDto? Features, string? OcrLanguage, bool AcknowledgeOffSite = false);

/// <summary>AI enrichment: what was found for an asset, accepting and rejecting suggestions, and the administrator's controls (ADR-0035).</summary>
public static class AiEndpoints
{
    public static void MapAiEndpoints(this RouteGroupBuilder v1)
    {
        // ---- per asset
        v1.MapGet("/assets/{id:guid}/ai", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetAssetAiQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("GetAssetAi")
            .WithSummary("What the AI found for the asset's current version: status, the run and its steps, suggestions (tags, language), text read from the picture or pages (clipped at 20,000 characters), the transcript, and whether the semantic-search vectors exist. Needs assets.read")
            .Produces<AssetAiDto>();
        v1.MapPost("/assets/{id:guid}/ai/enrich", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new EnrichAssetCommand(id), v => Task.FromResult(Results.Accepted($"/api/v1/assets/{id}/ai", v)), ct))
            .WithTags("AI").WithName("EnrichAsset")
            .WithSummary("Runs the AI again for the asset's current version (202; poll GET /assets/{id}/ai). Decided suggestions stay decided. 409 when AI is off for the tenant, the asset has no file, or a run is in progress. Needs assets.update")
            .Produces<AssetAiDto>(StatusCodes.Status202Accepted);
        v1.MapPost("/assets/{id:guid}/ai/suggestions/{suggestionId:guid}/accept", (Guid id, Guid suggestionId, IDispatcher d, CancellationToken ct) =>
                Run(d, new AcceptSuggestionCommand(id, suggestionId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("AcceptAiSuggestion")
            .WithSummary("Accepts a suggestion. A tag is added to the asset's tags through the normal update path (audited, re-indexed); a language is written to the asset's 'language' metadata field when its schema has one that accepts it, otherwise only the acceptance is recorded (see notes). Needs assets.update")
            .Produces<AiDecisionDto>();
        v1.MapPost("/assets/{id:guid}/ai/suggestions/{suggestionId:guid}/reject", (Guid id, Guid suggestionId, IDispatcher d, CancellationToken ct) =>
                Run(d, new RejectSuggestionCommand(id, suggestionId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("RejectAiSuggestion")
            .WithSummary("Rejects a suggestion; it is not proposed again. Needs assets.update")
            .Produces<AiDecisionDto>();
        v1.MapPost("/assets/{id:guid}/ai/suggestions/accept-all", (Guid id, string? kind, IDispatcher d, CancellationToken ct) =>
                Run(d, new AcceptAllSuggestionsCommand(id, kind), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("AI").WithName("AcceptAllAiSuggestions")
            .WithSummary("Accepts every pending suggestion of a kind (query parameter kind: tag, the default, or language) for the current version. Tags beyond the asset's 50-tag limit stay pending. Needs assets.update")
            .Produces<AiDecisionDto>();

        // ---- administration
        var ai = v1.MapGroup("/ai").WithTags("AI");
        ai.MapGet("/capabilities", (IDispatcher d, CancellationToken ct) => Run(d, new GetAiCapabilitiesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAiCapabilities")
            .WithSummary("What AI can do for the caller's tenant right now: enabled, semanticSearch (also requires a ready vector index), enrichment, textVectors, imageVectors, provider kind and whether data goes off-site. For any signed-in user; no URLs, keys or models")
            .Produces<AiCapabilitiesDto>();
        ai.MapGet("/settings", (IDispatcher d, CancellationToken ct) => Run(d, new GetAiSettingsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAiSettings")
            .WithSummary("The tenant's AI settings (off until saved), the deployment's default provider and which providers are configured. Needs ai.manage")
            .Produces<AiSettingsDto>();
        ai.MapPut("/settings", (AiSettingsRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateAiSettingsCommand(new AiSettingsInput(r.Enabled, r.Provider, r.Features, r.OcrLanguage), r.AcknowledgeOffSite), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateAiSettings")
            .WithSummary("Replaces the tenant's AI settings: on/off, provider (empty = deployment default), features and OCR language. Enabling needs a configured provider (422 on provider) and, for one that sends data off-site, acknowledgeOffSite true (422). Needs ai.manage")
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
