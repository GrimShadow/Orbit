using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
string Required(string k) => cfg[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} is not set.");

builder.Services.AddDamWorkerIdentity();
builder.Services.AddDamInfrastructure(Required("DAM_DB_CONNECTION"));
builder.Services.AddDamMessaging(cfg.ReadMessagingOptions());
builder.Services.AddEventConsumer<PingConsumer>();
// Relay last: consumers must have declared and bound their queues before anything is published.
builder.Services.AddOutboxRelay(Required("DAM_DB_SYSTEM_CONNECTION"));

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok("ok"));
app.Run();
