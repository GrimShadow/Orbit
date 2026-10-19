using Dam.Application.Abstractions;
using Dam.Application.Audit;
using Dam.Application.Messaging;
using Dam.Shared.Audit;
using Dam.Shared.Identity;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>Audit log viewer, export and chain verification (audit.read). ADR-0031.</summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this RouteGroupBuilder v1)
    {
        var audit = v1.MapGroup("/audit").WithTags("Audit");

        audit.MapGet("", (IDispatcher d, CancellationToken ct, DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, string? entityType, Guid? entityId, string? q, string? cursor, int? limit) =>
                Run(d, new ListAuditQuery(new AuditFilter(from, to, actor, action, entityType, entityId, q), cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAudit")
            .WithSummary("Audit entries, newest first. Filters: from/to (time), actor (identity subject or Orbit user id), action (prefix, e.g. asset.), entityType, entityId, q (free text over action, entity and actor). limit up to 200; cursor from nextCursor")
            .Produces<Page<AuditEntryDto>>();
        audit.MapGet("/actions", (IDispatcher d, CancellationToken ct) => Run(d, new ListAuditActionsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAuditActions").WithSummary("The distinct action names recorded, for a filter picker").Produces<IReadOnlyList<string>>();
        audit.MapGet("/verify", (IDispatcher d, CancellationToken ct, DateTimeOffset? from, DateTimeOffset? to) =>
                Run(d, new VerifyAuditQuery(from, to), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("VerifyAudit")
            .WithSummary("Recomputes the hash chain over the rows in the time range (all of them when no range is given) and reports whether it is intact, or the first row that is not")
            .Produces<AuditVerificationDto>();
        audit.MapGet("/export", async (HttpContext http, IDispatcher d, AuditExporter exporter, IClock clock, CancellationToken ct,
                string? format, DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, string? entityType, Guid? entityId, string? q) =>
            {
                var prepared = await d.Send(new PrepareAuditExportQuery(new AuditFilter(from, to, actor, action, entityType, entityId, q), format ?? AuditFormat.Csv), ct);
                if (prepared.IsFailure) return prepared.Error.ToProblem();
                var request = prepared.Value;
                http.Response.ContentType = AuditExporter.ContentType(request.Format);
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{AuditExporter.FileName(request.Format, clock.UtcNow)}\"";
                http.Response.Headers.CacheControl = "no-store";
                await exporter.WriteAsync(http.Response.Body, request, ct);
                return Results.Empty;
            })
            .WithName("ExportAudit")
            .WithSummary("Streams the filtered audit log as a file (format=csv or cef). It begins and ends with # comment lines stating whether the hash chain verified over the time range, the row count and the first and last hashes")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain");
    }
}
