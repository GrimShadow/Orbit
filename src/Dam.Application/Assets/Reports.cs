using System.Globalization;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Application.Audit;
using Dam.Application.Content;
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
/// Runs the reports of the report API and of scheduled emails (ADR-0037 decision 6) for a given reader. The reader is an explicit <see cref="AccessProfile"/>, not the
/// ambient user, because a scheduled report is built for each recipient with that recipient's own access: rows that name assets (top and unused assets) hold only
/// assets that reader may see, exactly as the lists in the console do. Aggregates (KPIs, storage, workflow SLA) name no asset and are tenant-wide.
/// </summary>
public sealed class ReportService(
    KpiService kpis, IAnalyticsStore store, TopAssetsReader topAssets, UnusedAssetsReader unused, IRepository<Folder> folders, IQueryExecutor q, IClock clock)
{
    public const int TopAssetRows = 100, UnusedRows = 5000;
    private const int UnusedPage = 200;

    /// <summary>Why a report request is not acceptable, or null: an unknown name is a not-found, a bad period is a validation error on <c>days</c>.</summary>
    public static Error? Check(string name, int? days)
    {
        if (ReportCatalogue.Find(name) is not { } def) return Error.NotFound($"There is no report '{name}'.");
        return days is { } d && (d < 1 || d > def.MaxDays)
            ? Error.Validation(ContentMapping.Fail("days", $"Must be between 1 and {def.MaxDays} for this report."))
            : null;
    }

    public async Task<Result<ReportDto>> RunAsync(string name, int? days, AccessProfile profile, CancellationToken ct)
    {
        if (Check(name, days) is { } problem) return problem;
        var def = ReportCatalogue.Find(name)!;
        var n = days ?? def.DefaultDays;
        var now = clock.UtcNow;
        var today = UsagePartitions.DayOf(now);
        var period = KpiPeriod.For(today, n);
        var periodDto = new AnalyticsPeriodDto(KpiPeriod.Iso(period.From), KpiPeriod.Iso(period.To), n);

        switch (def.Name)
        {
            case ReportCatalogue.Kpis:
                return Build(def, now, periodDto, KpiColumns, KpiRows(await kpis.ComputeAsync(n, ct)));

            case ReportCatalogue.TopAssets:
            {
                var top = await topAssets.ReadAsync(period.From, today.AddDays(1), today, TopAssetRows, 1000, profile, ct);
                var rows = top.Select((t, i) => Row(("rank", i + 1), ("title", t.Title), ("type", t.Type), ("uses", t.Count), ("assetId", t.AssetId.ToString()))).ToList();
                return Build(def, now, periodDto, TopAssetColumns, rows, truncated: top.Count >= TopAssetRows);
            }

            case ReportCatalogue.UnusedAssets:
                return await UnusedAsync(def, now, periodDto, n, profile, ct);

            case ReportCatalogue.Storage:
                return Build(def, now, new AnalyticsPeriodDto(KpiPeriod.Iso(today), KpiPeriod.Iso(today), 1), StorageColumns, await StorageRowsAsync(ct));

            default: // workflow-sla
            {
                var steps = await store.SlaByStepAsync(period.Start, period.End, now, ct);
                var rows = steps.Select(s => Row(("workflow", s.Workflow), ("step", s.Step), ("decided", s.Decided), ("onTime", s.OnTime),
                    ("attainment", KpiMath.Ratio(s.OnTime, s.Decided)), ("breached", s.Breached), ("overdueNow", s.OverdueOpen))).ToList();
                return Build(def, now, periodDto, SlaColumns, rows);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------- rows

    private static readonly ReportColumnDto[] KpiColumns =
    [
        new("kpi", "KPI", ReportColumnTypes.Text), new("metric", "Measure", ReportColumnTypes.Text), new("value", "This period", ReportColumnTypes.Number),
        new("unit", "Unit", ReportColumnTypes.Text), new("previous", "Previous period", ReportColumnTypes.Number),
    ];

    private static readonly ReportColumnDto[] TopAssetColumns =
    [
        new("rank", "#", ReportColumnTypes.Count), new("title", "Asset", ReportColumnTypes.Text), new("type", "Type", ReportColumnTypes.Text),
        new("uses", "Uses", ReportColumnTypes.Count), new("assetId", "Asset id", ReportColumnTypes.Text),
    ];

    private static readonly ReportColumnDto[] UnusedColumns =
    [
        new("title", "Asset", ReportColumnTypes.Text), new("type", "Type", ReportColumnTypes.Text), new("status", "Status", ReportColumnTypes.Text),
        new("folder", "Folder", ReportColumnTypes.Text), new("lastUsedAt", "Last used", ReportColumnTypes.DateTime), new("idleDays", "Idle days", ReportColumnTypes.Count),
        new("sizeBytes", "Size", ReportColumnTypes.Bytes), new("createdAt", "Created", ReportColumnTypes.DateTime), new("assetId", "Asset id", ReportColumnTypes.Text),
    ];

    private static readonly ReportColumnDto[] StorageColumns =
    [
        new("group", "Grouped by", ReportColumnTypes.Text), new("name", "Name", ReportColumnTypes.Text), new("assets", "Assets", ReportColumnTypes.Count),
        new("bytes", "Size", ReportColumnTypes.Bytes), new("subtreeAssets", "Assets incl. subfolders", ReportColumnTypes.Count),
        new("subtreeBytes", "Size incl. subfolders", ReportColumnTypes.Bytes),
    ];

    private static readonly ReportColumnDto[] SlaColumns =
    [
        new("workflow", "Workflow", ReportColumnTypes.Text), new("step", "Step", ReportColumnTypes.Text), new("decided", "Decided", ReportColumnTypes.Count),
        new("onTime", "On time", ReportColumnTypes.Count), new("attainment", "Attainment", ReportColumnTypes.Percent), new("breached", "Breached", ReportColumnTypes.Count),
        new("overdueNow", "Overdue now", ReportColumnTypes.Count),
    ];

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);

    private static Result<ReportDto> Build(ReportDefinition def, DateTimeOffset now, AnalyticsPeriodDto period, IReadOnlyList<ReportColumnDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows, bool truncated = false) => new ReportDto(def.Name, def.Title, now, period, columns, rows, truncated);

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static List<Dictionary<string, object?>> KpiRows(KpiDto k)
    {
        static object? Pct(double? ratio) => ratio is null ? null : Math.Round(ratio.Value * 100, 1, MidpointRounding.AwayFromZero);
        var rows = new List<Dictionary<string, object?>>();
        void Add(string kpi, string metric, object? value, string unit, object? previous) =>
            rows.Add(Row(("kpi", kpi), ("metric", metric), ("value", value), ("unit", unit), ("previous", previous)));

        var p = k.PublishingEffort;
        Add("Publishing effort", "Assets first published", p.PublishedCount, "assets", p.Previous.PublishedCount);
        Add("Publishing effort", "Median time to publish", p.MedianHours, "hours", p.Previous.MedianHours);
        Add("Publishing effort", "90th percentile time to publish", p.P90Hours, "hours", p.Previous.P90Hours);
        Add("Publishing effort", "Published first time through approval", Pct(p.FirstPassRate), "%", Pct(p.Previous.FirstPassRate));

        var d = k.Duplicates;
        Add("Duplicate reduction", "Uploads", d.Uploads, "uploads", d.Previous.Uploads);
        Add("Duplicate reduction", "Duplicates prevented", d.DuplicatesPrevented, "uploads", d.Previous.DuplicatesPrevented);
        Add("Duplicate reduction", "Duplicate rate", Pct(d.Rate), "%", Pct(d.Previous.Rate));
        Add("Duplicate reduction", "Storage not used", d.BytesNotStored, "bytes", d.Previous.BytesNotStored);
        Add("Duplicate reduction", "Duplicate assets in the library", d.DuplicateAssetsInLibrary, "assets", d.Previous.DuplicateAssetsInLibrary);

        var r = k.Reuse;
        Add("Reuse", "Assets used", r.UsedAssets, "assets", r.Previous.UsedAssets);
        Add("Reuse", "Assets used more than once", r.ReusedAssets, "assets", r.Previous.ReusedAssets);
        Add("Reuse", "Reuse rate", Pct(r.Rate), "%", Pct(r.Previous.Rate));
        Add("Reuse", "Average uses per used asset", r.AvgUsesPerUsedAsset, "uses", r.Previous.AvgUsesPerUsedAsset);
        Add("Reuse", "Assets used on several channels", r.MultiChannelAssets, "assets", r.Previous.MultiChannelAssets);

        var s = k.WorkflowSla;
        Add("Workflow SLA", "Tasks decided", s.Decided, "tasks", s.Previous.Decided);
        Add("Workflow SLA", "Decided on time", s.OnTime, "tasks", s.Previous.OnTime);
        Add("Workflow SLA", "SLA attainment", Pct(s.Attainment), "%", Pct(s.Previous.Attainment));
        Add("Workflow SLA", "SLA breaches", s.Breached, "tasks", s.Previous.Breached);
        Add("Workflow SLA", "Overdue now", s.OverdueOpen, "tasks", null);
        return rows;
    }

    private async Task<Result<ReportDto>> UnusedAsync(ReportDefinition def, DateTimeOffset now, AnalyticsPeriodDto period, int days, AccessProfile profile, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        string? cursor = null;
        var truncated = false;
        while (true)
        {
            var page = await unused.ReadAsync(profile, new ListUnusedAssetsQuery(days, null, null, null, null, true, cursor, UnusedPage), ct);
            if (page.IsFailure) return page.Error;
            foreach (var a in page.Value.Items)
            {
                if (rows.Count == UnusedRows) { truncated = true; break; }
                rows.Add(Row(("title", a.Title), ("type", a.Type), ("status", a.Status), ("folder", a.FolderPath), ("lastUsedAt", a.LastUsedAt is { } l ? Iso(l) : null),
                    ("idleDays", a.IdleDays), ("sizeBytes", a.SizeBytes), ("createdAt", Iso(a.CreatedAt)), ("assetId", a.Id.ToString())));
            }
            if (truncated || page.Value.NextCursor is null) break;
            cursor = page.Value.NextCursor;
        }
        return Build(def, now, period, UnusedColumns, rows, truncated);
    }

    private async Task<List<Dictionary<string, object?>>> StorageRowsAsync(CancellationToken ct)
    {
        var usage = await store.StorageAsync(ct);
        var all = await q.ToListAsync(folders.Query().Select(f => new { f.Id, f.Path, f.Name }), ct);
        var nameByPath = all.ToDictionary(f => f.Path, f => f.Name);
        var pathById = all.ToDictionary(f => f.Id, f => f.Path);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var f in all.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var direct = usage.Where(u => u.FolderId == f.Id).ToList();
            var subtree = usage.Where(u => u.FolderId is { } id && pathById.TryGetValue(id, out var p) && Paths.IsSelfOrDescendant(f.Path, p)).ToList();
            if (subtree.Count == 0) continue;
            rows.Add(Row(("group", "folder"), ("name", FolderLabels.Path(f.Id, pathById, nameByPath)), ("assets", direct.Sum(u => u.Assets)), ("bytes", direct.Sum(u => u.Bytes)),
                ("subtreeAssets", subtree.Sum(u => u.Assets)), ("subtreeBytes", subtree.Sum(u => u.Bytes))));
        }
        if (usage.Where(u => u.FolderId is null).ToList() is { Count: > 0 } loose)
            rows.Add(Row(("group", "folder"), ("name", "(no folder)"), ("assets", loose.Sum(u => u.Assets)), ("bytes", loose.Sum(u => u.Bytes)),
                ("subtreeAssets", loose.Sum(u => u.Assets)), ("subtreeBytes", loose.Sum(u => u.Bytes))));

        foreach (var t in usage.GroupBy(u => u.Type).Select(g => new { Type = g.Key, Assets = g.Sum(u => u.Assets), Bytes = g.Sum(u => u.Bytes) })
                     .OrderByDescending(t => t.Bytes).ThenBy(t => t.Type, StringComparer.Ordinal))
            rows.Add(Row(("group", "type"), ("name", t.Type), ("assets", t.Assets), ("bytes", t.Bytes), ("subtreeAssets", null), ("subtreeBytes", null)));
        return rows;
    }
}

/// <summary>A report as CSV for a spreadsheet: a UTF-8 byte-order mark (Excel needs it to read non-ASCII text), the column labels, then one line per row. Text cells a
/// spreadsheet would read as a formula get a leading apostrophe (the same neutralisation as the audit export); numbers are written plainly, percentages as "83.3%".</summary>
public static class ReportCsv
{
    public static byte[] Render(ReportDto report)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', report.Columns.Select(c => CsvFormatter.Cell(c.Label)))).Append("\r\n");
        foreach (var row in report.Rows)
            sb.Append(string.Join(',', report.Columns.Select(c => Cell(c, row.GetValueOrDefault(c.Key))))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string FileName(ReportDto report, DateTimeOffset at) => $"orbit-{report.Name}-{at.UtcDateTime:yyyyMMdd}.csv";

    internal static string Cell(ReportColumnDto column, object? value)
    {
        if (value is null) return "";
        return column.Type switch
        {
            ReportColumnTypes.Percent => Number(Convert.ToDouble(value, CultureInfo.InvariantCulture) * 100, "0.#") + "%",
            ReportColumnTypes.Number or ReportColumnTypes.Hours => Number(Convert.ToDouble(value, CultureInfo.InvariantCulture), "0.##"),
            ReportColumnTypes.Count or ReportColumnTypes.Bytes => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => CsvFormatter.Cell(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    private static string Number(double d, string format) => d.ToString(format, CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------------------------------------- queries

/// <summary>The reports a reader may run (any holder of <c>analytics.read</c>).</summary>
public sealed record ListReportsQuery : IQuery<IReadOnlyList<ReportInfoDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsRead;
}

public sealed class ListReportsHandler : IRequestHandler<ListReportsQuery, IReadOnlyList<ReportInfoDto>>
{
    public Task<Result<IReadOnlyList<ReportInfoDto>>> Handle(ListReportsQuery r, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<ReportInfoDto>>.Success(
            ReportCatalogue.All.Select(d => new ReportInfoDto(d.Name, d.Title, d.Description, d.NamesAssets, d.DefaultDays, d.MaxDays)).ToList()));
}

public static class ReportFormats
{
    public const string Json = "json", Csv = "csv";
    public static bool IsKnown(string? f) => f is null || f.Equals(Json, StringComparison.OrdinalIgnoreCase) || f.Equals(Csv, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Runs one report for the caller. Rows that name assets contain only assets the caller may read. Needs <c>analytics.read</c>.</summary>
public sealed record RunReportQuery(string Name, int? Days, string? Format) : IQuery<ReportDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsRead;
}

public sealed class RunReportValidator : AbstractValidator<RunReportQuery>
{
    public RunReportValidator() =>
        RuleFor(x => x.Format).Must(ReportFormats.IsKnown).WithMessage("Use 'json' or 'csv'.");
}

public sealed class RunReportHandler(ReportService reports, IAuthorizationService authz) : IRequestHandler<RunReportQuery, ReportDto>
{
    public async Task<Result<ReportDto>> Handle(RunReportQuery r, CancellationToken ct) =>
        await reports.RunAsync(r.Name, r.Days, await authz.GetProfileAsync(ct), ct);
}
