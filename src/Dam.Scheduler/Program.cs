using Dam.Application.Abstractions;
using Dam.Infrastructure.Scheduling;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDamScheduling();
builder.Services.AddScheduledJob<HeartbeatJob>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok("ok"));

// Recurring jobs are (re)registered on every start; the in-memory store is intentionally stateless.
await app.Services.GetRequiredService<IJobScheduler>()
    .ScheduleCronAsync("system.heartbeat", HeartbeatJob.JobKey, "0 * * * * ?");
app.Run();
