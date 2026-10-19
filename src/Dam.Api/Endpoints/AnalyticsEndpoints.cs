using Dam.Application.Assets;
using Dam.Application.Messaging;
using Dam.Shared.Content;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

public sealed record RecordUsageEventsRequest(IReadOnlyList<UsageEventInput> Events);

/// <summary>Usage tracking and the analytics built on it (ADR-0029).</summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/usage/events", (RecordUsageEventsRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new RecordUsageEventsCommand(r.Events ?? []), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Usage").WithName("RecordUsageEvents")
            .WithSummary("Record uses of assets (1-500 per call): console downloads, proof of play, portals, apps. Resending a clientEventId is a duplicate, not an error; assets that do not exist or cannot be read are rejected")
            .Produces<RecordUsageResultDto>();

        var analytics = v1.MapGroup("/analytics").WithTags("Analytics");

        analytics.MapGet("/unused-assets", (IDispatcher d, CancellationToken ct, int? days, string? before, string? status, string? type, Guid? folderId,
                bool? includeSubfolders, int? limit, string? cursor) =>
                Run(d, new ListUnusedAssetsQuery(days, before, status, type, folderId, includeSubfolders ?? true, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListUnusedAssets")
            .WithSummary("Assets not used in the last N days (days, default 30) or since a date (before); never-used assets are judged from their creation date. Longest idle first. Needs analytics.read")
            .Produces<UnusedAssetsDto>();

        analytics.MapGet("/overview", (IDispatcher d, CancellationToken ct, int? days) =>
                Run(d, new GetAnalyticsOverviewQuery(days), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAnalyticsOverview")
            .WithSummary("Usage, top assets, uploads per week and storage for the dashboard. Needs analytics.read")
            .Produces<AnalyticsOverviewDto>();
    }
}
