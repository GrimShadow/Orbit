using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>
/// Writes the progress of an enrichment in transactions of their own, so "running" and "waiting to retry" are visible while the worker's own transaction is
/// still open, and survive when it rolls back. Each call uses a fresh scope with the message's tenant.
/// </summary>
public sealed class AiRunTracker(IServiceScopeFactory scopes, IClock clock)
{
    public Task MarkRunningAsync(Guid tenantId, Guid assetId, Guid versionId, CancellationToken ct) =>
        UpdateAsync(tenantId, assetId, versionId, (run, now) => { run.Start(now); }, ct);

    public Task NoteRetryAsync(Guid tenantId, Guid assetId, Guid versionId, string error, CancellationToken ct) =>
        UpdateAsync(tenantId, assetId, versionId, (run, now) => run.NoteRetry(error, now), ct);

    public Task MarkFailedAsync(Guid tenantId, Guid assetId, Guid versionId, string error, CancellationToken ct) =>
        UpdateAsync(tenantId, assetId, versionId, (run, now) => run.Fail(run.Steps, error, now), ct);

    private async Task UpdateAsync(Guid tenantId, Guid assetId, Guid versionId, Action<AiRun, DateTimeOffset> change, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<DamDbContext>();
        var now = clock.UtcNow;
        // The asset may be gone (purged) by the time a late retry or a parked message gets here.
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) return;
        var run = await db.AiRuns.FirstOrDefaultAsync(r => r.AssetId == assetId && r.VersionId == versionId, ct);
        if (run is null) { run = AiRun.Create(tenantId, assetId, versionId, now); db.AiRuns.Add(run); }
        change(run, now);
        run.PullEvents();   // progress is not an event anyone needs
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Enriches assets with AI (spec Step 2.1, ADR-0035). It listens on its own queue, so a slow or broken AI service can never hold up ingest: a new version
/// (<c>asset.version.added</c>) and an explicit request (<c>asset.ai.requested</c>, from a re-run or a backfill). Handling is idempotent per (asset, version).
/// An unreachable provider throws, and the host retries the message with its tiered backoff and then parks it; the run then shows as failed.
/// </summary>
public sealed class EnrichConsumer(EnrichmentPipeline pipeline, AiRunTracker tracker, MessagingOptions messaging, ITenantContext tenant, DamDbContext db, ILogger<EnrichConsumer> log) : EventConsumer
{
    public override string Name => "ai-enrich";
    public override IReadOnlyList<string> RoutingKeys => ["asset.version.added", AssetAiRequested.EventName];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var (assetId, versionId, force) = Parse(message);
        try { await pipeline.RunAsync(assetId, versionId, force, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("AI enrichment of asset {Asset} failed on attempt {Attempt}/{Max}: {Reason}", assetId, message.Attempt, messaging.MaxAttempts, ex.Message);
            await NoteAsync(assetId, versionId, $"Attempt {message.Attempt} of {messaging.MaxAttempts} failed: {Trim(ex.Message)}", ct);
            throw;
        }
    }

    public override async Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct)
    {
        var (assetId, versionId, _) = Parse(message);
        if (await CurrentVersionAsync(assetId, versionId, ct) is { } vid)
            await tracker.MarkFailedAsync(tenant.TenantId, assetId, vid, $"Gave up after {messaging.MaxAttempts} attempts: {Trim(error)}", ct);
    }

    /// <summary>Records the failure where an administrator can see it. If even that cannot be done the original error is the one that matters.</summary>
    private async Task NoteAsync(Guid assetId, Guid? versionId, string error, CancellationToken ct)
    {
        try
        {
            var vid = versionId ?? await CurrentVersionAsync(assetId, null, ct);
            if (vid is { } v) await tracker.NoteRetryAsync(tenant.TenantId, assetId, v, error, ct);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            log.LogWarning(inner, "Could not record the AI retry for asset {Asset}", assetId);
        }
    }

    private async Task<Guid?> CurrentVersionAsync(Guid assetId, Guid? versionId, CancellationToken ct) =>
        versionId ?? await db.Assets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.CurrentVersionId).FirstOrDefaultAsync(ct);

    private static (Guid AssetId, Guid? VersionId, bool Force) Parse(EventEnvelope message)
    {
        using var doc = JsonDocument.Parse(message.Payload);
        var root = doc.RootElement;
        Guid? version = root.TryGetProperty("VersionId", out var v) && v.ValueKind == JsonValueKind.String && v.TryGetGuid(out var g) ? g : null;
        var force = root.TryGetProperty("Force", out var f) && f.ValueKind == JsonValueKind.True;
        return (root.GetProperty("AssetId").GetGuid(), version, force);
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
