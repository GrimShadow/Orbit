using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Scheduling;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Ai;

/// <summary>
/// Queues enrichment for existing assets whose current version has no results yet (ADR-0035): for one tenant (<c>tenantId</c> in the job's data, run it by
/// hand through <see cref="IJobScheduler.ScheduleOnceAsync"/>), or, with no data, for every tenant that has AI switched on. It only queues work, a chunk
/// (DAM_AI_BACKFILL_CHUNK) per tenant per run; the worker's own concurrency limit sets the pace. With <c>DAM_AI_BACKFILL_CRON</c>
/// set the scheduler runs it on that schedule as a catch-up (assets uploaded while AI was down or off); unset, it only runs when asked.
/// </summary>
public sealed class AiBackfillJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, AiOptions options, ILogger<AiBackfillJob> log)
    : CrossTenantJob(scopeFactory, systemDb, log), IScheduledJob
{
    public const string JobKey = "ai.backfill";
    public string Key => JobKey;

    private const int MaxChunksPerRun = 25;
    private const string EnabledTenantsSql = "SELECT tenant_id, tenant_id FROM ai_settings WHERE enabled";

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var leads = data.TryGetValue("tenantId", out var one) && Guid.TryParse(one, out var tenant)
            ? [new AssetLead(tenant, tenant)]
            : await FindAsync(EnabledTenantsSql, ct);

        var total = 0;
        for (var chunk = 0; chunk < MaxChunksPerRun; chunk++)
        {
            var queuedNow = 0;
            await ForEachScopedAsync(leads, async (sp, _, lead, c) =>
            {
                if (!(await sp.GetRequiredService<IAiResolver>().ResolveAsync(c)).IsActive) return false;
                var service = sp.GetRequiredService<Dam.Application.Ai.AiBackfillService>();
                var queued = await service.EnqueueAsync(new Dam.Application.Ai.AiBackfillCriteria(null, null), options.BackfillChunk, c);
                queuedNow += queued.Enqueued;
                return queued.Enqueued > 0;
            }, ct);
            total += queuedNow;
            if (queuedNow == 0) break;
        }
        if (total > 0) log.LogInformation("AI backfill queued {Count} asset(s)", total);
    }
}

/// <summary>Rebuilds a tenant's search index when asked (<c>search.reindex.requested</c>): needed once after the vector sizes change.</summary>
public sealed class SearchReindexConsumer(ISearchIndexAdmin admin, ILogger<SearchReindexConsumer> log) : EventConsumer
{
    public override string Name => "search-reindex";
    public override IReadOnlyList<string> RoutingKeys => [SearchReindexRequested.EventName];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var count = await admin.ReindexAsync(ct);
        log.LogInformation("Reindexed {Count} assets for tenant {Tenant}", count, message.TenantId);
    }
}

public static class AiRegistration
{
    /// <summary>
    /// What every host that touches AI needs: the options (env DAM_AI_*), the two providers (one instance each: they own HTTP connections and the cool-down),
    /// the per-tenant resolver and the semantic query embedder.
    /// </summary>
    public static IServiceCollection AddDamAi(this IServiceCollection services, IConfiguration cfg)
    {
        var options = AiOptions.Read(cfg);
        services.TryAddSingleton(options);
        services.TryAddSingleton(new Dam.Application.Ai.AiLimits(options.TextDim, options.ImageDim));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SelfHostedProvider>();
        services.TryAddSingleton<OpenAiCompatibleProvider>();
        services.TryAddSingleton<AiProviders>();
        services.TryAddSingleton<QueryEmbeddingCache>();
        services.TryAddScoped<IAiResolver, AiResolver>();
        services.TryAddScoped<ISemanticQueryEmbedder, SemanticQueryEmbedder>();
        services.TryAddScoped<Dam.Application.Ai.AiBackfillService>();
        return services;
    }

    /// <summary>The worker side: the enrichment pipeline and its consumer, and the index rebuild consumer. Needs rendering, search and messaging registered too.</summary>
    public static IServiceCollection AddDamAiWorker(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDamAi(cfg);
        services.TryAddSingleton<AiConcurrency>();
        services.TryAddSingleton<AiRunTracker>();
        services.TryAddSingleton<AudioExtractor>();
        services.TryAddScoped<EnrichmentPipeline>();
        services.AddEventConsumer<EnrichConsumer>();
        services.AddEventConsumer<SearchReindexConsumer>();
        return services;
    }

    /// <summary>The scheduler side: the catch-up backfill job (and a cron for it when DAM_AI_BACKFILL_CRON is set; see the scheduler's Program).</summary>
    public static IServiceCollection AddDamAiScheduling(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDamAi(cfg);
        services.AddScheduledJob<AiBackfillJob>();
        return services;
    }
}
