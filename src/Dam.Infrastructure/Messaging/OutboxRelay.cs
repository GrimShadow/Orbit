using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;

namespace Dam.Infrastructure.Messaging;

/// <summary>
/// Publishes committed outbox rows to RabbitMQ. Connects as the restricted `dam_system` role, which may read and
/// mark outbox rows across tenants and nothing else. Rows are locked with SKIP LOCKED so several relays can run.
/// Delivery is at-least-once (a crash between publish and commit re-sends); consumers de-duplicate on message id.
/// </summary>
public sealed class OutboxRelay(
    NpgsqlDataSource systemDb, RabbitConnection rabbit, MessagingOptions options, ILogger<OutboxRelay> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var ch = await (await rabbit.GetAsync(ct)).CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
                await Topology.DeclareExchangeAsync(ch, options, ct);
                while (!ct.IsCancellationRequested)
                {
                    var sent = await RelayBatchAsync(ch, ct);
                    if (sent == 0) await Task.Delay(options.OutboxPollMs, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox relay failed; retrying in 2s");
                await Task.Delay(2000, ct);
            }
        }
    }

    public async Task<int> RelayBatchAsync(IChannel ch, CancellationToken ct)
    {
        await using var conn = await systemDb.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = new List<(Guid Id, Guid Tenant, string Type, string Payload, string? Correlation)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, tenant_id, event_type, payload::text, correlation_id FROM outbox
            WHERE published_at IS NULL ORDER BY created_at, id LIMIT @n FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("n", options.OutboxBatchSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }
        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            var props = new BasicProperties
            {
                MessageId = row.Id.ToString(),
                Type = row.Type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object?>
                {
                    ["tenant_id"] = row.Tenant.ToString(),
                    ["correlation_id"] = row.Correlation,
                    ["x-attempt"] = "1",
                },
            };
            // Awaits the broker's publisher confirm; throws if the broker does not accept the message.
            await ch.BasicPublishAsync(options.Exchange, row.Type, mandatory: false, props, Encoding.UTF8.GetBytes(row.Payload), ct);
        }

        await using (var upd = new NpgsqlCommand(
            "UPDATE outbox SET published_at = now(), attempts = attempts + 1 WHERE id = ANY(@ids)", conn, tx))
        {
            upd.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
            await upd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return rows.Count;
    }
}
