using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Scheduling;
using Dam.Infrastructure.Search;
using Dam.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
string Required(string k) => cfg[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} is not set.");

builder.Services.AddDamScheduling();
builder.Services.AddScheduledJob<HeartbeatJob>();

// Retention and scheduled-publish jobs (spec Step 1.6b) need the same tenant-scoped data access a request or a worker
// consumer has, plus a read-only cross-tenant connection to find candidates in the first place.
builder.Services.AddDamWorkerIdentity();
builder.Services.AddDamInfrastructure(Required("DAM_DB_CONNECTION"));
builder.Services.AddDamStorage(cfg);
builder.Services.AddDamSearch(cfg);
builder.Services.AddDamRetentionScheduling(Required("DAM_DB_SYSTEM_CONNECTION"));
// Shares the retention scheduling's system connection (registered above) - one read-only cross-tenant connection per host.
builder.Services.AddScheduledJob<SearchReindexJob>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok("ok"));

// Recurring jobs are (re)registered on every start; the in-memory store is intentionally stateless.
var scheduler = app.Services.GetRequiredService<IJobScheduler>();
await scheduler.ScheduleCronAsync("system.heartbeat", HeartbeatJob.JobKey, "0 * * * * ?");
// Every 15s: comfortably inside the spec's "publish fires within 60s of publish_at".
await scheduler.ScheduleCronAsync("assets.publish_expire", PublishExpireJob.JobKey, cfg["DAM_SCHEDULE_POLL_CRON"] ?? "0/15 * * * * ?");
// Hourly: archive/purge have no tight SLA, and each check involves a policy join across every tenant.
await scheduler.ScheduleCronAsync("assets.retention", RetentionJob.JobKey, cfg["DAM_RETENTION_POLL_CRON"] ?? "0 0 * * * ?");
app.Run();
