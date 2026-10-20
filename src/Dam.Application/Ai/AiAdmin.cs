using System.Diagnostics;
using System.Text.RegularExpressions;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Ai;
using FluentValidation;

namespace Dam.Application.Ai;

// ----------------------------------------------------------------------------------------------------- settings

internal static partial class AiSettingsMapping
{
    [GeneratedRegex("^[a-z]{2,3}([-_][a-z]{2,4})?$")]
    private static partial Regex LanguageHint();

    public static bool IsLanguageHint(string? value) => value is null || LanguageHint().IsMatch(value.Trim().ToLowerInvariant());

    public static AiSettingsDto ToDto(AiSettings? s, IAiResolver resolver)
    {
        var features = s?.Features ?? new AiFeatureFlags();
        var effective = s?.Provider ?? resolver.DefaultProvider;
        var providers = resolver.Providers();
        var offSite = providers.FirstOrDefault(p => p.Kind == effective).SendsDataOffSite;
        return new AiSettingsDto(
            s?.Enabled ?? false, s?.Provider, resolver.DefaultProvider, effective, offSite, AiMapping.ToDto(features), s?.OcrLanguage,
            providers.Select(p => new AiProviderInfoDto(p.Kind, p.Configured, p.SendsDataOffSite, Describe(p.Kind))).ToList(), s?.UpdatedBy, s?.UpdatedAt);
    }

    private static string Describe(string kind) => kind switch
    {
        AiProviderKinds.SelfHosted => "Models run in this installation (the AI sidecar). Nothing leaves your network.",
        AiProviderKinds.OpenAiCompatible => "A cloud or self-run service with an OpenAI-compatible API. Pictures and audio are sent to it.",
        _ => "No AI.",
    };
}

/// <summary>The tenant's AI settings. Until an administrator saves some, AI is off. Needs <c>ai.manage</c>.</summary>
public sealed record GetAiSettingsQuery : IQuery<AiSettingsDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AiManage;
}

public sealed class GetAiSettingsHandler(IRepository<AiSettings> settings, IQueryExecutor q, IAiResolver resolver) : IRequestHandler<GetAiSettingsQuery, AiSettingsDto>
{
    public async Task<Result<AiSettingsDto>> Handle(GetAiSettingsQuery r, CancellationToken ct) =>
        AiSettingsMapping.ToDto(await q.FirstOrDefaultAsync(settings.Query(), ct), resolver);
}

/// <summary>
/// Saves the tenant's AI settings (all fields; this is a replacement). Turning AI on with a provider that sends pictures and audio off-site (a
/// cloud service) needs <paramref name="AcknowledgeOffSite"/> to be true. A provider that is not configured for the deployment cannot be enabled.
/// </summary>
public sealed record UpdateAiSettingsCommand(AiSettingsInput Input, bool AcknowledgeOffSite) : ICommand<AiSettingsDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AiManage;
}

public sealed class UpdateAiSettingsValidator : AbstractValidator<UpdateAiSettingsCommand>
{
    public UpdateAiSettingsValidator()
    {
        RuleFor(x => x.Input.Provider).Must(p => p is null || AiProviderKinds.IsKnown(p.Trim().ToLowerInvariant()))
            .WithMessage($"Provider must be one of: {string.Join(", ", AiProviderKinds.All)} (or empty for the deployment default).").OverridePropertyName("provider");
        RuleFor(x => x.Input.OcrLanguage).Must(AiSettingsMapping.IsLanguageHint).WithMessage("Use a language code such as en, hi or pt-br.").OverridePropertyName("ocrLanguage");
    }
}

public sealed class UpdateAiSettingsHandler(
    IRepository<AiSettings> settings, IQueryExecutor q, IAiResolver resolver, IAuthorizationService authz, ITenantContext tenant, IEventCollector events, IClock clock)
    : IRequestHandler<UpdateAiSettingsCommand, AiSettingsDto>
{
    public async Task<Result<AiSettingsDto>> Handle(UpdateAiSettingsCommand c, CancellationToken ct)
    {
        var input = c.Input;
        var provider = string.IsNullOrWhiteSpace(input.Provider) ? null : input.Provider.Trim().ToLowerInvariant();
        var effective = provider ?? resolver.DefaultProvider;
        var info = resolver.Providers().First(p => p.Kind == effective);
        var features = new AiFeatureFlags
        {
            Tags = input.Features?.Tags ?? true, Ocr = input.Features?.Ocr ?? true,
            Transcribe = input.Features?.Transcribe ?? true, Embeddings = input.Features?.Embeddings ?? true, Summaries = input.Features?.Summaries ?? true,
        };

        var existing = await q.FirstOrDefaultAsync(settings.Query(), ct);
        if (input.Enabled)
        {
            if (effective == AiProviderKinds.None) return Error.Validation(ContentMapping.Fail("provider", "Choose a provider: the deployment has none by default."));
            if (!info.Configured) return Error.Validation(ContentMapping.Fail("provider", $"The '{effective}' provider is not configured in this deployment. {ConfigHint(effective)}"));
            var alreadyOffSite = existing is { Enabled: true } && (existing.Provider ?? resolver.DefaultProvider) == effective;
            if (info.SendsDataOffSite && !c.AcknowledgeOffSite && !alreadyOffSite)
                return Error.Validation(ContentMapping.Fail("acknowledgeOffSite",
                    "This provider sends the tenant's pictures and audio to an external service. Confirm you are entitled to do that (consent, data-protection obligations) by setting acknowledgeOffSite to true."));
        }

        var me = (await authz.GetProfileAsync(ct)).UserId;
        var row = existing;
        if (row is null) { row = AiSettings.Create(tenant.TenantId, clock.UtcNow); settings.Add(row); }
        row.Update(input.Enabled, provider, features, input.OcrLanguage, me == Guid.Empty ? null : me, clock.UtcNow);
        events.Raise(new AiSettingsChanged(tenant.TenantId, row.Enabled, row.Provider, features.Tags, features.Ocr, features.Transcribe, features.Embeddings, features.Summaries));
        return AiSettingsMapping.ToDto(row, resolver);
    }

    private static string ConfigHint(string kind) => kind == AiProviderKinds.SelfHosted
        ? "Set DAM_AI_SIDECAR_URL."
        : "Set DAM_AI_BASE_URL and DAM_AI_API_KEY.";
}

// -------------------------------------------------------------------------------------------------------- status

/// <summary>Provider health, models, capabilities, what has been enriched and what is waiting. Needs <c>ai.manage</c>. Probes the provider (a few seconds at most).</summary>
public sealed record GetAiStatusQuery : IQuery<AiStatusDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AiManage;
}

public sealed class GetAiStatusHandler(
    IRepository<Asset> assets, IRepository<AiRun> runs, IQueryExecutor q, IAiResolver resolver, IVectorIndexInspector index) : IRequestHandler<GetAiStatusQuery, AiStatusDto>
{
    public async Task<Result<AiStatusDto>> Handle(GetAiStatusQuery r, CancellationToken ct)
    {
        var ai = await resolver.ResolveForTestAsync(ct);
        AiHealth health;
        if (ai.Provider.Kind == AiProviderKinds.None) health = new AiHealth(ai.Configured ? "disabled" : "unconfigured", ai.DisabledReason);
        else
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(6));
            try { health = await ai.Provider.CheckHealthAsync(probe.Token); }
            catch (Exception ex) when (ex is AiException or OperationCanceledException or HttpRequestException)
            {
                health = new AiHealth("down", ex is OperationCanceledException ? "The provider did not answer in time." : ex.Message);
            }
        }

        var live = assets.Query().Where(a => a.Status != AssetStatuses.Deleted && a.Status != AssetStatuses.Blocked && a.CurrentVersionId != null);
        var eligible = await q.CountAsync(live, ct);
        // Runs count only while they describe the asset's current file.
        var current = from run in runs.Query() join a in live on run.AssetId equals a.Id where run.VersionId == a.CurrentVersionId select run.Status;
        var byStatus = (await q.ToListAsync(current, ct)).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        int N(string s) => byStatus.GetValueOrDefault(s);
        var counts = new AiCountsDto(eligible, N(AiRunStatuses.Succeeded), N(AiRunStatuses.Pending), N(AiRunStatuses.Running), N(AiRunStatuses.Failed), N(AiRunStatuses.Skipped),
            Math.Max(0, eligible - byStatus.Values.Sum()));
        var v = await index.InspectAsync(ct);

        return new AiStatusDto(ai.Enabled, ai.ProviderKind, ai.Configured, AiMapping.ToDto(health), AiMapping.ToDto(ai.Provider.Capabilities), counts,
            counts.Pending + counts.Running, new AiVectorIndexDto(v.TextDim, v.ImageDim, v.LiveTextDim, v.LiveImageDim, v.NeedsReindex));
    }
}

// ---------------------------------------------------------------------------------------------------- capabilities

/// <summary>What AI can do for the tenant, for any signed-in user (authentication only): the console uses it to offer Smart search and show AI panels. Cheap: settings
/// and configuration only, no provider call; the index-readiness check is remembered for a few seconds per tenant.</summary>
public sealed record GetAiCapabilitiesQuery : IQuery<AiCapabilitiesDto>;

public sealed class GetAiCapabilitiesHandler(IAiResolver resolver, IVectorIndexInspector index, ITenantContext tenant, IClock clock) : IRequestHandler<GetAiCapabilitiesQuery, AiCapabilitiesDto>
{
    private static readonly TimeSpan Remember = TimeSpan.FromSeconds(5);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset At, bool Ready)> Readiness = new();

    public async Task<Result<AiCapabilitiesDto>> Handle(GetAiCapabilitiesQuery r, CancellationToken ct)
    {
        var ai = await resolver.ResolveAsync(ct);
        var caps = ai.Provider.Capabilities;
        var active = ai.IsActive;
        var f = ai.Features;
        var text = active && f.Embeddings && caps.TextEmbeddings;
        var image = active && f.Embeddings && caps.ImageEmbeddings;
        var enrichment = active && (f.Tags || f.Ocr || f.Transcribe || f.Embeddings || f.Summaries);
        var semantic = (text || image) && await IndexReadyAsync(ct);
        return new AiCapabilitiesDto(active, semantic, enrichment, text, image, ai.ProviderKind, active && ai.SendsDataOffSite,
            Translate: active && caps.Translate, Summarise: active && f.Summaries && caps.Summarise);
    }

    private async Task<bool> IndexReadyAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (Readiness.TryGetValue(tenant.TenantId, out var seen) && now - seen.At < Remember) return seen.Ready;
        var ready = !(await index.InspectAsync(ct)).NeedsReindex;
        Readiness[tenant.TenantId] = (now, ready);
        return ready;
    }
}

// ------------------------------------------------------------------------------------------------------ backfill

/// <param name="Types">Asset types to include; null for image, video, audio, document and manual.</param>
/// <param name="OnlyMissing">Skip assets whose current version already has results or is queued.</param>
public sealed record AiBackfillCriteria(IReadOnlyList<string>? Types, Guid? FolderId, bool IncludeSubfolders = true, bool OnlyMissing = true);

/// <summary>
/// Queues enrichment for existing assets: marks each one's run as pending and asks the worker (through the outbox) to enrich it. The worker does the
/// work at its own pace, so a big backfill never floods the provider. Repeat the call (OnlyMissing) to continue where the last stopped.
/// </summary>
public sealed class AiBackfillService(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<AiRun> runs, IQueryExecutor q, ITenantContext tenant, IEventCollector events, IClock clock)
{
    public static readonly IReadOnlyList<string> DefaultTypes = [AssetTypes.All[0], AssetTypes.All[1], AssetTypes.All[2], AssetTypes.All[3], AssetTypes.All[4]];
    public const int MaxPerCall = 5000;

    public async Task<AiBackfillResultDto> EnqueueAsync(AiBackfillCriteria c, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxPerCall);
        var types = (c.Types is { Count: > 0 } ? c.Types : DefaultTypes).ToList();
        var query = assets.Query().Where(a => a.Status != AssetStatuses.Deleted && a.Status != AssetStatuses.Blocked && a.CurrentVersionId != null && types.Contains(a.Type));
        if (c.FolderId is { } fid)
        {
            var all = await q.ToListAsync(folders.Query(), ct);
            var root = all.FirstOrDefault(f => f.Id == fid);
            var ids = root is null ? [fid] : c.IncludeSubfolders ? all.Where(f => Paths.IsSelfOrDescendant(root.Path, f.Path)).Select(f => f.Id).ToList() : [fid];
            query = query.Where(a => a.FolderId != null && ids.Contains(a.FolderId.Value));
        }
        if (c.OnlyMissing)
            query = query.Where(a => !runs.Query().Any(r => r.AssetId == a.Id && r.VersionId == a.CurrentVersionId
                && (r.Status == AiRunStatuses.Succeeded || r.Status == AiRunStatuses.Pending || r.Status == AiRunStatuses.Running)));

        var matched = await q.CountAsync(query, ct);
        var batch = await q.ToListAsync(query.OrderBy(a => a.Id).Select(a => new { a.Id, Version = a.CurrentVersionId!.Value }).Take(limit), ct);
        if (batch.Count == 0) return new AiBackfillResultDto(matched, 0, 0);

        var assetIds = batch.Select(b => b.Id).ToList();
        var existing = (await q.ToListAsync(runs.Query().Where(r => assetIds.Contains(r.AssetId)), ct)).ToLookup(r => (r.AssetId, r.VersionId));
        var now = clock.UtcNow;
        foreach (var b in batch)
        {
            var run = existing[(b.Id, b.Version)].FirstOrDefault();
            if (run is null) runs.Add(AiRun.Create(tenant.TenantId, b.Id, b.Version, now));
            else run.Queue();
            events.Raise(new AssetAiRequested(b.Id, b.Version, Force: false));
        }
        return new AiBackfillResultDto(matched, batch.Count, matched - batch.Count);
    }
}

/// <summary>Queues AI for existing assets (see <see cref="AiBackfillService"/>). At most 5,000 per call; the default limit is 500. Needs <c>ai.manage</c>. 409 when AI is off.</summary>
public sealed record AiBackfillCommand(AiBackfillInput Input) : ICommand<AiBackfillResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AiManage;
}

public sealed class AiBackfillValidator : AbstractValidator<AiBackfillCommand>
{
    public AiBackfillValidator()
    {
        RuleFor(x => x.Input.Limit).InclusiveBetween(1, AiBackfillService.MaxPerCall).When(x => x.Input.Limit is not null).OverridePropertyName("limit");
        RuleFor(x => x.Input.Types).Must(t => t is null || t.All(AssetTypes.IsKnown)).WithMessage($"Types must be among: {string.Join(", ", AssetTypes.All)}.").OverridePropertyName("types");
    }
}

public sealed class AiBackfillHandler(AiBackfillService service, IAiResolver resolver) : IRequestHandler<AiBackfillCommand, AiBackfillResultDto>
{
    public async Task<Result<AiBackfillResultDto>> Handle(AiBackfillCommand c, CancellationToken ct)
    {
        var ai = await resolver.ResolveAsync(ct);
        if (!ai.IsActive) return Error.Conflict(ai.DisabledReason ?? "AI is not enabled for this tenant. Turn it on in the AI settings first.");
        var i = c.Input;
        return await service.EnqueueAsync(new AiBackfillCriteria(i.Types, i.FolderId, i.IncludeSubfolders ?? true, i.OnlyMissing ?? true), i.Limit ?? 500, ct);
    }
}

// ------------------------------------------------------------------------------------------------------- reindex

/// <summary>Asks the worker to rebuild the tenant's search index (needed once after the vector size or the AI feature set changed; see the status's vectorIndex). Needs <c>ai.manage</c>.</summary>
public sealed record RequestReindexCommand : ICommand<AiReindexDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AiManage;
}

public sealed class RequestReindexHandler(ITenantContext tenant, IEventCollector events) : IRequestHandler<RequestReindexCommand, AiReindexDto>
{
    public Task<Result<AiReindexDto>> Handle(RequestReindexCommand c, CancellationToken ct)
    {
        events.Raise(new SearchReindexRequested(tenant.TenantId));
        return Task.FromResult<Result<AiReindexDto>>(new AiReindexDto(true, "The search index will be rebuilt in the background; search keeps working meanwhile."));
    }
}

// ------------------------------------------------------------------------------------------------------------ test

/// <summary>
/// Proves the provider works: embeds a tiny built-in picture and a phrase and reports timings and vector sizes. Sends nothing of the tenant's, so it
/// works before AI is switched on. Needs <c>ai.manage</c>.
/// </summary>
public sealed record AiTestQuery : IQuery<AiTestResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AiManage;
}

public sealed class AiTestHandler(IAiResolver resolver, AiLimits limits) : IRequestHandler<AiTestQuery, AiTestResultDto>
{
    public const string Phrase = "a red car parked on a sunny beach";

    /// <summary>A 32 x 32 PNG: a red square on a blue background.</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAMUlEQVR42mPQCDhBU8QwasGoBUPYgjtyciShUQtGLRi1YNSCUQuGpwWjVeaoBSPIAgAiotg9Sdp0NAAAAABJRU5ErkJggg==");

    public async Task<Result<AiTestResultDto>> Handle(AiTestQuery r, CancellationToken ct)
    {
        var ai = await resolver.ResolveForTestAsync(ct);
        var steps = new List<AiTestStepDto>();
        var total = Stopwatch.StartNew();
        if (ai.Provider.Kind == AiProviderKinds.None)
            return new AiTestResultDto(ai.ProviderKind, false, 0, [new AiTestStepDto("provider", false, 0, ai.DisabledReason ?? "No AI provider is configured.")]);

        async Task Step(string name, bool supported, Func<Task<string>> body)
        {
            if (!supported) { steps.Add(new AiTestStepDto(name, true, 0, "Skipped: this provider does not offer it.")); return; }
            var sw = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var detail = await body();
                steps.Add(new AiTestStepDto(name, true, (int)sw.ElapsedMilliseconds, detail));
            }
            catch (Exception ex) when (ex is AiException or OperationCanceledException or HttpRequestException)
            {
                steps.Add(new AiTestStepDto(name, false, (int)sw.ElapsedMilliseconds, ex is OperationCanceledException ? "Timed out." : ex.Message));
            }
        }

        var p = ai.Provider;
        var c = p.Capabilities;
        await Step("health", true, async () =>
        {
            var h = await p.CheckHealthAsync(ct);
            if (!h.IsUsable) throw new AiUnavailableException($"The provider reports '{h.Status}'. {h.Detail}");
            return h.Status + (h.Detail is null ? "" : ": " + h.Detail);
        });
        await Step("embed-image", c.ImageEmbeddings, async () =>
        {
            await using var png = new MemoryStream(TinyPng, writable: false);
            var e = await p.EmbedImageAsync(png, ct);
            if (e.Dim != limits.ImageDim) throw new AiRequestException($"The model returned {e.Dim}-dimensional image vectors but DAM_AI_IMAGE_DIM is {limits.ImageDim}.");
            return $"{e.Model}, {e.Dim} dimensions";
        });
        await Step("embed-text", c.TextEmbeddings, async () =>
        {
            var e = await p.EmbedTextAsync(Phrase, new TextEmbedOptions(TextEmbeddingKind.Text, TextEmbeddingRole.Passage), ct);
            if (e.Dim != limits.TextDim) throw new AiRequestException($"The model returned {e.Dim}-dimensional text vectors but DAM_AI_TEXT_DIM is {limits.TextDim}.");
            return $"{e.Model}, {e.Dim} dimensions";
        });
        await Step("embed-text-for-images", c.ImageEmbeddings, async () =>
        {
            var e = await p.EmbedTextAsync(Phrase, new TextEmbedOptions(TextEmbeddingKind.Clip, TextEmbeddingRole.Query), ct);
            if (e.Dim != limits.ImageDim) throw new AiRequestException($"The model returned {e.Dim}-dimensional vectors but DAM_AI_IMAGE_DIM is {limits.ImageDim}.");
            return $"{e.Model}, {e.Dim} dimensions";
        });
        return new AiTestResultDto(ai.ProviderKind, steps.All(s => s.Ok), (int)total.ElapsedMilliseconds, steps);
    }
}

/// <summary>The vector sizes the deployment is configured for (DAM_AI_TEXT_DIM, DAM_AI_IMAGE_DIM). Registered by the infrastructure.</summary>
public sealed record AiLimits(int TextDim, int ImageDim);
