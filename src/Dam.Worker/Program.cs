using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Ai;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Delivery;
using Dam.Infrastructure.Knowledge;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Notifications;
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
builder.Services.AddDamNotifications(cfg);
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
// Chapters (a PDF's bookmarks, an HTML file's headings) of knowledge documents; heavy parsing runs outside any long transaction (ADR-0038, ADR-0039).
builder.Services.AddDamKnowledgeWorker();
// AI enrichment (tags, OCR, transcripts, vectors) runs on its own queue, so a slow or absent AI service never holds up ingest (ADR-0035); also the index rebuild consumer.
builder.Services.AddDamAiWorker(cfg);
// Webhook fan-out and notifications (email, in-app, chat) both listen to the same public domain events, each on its own queue.
builder.Services.AddDamNotificationConsumers();
// Relay last: consumers must have declared and bound their queues before anything is published.
builder.Services.AddOutboxRelay(Required("DAM_DB_SYSTEM_CONNECTION"));

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok("ok"));
app.Run();
