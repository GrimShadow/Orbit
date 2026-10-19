using System.Globalization;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

// ------------------------------------------------------------------------------------------------------ unused assets

/// <summary>
/// Assets not used since a moment: the last <paramref name="Days"/> days, or since <paramref name="Before"/> (give one, or
/// neither for 30 days). Longest-idle first. The caller sees only assets they may read, exactly as in the asset listing
/// (ADR-0029). Needs <c>analytics.read</c>.
/// </summary>
public sealed record ListUnusedAssetsQuery(
    int? Days, string? Before, string? Status, string? Type, Guid? FolderId, bool IncludeSubfolders, string? Cursor, int? Limit)
    : IQuery<UnusedAssetsDto>;

public sealed class ListUnusedAssetsValidator : AbstractValidator<ListUnusedAssetsQuery>
{
    public ListUnusedAssetsValidator(IClock clock)
    {
        RuleFor(x => x).Custom((q, ctx) =>
        {
            IdleWindow.ResolveCutoff(q.Days, q.Before, clock.UtcNow, out var errors);
            foreach (var (field, messages) in errors ?? [])
                foreach (var message in messages) ctx.AddFailure(field, message);
        });
        RuleFor(x => x.Status).Must(s => IdleWindow.IsKnownStatusFilter(s!)).When(x => x.Status is not null)
            .WithMessage($"Must be 'all' or one of: {string.Join(", ", AssetStatusNames)}.");
        RuleFor(x => x.Type).Must(AssetTypes.IsKnown).When(x => x.Type is not null)
            .WithMessage($"Must be one of: {string.Join(", ", AssetTypes.All)}.");
        RuleFor(x => x.Cursor).Must(c => UnusedCursor.TryParse(c, out _)).When(x => x.Cursor is not null).WithMessage("Not a valid cursor.");
    }

    private static readonly string[] AssetStatusNames =
    [
        AssetStatuses.Draft, AssetStatuses.InReview, AssetStatuses.Approved, AssetStatuses.Published, AssetStatuses.Expired,
        AssetStatuses.Archived, AssetStatuses.Deleted, AssetStatuses.Blocked,
    ];
}

/// <summary>Keyset position in the (idle since, id) ordering: opaque to clients.</summary>
internal readonly record struct UnusedCursor(DateTimeOffset IdleSince, Guid Id)
{
    public override string ToString() => $"{IdleSince.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{Id:N}";

    public static bool TryParse(string? text, out UnusedCursor cursor)
    {
        cursor = default;
        var parts = text?.Split('.');
        if (parts is not [var ticks, var id] || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
            || t > DateTimeOffset.MaxValue.Ticks || !Guid.TryParseExact(id, "N", out var g)) return false;
        cursor = new UnusedCursor(new DateTimeOffset(t, TimeSpan.Zero), g);
        return true;
    }

    /// <summary>Postgres sorts uuids bytewise (RFC 4122 order), which is not <see cref="Guid.CompareTo(Guid)"/>.</summary>
    public static int CompareIds(Guid a, Guid b) => a.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(b.ToByteArray(bigEndian: true));

    /// <summary>Whether a row sorts strictly after this position.</summary>
    public bool Precedes(DateTimeOffset idleSince, Guid id) =>
        idleSince > IdleSince || (idleSince == IdleSince && CompareIds(id, Id) > 0);
}

public sealed class ListUnusedAssetsHandler(
    IRepository<Asset> assets, IRepository<AssetUsage> usage, IRepository<AssetVersion> versions, IRepository<Folder> folders,
    IQueryExecutor q, IAuthorizationService authz, IClock clock) : IRequestHandler<ListUnusedAssetsQuery, UnusedAssetsDto>
{
    /// <summary>The same cap as <see cref="ListAssetsHandler"/>: how many candidate rows are checked against a narrow access scope.</summary>
    private const int MaxRowsScanned = 5000;
    private const int ChunkSize = 500;

    private sealed record Row(
        Guid Id, string Title, string Type, string Status, Guid? FolderId, Guid? OwnerId, Dictionary<string, string[]> Attributes,
        DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, long UseCount, DateTimeOffset IdleSince, long? SizeBytes);

    public async Task<Result<UnusedAssetsDto>> Handle(ListUnusedAssetsQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.AnalyticsRead, ct: ct)) return Error.Forbidden();
        var now = clock.UtcNow;
        var cutoff = IdleWindow.ResolveCutoff(r.Days, r.Before, now, out _)!.Value;
        var status = r.Status ?? AssetStatuses.Published;
        var limit = Math.Clamp(r.Limit ?? 50, 1, 200);
        UnusedCursor? cursor = UnusedCursor.TryParse(r.Cursor, out var parsed) ? parsed : null;

        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return new UnusedAssetsDto([], 0, true, null, cutoff);

        var allFolders = await q.ToListAsync(folders.Query().Select(f => new { f.Id, f.Path, f.Name }), ct);
        var pathById = allFolders.ToDictionary(f => f.Id, f => f.Path);
        var nameByPath = allFolders.ToDictionary(f => f.Path, f => f.Name);
        Guid[]? folderIds = null;
        if (r.FolderId is { } fid)
            folderIds = r.IncludeSubfolders && pathById.TryGetValue(fid, out var root)
                ? allFolders.Where(f => Paths.IsSelfOrDescendant(root, f.Path)).Select(f => f.Id).ToArray() : [fid];

        var candidates = Candidates(cutoff, status, r.Type, folderIds);
        var (rows, total, exact, more) = IsUnrestricted(clauses, status)
            ? await ReadAllowedAsync(candidates, limit, cursor, ct)
            : await ReadScopedAsync(candidates, clauses, profile.UserId, pathById, limit, cursor, ct);

        var items = rows.Select(a => new UnusedAssetDto(
            a.Id, a.Title, a.Type, a.Status, a.FolderId, FolderPath(a.FolderId, pathById, nameByPath), a.CreatedAt, a.LastUsedAt,
            (int)Math.Min(a.UseCount, int.MaxValue), a.IdleSince, IdleWindow.IdleDays(a.IdleSince, now), a.SizeBytes)).ToList();
        return new UnusedAssetsDto(items, total, exact, more ? new UnusedCursor(rows[^1].IdleSince, rows[^1].Id).ToString() : null, cutoff);
    }

    /// <summary>One clause that covers every candidate makes the in-memory scope check unnecessary, so paging and the count
    /// can be done by the database (exact, whatever the library size): an unscoped grant with no attribute limits, which for a
    /// role that sees only published assets also needs the list to be about published assets.</summary>
    private static bool IsUnrestricted(IReadOnlyList<ScopeClause> clauses, string status) =>
        clauses.Any(c => c.Scope.Type == ScopeType.All && !c.OwnerOnly && c.Attributes.Count == 0
                         && (c.Statuses is null || status == AssetStatuses.Published));

    /// <summary>An asset with its usage summary (if any) and the moment it has been idle since.</summary>
    private sealed class Joined
    {
        public required Asset A { get; init; }
        public AssetUsage? U { get; init; }
        public DateTimeOffset Idle { get; init; }
    }

    /// <summary>Everything idle since before the cutoff that matches the filters; the database does this part.</summary>
    private IQueryable<Joined> Candidates(DateTimeOffset cutoff, string status, string? type, Guid[]? folderIds)
    {
        var query =
            from a in assets.Query()
            join u in usage.Query() on a.Id equals u.AssetId into used
            from u in used.DefaultIfEmpty()
            let idle = u == null ? a.CreatedAt : u.LastUsedAt
            where idle < cutoff
            select new Joined { A = a, U = u, Idle = idle };

        query = status == IdleWindow.AllStatuses
            ? query.Where(x => x.A.Status != AssetStatuses.Deleted && x.A.Status != AssetStatuses.Blocked)
            : query.Where(x => x.A.Status == status);
        if (type is not null) query = query.Where(x => x.A.Type == type);
        if (folderIds is not null) query = query.Where(x => x.A.FolderId != null && folderIds.Contains(x.A.FolderId.Value));
        return query;
    }

    private static IQueryable<Joined> Ordered(IQueryable<Joined> rows) => rows.OrderBy(x => x.Idle).ThenBy(x => x.A.Id);

    private static IQueryable<Joined> After(IQueryable<Joined> rows, UnusedCursor c) =>
        rows.Where(x => x.Idle > c.IdleSince || (x.Idle == c.IdleSince && x.A.Id.CompareTo(c.Id) > 0));

    private IQueryable<Row> ToRows(IQueryable<Joined> rows) => rows.Select(x => new Row(
        x.A.Id, x.A.Title, x.A.Type, x.A.Status, x.A.FolderId, x.A.OwnerId, x.A.Attributes, x.A.CreatedAt,
        x.U == null ? null : x.U.LastUsedAt, x.U == null ? 0 : x.U.UseCount, x.Idle,
        versions.Query().Where(v => v.Id == x.A.CurrentVersionId).Select(v => (long?)v.SizeBytes).FirstOrDefault()));

    private async Task<(List<Row> Page, int Total, bool Exact, bool More)> ReadAllowedAsync(
        IQueryable<Joined> candidates, int limit, UnusedCursor? cursor, CancellationToken ct)
    {
        var total = await q.CountAsync(candidates, ct);
        var page = await q.ToListAsync(ToRows(Ordered(cursor is { } c ? After(candidates, c) : candidates).Take(limit + 1)), ct);
        var more = page.Count > limit;
        if (more) page.RemoveAt(limit);
        return (page, total, true, more);
    }

    /// <summary>A narrower scope (a folder, a region, a role that sees only some statuses…) is checked row by row like
    /// the asset listing does, in the order the list is shown, so one pass yields the page and the count together.</summary>
    private async Task<(List<Row> Page, int Total, bool Exact, bool More)> ReadScopedAsync(
        IQueryable<Joined> candidates, IReadOnlyList<ScopeClause> clauses, Guid userId, Dictionary<Guid, string> pathById,
        int limit, UnusedCursor? cursor, CancellationToken ct)
    {
        var page = new List<Row>();
        var total = 0;
        var scanned = 0;
        var more = false;
        UnusedCursor? position = null;
        while (true)
        {
            var chunk = Ordered(position is { } p ? After(candidates, p) : candidates).Take(ChunkSize);
            var rows = await q.ToListAsync(ToRows(chunk), ct);
            foreach (var a in rows)
            {
                if (scanned == MaxRowsScanned) return (page, total, false, more);
                scanned++;
                position = new UnusedCursor(a.IdleSince, a.Id);
                var path = a.FolderId is { } f && pathById.TryGetValue(f, out var fp) ? fp : null;
                var resource = new ResourceContext
                {
                    Type = "asset",
                    Status = a.Status,
                    FolderPath = path,
                    AssetType = a.Type,
                    OwnerId = a.OwnerId,
                    Attributes = a.Attributes,
                };
                if (!clauses.Any(c => c.Matches(resource, userId))) continue;
                total++;
                if (cursor is { } after && !after.Precedes(a.IdleSince, a.Id)) continue;
                if (page.Count < limit) page.Add(a); else more = true;
            }
            if (rows.Count < ChunkSize) return (page, total, true, more);
        }
    }

    private static string? FolderPath(Guid? folderId, Dictionary<Guid, string> pathById, Dictionary<string, string> nameByPath)
    {
        if (folderId is not { } id || !pathById.TryGetValue(id, out var path)) return null;
        var labels = path.Split('.');
        return string.Join(" / ", Enumerable.Range(1, labels.Length).Select(n => string.Join('.', labels.Take(n)))
            .Select(prefix => nameByPath.GetValueOrDefault(prefix) ?? prefix[(prefix.LastIndexOf('.') + 1)..]));
    }
}

// --------------------------------------------------------------------------------------------------------- overview

/// <summary>
/// The figures behind the dashboard for the last <paramref name="Days"/> days (UTC calendar days, today included, 30 by
/// default). Usage, uploads and storage are tenant-wide; the top assets are limited to those the caller may read.
/// Needs <c>analytics.read</c>.
/// </summary>
public sealed record GetAnalyticsOverviewQuery(int? Days) : IQuery<AnalyticsOverviewDto>;

public sealed class GetAnalyticsOverviewValidator : AbstractValidator<GetAnalyticsOverviewQuery>
{
    public GetAnalyticsOverviewValidator() =>
        RuleFor(x => x.Days).InclusiveBetween(1, AnalyticsPeriods.MaxDays).When(x => x.Days is not null);
}

public sealed class GetAnalyticsOverviewHandler(
    IRepository<UsageEvent> events, IRepository<Asset> assets, IRepository<AssetVersion> versions, IRepository<Folder> folders,
    IQueryExecutor q, IAuthorizationService authz, IClock clock) : IRequestHandler<GetAnalyticsOverviewQuery, AnalyticsOverviewDto>
{
    private const int TopAssetCount = 5, TopChannelCount = 6;
    /// <summary>How many of the busiest assets are looked at to find five the caller may read.</summary>
    private const int TopAssetCandidates = 200;

    public async Task<Result<AnalyticsOverviewDto>> Handle(GetAnalyticsOverviewQuery r, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.AnalyticsRead, ct: ct)) return Error.Forbidden();
        var now = clock.UtcNow;
        var days = r.Days ?? AnalyticsPeriods.DefaultDays;
        var today = AnalyticsPeriods.StartOfDay(now);
        var start = today.AddDays(-(days - 1));
        var previousStart = start.AddDays(-days);
        var end = today.AddDays(1);

        // One grouped read covers this period and the one before it: the trend is the second against the first.
        var perDay = (await q.ToListAsync(events.Query()
            .Where(e => e.OccurredAt >= previousStart && e.OccurredAt < end)
            .GroupBy(e => new { e.OccurredAt.UtcDateTime.Year, e.OccurredAt.UtcDateTime.Month, e.OccurredAt.UtcDateTime.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.LongCount() }), ct))
            .ToDictionary(x => new DateOnly(x.Year, x.Month, x.Day), x => x.Count);
        var byDay = Enumerable.Range(0, days).Select(i => DateOnly.FromDateTime(start.UtcDateTime.AddDays(i)))
            .Select(d => new UsageDayDto(Iso(d), perDay.GetValueOrDefault(d))).ToList();
        var previousTotal = Enumerable.Range(1, days).Select(i => DateOnly.FromDateTime(start.UtcDateTime.AddDays(-i)))
            .Sum(d => perDay.GetValueOrDefault(d));

        var inPeriod = events.Query().Where(e => e.OccurredAt >= start && e.OccurredAt < end);
        var byType = (await q.ToListAsync(inPeriod.GroupBy(e => e.EventType).Select(g => new { Type = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Type), ct)).Select(x => new UsageTypeDto(x.Type, x.Count)).ToList();
        var byChannel = (await q.ToListAsync(inPeriod.GroupBy(e => e.Channel).Select(g => new { Channel = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Channel).Take(TopChannelCount), ct))
            .Select(x => new UsageChannelDto(x.Channel ?? "unspecified", x.Count)).ToList();

        return new AnalyticsOverviewDto(
            now, days,
            new UsageSummaryDto(byDay.Sum(d => d.Count), previousTotal, byDay, byType, byChannel),
            await TopAssetsAsync(inPeriod, ct), await UploadsAsync(now, ct), await StorageAsync(ct));
    }

    private async Task<List<TopAssetDto>> TopAssetsAsync(IQueryable<UsageEvent> inPeriod, CancellationToken ct)
    {
        var busiest = await q.ToListAsync(inPeriod.GroupBy(e => e.AssetId).Select(g => new { AssetId = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.AssetId).Take(TopAssetCandidates), ct);
        if (busiest.Count == 0) return [];

        var profile = await authz.GetProfileAsync(ct);
        var ids = busiest.Select(x => x.AssetId).ToList();
        var found = (await q.ToListAsync(assets.Query().Where(a => ids.Contains(a.Id)), ct)).ToDictionary(a => a.Id);
        var folderIds = found.Values.Where(a => a.FolderId != null).Select(a => a.FolderId!.Value).Distinct().ToList();
        var pathById = folderIds.Count == 0
            ? []
            : (await q.ToListAsync(folders.Query().Where(f => folderIds.Contains(f.Id)).Select(f => new { f.Id, f.Path }), ct)).ToDictionary(f => f.Id, f => f.Path);

        return busiest
            .Where(x => found.TryGetValue(x.AssetId, out var a) && a.Status is not (AssetStatuses.Deleted or AssetStatuses.Blocked)
                        && PolicyEngine.Can(profile, Permissions.AssetsRead, a.ToResource(a.FolderId is { } f && pathById.TryGetValue(f, out var p) ? p : null)))
            .Take(TopAssetCount)
            .Select(x => new TopAssetDto(x.AssetId, found[x.AssetId].Title, found[x.AssetId].Type, x.Count)).ToList();
    }

    /// <summary>Assets created per week for the last twelve weeks, the current (partial) week last; empty weeks are zero.</summary>
    private async Task<List<UploadWeekDto>> UploadsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var first = AnalyticsPeriods.StartOfWeek(now).AddDays(-7 * (AnalyticsPeriods.UploadWeeks - 1));
        var perDay = await q.ToListAsync(assets.Query()
            .Where(a => a.CreatedAt >= first && a.Status != AssetStatuses.Deleted)
            .GroupBy(a => new { a.CreatedAt.UtcDateTime.Year, a.CreatedAt.UtcDateTime.Month, a.CreatedAt.UtcDateTime.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.LongCount() }), ct);
        var perWeek = perDay.GroupBy(x => DateOnly.FromDateTime(AnalyticsPeriods.StartOfWeek(new DateTimeOffset(x.Year, x.Month, x.Day, 0, 0, 0, TimeSpan.Zero)).UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        return Enumerable.Range(0, AnalyticsPeriods.UploadWeeks).Select(i => DateOnly.FromDateTime(first.UtcDateTime.AddDays(7 * i)))
            .Select(week => new UploadWeekDto(Iso(week), perWeek.GetValueOrDefault(week))).ToList();
    }

    /// <summary>Storage is the size of each live asset's current version; older versions and renditions are not counted (ADR-0029).</summary>
    private async Task<StorageSummaryDto> StorageAsync(CancellationToken ct)
    {
        var sizes = assets.Query().Where(a => a.Status != AssetStatuses.Deleted)
            .Select(a => new { a.Type, Size = versions.Query().Where(v => v.Id == a.CurrentVersionId).Select(v => (long?)v.SizeBytes).FirstOrDefault() });
        var byType = (await q.ToListAsync(sizes.GroupBy(x => x.Type).Select(g => new { Type = g.Key, Count = g.LongCount(), Bytes = g.Sum(x => x.Size ?? 0) })
            .OrderByDescending(x => x.Bytes).ThenBy(x => x.Type), ct)).Select(x => new StorageTypeDto(x.Type, x.Count, x.Bytes)).ToList();
        return new StorageSummaryDto(byType.Sum(x => x.Bytes), byType);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
