using System.Text.Json;
using Dam.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Persistence;

/// <summary>Reads one asset's audit entries through the ordinary tenant-scoped context, so row-level security applies like everywhere else.</summary>
public sealed class EfAssetActivityReader(DamDbContext db) : IAssetActivityReader
{
    public async Task<IReadOnlyList<ActivityAuditRow>> ReadAsync(
        Guid assetId, IReadOnlyCollection<string> actions, (DateTimeOffset At, Guid Id)? before, int take, CancellationToken ct)
    {
        var taskIds = db.WorkflowTasks.Where(t => t.AssetId == assetId).Select(t => (Guid?)t.Id);
        var commentIds = db.Comments.Where(c => c.AssetId == assetId).Select(c => (Guid?)c.Id);
        var entries = db.AuditLog.AsNoTracking().Where(a => actions.Contains(a.Action)
            && ((a.EntityType == "asset" && a.EntityId == assetId)
                || (a.EntityType == "workflow_task" && taskIds.Contains(a.EntityId))
                || (a.EntityType == "comment" && commentIds.Contains(a.EntityId))));
        // Creating an asset also records its first metadata, in the same instant; that is not an edit worth showing.
        entries = entries.Where(a => !(a.Action == "asset.metadata.updated" && db.AuditLog.Any(c => c.Action == "asset.created" && c.EntityId == a.EntityId && c.Ts == a.Ts)));
        if (before is { } b) entries = entries.Where(a => a.Ts < b.At || (a.Ts == b.At && a.Id.CompareTo(b.Id) < 0));

        var rows = await (from a in entries.OrderByDescending(a => a.Ts).ThenByDescending(a => a.Id).Take(take)
                          join u in db.Users.AsNoTracking() on a.Actor equals u.ExternalSubject into people
                          from u in people.DefaultIfEmpty()
                          orderby a.Ts descending, a.Id descending
                          select new { a.Id, a.Ts, a.Actor, UserId = (Guid?)u!.Id, u.DisplayName, u.Email, a.Action, a.EntityId, a.After }).ToListAsync(ct);

        return rows.Select(r => new ActivityAuditRow(
            r.Id, r.Ts, r.Actor, r.UserId, string.IsNullOrWhiteSpace(r.DisplayName) ? r.Email : r.DisplayName, r.Action, r.EntityId,
            Payload(r.After))).ToList();
    }

    private static JsonElement? Payload(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
