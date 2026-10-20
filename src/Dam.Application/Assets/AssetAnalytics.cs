using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Analytics;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

/// <summary>
/// How one asset has been used (ADR-0037 decision 7): uses in the last <paramref name="Days"/> UTC days against the period before, a zero-filled daily series, uses by event
/// type and channel, first and last use ever, and the 20 most recent uses. Needs <c>analytics.read</c> and read access to the asset; an asset the caller may not read is not found.
/// </summary>
public sealed record GetAssetAnalyticsQuery(Guid AssetId, int? Days) : IQuery<AssetAnalyticsDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsRead;
}

public sealed class GetAssetAnalyticsValidator : AbstractValidator<GetAssetAnalyticsQuery>
{
    public GetAssetAnalyticsValidator() => RuleFor(x => x.Days).InclusiveBetween(1, KpiPeriod.MaxDays).When(x => x.Days is not null);
}

public sealed class GetAssetAnalyticsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IAnalyticsStore store, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<GetAssetAnalyticsQuery, AssetAnalyticsDto>
{
    private const int RecentCount = 20, ChannelCount = 20;

    public async Task<Result<AssetAnalyticsDto>> Handle(GetAssetAnalyticsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");

        var days = r.Days ?? KpiPeriod.DefaultDays;
        var period = KpiPeriod.For(UsagePartitions.DayOf(clock.UtcNow), days);
        var today = period.To;

        var cells = await store.UsageCellsAsync(period.PreviousFrom, today.AddDays(1), today, asset.Id, ct);
        var perDay = cells.GroupBy(c => c.Day).ToDictionary(g => g.Key, g => g.Sum(c => c.Uses));
        var byDay = Enumerable.Range(0, days).Select(i => period.From.AddDays(i))
            .Select(d => new UsageDayDto(KpiPeriod.Iso(d), perDay.GetValueOrDefault(d))).ToList();
        var previousTotal = cells.Where(c => c.Day < period.From).Sum(c => c.Uses);

        var inPeriod = cells.Where(c => c.Day >= period.From).ToList();
        var byType = inPeriod.GroupBy(c => c.EventType).Select(g => new UsageTypeDto(g.Key, g.Sum(c => c.Uses)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.EventType, StringComparer.Ordinal).ToList();
        var byChannel = inPeriod.GroupBy(c => c.Channel).Select(g => (Channel: g.Key, Count: g.Sum(c => c.Uses)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Channel.Length == 0 ? 1 : 0).ThenBy(x => x.Channel, StringComparer.Ordinal).Take(ChannelCount)
            .Select(x => new UsageChannelDto(x.Channel.Length == 0 ? "unspecified" : x.Channel, x.Count)).ToList();

        var totals = await store.AssetTotalsAsync(asset.Id, ct);
        var recent = (await store.RecentUsesAsync(asset.Id, RecentCount, ct)).Select(u => new RecentUsageDto(u.Id, u.ActorId, u.EventType, u.Channel, u.OccurredAt)).ToList();

        return new AssetAnalyticsDto(
            asset.Id, asset.Title, asset.Type, new AnalyticsPeriodDto(KpiPeriod.Iso(period.From), KpiPeriod.Iso(period.To), days),
            byDay.Sum(d => d.Count), previousTotal, totals.UseCount, totals.FirstUsedAt, totals.LastUsedAt, byDay, byType, byChannel, recent);
    }
}
