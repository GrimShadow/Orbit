using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Ai;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Content;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>Marks a translation job "running" in a transaction of its own, so it is visible while the worker's own transaction (which holds the results) is still open.</summary>
public sealed class TranslationJobTracker(IServiceScopeFactory scopes, IClock clock)
{
    public async Task MarkRunningAsync(Guid tenantId, Guid jobId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<DamDbContext>();
        var job = await db.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.IsFinished) return;
        job.Start(clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Runs a translation job (ADR-0036): for every requested field and language it takes the source text, swaps the tenant's glossary terms for tokens
/// (<see cref="GlossaryProtector"/>), has the provider translate the protected text, checks every token came back and swaps them back. Only a translation that passes
/// is stored, as a pending suggestion (replacing one still waiting for the same field and language); anything else is recorded against its item and nothing is stored.
/// An unreachable provider throws, so the host retries the message with its backoff and then parks it, which fails the job; a provider that refuses one text fails only that item.
/// </summary>
public sealed class TranslationPipeline(
    DamDbContext db, IAiResolver resolver, AiConcurrency gate, TranslationJobTracker tracker, IClock clock, ITenantContext tenant, ILogger<TranslationPipeline> log)
{
    /// <summary>The words an item carries when the provider changed a glossary term or placeholder.</summary>
    public const string ProtectedTermChanged = "The provider changed a protected term";

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.IsFinished) return;   // gone, or a redelivery of finished work

        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == job.AssetId, ct);
        if (asset is null || asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) { FailJob(job, "The asset is no longer available."); return; }

        var ai = await resolver.ResolveAsync(ct);
        if (!ai.IsActive) { FailJob(job, ai.DisabledReason ?? "AI is not enabled for this tenant."); return; }
        if (!ai.Provider.Capabilities.Translate) { FailJob(job, $"The '{ai.ProviderKind}' AI provider cannot translate."); return; }

        await tracker.MarkRunningAsync(tenant.TenantId, job.Id, ct);
        job.Start(clock.UtcNow);

        var schema = await db.MetadataSchemas.AsNoTracking().FirstOrDefaultAsync(s => s.AssetType == asset.Type && s.IsCurrent, ct);
        var languages = TenantLanguages.Parse(await db.Tenants.AsNoTracking().Select(t => t.SettingsJson).FirstOrDefaultAsync(ct));
        var glossary = (await db.GlossaryTerms.AsNoTracking().ToListAsync(ct)).Select(t => t.ToEntry()).ToList();
        var waiting = await db.AiSuggestions.Where(s => s.AssetId == asset.Id && s.Kind == AiSuggestionKinds.Translation && s.Status == AiSuggestionStatuses.Pending).ToListAsync(ct);
        var plan = TranslationPlanner.Plan(job.Fields, job.TargetLanguages, job.SourceLanguage, languages.Default, job.Overwrite,
            name => asset.Metadata.TryGetValue(name, out var v) ? v : null);

        var items = new List<TranslationItem>();
        foreach (var work in plan)
        {
            var item = new TranslationItem { Field = work.Field, Language = work.Language };
            items.Add(item);
            var def = schema?.Fields.FirstOrDefault(f => f.Name == work.Field && f.Type == FieldType.MultilingualText);
            if (def is null) { Mark(item, TranslationItemStatuses.Failed, $"'{work.Field}' is not a multilingual text field of {asset.Type} assets (any more)."); continue; }
            if (!work.ShouldTranslate) { Mark(item, TranslationItemStatuses.Skipped, work.SkipReason); continue; }
            if (work.SourceText!.Length > TranslationRules.SourceLimit(def.MaxLength))
            {
                Mark(item, TranslationItemStatuses.Failed, string.Create(CultureInfo.InvariantCulture,
                    $"The {work.SourceLanguage} text is longer than {TranslationRules.SourceLimit(def.MaxLength):N0} characters, the most that is translated at once."));
                continue;
            }

            try
            {
                var result = await TranslateOneAsync(ai.Provider, work, glossary, ct);
                if (result.Problem is not null) { Mark(item, TranslationItemStatuses.Failed, result.Problem); continue; }
                var text = result.Text!;
                var cap = def.MaxLength ?? MetadataValidator.MaxTextLength;
                if (text.Length > cap) { Mark(item, TranslationItemStatuses.Failed, string.Create(CultureInfo.InvariantCulture, $"The translation is {text.Length:N0} characters, more than the field allows ({cap:N0}).")); continue; }

                Store(asset, work, text, result.Model, waiting);
                Mark(item, TranslationItemStatuses.Ok, null);
            }
            catch (AiNotSupportedException e) { FailJob(job, e.Message); return; }
            catch (AiRequestException e)
            {
                // Only the message goes into the record: it never contains the text or a credential.
                log.LogInformation("Translating {Field} to {Language} for asset {Asset} was refused: {Reason}", work.Field, work.Language, asset.Id, e.Message);
                Mark(item, TranslationItemStatuses.Failed, Trim(e.Message));
            }
        }

        job.Finish(items, clock.UtcNow);
        Announce(job);
        log.LogInformation("Translation job {Job} for asset {Asset}: {Status} ({Ok} translated, {Skipped} skipped, {Failed} failed)",
            job.Id, asset.Id, job.Status, job.Count(TranslationItemStatuses.Ok), job.Count(TranslationItemStatuses.Skipped), job.Count(TranslationItemStatuses.Failed));
    }

    /// <summary>Fails a job outright (the provider gave up, the asset went): used by the consumer when the message is parked.</summary>
    public async Task FailAsync(Guid jobId, string reason, CancellationToken ct)
    {
        var job = await db.TranslationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is { IsFinished: false }) FailJob(job, reason);
    }

    private sealed record Outcome(string? Text, string Model, string? Problem);

    private async Task<Outcome> TranslateOneAsync(IAiProvider provider, TranslationWork work, IReadOnlyList<GlossaryEntry> glossary, CancellationToken ct)
    {
        var sent = GlossaryProtector.Protect(work.SourceText!, glossary, work.Language);
        TextResult reply;
        await gate.WaitAsync(ct);
        try { reply = await provider.TranslateAsync(sent.Text, work.SourceLanguage!, work.Language, Glossary.Empty, ct); }
        finally { gate.Release(); }

        if (string.IsNullOrWhiteSpace(reply.Text)) return new Outcome(null, reply.Model, "The provider returned no text.");
        var restored = GlossaryProtector.Restore(reply.Text, sent);
        return restored.Ok ? new Outcome(restored.Text.Trim(), reply.Model, null) : new Outcome(null, reply.Model, $"{ProtectedTermChanged}: {restored.Problem}");
    }

    private void Store(Asset asset, TranslationWork work, string text, string model, List<AiSuggestion> waiting)
    {
        var now = clock.UtcNow;
        var versionId = asset.CurrentVersionId ?? Guid.Empty;
        var hash = TranslationRules.Hash(work.SourceText!);
        var existing = waiting.FirstOrDefault(s => s.Field == work.Field && s.Language == work.Language);
        if (existing is not null) { existing.Supersede(versionId, work.SourceLanguage!, hash, text, model, now); return; }
        var created = AiSuggestion.CreateTranslation(asset.TenantId, asset.Id, versionId, work.Field, work.Language, work.SourceLanguage!, hash, text, model, now);
        db.AiSuggestions.Add(created);
        waiting.Add(created);
    }

    private void FailJob(TranslationJob job, string reason)
    {
        job.Fail(reason, clock.UtcNow);
        Announce(job);
        log.LogWarning("Translation job {Job} failed: {Reason}", job.Id, reason);
    }

    /// <summary>Tells the rest of the system the job is over (a notification for whoever asked, webhooks). Written to the outbox directly, like <see cref="AssetAiEnriched"/>.</summary>
    private void Announce(TranslationJob job)
    {
        var ready = new AssetTranslationReady(job.AssetId, job.Id, job.RequestedBy, job.Status,
            job.Count(TranslationItemStatuses.Ok), job.Count(TranslationItemStatuses.Skipped), job.Count(TranslationItemStatuses.Failed));
        db.Outbox.Add(new OutboxMessage { EventType = AssetTranslationReady.EventName, Payload = JsonSerializer.Serialize(ready), CorrelationId = Activity.Current?.TraceId.ToString(), CreatedAt = clock.UtcNow });
    }

    private static void Mark(TranslationItem item, string status, string? reason)
    {
        item.Status = status;
        item.Reason = reason is { Length: > 500 } ? reason[..500] : reason;
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}

/// <summary>
/// Works through translation jobs on its own queue (<c>ai-translate</c>), so a slow provider never holds up anything else (ADR-0036). Handling is idempotent per job. An unreachable
/// provider throws and the host retries with its tiered backoff; when the message is finally parked the job is failed, so the person who asked is told.
/// </summary>
public sealed class TranslationConsumer(TranslationPipeline pipeline, MessagingOptions messaging, ILogger<TranslationConsumer> log) : EventConsumer
{
    public override string Name => "ai-translate";
    public override IReadOnlyList<string> RoutingKeys => [AssetTranslationRequested.EventName];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        try { await pipeline.RunAsync(JobIdOf(message), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Translation job {Job} failed on attempt {Attempt}/{Max}: {Reason}", JobIdOf(message), message.Attempt, messaging.MaxAttempts, ex.Message);
            throw;
        }
    }

    public override Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct) =>
        pipeline.FailAsync(JobIdOf(message), $"Gave up after {messaging.MaxAttempts} attempts: {(error.Length > 400 ? error[..400] : error)}", ct);

    private static Guid JobIdOf(EventEnvelope message)
    {
        using var doc = JsonDocument.Parse(message.Payload);
        return doc.RootElement.GetProperty("JobId").GetGuid();
    }
}
