using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>A syslog collector for tests: accepts TCP or UDP and keeps every message it is sent.</summary>
public sealed class SyslogSink : IAsyncDisposable
{
    private readonly TcpListener? _tcp;
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _octet;
    private readonly List<Task> _loops = [];

    public ConcurrentQueue<string> Messages { get; } = new();
    public int Port { get; }

    public SyslogSink(bool udp = false, bool octet = false, int port = 0)
    {
        _octet = octet;
        if (udp)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loops.Add(Task.Run(UdpLoop));
        }
        else
        {
            _tcp = new TcpListener(IPAddress.Loopback, port);
            _tcp.Start();
            Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            _loops.Add(Task.Run(TcpLoop));
        }
    }

    /// <summary>A port nothing is listening on right now (and that can be listened on a moment later).</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task UdpLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { Messages.Enqueue(Encoding.UTF8.GetString((await _udp!.ReceiveAsync(_stop.Token)).Buffer)); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
        }
    }

    private async Task TcpLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp!.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        var pending = new List<byte>();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            pending.AddRange(buffer.AsSpan(0, n).ToArray());
            while (true)
            {
                if (_octet)
                {
                    var space = pending.IndexOf((byte)' ');
                    if (space < 0 || !int.TryParse(Encoding.ASCII.GetString([.. pending.Take(space)]), out var length) || pending.Count < space + 1 + length) break;
                    Messages.Enqueue(Encoding.UTF8.GetString([.. pending.Skip(space + 1).Take(length)]));
                    pending.RemoveRange(0, space + 1 + length);
                }
                else
                {
                    var nl = pending.IndexOf((byte)'\n');
                    if (nl < 0) break;
                    Messages.Enqueue(Encoding.UTF8.GetString([.. pending.Take(nl)]));
                    pending.RemoveRange(0, nl + 1);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _tcp?.Stop();
        _udp?.Dispose();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { /* shutting down */ }
        _stop.Dispose();
    }
}

/// <summary>The SIEM forwarder (ADR-0031): CEF over syslog, a per-tenant cursor, no resend and no skipping across runs, failures retried.</summary>
[Collection("pg")]
public sealed partial class SiemTests(PostgresFixture pg) : AssetTestBase(pg)
{
    private static readonly DateTimeOffset Day = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"^<(?<pri>\d+)>1 (?<ts>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z) (?<host>\S+) orbit-dam - audit - CEF:0\|Orbit\|DAM\|(?<ver>[^|]+)\|(?<action>(?:[^|\\]|\\.)*)\|(?<name>(?:[^|\\]|\\.)*)\|(?<sev>\d+)\|(?<ext>.*)$")]
    private static partial Regex SyslogCef();

    private void InsertAudit(Guid tenant, int count, string action = "asset.created", int offset = 0)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        for (var i = 0; i < count; i++)
        {
            using var cmd = new NpgsqlCommand("INSERT INTO audit_log (id, tenant_id, ts, actor, action, entity_type, entity_id, after) VALUES (@id, @t, @ts, 'subject-a', @a, 'asset', @e, @after::jsonb)", c);
            cmd.Parameters.AddWithValue("id", Dam.Domain.Common.Uuid7.NewGuid());
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("ts", Day.AddMinutes(offset + i).ToUniversalTime());
            cmd.Parameters.AddWithValue("a", action);
            cmd.Parameters.AddWithValue("e", Guid.NewGuid());
            cmd.Parameters.AddWithValue("after", $$"""{"n":{{offset + i}}}""");
            cmd.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, string?> Siem(int port, Action<Dictionary<string, string?>>? more = null)
    {
        var s = NotifyHosts.Settings(v =>
        {
            v["DAM_SIEM_ENABLED"] = "true"; v["DAM_SIEM_HOST"] = "127.0.0.1"; v["DAM_SIEM_PORT"] = port.ToString(); v["DAM_SIEM_HOSTNAME"] = "orbit-test";
        });
        more?.Invoke(s);
        return s;
    }

    private IHost Scheduler(Dictionary<string, string?> settings) => NotifyHosts.Scheduler(Pg, new FakeClock(DateTimeOffset.UtcNow), settings);

    private static List<string> ForTenant(SyslogSink sink, Guid tenant) => sink.Messages.Where(m => m.Contains($"cs3={tenant}", StringComparison.Ordinal)).ToList();

    private static long ChainNo(string line) => long.Parse(Regex.Match(line, @"cn1=(\d+)").Groups[1].Value);

    private long Cursor(Guid tenant) => Count(tenant, "SELECT coalesce(max(last_chain_no), -1) FROM siem_cursors");

    [Fact]
    public async Task New_audit_rows_are_forwarded_once_in_order_as_rfc5424_syslog_carrying_cef()
    {
        var t = await NewTenantAsync();
        InsertAudit(t, 5, "asset.created");
        InsertAudit(t, 1, "webhook.secret_rotated", offset: 5);
        await using var sink = new SyslogSink();
        using var host = Scheduler(Siem(sink.Port));

        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        var total = Count(t, "SELECT count(*) FROM audit_log");
        var lines = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t)), l => l.Count >= total);
        Assert.Equal(total, lines.Count);
        Assert.Equal(lines.Select(ChainNo).Order(), lines.Select(ChainNo));   // oldest first
        Assert.Equal(Enumerable.Range(1, (int)total).Select(i => (long)i), lines.Select(ChainNo));   // no gaps

        var rotated = lines.Single(l => l.Contains("webhook.secret_rotated", StringComparison.Ordinal));
        var m = SyslogCef().Match(rotated);
        Assert.True(m.Success, rotated);
        Assert.Equal("orbit-test", m.Groups["host"].Value);
        Assert.Equal("6", m.Groups["sev"].Value);
        Assert.Equal((16 * 8) + 5, int.Parse(m.Groups["pri"].Value));   // local0.notice
        Assert.Equal("webhook.secret_rotated", m.Groups["action"].Value);
        Assert.Contains("suid=subject-a", m.Groups["ext"].Value);
        Assert.Equal(total, Cursor(t));

        // Nothing new: nothing sent.
        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        await Task.Delay(300);
        Assert.Equal(lines.Count, ForTenant(sink, t).Count);

        // New rows: only those.
        InsertAudit(t, 2, "asset.deleted", offset: 10);
        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        var more = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t)), l => l.Count >= total + 2);
        Assert.Equal(total + 2, more.Count);
        Assert.Equal([total + 1, total + 2], more.Skip((int)total).Select(ChainNo));
        Assert.All(more.Skip((int)total), l => Assert.Contains("|asset.deleted|", l));
        Assert.Equal(total + 2, Cursor(t));
    }

    [Fact]
    public async Task A_restart_neither_resends_nor_skips()
    {
        var t = await NewTenantAsync();
        InsertAudit(t, 4);
        await using var sink = new SyslogSink();
        var settings = Siem(sink.Port);
        var total = Count(t, "SELECT count(*) FROM audit_log");
        using (var first = Scheduler(settings))
            await NotifyHosts.RunJobAsync(first, SiemForwardJob.JobKey);
        await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t).Count), c => c >= total);

        InsertAudit(t, 3, offset: 4);   // recorded while the scheduler was "down"
        using var second = Scheduler(settings);   // a new process: all it has is the stored cursor
        await NotifyHosts.RunJobAsync(second, SiemForwardJob.JobKey);
        var lines = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t)), l => l.Count >= total + 3);
        await Task.Delay(300);
        lines = ForTenant(sink, t);
        Assert.Equal(Enumerable.Range(1, (int)total + 3).Select(i => (long)i), lines.Select(ChainNo));   // each row exactly once, in order
    }

    [Fact]
    public async Task A_collector_that_is_down_is_retried_on_the_next_run_without_losing_or_repeating_anything()
    {
        var t = await NewTenantAsync();
        InsertAudit(t, 6);
        var port = SyslogSink.FreePort();
        using var host = Scheduler(Siem(port));

        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);   // nobody listening: logged, not thrown
        Assert.Equal(-1, Cursor(t));                                   // no cursor was saved: it moves only after a batch was sent

        await using var sink = new SyslogSink(port: port);
        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        var total = Count(t, "SELECT count(*) FROM audit_log");
        var lines = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t)), l => l.Count >= total);
        Assert.Equal(Enumerable.Range(1, (int)total).Select(i => (long)i), lines.Select(ChainNo));
        Assert.Equal(total, Cursor(t));
    }

    [Fact]
    public async Task A_first_run_with_backfill_off_starts_from_now()
    {
        var t = await NewTenantAsync();
        InsertAudit(t, 4);
        var existing = Count(t, "SELECT count(*) FROM audit_log");
        await using var sink = new SyslogSink();
        using var host = Scheduler(Siem(sink.Port, v => v["DAM_SIEM_BACKFILL"] = "false"));

        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        await Task.Delay(400);
        Assert.Empty(ForTenant(sink, t));
        Assert.Equal(existing, Cursor(t));

        InsertAudit(t, 2, offset: 4);
        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        var lines = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t)), l => l.Count >= 2);
        Assert.Equal([existing + 1, existing + 2], lines.Select(ChainNo));
    }

    [Fact]
    public async Task Octet_counted_framing_and_udp_are_supported()
    {
        var t = await NewTenantAsync();
        InsertAudit(t, 3);
        var total = Count(t, "SELECT count(*) FROM audit_log");

        await using (var octet = new SyslogSink(octet: true))
        {
            using var host = Scheduler(Siem(octet.Port, v => v["DAM_SIEM_FRAMING"] = "octet"));
            await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
            var lines = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(octet, t)), l => l.Count >= total);
            Assert.Equal(total, lines.Count);
            Assert.All(lines, l => Assert.Matches(@"^<\d+>1 ", l));
        }

        var u = await NewTenantAsync();
        InsertAudit(u, 3);
        var udpTotal = Count(u, "SELECT count(*) FROM audit_log");
        await using var udp = new SyslogSink(udp: true);
        using var udpHost = Scheduler(Siem(udp.Port, v => v["DAM_SIEM_PROTOCOL"] = "udp"));
        await NotifyHosts.RunJobAsync(udpHost, SiemForwardJob.JobKey);
        var datagrams = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(udp, u)), l => l.Count >= udpTotal);
        Assert.Equal(udpTotal, datagrams.Count);
        Assert.All(datagrams, l => Assert.True(SyslogCef().IsMatch(l), l));
    }

    [Fact]
    public async Task Each_tenant_has_its_own_cursor_and_a_disabled_forwarder_sends_nothing()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        InsertAudit(a, 3); InsertAudit(b, 5);
        await using var sink = new SyslogSink();

        using (var off = Scheduler(Siem(sink.Port, v => v["DAM_SIEM_ENABLED"] = "false")))
            await NotifyHosts.RunJobAsync(off, SiemForwardJob.JobKey);
        await Task.Delay(300);
        Assert.Empty(ForTenant(sink, a));
        Assert.Equal(-1, Cursor(a));

        using var on = Scheduler(Siem(sink.Port));
        await NotifyHosts.RunJobAsync(on, SiemForwardJob.JobKey);
        var totalA = Count(a, "SELECT count(*) FROM audit_log"); var totalB = Count(b, "SELECT count(*) FROM audit_log");
        await NotifyHosts.Eventually(() => Task.FromResult((ForTenant(sink, a).Count, ForTenant(sink, b).Count)), c => c.Item1 >= totalA && c.Item2 >= totalB);
        Assert.Equal(totalA, ForTenant(sink, a).Count);
        Assert.Equal(totalB, ForTenant(sink, b).Count);
        Assert.Equal(totalA, Cursor(a));
        Assert.Equal(totalB, Cursor(b));
        Assert.DoesNotContain(ForTenant(sink, a), l => l.Contains($"cs3={b}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_large_backlog_drains_over_several_batches_and_runs()
    {
        var t = await NewTenantAsync();
        using (var c = new NpgsqlConnection(Pg.OwnerConnection))
        {
            c.Open();
            using var cmd = new NpgsqlCommand("INSERT INTO audit_log (id, tenant_id, ts, actor, action) SELECT gen_random_uuid(), @t, now(), 'bulk', 'asset.metadata.updated' FROM generate_series(1, 2300)", c);
            cmd.Parameters.AddWithValue("t", t);
            cmd.ExecuteNonQuery();
        }
        var total = Count(t, "SELECT count(*) FROM audit_log");
        await using var sink = new SyslogSink();
        using var host = Scheduler(Siem(sink.Port));
        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);   // up to four batches of 500 per run
        await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t).Count), c => c >= 2000, seconds: 30);
        Assert.Equal(2000, Cursor(t));
        await NotifyHosts.RunJobAsync(host, SiemForwardJob.JobKey);
        var lines = await NotifyHosts.Eventually(() => Task.FromResult(ForTenant(sink, t)), l => l.Count >= total, seconds: 30);
        Assert.Equal(Enumerable.Range(1, (int)total).Select(i => (long)i), lines.Select(ChainNo));
    }
}
