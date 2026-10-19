using System.Text.Json;

namespace Dam.Application.Abstractions;

/// <summary>One audit-log entry about an asset, as the activity feed needs it. The payload is for building a short summary only and is never returned.</summary>
/// <param name="Actor">What the log recorded: the identity-provider subject, or system / worker.</param>
/// <param name="ActorUserId">The Orbit user that subject belongs to, when it is a known person.</param>
public sealed record ActivityAuditRow(
    Guid Id, DateTimeOffset At, string Actor, Guid? ActorUserId, string? ActorName, string Action, Guid? EntityId, JsonElement? Payload);

/// <summary>
/// Reads the audit entries that belong to one asset (its own events, the decisions on its workflow tasks, the resolutions of its comment
/// threads) from the tenant's audit log, newest first. Only the actions asked for are returned. This is the curated source of the activity
/// feed; the audit log itself stays behind <c>audit.read</c>.
/// </summary>
public interface IAssetActivityReader
{
    /// <param name="before">When set, only entries strictly older than this (time, then id) are returned.</param>
    Task<IReadOnlyList<ActivityAuditRow>> ReadAsync(Guid assetId, IReadOnlyCollection<string> actions, (DateTimeOffset At, Guid Id)? before, int take, CancellationToken ct);
}
