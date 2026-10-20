using System.Globalization;
using System.Net;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Application.Notifications;
using Dam.Domain.Analytics;
using Dam.Domain.Authorization;
using Dam.Domain.Identity;
using Dam.Shared.Content;

namespace Dam.Application.Assets;

/// <param name="Sent">Recipients who now have this run's email (including ones who already had it from an earlier attempt).</param>
/// <param name="Skipped">Recipients left out on purpose: no longer an active user, no email address, or no analytics access.</param>
/// <param name="Failed">Recipients whose email could not be handed to the mail server; they are tried again.</param>
/// <param name="Error">Why the failures happened (the first one), or why nothing could be sent.</param>
/// <param name="Note">A remark for the schedule's status when everything that could be sent was (for example that some recipients were skipped).</param>
public sealed record MailOutcome(int Sent, int Skipped, int Failed, string? Error, string? Note);

/// <summary>
/// Emails a scheduled report (ADR-0037 decision 8) to the recipients of a schedule through the ordinary email port. Each recipient gets the report as THEY may see it (their own
/// access profile decides which assets are named), as an HTML table of the first 50 rows with a link to the report in the console. The caller is told after every successful
/// send, so it can record that recipient before the next one is attempted: a retry after a failure, or after a crash, then mails only those who have not been mailed.
/// </summary>
public sealed class ReportMailer(
    ReportService reports, UserProfiles profiles, IRepository<User> users, IRepository<Tenant> tenants, IEmailSender email, NotificationSettings settings,
    IQueryExecutor q)
{
    public const string NotConfigured = "Email is not configured on this server (set DAM_SMTP_HOST).";
    public const int EmailRows = 50;

    public async Task<MailOutcome> SendAsync(ReportSchedule schedule, IReadOnlyCollection<Guid> alreadySent, Func<Guid, CancellationToken, Task> sent, CancellationToken ct)
    {
        if (!email.IsEnabled) return new MailOutcome(0, 0, 0, NotConfigured, null);

        var wanted = schedule.RecipientUserIds.Where(id => !alreadySent.Contains(id)).ToList();
        var done = schedule.RecipientUserIds.Count - wanted.Count;
        if (wanted.Count == 0) return new MailOutcome(done, 0, 0, null, null);

        var people = (await q.ToListAsync(users.Query().Where(u => wanted.Contains(u.Id)), ct)).ToDictionary(u => u.Id);
        var access = await profiles.LoadAsync(wanted, ct);
        var tenantName = (await q.FirstOrDefaultAsync(tenants.Query().Select(t => t.Name), ct)) ?? "";

        int ok = done, skipped = 0, failed = 0;
        string? firstError = null;
        var names = ReportCatalogue.Find(schedule.Report)!.NamesAssets;
        ReportDto? shared = null;   // a report that names no asset reads the same for everyone
        foreach (var id in wanted)
        {
            if (!people.TryGetValue(id, out var person) || person.Status != UserStatus.Active || person.Email.Length == 0 || !access.TryGetValue(id, out var profile)
                || !PolicyEngine.Can(profile, Permissions.AnalyticsRead))
            {
                skipped++;
                await sent(id, ct);   // nothing will ever change that for this slot: it must not keep the slot open
                continue;
            }

            try
            {
                var report = shared;
                if (report is null)
                {
                    var run = await reports.RunAsync(schedule.Report, schedule.Days, profile, ct);
                    if (run.IsFailure) { failed++; firstError ??= run.Error.Message; continue; }
                    report = run.Value;
                    if (!names) shared = report;
                }
                var message = ReportEmail.Render(report, tenantName, schedule.Days, EmailTemplates.Absolute(settings.ConsolePublicUrl, $"/insights/reports/{report.Name}?days={schedule.Days}"));
                await email.SendAsync(new EmailMessage(person.Email, person.DisplayName, message.Subject, message.Html, message.Text), ct);
                await sent(id, ct);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                firstError ??= Brief(ex);
            }
        }

        var note = skipped > 0 && failed == 0
            ? $"Not sent to {skipped.ToString(CultureInfo.InvariantCulture)} recipient(s): no longer an active user, no email address, or no analytics access."
            : null;
        return new MailOutcome(ok, skipped, failed, firstError, note);
    }

    private static string Brief(Exception ex)
    {
        var text = $"Could not send: {ex.Message}".Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 300 ? text[..300] : text;
    }
}

/// <summary>The email of a scheduled report: a title, the period, a compact table of the first rows and a button to open the full report. Everything that came from data is escaped.</summary>
public static class ReportEmail
{
    public sealed record Rendered(string Subject, string Html, string Text);

    public static Rendered Render(ReportDto report, string tenantName, int days, string? linkUrl)
    {
        var tenant = Clean(tenantName);
        var title = report.Title;
        var subject = (tenant.Length == 0 ? "" : $"[{tenant}] ") + $"{title} ({Period(report, days)})";
        if (subject.Length > 200) subject = subject[..199] + "…";

        var shown = report.Rows.Take(ReportMailer.EmailRows).ToList();
        var more = report.Rows.Count - shown.Count;
        var summary = report.Rows.Count == 0 ? "Nothing to report for this period."
            : more > 0 ? $"Showing the first {shown.Count.ToString(CultureInfo.InvariantCulture)} of {report.Rows.Count.ToString(CultureInfo.InvariantCulture)} rows."
            : $"{report.Rows.Count.ToString(CultureInfo.InvariantCulture)} row(s).";

        var text = new StringBuilder();
        text.Append(title).Append(" - ").Append(Period(report, days)).Append("\n\n");
        text.Append(string.Join(" | ", report.Columns.Select(c => c.Label))).Append('\n');
        foreach (var row in shown) text.Append(string.Join(" | ", report.Columns.Select(c => Display(c, row.GetValueOrDefault(c.Key), report, row)))).Append('\n');
        text.Append('\n').Append(summary).Append('\n');
        if (linkUrl is not null) text.Append("\nOpen in Orbit: ").Append(linkUrl).Append('\n');

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>").Append(H(title)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f4f4f2;font-family:-apple-system,'Segoe UI',Helvetica,Arial,sans-serif;color:#1c2321;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"background:#f4f4f2;padding:24px 0;\"><tr><td align=\"center\">")
            .Append("<table role=\"presentation\" width=\"720\" cellspacing=\"0\" cellpadding=\"0\" style=\"max-width:720px;width:100%;background:#ffffff;border-radius:8px;overflow:hidden;\">");
        if (tenant.Length > 0)
            html.Append("<tr><td style=\"background:#2f4a3e;color:#ffffff;padding:16px 24px;font-size:14px;font-weight:600;\">").Append(H(tenant)).Append("</td></tr>");
        html.Append("<tr><td style=\"padding:24px;\"><h1 style=\"margin:0 0 4px 0;font-size:20px;line-height:1.3;\">").Append(H(title)).Append("</h1>")
            .Append("<p style=\"margin:0 0 16px 0;font-size:13px;color:#6b7370;\">").Append(H(Period(report, days))).Append("</p>");
        if (report.Rows.Count > 0)
        {
            html.Append("<table role=\"table\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"border-collapse:collapse;font-size:13px;\"><thead><tr>");
            foreach (var c in report.Columns)
                html.Append("<th align=\"").Append(IsNumeric(c) ? "right" : "left").Append("\" style=\"padding:6px 8px;border-bottom:2px solid #d9dedb;white-space:nowrap;\">").Append(H(c.Label)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            foreach (var row in shown)
            {
                html.Append("<tr>");
                foreach (var c in report.Columns)
                    html.Append("<td align=\"").Append(IsNumeric(c) ? "right" : "left").Append("\" style=\"padding:6px 8px;border-bottom:1px solid #eceeed;\">").Append(H(Display(c, row.GetValueOrDefault(c.Key), report, row))).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
        }
        html.Append("<p style=\"margin:16px 0 4px 0;font-size:13px;color:#6b7370;\">").Append(H(summary)).Append("</p>");
        if (linkUrl is not null)
            html.Append("<p style=\"margin:20px 0 4px 0;\"><a href=\"").Append(H(linkUrl)).Append("\" style=\"display:inline-block;background:#2f4a3e;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:6px;font-size:14px;font-weight:600;\">Open in Orbit</a></p>");
        html.Append("</td></tr><tr><td style=\"padding:12px 24px 20px 24px;font-size:12px;color:#6b7370;\">You receive this because you are a recipient of a scheduled report in Orbit.</td></tr></table></td></tr></table></body></html>");

        return new Rendered(subject, html.ToString(), text.ToString());
    }

    private static string Period(ReportDto report, int days) =>
        report.Name == ReportCatalogue.Storage ? $"as of {report.Period.To}"
        : report.Name == ReportCatalogue.UnusedAssets ? $"not used for {days.ToString(CultureInfo.InvariantCulture)} days"
        : $"{report.Period.From} to {report.Period.To}";

    private static bool IsNumeric(ReportColumnDto c) => c.Type is ReportColumnTypes.Count or ReportColumnTypes.Number or ReportColumnTypes.Percent or ReportColumnTypes.Bytes or ReportColumnTypes.Hours;

    /// <summary>
    /// A cell as a person reads it (never the CSV's raw figures): 102.3 MB, 83.3%, 5.5 h, 12,345, a date. The KPI report mixes units in one column, so its
    /// "value" and "previous" cells are formatted by the row's own unit; the unit cell is blank when the figure already carries it.
    /// </summary>
    internal static string Display(ReportColumnDto column, object? value, ReportDto? report = null, IReadOnlyDictionary<string, object?>? row = null)
    {
        if (value is null) return "";
        var unit = row is not null && report is not null && report.Columns.Any(c => c.Key == UnitKey) ? Convert.ToString(row.GetValueOrDefault(UnitKey), CultureInfo.InvariantCulture) : null;
        if (column.Key == UnitKey && column.Type == ReportColumnTypes.Text && value is string u && ReportFigures.UnitIsInFigure(u)) return "";
        if (unit is not null && column.Type == ReportColumnTypes.Number && column.Key != UnitKey && ReportFigures.ByUnit(unit, value) is { } byUnit) return byUnit;
        return column.Type switch
        {
            ReportColumnTypes.Percent => ReportFigures.Ratio(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            ReportColumnTypes.Number => ReportFigures.Decimal(Convert.ToDouble(value, CultureInfo.InvariantCulture), "#,##0.##"),
            ReportColumnTypes.Hours => ReportFigures.Hours(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            ReportColumnTypes.Count => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture),
            ReportColumnTypes.Bytes => ReportFigures.Bytes(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }

    private const string UnitKey = "unit";

    public static string Bytes(long bytes) => ReportFigures.Bytes(bytes);

    private static string Clean(string s) => string.Join(' ', s.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string H(string s) => WebUtility.HtmlEncode(s);
}

/// <summary>
/// Figures for people (the email), in the wording of the console's report tables: sizes with one decimal from KB up, hours as minutes, hours or days,
/// counts with thousands separators. Culture-free (invariant), because the email has no locale. The CSV does NOT use these: machines get raw numbers.
/// </summary>
internal static class ReportFigures
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var v = bytes / 1024.0;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v.ToString("0.0", CultureInfo.InvariantCulture)} {units[i]}";
    }

    /// <summary>Decimal hours as minutes, hours or days: 0.75 is "45 min", 5.5 is "5.5 h", 72 is "3 d".</summary>
    public static string Hours(double hours)
    {
        if (hours < 1) return $"{Math.Max(1, Math.Round(hours * 60, MidpointRounding.AwayFromZero)).ToString("0", CultureInfo.InvariantCulture)} min";
        if (hours < 48) return $"{hours.ToString("#,##0.#", CultureInfo.InvariantCulture)} h";
        return $"{(hours / 24).ToString("#,##0.#", CultureInfo.InvariantCulture)} d";
    }

    /// <summary>A fraction (0.943) as "94.3%". A near miss never reads as perfect: 0.9996 is "99.9%".</summary>
    public static string Ratio(double ratio)
    {
        var v = ratio;
        if (v < 1 && Math.Round(v * 1000, MidpointRounding.AwayFromZero) / 1000 >= 1) v = 0.999;
        return (Math.Max(0, v) * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    public static string Decimal(double d, string format) => d.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Units whose figure is formatted so that it already says what it is (so the unit cell would repeat it).</summary>
    public static bool UnitIsInFigure(string unit) => unit is "bytes" or "hours" or "%";

    /// <summary>The KPI report's "value"/"previous" cells, by the row's unit; null when the unit is not one this knows (the plain number is used).</summary>
    public static string? ByUnit(string unit, object value)
    {
        var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return unit switch
        {
            "bytes" => Bytes((long)Math.Round(d, MidpointRounding.AwayFromZero)),
            "hours" => Hours(d),
            "%" => d.ToString("#,##0.#", CultureInfo.InvariantCulture) + "%",   // already percentage points
            _ => d == Math.Floor(d) ? d.ToString("N0", CultureInfo.InvariantCulture) : d.ToString("#,##0.0", CultureInfo.InvariantCulture),
        };
    }
}
