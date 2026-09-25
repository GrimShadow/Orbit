using Dam.Application.Abstractions;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

public sealed class RecordingJob : IScheduledJob
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Runs = new();
    public string Key => "test.record";
    public Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        Runs.Enqueue(data.GetValueOrDefault("tag", "none"));
        return Task.CompletedTask;
    }
}

/// <summary>
/// One scheduler host for the whole class: Quartz keeps a process-wide static logger provider, so building a second
/// Quartz host in the same test process would inherit the first host's disposed LoggerFactory.
/// </summary>
public sealed class SchedulerFixture : IAsyncLifetime
{
    private IHost? _host;
    public IJobScheduler Scheduler => _host!.Services.GetRequiredService<IJobScheduler>();

    public async Task InitializeAsync()
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Services.AddDamScheduling("test-scheduler");
        b.Services.AddScheduledJob<RecordingJob>();
        _host = b.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host!.StopAsync();
        _host.Dispose();
    }
}

public sealed class SchedulerTests(SchedulerFixture fx) : IClassFixture<SchedulerFixture>
{
    private static async Task WaitFor(Func<bool> cond, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(50);
    }

    [Fact]
    public async Task One_off_job_fires_with_its_data_and_can_be_rescheduled_or_cancelled()
    {
        RecordingJob.Runs.Clear();
        var scheduler = fx.Scheduler;

        await scheduler.ScheduleOnceAsync("a", "test.record", DateTimeOffset.UtcNow.AddSeconds(1), new Dictionary<string, string> { ["tag"] = "first" });
        // Same schedule id replaces the earlier trigger: pushed a day out, so it must not fire.
        await scheduler.ScheduleOnceAsync("b", "test.record", DateTimeOffset.UtcNow.AddSeconds(1), new Dictionary<string, string> { ["tag"] = "old" });
        await scheduler.ScheduleOnceAsync("b", "test.record", DateTimeOffset.UtcNow.AddDays(1), new Dictionary<string, string> { ["tag"] = "new" });
        // Cancelled before it fires.
        await scheduler.ScheduleOnceAsync("c", "test.record", DateTimeOffset.UtcNow.AddSeconds(1), new Dictionary<string, string> { ["tag"] = "cancelled" });
        Assert.True(await scheduler.CancelAsync("c"));

        await WaitFor(() => RecordingJob.Runs.Contains("first"));
        await Task.Delay(1500);
        Assert.Equal(["first"], RecordingJob.Runs.Where(t => t != "tick").ToArray());
        await scheduler.CancelAsync("b");
    }

    [Fact]
    public async Task Cron_job_fires_repeatedly()
    {
        await fx.Scheduler.ScheduleCronAsync("tick", "test.record", "* * * * * ?", new Dictionary<string, string> { ["tag"] = "tick" });
        var before = RecordingJob.Runs.Count(t => t == "tick");
        await WaitFor(() => RecordingJob.Runs.Count(t => t == "tick") >= before + 2);
        Assert.True(RecordingJob.Runs.Count(t => t == "tick") >= before + 2);
        await fx.Scheduler.CancelAsync("tick");
    }
}
