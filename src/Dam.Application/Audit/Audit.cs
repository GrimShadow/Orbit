using System.Globalization;
using System.Reflection;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Shared.Audit;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Audit;

public static class AuditFormat
{
    public const string Csv = "csv";
    public const string Cef = "cef";
    public static bool IsKnown(string? f) => f is Csv or Cef;
}

/// <summary>The product version CEF events carry (<c>CEF:0|Orbit|DAM|&lt;version&gt;|...</c>).</summary>
public static class AppInfo
{
    public static readonly string Version =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0").Split('+')[0];
}

internal static class AuditRules
{
    public static IEnumerable<string> Problems(AuditFilter f)
    {
        if (f.From is { } from && f.To is { } to && to < from) yield return "'to' must not be before 'from'.";
        if (f.Actor is { Length: > 200 }) yield return "'actor' is too long.";
        if (f.ActionPrefix is { Length: > 100 }) yield return "'action' is too long.";
        if (f.EntityType is { Length: > 100 }) yield return "'entityType' is too long.";
        if (f.Text is { Length: > 200 }) yield return "'q' is too long.";
    }

    public static Error? Validate(AuditFilter f)
    {
        var problems = Problems(f).ToArray();
        return problems.Length == 0 ? null : Error.Validation(ContentMapping.Fail("filter", problems[0]));
    }

    /// <summary>Trims, and turns empty strings into "no filter".</summary>
    public static AuditFilter Clean(AuditFilter f) => f with
    {
        Actor = Blank(f.Actor), ActionPrefix = Blank(f.ActionPrefix), EntityType = Blank(f.EntityType), Text = Blank(f.Text),
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>The audit log, newest first, filtered and paged by a keyset cursor (the chain position of the last row seen).</summary>
public sealed record ListAuditQuery(AuditFilter Filter, string? Cursor, int? Limit) : IQuery<Page<AuditEntryDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AuditRead;
}

public sealed class ListAuditHandler(IAuditReader reader) : IRequestHandler<ListAuditQuery, Page<AuditEntryDto>>
{
    public async Task<Result<Page<AuditEntryDto>>> Handle(ListAuditQuery r, CancellationToken ct)
    {
        var filter = AuditRules.Clean(r.Filter);
        if (AuditRules.Validate(filter) is { } error) return error;
        long? before = null;
        if (!string.IsNullOrWhiteSpace(r.Cursor))
        {
            if (!long.TryParse(r.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return Error.Validation(ContentMapping.Fail("cursor", "Invalid cursor."));
            before = n;
        }
        await using var snapshot = await reader.OpenAsync(ct);
        var page = await snapshot.ListAsync(filter, before, Math.Clamp(r.Limit ?? 50, 1, 200), ct);
        return new Page<AuditEntryDto>(page.Items, page.NextBefore?.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed record VerifyAuditQuery(DateTimeOffset? From, DateTimeOffset? To) : IQuery<AuditVerificationDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AuditRead;
}

public sealed class VerifyAuditHandler(IAuditReader reader) : IRequestHandler<VerifyAuditQuery, AuditVerificationDto>
{
    public async Task<Result<AuditVerificationDto>> Handle(VerifyAuditQuery r, CancellationToken ct)
    {
        if (r.From is { } from && r.To is { } to && to < from) return Error.Validation(ContentMapping.Fail("to", "'to' must not be before 'from'."));
        await using var snapshot = await reader.OpenAsync(ct);
        return await snapshot.VerifyAsync(r.From, r.To, ct);
    }
}

public sealed record ListAuditActionsQuery : IQuery<IReadOnlyList<string>>, IAuthorizedRequest
{
    public string Permission => Permissions.AuditRead;
}

public sealed class ListAuditActionsHandler(IAuditReader reader) : IRequestHandler<ListAuditActionsQuery, IReadOnlyList<string>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(ListAuditActionsQuery r, CancellationToken ct)
    {
        await using var snapshot = await reader.OpenAsync(ct);
        return Result<IReadOnlyList<string>>.Success(await snapshot.ActionsAsync(ct));
    }
}

/// <summary>Checks permission and parameters for an export before the response starts. The file itself is streamed by <see cref="AuditExporter"/>.</summary>
public sealed record PrepareAuditExportQuery(AuditFilter Filter, string? Format) : IQuery<AuditExportRequest>, IAuthorizedRequest
{
    public string Permission => Permissions.AuditRead;
}

public sealed record AuditExportRequest(AuditFilter Filter, string Format);

public sealed class PrepareAuditExportHandler : IRequestHandler<PrepareAuditExportQuery, AuditExportRequest>
{
    public Task<Result<AuditExportRequest>> Handle(PrepareAuditExportQuery r, CancellationToken ct)
    {
        if (!AuditFormat.IsKnown(r.Format)) return Task.FromResult<Result<AuditExportRequest>>(Error.Validation(ContentMapping.Fail("format", "Use 'csv' or 'cef'.")));
        var filter = AuditRules.Clean(r.Filter);
        return Task.FromResult<Result<AuditExportRequest>>(AuditRules.Validate(filter) is { } error ? error : new AuditExportRequest(filter, r.Format!));
    }
}

/// <summary>
/// Streams an audit export without ever holding the range in memory. The file begins with comment lines (prefixed <c>#</c>) stating
/// whether the tenant's hash chain verified over the requested time range, then one line per row, then the same verdict again with what
/// was actually written - so a truncated download is recognisable by its missing end. Everything is read from one snapshot, so the
/// header and the rows agree even while new events are being recorded.
/// </summary>
public sealed class AuditExporter(IAuditReader reader, ITenantContext tenant, IClock clock)
{
    private const int FlushEvery = 200;

    public static string ContentType(string format) => format == AuditFormat.Cef ? "text/plain; charset=utf-8" : "text/csv; charset=utf-8";

    public static string FileName(string format, DateTimeOffset at) => $"orbit-audit-{at.UtcDateTime:yyyyMMdd-HHmmss}.{(format == AuditFormat.Cef ? "cef" : "csv")}";

    public async Task WriteAsync(Stream output, AuditExportRequest request, CancellationToken ct)
    {
        var filter = request.Filter;
        var cef = request.Format == AuditFormat.Cef;
        await using var snapshot = await reader.OpenAsync(ct);
        var verification = await snapshot.VerifyAsync(filter.From, filter.To, ct);
        var expected = await snapshot.CountAsync(filter, ct);
        var verdict = verification.Verified ? "VERIFIED" : "FAILED";

        await using var w = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { NewLine = "\n" };
        await w.WriteLineAsync($"# Orbit audit export ({(cef ? "CEF" : "CSV")})");
        await w.WriteLineAsync($"# tenant: {tenant.TenantId}");
        await w.WriteLineAsync($"# generated: {CsvFormatter.Iso(clock.UtcNow)}");
        await w.WriteLineAsync($"# range: from={Iso(filter.From)} to={Iso(filter.To)}");
        await w.WriteLineAsync($"# filters: {Describe(filter)}");
        await w.WriteLineAsync($"# chain-verification: {verdict}");
        await w.WriteLineAsync($"# chain-rows-checked: {verification.Checked}" + (verification.Checked > 0 ? $" (chain {verification.FirstChainNo}..{verification.LastChainNo})" : ""));
        await w.WriteLineAsync($"# chain-first-hash: {verification.FirstHash ?? "-"}");
        await w.WriteLineAsync($"# chain-last-hash: {verification.LastHash ?? "-"}");
        if (!verification.Verified)
            await w.WriteLineAsync($"# chain-broken-at: chain {verification.BrokenAtChainNo} id {verification.BrokenAtId} reason {verification.Reason}");
        await w.WriteLineAsync($"# rows-in-export: {expected}");
        if (!cef) await w.WriteLineAsync(CsvFormatter.HeaderRow());

        long written = 0;
        string? firstHash = null, lastHash = null;
        await foreach (var e in snapshot.StreamAsync(filter, ct))
        {
            await w.WriteLineAsync(cef ? CefFormatter.Format(e, tenant.TenantId, AppInfo.Version) : CsvFormatter.Row(e));
            firstHash ??= e.Hash;
            lastHash = e.Hash;
            if (++written % FlushEvery == 0) await w.FlushAsync(ct);
        }

        await w.WriteLineAsync($"# end of export: rows={written} first-hash={firstHash ?? "-"} last-hash={lastHash ?? "-"} chain-verification={verdict}");
        await w.FlushAsync(ct);
    }

    private static string Iso(DateTimeOffset? t) => t is { } v ? CsvFormatter.Iso(v) : "-";

    private static string Describe(AuditFilter f)
    {
        var parts = new List<string>();
        if (f.Actor is not null) parts.Add($"actor={f.Actor}");
        if (f.ActionPrefix is not null) parts.Add($"action={f.ActionPrefix}*");
        if (f.EntityType is not null) parts.Add($"entityType={f.EntityType}");
        if (f.EntityId is { } id) parts.Add($"entityId={id}");
        if (f.Text is not null) parts.Add($"q={f.Text}");
        // Free text could contain a line break; a header line must stay one line.
        return parts.Count == 0 ? "none" : string.Join(' ', parts).Replace('\n', ' ').Replace('\r', ' ');
    }
}
