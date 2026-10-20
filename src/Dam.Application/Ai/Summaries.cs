using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Ai;

namespace Dam.Application.Ai;

/// <summary>
/// Writes (or rewrites) the summary of the asset's current version from the text already extracted from it (ADR-0036); 202, and the summary appears in
/// <c>GET /assets/{id}/ai</c> when the worker has made it. Needs <c>assets.update</c>. 409 when AI is off, summaries are switched off, the provider cannot summarise,
/// the asset has no file, or no text has been extracted from it yet.
/// </summary>
public sealed record SummariseAssetCommand(Guid AssetId) : ICommand<AssetAiDto>;

public sealed class SummariseAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AssetExtract> extracts, IQueryExecutor q, IAuthorizationService authz, IAiResolver resolver,
    IEventCollector events, IDispatcher dispatcher) : IRequestHandler<SummariseAssetCommand, AssetAiDto>
{
    public async Task<Result<AssetAiDto>> Handle(SummariseAssetCommand c, CancellationToken ct)
    {
        var (asset, error) = await AiAssetAccess.LoadForUpdateAsync(assets, folders, q, authz, c.AssetId, ct);
        if (error is not null) return error;
        if (asset!.CurrentVersionId is not { } versionId) return Error.Conflict("The asset has no file yet.");

        var ai = await resolver.ResolveAsync(ct);
        if (!ai.IsActive) return Error.Conflict(ai.DisabledReason ?? "AI is not enabled for this tenant.");
        if (!ai.Features.Summaries) return Error.Conflict("Summaries are switched off in this tenant's AI settings.");
        if (!ai.Provider.Capabilities.Summarise) return Error.Conflict($"The '{ai.ProviderKind}' AI provider cannot summarise.");

        var extract = await q.FirstOrDefaultAsync(extracts.Query().Where(x => x.AssetId == asset.Id && x.VersionId == versionId), ct);
        if (string.IsNullOrWhiteSpace(extract?.OcrText) && string.IsNullOrWhiteSpace(extract?.TranscriptText))
            return Error.Conflict("There is no extracted text to summarise yet: run the AI on this asset first, and make sure OCR or transcripts are on.");

        events.Raise(new AssetSummariseRequested(asset.Id, versionId));
        return await dispatcher.Send(new GetAssetAiQuery(asset.Id), ct);
    }
}
