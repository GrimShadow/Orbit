using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Dam.Infrastructure.Persistence;

/// <summary>
/// Appends a batch of usage events and folds it into <c>asset_usage</c> with one statement, inside the request's
/// transaction. <c>ON CONFLICT DO NOTHING</c> on (tenant, client event id) makes a re-sent event a no-op even when two
/// requests race, and the counters are incremented in SQL, so concurrent batches for the same asset do not lose updates.
/// </summary>
public sealed class EfUsageStore(DamDbContext db) : IUsageStore
{
    private const string Sql = """
        WITH input AS (
            SELECT * FROM unnest(@ids, @assets, @types, @channels, @renditions, @actors, @occurred, @clients, @contexts)
                AS t(id, asset_id, event_type, channel, rendition, actor_id, occurred_at, client_event_id, context)
        ), ins AS (
            INSERT INTO usage_events (id, tenant_id, asset_id, event_type, channel, rendition, actor_id, occurred_at, client_event_id, context, created_at)
            SELECT id, @tenant, asset_id, event_type, channel, rendition, actor_id, occurred_at, client_event_id, context::jsonb, @now FROM input
            ON CONFLICT (tenant_id, client_event_id) WHERE client_event_id IS NOT NULL DO NOTHING
            RETURNING asset_id, occurred_at
        ), summary AS (
            SELECT asset_id, max(occurred_at) AS last_at, min(occurred_at) AS first_at, count(*) AS n FROM ins GROUP BY asset_id
        ), upsert AS (
            INSERT INTO asset_usage (asset_id, tenant_id, last_used_at, first_used_at, use_count)
            SELECT asset_id, @tenant, last_at, first_at, n FROM summary
            ON CONFLICT (asset_id) DO UPDATE SET
                last_used_at = GREATEST(asset_usage.last_used_at, EXCLUDED.last_used_at),
                first_used_at = LEAST(asset_usage.first_used_at, EXCLUDED.first_used_at),
                use_count = asset_usage.use_count + EXCLUDED.use_count
            RETURNING 1
        )
        SELECT count(*) FROM ins
        """;

    public async Task<int> AppendAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return 0;
        await db.Database.OpenConnectionAsync(ct); // the request's transaction already has it open; this is a no-op then
        try
        {
            await using var cmd = new NpgsqlCommand(Sql, (NpgsqlConnection)db.Database.GetDbConnection());
            if (db.Database.CurrentTransaction?.GetDbTransaction() is NpgsqlTransaction tx) cmd.Transaction = tx;

            Add(cmd, "ids", NpgsqlDbType.Uuid, events.Select(e => e.Id).ToArray());
            Add(cmd, "assets", NpgsqlDbType.Uuid, events.Select(e => e.AssetId).ToArray());
            Add(cmd, "types", NpgsqlDbType.Text, events.Select(e => e.EventType).ToArray());
            Add(cmd, "channels", NpgsqlDbType.Text, events.Select(e => e.Channel).ToArray());
            Add(cmd, "renditions", NpgsqlDbType.Text, events.Select(e => e.Rendition).ToArray());
            Add(cmd, "actors", NpgsqlDbType.Uuid, events.Select(e => e.ActorId).ToArray());
            Add(cmd, "occurred", NpgsqlDbType.TimestampTz, events.Select(e => e.OccurredAt.UtcDateTime).ToArray());
            Add(cmd, "clients", NpgsqlDbType.Text, events.Select(e => e.ClientEventId).ToArray());
            Add(cmd, "contexts", NpgsqlDbType.Text, events.Select(e => e.Context is null ? null : JsonSerializer.Serialize(e.Context)).ToArray());
            cmd.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = events[0].TenantId });
            cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = events[0].CreatedAt.UtcDateTime });

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void Add<T>(NpgsqlCommand cmd, string name, NpgsqlDbType element, T[] values) =>
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | element) { Value = values });
}
