using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Analytics;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

/// <summary>
/// Computes the four BRD key performance indicators (ADR-0037 decision 5) for the last N UTC days and the equal period before. The definitions live in the ADR; the
/// queries are in <see cref="IAnalyticsStore"/>. A KPI with nothing to measure reports null for its ratio, never zero.
/// </summary>
public sealed class KpiService(IAnalyticsStore store, IClock clock)
{
    public async Task<KpiDto> ComputeAsync(int days, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var period = KpiPeriod.For(UsagePartitions.DayOf(now), days);
        var today = period.To;

        var published = await store.PublishingAsync(period.Start, period.End, ct);
        var publishedBefore = await store.PublishingAsync(period.PreviousStart, period.Start, ct);

        var uploads = await store.DuplicatesAsync(period.Start, period.End, ct);
        var uploadsBefore = await store.DuplicatesAsync(period.PreviousStart, period.Start, ct);
        var dupAssets = await store.DuplicateAssetsAsync(null, ct);
        var dupAssetsBefore = await store.DuplicateAssetsAsync(period.Start, ct);

        var reuse = await store.ReuseAsync(period.From, period.To.AddDays(1), today, ct);
        var reuseBefore = await store.ReuseAsync(period.PreviousFrom, period.From, today, ct);

        var sla = await store.SlaAsync(period.Start, period.End, ct);
        var slaBefore = await store.SlaAsync(period.PreviousStart, period.Start, ct);
        var overdue = await store.OverdueOpenTasksAsync(now, ct);

        return new KpiDto(
            new AnalyticsPeriodDto(KpiPeriod.Iso(period.From), KpiPeriod.Iso(period.To), days),
            new AnalyticsPreviousPeriodDto(KpiPeriod.Iso(period.PreviousFrom), KpiPeriod.Iso(period.PreviousTo)),
            now,
            PublishingEffort(published, publishedBefore),
            Duplicates(uploads, dupAssets, uploadsBefore, dupAssetsBefore),
            Reuse(reuse, reuseBefore),
            WorkflowSla(sla, overdue, slaBefore));
    }

    private static PublishingEffortMetricsDto PublishingMetrics(PublishingAggregate a) =>
        new(a.Published, KpiMath.Hours(a.MedianHours), KpiMath.Hours(a.P90Hours), KpiMath.Ratio(a.FirstPass, a.Published));

    private static PublishingEffortKpiDto PublishingEffort(PublishingAggregate now, PublishingAggregate before)
    {
        var m = PublishingMetrics(now);
        return new(m.PublishedCount, m.MedianHours, m.P90Hours, m.FirstPassRate, PublishingMetrics(before));
    }

    private static DuplicateMetricsDto DuplicateMetrics(DuplicateAggregate a, long libraryDuplicates) =>
        new(a.Uploads, a.Prevented, KpiMath.Ratio(a.Prevented, a.Uploads), a.BytesNotStored, libraryDuplicates);

    private static DuplicateKpiDto Duplicates(DuplicateAggregate now, long libraryNow, DuplicateAggregate before, long libraryBefore)
    {
        var m = DuplicateMetrics(now, libraryNow);
        return new(m.Uploads, m.DuplicatesPrevented, m.Rate, m.BytesNotStored, m.DuplicateAssetsInLibrary, DuplicateMetrics(before, libraryBefore));
    }

    private static ReuseMetricsDto ReuseMetrics(ReuseAggregate a) =>
        new(a.UsedAssets, a.ReusedAssets, KpiMath.Ratio(a.ReusedAssets, a.UsedAssets), KpiMath.Average(a.TotalUses, a.UsedAssets), a.MultiChannelAssets);

    private static ReuseKpiDto Reuse(ReuseAggregate now, ReuseAggregate before)
    {
        var m = ReuseMetrics(now);
        return new(m.UsedAssets, m.ReusedAssets, m.Rate, m.AvgUsesPerUsedAsset, m.MultiChannelAssets, ReuseMetrics(before));
    }

    private static WorkflowSlaMetricsDto SlaMetrics(SlaAggregate a, long? overdue) =>
        new(a.Decided, a.OnTime, KpiMath.Ratio(a.OnTime, a.Decided), a.Breached, overdue);

    private static WorkflowSlaKpiDto WorkflowSla(SlaAggregate now, long overdue, SlaAggregate before)
    {
        var m = SlaMetrics(now, overdue);
        return new(m.Decided, m.OnTime, m.Attainment, m.Breached, m.OverdueOpen, SlaMetrics(before, null));
    }
}

/// <summary>The four BRD KPIs for the last <paramref name="Days"/> UTC days (30 by default, 1 to 365). Tenant-wide aggregates; needs <c>analytics.read</c>.</summary>
public sealed record GetKpisQuery(int? Days) : IQuery<KpiDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsRead;
}

public sealed class GetKpisValidator : AbstractValidator<GetKpisQuery>
{
    public GetKpisValidator() => RuleFor(x => x.Days).InclusiveBetween(1, KpiPeriod.MaxDays).When(x => x.Days is not null);
}

public sealed class GetKpisHandler(KpiService kpis) : IRequestHandler<GetKpisQuery, KpiDto>
{
    public async Task<Result<KpiDto>> Handle(GetKpisQuery r, CancellationToken ct) => await kpis.ComputeAsync(r.Days ?? KpiPeriod.DefaultDays, ct);
}
