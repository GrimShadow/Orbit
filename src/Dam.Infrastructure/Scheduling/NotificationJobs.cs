using System.Net.Sockets;
using Dam.Application.Abstractions;
using Dam.Application.Audit;
using Dam.Domain.Common;
using Dam.Domain.Notifications;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Scheduling;

/// <summary>
/// Retries webhook deliveries that failed (ADR-0031). Every ten seconds it looks across all tenants, through the read-only system
/// connection, for pending deliveries whose time has come, then attempts each through an ordinary tenant-scoped scope. The attempts
/// run a few at a time so a handful of slow receivers cannot hold up the rest. The clock is <see cref="IClock"/>, so tests drive it.
/// </summary>
public sealed class WebhookRetryJob(IServiceScopeFactory scopes, NpgsqlDataSource systemDb, IClock clock, ILogger<WebhookRetryJob> log)
    : CrossTenantJob(scopes, systemDb, log), IScheduledJob
{
    public const string JobKey = "webhooks.retry";
    public string Key => JobKey;

    private const string DueSql = """
        SELECT id, tenant_id FROM webhook_deliveries
        WHERE status = 'pending' AND next_attempt_at <= @now ORDER BY next_attempt_at LIMIT 200
        """;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var due = await FindAsync(DueSql, new Dictionary<string, object> { ["now"] = clock.UtcNow.ToUniversalTime() }, ct);
        if (due.Count == 0) return;
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (lead, c) =>
        {
            try
            {
                await using var scope = Scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = lead.TenantId;
                await scope.ServiceProvider.GetRequiredService<IWebhookDeliverer>().AttemptAsync(lead.AssetId, c);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Retrying webhook delivery {Delivery} failed", lead.AssetId);
            }
        });
    }
}

/// <summary>SIEM forwarding settings (env: DAM_SIEM_*).</summary>
public sealed class SiemOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 514;
    /// <summary>tcp (default) or udp.</summary>
    public string Protocol { get; set; } = "tcp";
    /// <summary>newline (default; RFC 6587 non-transparent framing) or octet (RFC 6587 octet counting). Ignored for UDP.</summary>
    public string Framing { get; set; } = "newline";
    /// <summary>On first run, forward the existing history (true, default) or only rows recorded from now on.</summary>
    public bool Backfill { get; set; } = true;
    public string Hostname { get; set; } = Environment.MachineName;
    public int BatchSize { get; set; } = 500;
    /// <summary>Batches per tenant per run; a backlog drains over several runs.</summary>
    public int MaxBatchesPerRun { get; set; } = 4;

    public static SiemOptions Read(IConfiguration cfg) => new()
    {
        Enabled = string.Equals(cfg["DAM_SIEM_ENABLED"], "true", StringComparison.OrdinalIgnoreCase),
        Host = cfg["DAM_SIEM_HOST"] ?? "",
        Port = int.TryParse(cfg["DAM_SIEM_PORT"], out var p) ? p : 514,
        Protocol = (cfg["DAM_SIEM_PROTOCOL"] ?? "tcp").Trim().ToLowerInvariant(),
        Framing = (cfg["DAM_SIEM_FRAMING"] ?? "newline").Trim().ToLowerInvariant(),
        Backfill = !string.Equals(cfg["DAM_SIEM_BACKFILL"], "false", StringComparison.OrdinalIgnoreCase),
        Hostname = cfg["DAM_SIEM_HOSTNAME"] is { Length: > 0 } h ? h : Environment.MachineName,
    };
}

/// <summary>
/// Forwards new audit rows to a SIEM as CEF over syslog (RFC 5424; spec A7.10, ADR-0031). Per tenant it keeps a cursor, the chain position
/// of the last row sent, in <c>siem_cursors</c>, and moves it only after the batch was handed to the network: a restart neither re-sends nor
/// skips, and a failed send is simply tried again on the next run. Delivery is at-least-once (a crash between sending and saving the cursor
/// repeats one batch; every row carries its chain number and hash so a collector can drop repeats); over UDP there is no delivery guarantee.
/// </summary>
public sealed class SiemForwardJob(IServiceScopeFactory scopes, NpgsqlDataSource systemDb, SiemOptions options, IClock clock, ILogger<SiemForwardJob> log)
    : CrossTenantJob(scopes, systemDb, log), IScheduledJob
{
    public const string JobKey = "audit.siem";
    public string Key => JobKey;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        if (!options.Enabled || options.Host.Length == 0) return;
        var tenants = await FindAsync("SELECT id, id FROM tenants", ct);
        foreach (var tenant in tenants)
        {
            try { await ForwardTenantAsync(tenant.TenantId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "SIEM forwarding failed for tenant {Tenant}; it will be retried on the next run", tenant.TenantId);
            }
        }
    }

    public async Task ForwardTenantAsync(Guid tenantId, CancellationToken ct)
    {
        for (var batch = 0; batch < options.MaxBatchesPerRun; batch++)
        {
            await using var scope = Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
            var db = sp.GetRequiredService<DamDbContext>();
            var cursor = await db.SiemCursors.FirstOrDefaultAsync(ct);

            IReadOnlyList<Dam.Shared.Audit.AuditEntryDto> rows;
            await using (var snapshot = await sp.GetRequiredService<IAuditReader>().OpenAsync(ct))
            {
                if (cursor is null)
                {
                    cursor = SiemCursor.Create(tenantId, options.Backfill ? 0 : await snapshot.MaxChainNoAsync(ct), clock.UtcNow);
                    db.SiemCursors.Add(cursor);
                }
                rows = await snapshot.ReadAfterAsync(cursor.LastChainNo, options.BatchSize, ct);
            }
            if (rows.Count == 0) { await db.SaveChangesAsync(ct); return; }

            var lines = rows.Select(r =>
            {
                var cef = CefFormatter.Format(r, tenantId, AppInfo.Version);
                return SyslogFormatter.Format(cef, CefFormatter.Severity(r.Action), r.OccurredAt, options.Hostname, "orbit-dam", "audit");
            }).ToList();
            await SendAsync(lines, ct);

            cursor.Advance(rows[^1].ChainNo, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            if (rows.Count < options.BatchSize) return;
        }
    }

    private async Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        if (options.Protocol == "udp")
        {
            using var udp = new UdpClient();
            udp.Connect(options.Host, options.Port);
            foreach (var line in lines)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                await udp.SendAsync(bytes.Length <= 8192 ? bytes : bytes[..8192], timeout.Token);   // one datagram per event; oversize events are cut
            }
            return;
        }
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(options.Host, options.Port, timeout.Token);
        await using var stream = tcp.GetStream();
        foreach (var line in lines)
        {
            var frame = options.Framing == "octet" ? SyslogFormatter.OctetCounted(line) : SyslogFormatter.NewlineDelimited(line);
            await stream.WriteAsync(frame, timeout.Token);
        }
        await stream.FlushAsync(timeout.Token);
    }
}

public static class NotificationSchedulingRegistration
{
    /// <summary>Registers the webhook retry job and the SIEM forwarder. Needs <c>AddDamRetentionScheduling</c> (the read-only system connection).</summary>
    public static IServiceCollection AddDamNotificationJobs(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton(SiemOptions.Read(cfg));
        services.AddScheduledJob<WebhookRetryJob>();
        services.AddScheduledJob<SiemForwardJob>();
        return services;
    }
}
