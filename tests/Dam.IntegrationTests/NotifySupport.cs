using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Notifications;
using Dam.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.IntegrationTests;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>An email sender that keeps what it was asked to send, and can be told to fail.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();
    private volatile bool _fail;
    public bool Fail { get => _fail; set => _fail = value; }
    public bool IsEnabled => true;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("SMTP is down");
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>A local HTTP server standing in for a webhook receiver or a chat service. Records every request; answers as told.</summary>
public sealed class TestReceiver : IAsyncDisposable
{
    public sealed record Hit(DateTimeOffset At, string Path, string Body, IReadOnlyDictionary<string, string> Headers);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Hit> _hits = new();

    private TestReceiver(WebApplication app) => _app = app;

    /// <summary>The status to answer the n-th request (1-based) with.</summary>
    public Func<int, int> Respond { get; set; } = _ => 200;
    public TimeSpan Delay { get; set; }
    public string ResponseBody { get; set; } = "ok";
    public string Url => _app.Urls.First().TrimEnd('/') + "/hook";
    public IReadOnlyList<Hit> Hits => [.. _hits];

    public static async Task<TestReceiver> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var receiver = new TestReceiver(app);
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            receiver._hits.Enqueue(new Hit(DateTimeOffset.UtcNow, ctx.Request.Path, body, headers));
            try
            {
                if (receiver.Delay > TimeSpan.Zero) await Task.Delay(receiver.Delay, ctx.RequestAborted);
                ctx.Response.StatusCode = receiver.Respond(receiver._hits.Count);
                await ctx.Response.WriteAsync(receiver.ResponseBody);
            }
            catch (OperationCanceledException) { /* the sender gave up (its timeout): nothing to answer */ }
        });
        await app.StartAsync();
        return receiver;
    }

    public async Task<Hit> WaitForAsync(Func<Hit, bool>? match = null, int seconds = 15, int skip = 0)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var found = Hits.Where(h => match?.Invoke(h) ?? true).Skip(skip).FirstOrDefault();
            if (found is not null) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException($"The receiver was not called as expected within {seconds}s (it saw {_hits.Count} request(s)).");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Just enough SMTP to receive a message from MailKit: no TLS, no auth, keeps the raw DATA of every message.</summary>
public sealed class TinySmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public ConcurrentQueue<(string From, string To, string Data)> Messages { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public TinySmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        string from = "", to = "";
        await writer.WriteLineAsync("220 tiny ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            var cmd = line.ToUpperInvariant();
            if (cmd.StartsWith("EHLO", StringComparison.Ordinal) || cmd.StartsWith("HELO", StringComparison.Ordinal)) await writer.WriteLineAsync("250 tiny");
            else if (cmd.StartsWith("MAIL FROM", StringComparison.Ordinal)) { from = line[10..].Trim(); await writer.WriteLineAsync("250 OK"); }
            else if (cmd.StartsWith("RCPT TO", StringComparison.Ordinal)) { to = line[8..].Trim(); await writer.WriteLineAsync("250 OK"); }
            else if (cmd == "DATA")
            {
                await writer.WriteLineAsync("354 go ahead");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } l && l != ".") data.AppendLine(l.StartsWith("..", StringComparison.Ordinal) ? l[1..] : l);
                Messages.Enqueue((from, to, data.ToString()));
                await writer.WriteLineAsync("250 queued");
            }
            else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
            else await writer.WriteLineAsync("250 OK");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { /* shutting down */ }
        _stop.Dispose();
    }
}

/// <summary>Builds the worker and scheduler hosts the notification tests run, wired like Dam.Worker and Dam.Scheduler.</summary>
public static class NotifyHosts
{
    public static Dictionary<string, string?> Settings(Action<Dictionary<string, string?>>? more = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["DAM_SECRETS_KEY"] = ApiTestHost.SecretsKey,
            ["DAM_WEBHOOK_ALLOW_PRIVATE"] = "true",
            ["DAM_CONSOLE_PUBLIC_URL"] = "https://orbit.example.test",
        };
        more?.Invoke(values);
        return values;
    }

    /// <summary>The worker: webhook and notification consumers plus the outbox relay, on the fixture database and broker.</summary>
    public static IHost Worker(PostgresFixture pg, RabbitFixture mq, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null,
        int maxAttempts = 3, int baseDelayMs = 100)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Critical);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings()).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(pg.AppConnection);
        b.Services.AddDamNotifications(cfg);
        b.Services.AddDamMessaging(new MessagingOptions { RabbitUrl = mq.Url, MaxAttempts = maxAttempts, RetryBaseDelayMs = baseDelayMs, OutboxPollMs = 50 });
        b.Services.AddDamNotificationConsumers();
        configure?.Invoke(b.Services);
        b.Services.AddOutboxRelay(pg.SystemConnection);
        return b.Build();
    }

    /// <summary>The scheduler's jobs (webhook retry, SIEM forwarder, workflow SLA) on a clock the test controls.</summary>
    public static IHost Scheduler(PostgresFixture pg, IClock clock, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Critical);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings()).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(pg.AppConnection);
        b.Services.AddDamNotifications(cfg);
        b.Services.AddSingleton(new NpgsqlDataSourceBuilder(pg.SystemConnection).Build());
        b.Services.AddScheduledJob<WorkflowSlaJob>();
        b.Services.AddDamNotificationJobs(cfg);
        b.Services.AddSingleton(clock);   // the last registration wins: jobs and deliverers see the test's time
        configure?.Invoke(b.Services);
        return b.Build();
    }

    public static async Task RunJobAsync(IHost host, string key)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetServices<IScheduledJob>().First(j => j.Key == key);
        await job.RunAsync(new Dictionary<string, string>(), default);
    }

    public static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> ok, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T last;
        do
        {
            last = await probe();
            if (ok(last)) return last;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        return last;
    }
}
