using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Delivery;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Ingest;
using Dam.Infrastructure.Search;
using Dam.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
string Required(string k) => cfg[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} is not set.");

builder.Services.AddDamWorkerIdentity();
builder.Services.AddDamInfrastructure(Required("DAM_DB_CONNECTION"));
builder.Services.AddDamStorage(cfg);
builder.Services.AddDamIngest(cfg);
builder.Services.AddDamRendering(cfg);
builder.Services.AddDamSearch(cfg);
builder.Services.AddDamMessaging(cfg.ReadMessagingOptions());
builder.Services.AddEventConsumer<PingConsumer>();
builder.Services.AddEventConsumer<HashUploadConsumer>();
builder.Services.AddEventConsumer<ScanUploadConsumer>();
builder.Services.AddEventConsumer<SniffUploadConsumer>();
builder.Services.AddEventConsumer<ExtractUploadConsumer>();
builder.Services.AddEventConsumer<PromoteUploadConsumer>();
builder.Services.AddEventConsumer<PerceptualHashConsumer>();
builder.Services.AddEventConsumer<RenditionConsumer>();
builder.Services.AddEventConsumer<IndexConsumer>();
// Relay last: consumers must have declared and bound their queues before anything is published.
builder.Services.AddOutboxRelay(Required("DAM_DB_SYSTEM_CONNECTION"));

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok("ok"));
app.Run();
