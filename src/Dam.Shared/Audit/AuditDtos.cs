using System.Text.Json;

namespace Dam.Shared.Audit;

/// <param name="ChainNo">Position in the tenant's hash chain (1, 2, 3, ...); the paging cursor is built from it.</param>
/// <param name="ActorId">The actor as recorded: the identity-provider subject of the user, or <c>system</c> / <c>worker</c> for automated changes.</param>
/// <param name="ActorUserId">The Orbit user that subject belongs to, when it can be resolved.</param>
/// <param name="ActorName">Display name (or email) of that user; for automated actors, the actor itself.</param>
/// <param name="Action">The event name, e.g. <c>asset.status.changed</c>.</param>
/// <param name="Before">State before the change, when the event recorded one.</param>
/// <param name="After">The event payload.</param>
/// <param name="Hash">SHA-256 over this row and <paramref name="PrevHash"/>.</param>
/// <param name="PrevHash">The previous row's hash (empty for the first row).</param>
public sealed record AuditEntryDto(
    Guid Id, long ChainNo, DateTimeOffset OccurredAt, string ActorId, Guid? ActorUserId, string ActorName, string Action, string? EntityType,
    Guid? EntityId, JsonElement? Before, JsonElement? After, string? Ip, string Hash, string PrevHash);

/// <param name="Verified">True when every row in the range hashes correctly and links to its predecessor with no gap.</param>
/// <param name="Checked">Rows examined (the whole chain between the first and last row in the range).</param>
/// <param name="FirstId">Id of the first row examined.</param>
/// <param name="LastId">Id of the last row examined.</param>
/// <param name="BrokenAtId">Id of the first row that failed, when not verified.</param>
/// <param name="BrokenAtChainNo">Chain position of that row.</param>
/// <param name="Reason">hash_mismatch (row changed), prev_hash_mismatch (row links to a different predecessor) or gap (a row is missing).</param>
public sealed record AuditVerificationDto(
    bool Verified, long Checked, Guid? FirstId, Guid? LastId, long? FirstChainNo, long? LastChainNo, string? FirstHash, string? LastHash,
    Guid? BrokenAtId, long? BrokenAtChainNo, string? Reason, DateTimeOffset? From, DateTimeOffset? To);
