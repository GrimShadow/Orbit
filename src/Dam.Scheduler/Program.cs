using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Notifications;
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
// Webhook retries (needs the secrets key and outbound HTTP) and the SIEM forwarder.
builder.Services.AddDamNotifications(cfg);
builder.Services.AddDamNotificationJobs(cfg);

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok("ok"));

// Recurring jobs are (re)registered on every start; the in-memory store is intentionally stateless.
var scheduler = app.Services.GetRequiredService<IJobScheduler>();
await scheduler.ScheduleCronAsync("system.heartbeat", HeartbeatJob.JobKey, "0 * * * * ?");
// Every 15s: comfortably inside the spec's "publish fires within 60s of publish_at".
await scheduler.ScheduleCronAsync("assets.publish_expire", PublishExpireJob.JobKey, cfg["DAM_SCHEDULE_POLL_CRON"] ?? "0/15 * * * * ?");
// Every 30s: a missed workflow SLA shows in the API well within a minute of the due time (spec Step 2.3).
await scheduler.ScheduleCronAsync("workflow.sla", WorkflowSlaJob.JobKey, cfg["DAM_WORKFLOW_SLA_POLL_CRON"] ?? "0/30 * * * * ?");
// Hourly: archive/purge have no tight SLA, and each check involves a policy join across every tenant.
await scheduler.ScheduleCronAsync("assets.retention", RetentionJob.JobKey, cfg["DAM_RETENTION_POLL_CRON"] ?? "0 0 * * * ?");
// Hourly, and once shortly after start: licence alerts (30, 7, 1 days), expiring published assets whose licence ended, and catching up rights-derived access attributes.
await scheduler.ScheduleCronAsync("rights.expiry", RightsExpiryJob.JobKey, cfg["DAM_RIGHTS_POLL_CRON"] ?? "0 5 * * * ?");
await scheduler.ScheduleOnceAsync("rights.expiry.start", RightsExpiryJob.JobKey, DateTimeOffset.UtcNow.AddSeconds(20));
// Every 10s: a failed webhook delivery is retried on its backoff schedule to within ten seconds of its due time (ADR-0031).
await scheduler.ScheduleCronAsync("webhooks.retry", WebhookRetryJob.JobKey, cfg["DAM_WEBHOOK_RETRY_POLL_CRON"] ?? "0/10 * * * * ?");
// Every 30s, only when DAM_SIEM_ENABLED=true: forward new audit rows to the SIEM as CEF over syslog.
if (string.Equals(cfg["DAM_SIEM_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
    await scheduler.ScheduleCronAsync("audit.siem", SiemForwardJob.JobKey, cfg["DAM_SIEM_POLL_CRON"] ?? "0/30 * * * * ?");
app.Run();
