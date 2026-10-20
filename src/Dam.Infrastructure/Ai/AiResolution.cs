using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>The provider that does nothing: used when AI is off for a tenant, so that nothing can reach an external service by accident.</summary>
public sealed class NullProvider(string? reason = null) : IAiProvider
{
    public string Kind => AiProviderKinds.None;
    public AiCapabilities Capabilities => AiCapabilities.None;
    public Task<AiHealth> CheckHealthAsync(CancellationToken ct) => Task.FromResult(new AiHealth("disabled", reason));

    private AiNotSupportedException No(string what) => new(what, Kind);
    public Task<TagResult> TagImageAsync(Stream image, TagOptions options, CancellationToken ct) => throw No("tagging");
    public Task<OcrResult> OcrAsync(Stream image, OcrOptions options, CancellationToken ct) => throw No("OCR");
    public Task<TranscriptResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct) => throw No("transcription");
    public Task<EmbeddingResult> EmbedTextAsync(string text, TextEmbedOptions options, CancellationToken ct) => throw No("text embeddings");
    public Task<EmbeddingBatchResult> EmbedTextsAsync(IReadOnlyList<string> texts, TextEmbedOptions options, CancellationToken ct) => throw No("text embeddings");
    public Task<EmbeddingResult> EmbedImageAsync(Stream image, CancellationToken ct) => throw No("image embeddings");
    public Task<LanguageResult> DetectLanguageAsync(string text, CancellationToken ct) => throw No("language detection");
    public Task<TextResult> TranslateAsync(string text, string from, string to, Glossary glossary, CancellationToken ct) => throw No("translation");
    public Task<TextResult> SummariseAsync(string text, SummaryOptions options, CancellationToken ct) => throw No("summaries");
}

/// <summary>The provider instances of this process (they hold HTTP connections and the cool-down state, so there is one of each).</summary>
public sealed class AiProviders(AiOptions options, SelfHostedProvider selfHosted, OpenAiCompatibleProvider openAi)
{
    public AiOptions Options { get; } = options;

    public IAiProvider Get(string kind) => kind switch
    {
        AiProviderKinds.SelfHosted => selfHosted,
        AiProviderKinds.OpenAiCompatible => openAi,
        _ => new NullProvider(),
    };
}

/// <summary>
/// Decides, for the ambient tenant, which provider runs and whether it may (ADR-0035). A tenant has AI off until an administrator turns it on, whatever
/// the deployment default is; with it off the provider handed back is a <see cref="NullProvider"/>, so no code path can send content out.
/// </summary>
public sealed class AiResolver(DamDbContext db, AiOptions options, AiProviders providers) : IAiResolver
{
    private Task<AiSettings?>? _settings;

    public string DefaultProvider => options.DefaultProvider;

    public IReadOnlyList<(string Kind, bool Configured, bool SendsDataOffSite)> Providers() =>
        AiProviderKinds.All.Select(k => (k, options.IsConfigured(k), options.SendsDataOffSite(k))).ToList();

    private Task<AiSettings?> SettingsAsync(CancellationToken ct) => _settings ??= db.AiSettings.AsNoTracking().FirstOrDefaultAsync(ct);

    public Task<AiTenantContext> ResolveAsync(CancellationToken ct) => BuildAsync(requireEnabled: true, ct);
    public Task<AiTenantContext> ResolveForTestAsync(CancellationToken ct) => BuildAsync(requireEnabled: false, ct);

    private async Task<AiTenantContext> BuildAsync(bool requireEnabled, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var kind = s?.Provider ?? options.DefaultProvider;
        var enabled = s?.Enabled ?? false;
        var configured = options.IsConfigured(kind);
        var features = s?.Features ?? new AiFeatureFlags();
        var offSite = options.SendsDataOffSite(kind);

        string? reason = null;
        if (kind == AiProviderKinds.None) reason = "No AI provider is selected.";
        else if (!configured) reason = $"The '{kind}' AI provider is not configured in this deployment.";
        else if (requireEnabled && !enabled) reason = "AI is not enabled for this tenant.";

        var provider = reason is null ? providers.Get(kind) : new NullProvider(reason);
        return new AiTenantContext(kind, enabled, configured, features, s?.OcrLanguage, provider, reason, offSite);
    }
}

/// <summary>Turns a search phrase into the two query vectors semantic search needs (ADR-0035), reusing recent answers so paging does not call the provider again.</summary>
public sealed class SemanticQueryEmbedder(IAiResolver resolver, AiOptions options, QueryEmbeddingCache cache, ILogger<SemanticQueryEmbedder> log) : ISemanticQueryEmbedder
{
    public async Task<SemanticQueryVectors> EmbedAsync(string query, CancellationToken ct)
    {
        var ai = await resolver.ResolveAsync(ct);
        if (!ai.Enabled) return SemanticQueryVectors.Unavailable("ai_disabled");
        if (!ai.IsActive) return SemanticQueryVectors.Unavailable("provider_not_configured");
        if (!ai.Features.Embeddings) return SemanticQueryVectors.Unavailable("embeddings_disabled");

        var text = query.Length > 1000 ? query[..1000] : query;
        var caps = ai.Provider.Capabilities;
        var clipTask = caps.ImageEmbeddings ? OneAsync(ai, text, TextEmbeddingKind.Clip, options.ImageDim, ct) : Task.FromResult<float[]?>(null);
        var textTask = caps.TextEmbeddings ? OneAsync(ai, text, TextEmbeddingKind.Text, options.TextDim, ct) : Task.FromResult<float[]?>(null);
        await Task.WhenAll(clipTask, textTask);
        var (clip, vec) = (await clipTask, await textTask);
        if (clip is null && vec is null)
            return SemanticQueryVectors.Unavailable(!caps.ImageEmbeddings && !caps.TextEmbeddings ? "embeddings_unsupported" : "provider_unavailable");
        return new SemanticQueryVectors(clip, vec, null, options.ImageFloor, options.TextFloor(ai.ProviderKind));
    }

    private async Task<float[]?> OneAsync(AiTenantContext ai, string text, TextEmbeddingKind kind, int dim, CancellationToken ct)
    {
        var key = QueryEmbeddingCache.KeyFor(ai.ProviderKind, kind, text);
        if (cache.TryGet(key, out var hit)) return hit;
        try
        {
            var r = await ai.Provider.EmbedTextAsync(text, new TextEmbedOptions(kind, TextEmbeddingRole.Query), ct);
            if (r.Dim != dim)
            {
                log.LogWarning("The {Kind} embedding model returned {Got}-dimensional vectors; {Want} are configured. Semantic search is skipped for that leg", kind, r.Dim, dim);
                return null;
            }
            cache.Set(key, r.Vector);
            return r.Vector;
        }
        catch (AiException ex)
        {
            log.LogWarning("Embedding a search phrase failed ({Kind}): {Reason}", kind, ex.Message);
            return null;
        }
    }
}

/// <summary>A small, short-lived memory of query vectors (the same phrase is embedded again for every page and every keystroke-pause).</summary>
public sealed class QueryEmbeddingCache
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (float[] Vector, DateTimeOffset At)> _entries = new();

    public static string KeyFor(string provider, TextEmbeddingKind kind, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}|{kind}|{text}")));

    public bool TryGet(string key, out float[]? vector)
    {
        if (_entries.TryGetValue(key, out var e) && DateTimeOffset.UtcNow - e.At < Ttl) { vector = e.Vector; return true; }
        vector = null;
        return false;
    }

    public void Set(string key, float[] vector)
    {
        if (_entries.Count >= MaxEntries)
            foreach (var old in _entries.OrderBy(e => e.Value.At).Take(MaxEntries / 4).Select(e => e.Key).ToList()) _entries.TryRemove(old, out _);
        _entries[key] = (vector, DateTimeOffset.UtcNow);
    }
}
