using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit.Abstractions;

namespace Dam.IntegrationTests;

/// <summary>
/// Ingest performance (ADR-0037 decision 9; spec Step 2.7: 1M events/day without API p95 above 500 ms). Batches of 500 events with unique client ids over many assets are posted
/// by several workers at once to an API that uses pooled connections like a deployed one; the p95 of a request must stay under the budget with headroom for a slow CI machine,
/// and nothing may be lost, doubled or left unmarked. Setting DAM_PERF_SCRIPT=1 also runs <c>tools/perf/usage-ingest.mjs</c> (needs node) against the same API over a real socket.
/// </summary>
[Collection("pg")]
public sealed class UsageIngestPerformanceTests(PostgresFixture pg, ITestOutputHelper output) : AnalyticsTestBase(pg)
{
    private const int Batch = 500, Batches = 40, AssetCount = 300;
    private const int Workers = 4;
    private static readonly string[] Channels = ["console", "portal", "kiosk-lobby", "kiosk-showroom", "app-ios", "app-android", "web", "signage-north", "signage-south", "signage-east", "signage-west"];
    private static readonly string[] Types = ["view", "download", "share", "embed", "place", "play"];

    /// <summary>The fixture's app connection does not pool (so connections can be dropped between tests); a deployed API does.</summary>
    private string PooledConnection => new NpgsqlConnectionStringBuilder(Pg.AppConnection) { Pooling = true, MaxPoolSize = 50 }.ConnectionString;

    private async Task<(Guid Tenant, string Token, Guid[] Assets)> Library()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var seed = await Aged(admin, folder, 100, "Seed");
        Sql("INSERT INTO assets (id, tenant_id, folder_id, type, status, title, metadata, metadata_schema_version, taxonomy_terms, tags, attributes, metadata_attributes, rights_channels, rights_regions, legal_hold, status_reason, created_at, updated_at) "
            + "SELECT gen_random_uuid(), tenant_id, folder_id, type, status, 'Perf asset ' || n, '{}'::jsonb, 0, '{}', '{}', '{}'::jsonb, '{}'::jsonb, '{}', '{}', false, '', created_at, created_at "
            + $"FROM assets, generate_series(1, {AssetCount - 1}) AS n WHERE id = '{seed}'");
        var ids = new List<Guid>();
        using var c = Pg.RawAppConnection(t);
        using var cmd = new NpgsqlCommand("SELECT id FROM assets ORDER BY id", c);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetGuid(0));
        return (t, admin, [.. ids]);
    }

    private static string BatchJson(Guid[] assets, string run, int index, Random rng)
    {
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, Batch).Select(i => new
        {
            clientEventId = $"{run}-{index * Batch + i}",
            assetId = assets[rng.Next(assets.Length)],
            eventType = Types[rng.Next(Types.Length)],
            channel = Channels[rng.Next(Channels.Length)],
            occurredAt = now.AddMinutes(-rng.Next(0, 30 * 24 * 60)),     // spread over the last 30 days: every batch touches many days
        });
        return JsonSerializer.Serialize(new { events });
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * sorted.Count) - 1))];

    private sealed record Load(int Events, double Seconds, double P50, double P95, double P99, double Max);

    /// <summary>Posts <see cref="Batches"/> batches of <see cref="Batch"/> events, <paramref name="workers"/> requests at a time, and times every request.</summary>
    private async Task<Load> Post(HttpClient http, Guid[] assets, string run, int workers)
    {
        var payloads = Enumerable.Range(0, Batches).Select(i => BatchJson(assets, run, i, new Random(i + 100))).ToArray();
        var latencies = new double[Batches];
        var accepted = new int[Batches];
        var next = -1;
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= Batches) return;
                var sw = Stopwatch.StartNew();
                using var res = await http.PostAsync(EventsUrl, new StringContent(payloads[i], Encoding.UTF8, "application/json"));
                var body = await res.Content.ReadAsStringAsync();
                latencies[i] = sw.Elapsed.TotalMilliseconds;
                Assert.True(res.StatusCode == HttpStatusCode.OK, $"batch {i}: {(int)res.StatusCode} {body}");
                accepted[i] = JsonDocument.Parse(body).RootElement.GetProperty("accepted").GetInt32();
            }
        })));
        wall.Stop();

        var sorted = latencies.Order().ToList();
        var load = new Load(accepted.Sum(), wall.Elapsed.TotalSeconds, Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), sorted[^1]);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{load.Events} events in {Batches} batches of {Batch}, {workers} workers, {assets.Length} assets: {load.Seconds:F2} s, {load.Events / load.Seconds:F0} events/s ({load.Events / load.Seconds * 86400:N0}/day); per request p50 {load.P50:F0} ms, p95 {load.P95:F0} ms, p99 {load.P99:F0} ms, max {load.Max:F0} ms"));
        return load;
    }

    [Fact]
    public async Task Batches_of_500_events_posted_concurrently_stay_within_the_p95_budget_and_lose_nothing()
    {
        var (t, token, assets) = await Library();
        await using var api = new ApiTestHost(Pg).Factory(dbConnection: PooledConnection);
        using var http = api.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var run = "perf-" + Guid.NewGuid().ToString("N")[..8];

        // warm up: JIT, the tenant's first request, connection pool
        using (var warm = await http.PostAsync(EventsUrl, new StringContent(BatchJson(assets, run + "-warm", 0, new Random(1)), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);

        // Four requests at a time is already thousands of times the target (1M events a day is about one batch every seven seconds on average): the budget applies in full.
        var steady = await Post(http, assets, run + "-a", Workers);
        Assert.Equal(Batches * Batch, steady.Events);
        Assert.True(steady.P95 < 500, $"p95 was {steady.P95:F0} ms with {Workers} workers (p50 {steady.P50:F0} ms, p99 {steady.P99:F0} ms)");

        // Twice the workers saturates a small CI machine (and all of them write the same tenant's day rows and hot assets, which queue behind one another): requests wait
        // their turn, so the bound is looser. It guards against a pathology (a lock convoy, deadlocks, a failing request), not against a slow runner.
        var saturated = await Post(http, assets, run + "-b", Workers * 2);
        Assert.Equal(Batches * Batch, saturated.Events);
        Assert.True(saturated.P95 < 1500, $"p95 was {saturated.P95:F0} ms with {Workers * 2} workers (p50 {saturated.P50:F0} ms, p99 {saturated.P99:F0} ms)");

        // nothing lost, nothing doubled, every day marked for rollup
        const int expected = 2 * Batches * Batch + Batch;
        Assert.Equal(expected, Count(t, "SELECT count(*) FROM usage_events"));
        Assert.Equal(expected, Count(t, "SELECT count(*) FROM usage_event_keys"));
        Assert.Equal(expected, Count(t, "SELECT sum(use_count) FROM asset_usage"));
        Assert.Equal(Count(t, "SELECT count(DISTINCT (occurred_at AT TIME ZONE 'UTC')::date) FROM usage_events"), Count(t, "SELECT count(*) FROM usage_rollup_dirty"));
        Assert.True(Count(t, "SELECT count(DISTINCT asset_id) FROM usage_events") > AssetCount / 2);
    }

    [Fact]
    public async Task The_load_script_runs_against_a_real_socket()
    {
        if (Environment.GetEnvironmentVariable("DAM_PERF_SCRIPT") is not "1") return;   // opt in: needs node, and takes a while
        var (t, token, _) = await Library();
        await using var api = new ApiTestHost(Pg).Factory(dbConnection: PooledConnection);
        await using var proxy = await Proxy.StartAsync(api);
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/perf/usage-ingest.mjs"));
        Assert.True(File.Exists(script), script);

        var total = Environment.GetEnvironmentVariable("DAM_PERF_EVENTS") ?? "20000";
        var concurrency = Environment.GetEnvironmentVariable("DAM_PERF_CONCURRENCY") ?? Workers.ToString(CultureInfo.InvariantCulture);
        var info = new ProcessStartInfo("node", [script, proxy.Url, token, total, concurrency]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(long.Parse(total, CultureInfo.InvariantCulture), Count(t, "SELECT count(*) FROM usage_events"));
    }

    /// <summary>A real Kestrel listener that hands every request to the in-process API, so an external tool can talk to it.</summary>
    private sealed class Proxy : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private Proxy(WebApplication app) => _app = app;
        public string Url => _app.Urls.First();

        public static async Task<Proxy> StartAsync(WebApplicationFactory<Program> api)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var app = builder.Build();
            var upstream = api.CreateClient();
            app.Run(async ctx =>
            {
                using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), ctx.Request.Path + ctx.Request.QueryString);
                if (ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } auth) request.Headers.TryAddWithoutValidation("Authorization", auth);
                if (ctx.Request.ContentLength is > 0)
                {
                    request.Content = new StreamContent(ctx.Request.Body);
                    if (ctx.Request.ContentType is { } type) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                }
                using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                ctx.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } contentType) ctx.Response.ContentType = contentType.ToString();
                await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            });
            await app.StartAsync();
            return new Proxy(app);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
