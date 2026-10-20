using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Search;

namespace Dam.Infrastructure.Consumers;

/// <summary>
/// Keeps a tenant's search index in step with its assets and their static-collection membership (spec Steps 1.7/1.7c).
/// Every asset routing key carries only ids (see the events in <c>Dam.Domain.Assets.Assets.cs</c>), so this always
/// re-reads the asset rather than trusting the event's own fields - the one exception is a status change to "deleted",
/// which removes the document without a read since a deleted asset needs nothing else in it. A collection's own
/// `collection.assets_changed` (<c>Dam.Domain.Content.Collections.cs</c>) carries the actually-affected asset ids
/// directly, so no separate lookup is needed to know which documents to refresh.
/// </summary>
public sealed class IndexConsumer(ISearchIndexWriter writer) : EventConsumer
{
    public override string Name => "search-index";

    public override IReadOnlyList<string> RoutingKeys =>
    [
        "asset.created", "asset.metadata.updated", "asset.status.changed", "asset.version.added", "asset.legal_hold.changed",
        Dam.Domain.Licensing.AssetRightsChanged.EventName, Dam.Domain.Ai.AssetAiEnriched.EventName, "collection.assets_changed",
    ];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(message.Payload);

        if (message.Name == "collection.assets_changed")
        {
            var affected = payload.RootElement.GetProperty("AddedAssetIds").EnumerateArray().Select(x => x.GetGuid())
                .Concat(payload.RootElement.GetProperty("RemovedAssetIds").EnumerateArray().Select(x => x.GetGuid()))
                .Distinct();
            foreach (var assetId in affected) await writer.IndexAsync(assetId, ct);
            return;
        }

        var id = payload.RootElement.GetProperty("AssetId").GetGuid();
        if (message.Name == "asset.status.changed" && payload.RootElement.GetProperty("To").GetString() == AssetStatuses.Deleted)
        {
            await writer.DeleteAsync(id, ct);
            return;
        }
        await writer.IndexAsync(id, ct);
    }
}
