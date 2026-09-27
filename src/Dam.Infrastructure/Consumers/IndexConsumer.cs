using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Search;

namespace Dam.Infrastructure.Consumers;

/// <summary>
/// Keeps a tenant's search index in step with its assets (spec Step 1.7). Every routing key below carries only ids
/// (see the events in <c>Dam.Domain.Assets.Assets.cs</c>), so this always re-reads the asset rather than trusting the
/// event's own fields - the one exception is a status change to "deleted", which removes the document without a read
/// since a deleted asset needs nothing else in it.
/// </summary>
public sealed class IndexConsumer(ISearchIndexWriter writer) : EventConsumer
{
    public override string Name => "search-index";

    public override IReadOnlyList<string> RoutingKeys =>
    [
        "asset.created", "asset.metadata.updated", "asset.status.changed", "asset.version.added", "asset.legal_hold.changed",
    ];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(message.Payload);
        var assetId = payload.RootElement.GetProperty("AssetId").GetGuid();

        if (message.Name == "asset.status.changed" && payload.RootElement.GetProperty("To").GetString() == AssetStatuses.Deleted)
        {
            await writer.DeleteAsync(assetId, ct);
            return;
        }
        await writer.IndexAsync(assetId, ct);
    }
}
