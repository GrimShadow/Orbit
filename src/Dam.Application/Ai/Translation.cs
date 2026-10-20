using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Shared.Ai;
using FluentValidation;

namespace Dam.Application.Ai;

internal static class TranslationMapping
{
    public static TranslationJobDto ToDto(TranslationJob j) =>
        new(j.Id, j.AssetId, j.Status, j.Fields, j.TargetLanguages, j.SourceLanguage, j.Overwrite, j.RequestedBy, j.CreatedAt, j.FinishedAt,
            j.Items.Select(i => new TranslationItemDto(i.Field, i.Language, i.Status, i.Reason)).ToList(), j.Error.Length == 0 ? null : j.Error);

    /// <summary>The words for an error, for a note about one suggestion in a batch.</summary>
    public static string Describe(Error e) =>
        e.FieldErrors is { Count: > 0 } f ? string.Join(" ", f.SelectMany(kv => kv.Value)) : e.Message;
}

// ----------------------------------------------------------------------------------------------------------- accept

/// <summary>
/// Puts a reviewed translation into the asset (ADR-0036): the text goes into the multilingual field under its language and every other language stays as it
/// is, through the same metadata checks, audit event and re-index as a manual edit. Refuses (409) to overwrite text the language already has unless asked.
/// </summary>
public sealed class TranslationAcceptor(
    AssetMetadataBuilder builder, IRepository<MetadataSchema> schemas, IQueryExecutor q, IAuthorizationService authz, IEventCollector events, IClock clock)
{
    /// <returns>Null when the translation was applied and the suggestion accepted; otherwise why not (nothing is changed).</returns>
    public async Task<Error?> ApplyAsync(Asset asset, AiSuggestion s, string? text, bool replace, CancellationToken ct)
    {
        if (s.Field is null || s.Language is null) return Error.Conflict("This translation does not say which field and language it is for.");
        var schema = await q.FirstOrDefaultAsync(schemas.Query().Where(x => x.AssetType == asset.Type && x.IsCurrent), ct);
        var field = schema?.Fields.FirstOrDefault(f => f.Name == s.Field && f.Type == FieldType.MultilingualText);
        if (field is null) return Error.Conflict($"The {asset.Type} metadata schema no longer has a multilingual field '{s.Field}'.");

        var final = (text ?? s.Value).Trim();
        if (final.Length == 0) return Error.Validation(ContentMapping.Fail("text", "The translation cannot be empty."));
        var language = s.Language;

        JsonElement? current = asset.Metadata.TryGetValue(field.Name, out var existing) && existing.ValueKind == JsonValueKind.Object ? existing : null;
        if (!replace && TranslationRules.TextOf(current, language) is not null)
            return Error.Conflict($"'{field.Name}' already has text in '{language}'. Accept with replace set to true to overwrite it.");

        // The field's other languages are carried over untouched, in their order.
        var languages = new Dictionary<string, JsonElement>();
        if (current is { } obj)
            foreach (var p in obj.EnumerateObject())
                if (!string.Equals(p.Name, language, StringComparison.OrdinalIgnoreCase)) languages[p.Name] = p.Value.Clone();
        languages[language] = JsonSerializer.SerializeToElement(final);
        var merged = new Dictionary<string, JsonElement>(asset.Metadata) { [field.Name] = JsonSerializer.SerializeToElement(languages) };

        // Every value is checked as for any edit; what is not demanded here is the completeness of fields this change does not touch.
        var built = await builder.BuildAsync(asset.Type, null, JsonSerializer.SerializeToElement(merged), requireFields: false, ct);
        if (built.IsFailure) return built.Error;

        var now = clock.UtcNow;
        asset.SetMetadata(built.Value.Metadata, built.Value.SchemaVersion, built.Value.Projection, now);
        var me = (await authz.GetProfileAsync(ct)).UserId;
        s.AcceptTranslation(final, me == Guid.Empty ? null : me, now);
        events.Raise(new AiSuggestionDecided(asset.Id, s.Id, s.Kind, $"{s.Field}/{s.Language}", "accepted"));
        return null;
    }
}

// ----------------------------------------------------------------------------------------------------------- request

/// <summary>
/// Asks for translations of an asset's multilingual fields into some languages (ADR-0036). The work is done by the worker; the results are suggestions waiting for
/// review. Needs <c>assets.update</c> on the asset. 409 when AI is off for the tenant or its provider cannot translate; 422 for fields that are not multilingual
/// text fields of the asset's type, languages the tenant does not use, more than 20 fields or 10 languages, a source text over the limit, or nothing to translate from.
/// </summary>
public sealed record TranslateAssetCommand(Guid AssetId, TranslateInput Input) : ICommand<TranslationJobDto>;

public sealed class TranslateAssetValidator : AbstractValidator<TranslateAssetCommand>
{
    public TranslateAssetValidator()
    {
        RuleFor(x => x.Input.Fields).Must(f => f is { Count: > 0 }).WithMessage("Name at least one multilingual field to translate.")
            .Must(f => f is null || f.Count <= TranslationRules.MaxFields).WithMessage($"At most {TranslationRules.MaxFields} fields per request.")
            .Must(f => f is null || f.All(n => !string.IsNullOrWhiteSpace(n))).WithMessage("Field names cannot be empty.")
            .OverridePropertyName("fields");
        RuleFor(x => x.Input.TargetLanguages).Must(l => l is { Count: > 0 }).WithMessage("Name at least one language to translate into.")
            .Must(l => l is null || l.Select(TranslationRules.NormalizeLanguage).Where(x => x is not null).Distinct().Count() <= TranslationRules.MaxLanguages)
            .WithMessage($"At most {TranslationRules.MaxLanguages} languages per request.")
            .Must(l => l is null || l.All(c => TranslationRules.NormalizeLanguage(c) is not null)).WithMessage("Use language codes such as hi, ta or pt-br.")
            .OverridePropertyName("targetLanguages");
        RuleFor(x => x.Input.SourceLanguage).Must(l => l is null || TranslationRules.NormalizeLanguage(l) is not null).WithMessage("Use a language code such as en.")
            .OverridePropertyName("sourceLanguage");
    }
}

public sealed class TranslateAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<MetadataSchema> schemas, IRepository<TranslationJob> jobs, IRepository<Tenant> tenants,
    IQueryExecutor q, IAuthorizationService authz, IAiResolver resolver, IEventCollector events, ITenantContext tenant, IClock clock)
    : IRequestHandler<TranslateAssetCommand, TranslationJobDto>
{
    public async Task<Result<TranslationJobDto>> Handle(TranslateAssetCommand c, CancellationToken ct)
    {
        var (asset, error) = await AiAssetAccess.LoadForUpdateAsync(assets, folders, q, authz, c.AssetId, ct);
        if (error is not null) return error;

        var ai = await resolver.ResolveAsync(ct);
        if (!ai.IsActive) return Error.Conflict(ai.DisabledReason ?? "AI is not enabled for this tenant.");
        if (!ai.Provider.Capabilities.Translate)
            return Error.Conflict($"The '{ai.ProviderKind}' AI provider cannot translate. Translation needs the OpenAI-compatible provider (OpenAI, Azure OpenAI or a self-run vLLM); choose it in the AI settings.");

        var fields = c.Input.Fields!.Select(f => f.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var languages = c.Input.TargetLanguages!.Select(l => TranslationRules.NormalizeLanguage(l)!).Distinct().ToList();
        var source = TranslationRules.NormalizeLanguage(c.Input.SourceLanguage);
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var l)) errors[key] = l = []; l.Add(message); }

        var tenantRow = await q.FirstOrDefaultAsync(tenants.Query(), ct);
        var tenantLanguages = TenantLanguages.Parse(tenantRow?.SettingsJson);
        foreach (var l in languages.Where(l => !tenantLanguages.Allows(l))) Add("targetLanguages", $"Language '{l}' is not enabled for this tenant.");
        if (source is not null && !tenantLanguages.Allows(source)) Add("sourceLanguage", $"Language '{source}' is not enabled for this tenant.");
        if (source is not null && languages.Contains(source)) Add("targetLanguages", $"'{source}' is the source language; leave it out of the target languages.");

        var schema = await q.FirstOrDefaultAsync(schemas.Query().Where(s => s.AssetType == asset!.Type && s.IsCurrent), ct);
        var definitions = new Dictionary<string, FieldDefinition>();
        foreach (var name in fields)
        {
            var def = schema?.Fields.FirstOrDefault(f => f.Name == name);
            if (def is null) Add("fields", $"'{name}' is not a field of {asset!.Type} assets.");
            else if (def.Type != FieldType.MultilingualText) Add("fields", $"'{name}' is not a multilingual text (ml_text) field; only those can be translated.");
            else definitions[name] = def;
        }
        if (errors.Count > 0) return Error.Validation(errors.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray()));

        var plan = TranslationPlanner.Plan(fields, languages, source, tenantLanguages.Default, c.Input.Overwrite ?? false,
            name => asset!.Metadata.TryGetValue(name, out var v) ? v : null);
        foreach (var w in plan.Where(w => w.SourceText is { } t && t.Length > TranslationRules.SourceLimit(definitions[w.Field].MaxLength)))
            Add("fields", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The {w.SourceLanguage} text of '{w.Field}' is longer than {TranslationRules.SourceLimit(definitions[w.Field].MaxLength):N0} characters, the most that is translated at once."));
        if (plan.All(w => w.NoSource)) Add("fields", "None of these fields has text to translate from. Add text in one language first, or name the language to translate from.");
        if (errors.Count > 0) return Error.Validation(errors.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray()));

        var me = (await authz.GetProfileAsync(ct)).UserId;
        var job = TranslationJob.Create(tenant.TenantId, asset!.Id, fields, languages, source, c.Input.Overwrite ?? false, me == Guid.Empty ? null : me, clock.UtcNow);
        jobs.Add(job);
        events.Raise(new AssetTranslationRequested(asset.Id, job.Id, fields, languages, job.Overwrite));
        return TranslationMapping.ToDto(job);
    }
}

// ------------------------------------------------------------------------------------------------------------- list

/// <summary>The latest ten translation jobs of an asset, newest first, with the outcome of each item. Needs <c>assets.read</c> on the asset.</summary>
public sealed record ListTranslationsQuery(Guid AssetId) : IQuery<AssetTranslationsDto>;

public sealed class ListTranslationsHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<TranslationJob> jobs, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListTranslationsQuery, AssetTranslationsDto>
{
    public const int Latest = 10;

    public async Task<Result<AssetTranslationsDto>> Handle(ListTranslationsQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        var list = await q.ToListAsync(jobs.Query().Where(j => j.AssetId == asset.Id).OrderByDescending(j => j.Id).Take(Latest), ct);
        return new AssetTranslationsDto(list.Select(TranslationMapping.ToDto).ToList());
    }
}
