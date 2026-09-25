using Dam.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Dam.Infrastructure.Scheduling;

/// <summary>Single Quartz job that dispatches to the <see cref="IScheduledJob"/> registered under the key in its data map.</summary>
[DisallowConcurrentExecution]
public sealed class DispatchJob(IEnumerable<IScheduledJob> jobs) : IJob
{
    public const string KeyField = "__job_key";

    public async Task Execute(IJobExecutionContext context)
    {
        var map = context.MergedJobDataMap;
        var key = map.GetString(KeyField) ?? throw new InvalidOperationException("Missing job key.");
        var job = jobs.FirstOrDefault(j => j.Key == key) ?? throw new InvalidOperationException($"No scheduled job registered for '{key}'.");
        var data = map.Where(kv => kv.Key != KeyField).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        await job.RunAsync(data, context.CancellationToken);
    }
}

/// <summary>
/// Quartz-backed scheduler using the in-memory store. Recurring jobs are re-registered on every scheduler start.
/// One-off schedules that must survive restarts (publish_at, expire_at) are persisted in the database by their
/// owning feature and re-armed by a polling job (Step 1.6), not stored here.
/// </summary>
public sealed class QuartzJobScheduler(ISchedulerFactory factory) : IJobScheduler
{
    public Task ScheduleOnceAsync(string scheduleId, string jobKey, DateTimeOffset at,
        IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default) =>
        ScheduleAsync(scheduleId, jobKey, data, b => b.StartAt(at), ct);

    public Task ScheduleCronAsync(string scheduleId, string jobKey, string cron,
        IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default) =>
        ScheduleAsync(scheduleId, jobKey, data, b => b.WithCronSchedule(cron), ct);

    public async Task<bool> CancelAsync(string scheduleId, CancellationToken ct = default) =>
        await (await factory.GetScheduler(ct)).DeleteJob(new JobKey(scheduleId), ct);

    private async Task ScheduleAsync(string scheduleId, string jobKey, IReadOnlyDictionary<string, string>? data,
        Func<TriggerBuilder, TriggerBuilder> when, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        var map = new JobDataMap { [DispatchJob.KeyField] = jobKey };
        if (data is not null) foreach (var (k, v) in data) map[k] = v;

        var job = JobBuilder.Create<DispatchJob>().WithIdentity(scheduleId).UsingJobData(map).Build();
        var trigger = when(TriggerBuilder.Create().WithIdentity(scheduleId).ForJob(job)).Build();
        await scheduler.DeleteJob(job.Key, ct); // replace semantics
        await scheduler.ScheduleJob(job, trigger, ct);
    }
}

public static class SchedulingRegistration
{
    /// <param name="schedulerName">Quartz keeps schedulers in a process-wide registry by name; only tests need a unique one.</param>
    public static IServiceCollection AddDamScheduling(this IServiceCollection services, string schedulerName = "dam-scheduler")
    {
        services.AddQuartz(q =>
        {
            q.SchedulerName = schedulerName;
            q.UseInMemoryStore();
        });
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        services.AddSingleton<IJobScheduler, QuartzJobScheduler>();
        return services;
    }

    public static IServiceCollection AddScheduledJob<T>(this IServiceCollection services) where T : class, IScheduledJob =>
        services.AddTransient<IScheduledJob, T>();
}

public sealed class HeartbeatJob(Microsoft.Extensions.Logging.ILogger<HeartbeatJob> log) : IScheduledJob
{
    public const string JobKey = "system.heartbeat";
    public string Key => JobKey;

    public Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        log.LogInformation("scheduler heartbeat");
        return Task.CompletedTask;
    }
}
