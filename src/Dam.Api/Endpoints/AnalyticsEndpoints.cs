using Dam.Application.Abstractions;
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

        analytics.MapGet("/kpis", (IDispatcher d, CancellationToken ct, int? days) =>
                Run(d, new GetKpisQuery(days), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetKpis")
            .WithSummary("The four key performance indicators (publishing effort, duplicate reduction, reuse, workflow SLA attainment) for the last N UTC days (days, default 30, 1-365), each with the equal period before it. A ratio is null when there is nothing to measure. Needs analytics.read")
            .Produces<KpiDto>();

        analytics.MapGet("/assets/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct, int? days) =>
                Run(d, new GetAssetAnalyticsQuery(id, days), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAssetAnalytics")
            .WithSummary("How one asset has been used: totals against the previous period, first and last use, a zero-filled daily series, uses by event type and channel, and the 20 most recent uses (days, default 30, 1-365). Needs analytics.read and read access to the asset; otherwise 404")
            .Produces<AssetAnalyticsDto>();

        analytics.MapGet("/reports", (IDispatcher d, CancellationToken ct) =>
                Run(d, new ListReportsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListReports")
            .WithSummary("The reports that can be run or scheduled: kpis, top-assets, unused-assets, storage, workflow-sla. Needs analytics.read")
            .Produces<IReadOnlyList<ReportInfoDto>>();

        analytics.MapGet("/reports/{name}", (string name, IDispatcher d, IClock clock, CancellationToken ct, int? days, string? format) =>
                Run(d, new RunReportQuery(name, days, format), report => Task.FromResult(
                    string.Equals(format, ReportFormats.Csv, StringComparison.OrdinalIgnoreCase)
                        ? Results.File(ReportCsv.Render(report), "text/csv; charset=utf-8", ReportCsv.FileName(report, clock.UtcNow))
                        : Results.Ok(report)), ct))
            .WithName("RunReport")
            .WithSummary("Runs a report as JSON (columns and rows) or, with format=csv, as a spreadsheet-ready CSV (content type text/csv instead of JSON; UTF-8 with a byte-order mark; text cells that could be read as formulas get a leading apostrophe). days is the period (default and maximum depend on the report; for unused-assets it is the idle window). Rows that name assets contain only assets the caller may read. Needs analytics.read")
            .Produces<ReportDto>();

        var schedules = analytics.MapGroup("/report-schedules");

        schedules.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListReportSchedulesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListReportSchedules")
            .WithSummary("The emailed (MIS) report schedules of the tenant, with when each last went out, why it did not if it failed, and when it is next due. Needs analytics.manage")
            .Produces<IReadOnlyList<ReportScheduleDto>>();
        schedules.MapPost("", (ReportScheduleInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateReportScheduleCommand(r), v => Task.FromResult(Results.Created($"/api/v1/analytics/report-schedules/{v.Id}", v)), ct))
            .WithName("CreateReportSchedule")
            .WithSummary("Schedules a report to be emailed daily, weekly (dayOfWeek 1-7) or monthly (dayOfMonth 1-31, the last day in a shorter month) at an hour UTC. Recipients are active users of the tenant (ids), not addresses; at most 50 schedules. Audited. Needs analytics.manage")
            .Produces<ReportScheduleDto>(StatusCodes.Status201Created);
        schedules.MapPut("/{id:guid}", (Guid id, ReportScheduleInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateReportScheduleCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateReportSchedule")
            .WithSummary("Replaces a schedule's settings; its next run is recalculated from now. Audited. Needs analytics.manage")
            .Produces<ReportScheduleDto>();
        schedules.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteReportScheduleCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteReportSchedule").WithSummary("Deletes a schedule. Audited. Needs analytics.manage");
        schedules.MapPost("/{id:guid}/send-now", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new SendReportScheduleNowCommand(id), v => Task.FromResult(Results.Accepted(value: v)), ct))
            .WithName("SendReportScheduleNow")
            .WithSummary("Emails the report to the schedule's recipients now, each as they may see it, without changing when the schedule next runs. 202 with how many were sent, skipped and failed; 409 when email is not configured on the server or nothing could be sent. Needs analytics.manage")
            .Produces<SendReportNowResultDto>(StatusCodes.Status202Accepted);
    }
}
