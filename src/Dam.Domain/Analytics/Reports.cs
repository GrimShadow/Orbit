namespace Dam.Domain.Analytics;

/// <param name="NamesAssets">Rows name individual assets, so the caller's read scope applies to them.</param>
/// <param name="MaxDays">Longest period. For <c>unused-assets</c> the period is the idle window.</param>
public sealed record ReportDefinition(string Name, string Title, string Description, bool NamesAssets, int DefaultDays, int MaxDays);

/// <summary>The reports the report API can run and a schedule can email (ADR-0037).</summary>
public static class ReportCatalogue
{
    public const string Kpis = "kpis";
    public const string TopAssets = "top-assets";
    public const string UnusedAssets = "unused-assets";
    public const string Storage = "storage";
    public const string WorkflowSla = "workflow-sla";

    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(Kpis, "Key performance indicators", "Publishing effort, duplicate reduction, reuse and workflow SLA attainment for the period, with the previous period beside it.", false, 30, 365),
        new(TopAssets, "Most used assets", "The assets used most in the period, among those the reader may see.", true, 30, 365),
        new(UnusedAssets, "Unused assets", "Assets not used for the given number of days (never-used assets are judged from their creation date), longest idle first, among those the reader may see.", true, 30, 3650),
        new(Storage, "Storage", "What the library holds: by folder with the totals of each folder's subtree, and by asset type. The size of each asset's current version; the period does not apply.", false, 30, 365),
        new(WorkflowSla, "Workflow SLA", "Approval tasks decided in the period against their due time, per workflow step, with breaches and tasks overdue now.", false, 30, 365),
    ];

    public static ReportDefinition? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Column types a report declares; a client formats a cell by its column's type.</summary>
public static class ReportColumnTypes
{
    public const string Text = "text";
    public const string Count = "count";
    public const string Number = "number";
    /// <summary>A ratio from 0 to 1.</summary>
    public const string Percent = "percent";
    public const string Bytes = "bytes";
    public const string Hours = "hours";
    public const string DateTime = "datetime";
}
