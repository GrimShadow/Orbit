using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Content;

/// <summary>
/// Full validation of a metadata document: the pure structural checks, then vocabulary membership against the database
/// and the languages enabled for the tenant. Asset commands (Step 1.3) call this before saving.
/// </summary>
public sealed class MetadataValidationService(IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IRepository<Tenant> tenants, IQueryExecutor q)
{
    /// <returns>Field-level errors keyed "metadata.&lt;field&gt;", empty when valid.</returns>
    public async Task<Dictionary<string, string[]>> ValidateAsync(MetadataSchema schema, JsonElement metadata, bool allowDeprecatedTerms, CancellationToken ct)
    {
        var languages = await EnabledLanguagesAsync(ct);
        var check = MetadataValidator.Check(schema.Fields, metadata, languages);
        var errors = check.Errors.ToDictionary(kv => kv.Key, kv => kv.Value);
        void Add(string field, string msg) { var k = "metadata." + field; if (!errors.TryGetValue(k, out var l)) errors[k] = l = []; l.Add(msg); }

        if (check.References.Count > 0)
        {
            var keys = check.References.Select(r => r.VocabularyKey).Distinct().ToList();
            var vocabs = (await q.ToListAsync(vocabularies.Query().Where(v => keys.Contains(v.Key)), ct)).ToDictionary(v => v.Key);
            var vocabIds = vocabs.Values.Select(v => v.Id).ToList();
            var codes = check.References.SelectMany(r => r.Codes).Distinct().ToList();
            var found = await q.ToListAsync(terms.Query().Where(t => vocabIds.Contains(t.VocabularyId) && codes.Contains(t.Code)), ct);

            foreach (var reference in check.References)
            {
                if (!vocabs.TryGetValue(reference.VocabularyKey, out var vocab)) { Add(reference.Field, $"Vocabulary '{reference.VocabularyKey}' does not exist."); continue; }
                foreach (var code in reference.Codes)
                {
                    var term = found.FirstOrDefault(t => t.VocabularyId == vocab.Id && t.Code == code);
                    if (term is null) Add(reference.Field, $"Unknown term '{code}' in vocabulary '{vocab.Key}'.");
                    else if (term.Status == TermStatus.Deprecated && !allowDeprecatedTerms) Add(reference.Field, $"Term '{code}' is deprecated.");
                }
            }
        }
        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray());
    }

    private async Task<IReadOnlySet<string>?> EnabledLanguagesAsync(CancellationToken ct)
    {
        var tenant = await q.FirstOrDefaultAsync(tenants.Query(), ct);
        if (tenant is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(tenant.SettingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("languages", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                var set = l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant()).ToHashSet();
                return set.Count > 0 ? set : null; // an empty list means "no restriction"
            }
        }
        catch (JsonException) { /* malformed settings: no restriction */ }
        return null;
    }
}

public sealed record ListSchemasQuery : IQuery<IReadOnlyList<SchemaSummaryDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ListSchemasHandler(IRepository<MetadataSchema> schemas, IQueryExecutor q) : IRequestHandler<ListSchemasQuery, IReadOnlyList<SchemaSummaryDto>>
{
    public async Task<Result<IReadOnlyList<SchemaSummaryDto>>> Handle(ListSchemasQuery r, CancellationToken ct) =>
        (await q.ToListAsync(schemas.Query().Where(s => s.IsCurrent), ct))
            .OrderBy(s => AssetTypes.All.ToList().IndexOf(s.AssetType))
            .Select(s => new SchemaSummaryDto(s.AssetType, s.Version, s.Fields.Count)).ToList();
}

/// <summary>The current schema for an asset type, or a specific historical version.</summary>
public sealed record GetSchemaQuery(string AssetType, int? Version) : IQuery<SchemaDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class GetSchemaHandler(IRepository<MetadataSchema> schemas, IQueryExecutor q) : IRequestHandler<GetSchemaQuery, SchemaDto>
{
    public async Task<Result<SchemaDto>> Handle(GetSchemaQuery r, CancellationToken ct)
    {
        if (!AssetTypes.IsKnown(r.AssetType)) return Error.NotFound($"Unknown asset type '{r.AssetType}'.");
        var s = await q.FirstOrDefaultAsync(schemas.Query().Where(x => x.AssetType == r.AssetType && (r.Version == null ? x.IsCurrent : x.Version == r.Version)), ct);
        return s is null ? Error.NotFound("No metadata schema for that asset type" + (r.Version is null ? "." : $" and version {r.Version}.")) : ContentMapping.ToDto(s);
    }
}

public sealed record ListSchemaVersionsQuery(string AssetType) : IQuery<IReadOnlyList<SchemaDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ListSchemaVersionsHandler(IRepository<MetadataSchema> schemas, IQueryExecutor q) : IRequestHandler<ListSchemaVersionsQuery, IReadOnlyList<SchemaDto>>
{
    public async Task<Result<IReadOnlyList<SchemaDto>>> Handle(ListSchemaVersionsQuery r, CancellationToken ct)
    {
        if (!AssetTypes.IsKnown(r.AssetType)) return Error.NotFound($"Unknown asset type '{r.AssetType}'.");
        return (await q.ToListAsync(schemas.Query().Where(s => s.AssetType == r.AssetType).OrderByDescending(s => s.Version), ct)).Select(ContentMapping.ToDto).ToList();
    }
}

/// <summary>Saves the fields for an asset type. If they differ from the current version, a new version is created.</summary>
public sealed record UpdateSchemaCommand(string AssetType, IReadOnlyList<FieldDto> Fields, string? ChangeNote)
    : ICommand<SchemaUpdateResultDto>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.MetadataSchemaManage;
}

public sealed class UpdateSchemaValidator : AbstractValidator<UpdateSchemaCommand>
{
    public UpdateSchemaValidator()
    {
        RuleFor(x => x.AssetType).Must(AssetTypes.IsKnown).WithMessage($"Asset type must be one of: {string.Join(", ", AssetTypes.All)}.");
        RuleFor(x => x.ChangeNote).MaximumLength(500);
        RuleFor(x => x.Fields).NotNull();
    }
}

public sealed class UpdateSchemaHandler(
    IRepository<MetadataSchema> schemas, IRepository<Vocabulary> vocabularies, IQueryExecutor q, ITenantContext tenant, ICurrentUser me,
    IClock clock, IEventCollector events) : IRequestHandler<UpdateSchemaCommand, SchemaUpdateResultDto>
{
    public async Task<Result<SchemaUpdateResultDto>> Handle(UpdateSchemaCommand c, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var fields = new List<FieldDefinition>();
        for (var i = 0; i < c.Fields.Count; i++)
        {
            if (!ContentMapping.TryFromWire(c.Fields[i].Type, out _))
            {
                errors[$"fields[{i}].type"] = [$"Type must be one of: {string.Join(", ", ContentMapping.WireNames)}."];
                continue;
            }
            fields.Add(ContentMapping.FromDto(c.Fields[i]));
        }
        if (errors.Count > 0) return Error.Validation(errors);
        foreach (var kv in SchemaDefinitionValidator.Validate(fields)) errors[kv.Key] = kv.Value;

        // Vocabulary fields must point at a vocabulary that exists (and term_tree at a hierarchical one).
        var keys = fields.Where(f => f.Vocabulary is not null).Select(f => f.Vocabulary!).Distinct().ToList();
        var vocabs = keys.Count == 0 ? [] : (await q.ToListAsync(vocabularies.Query().Where(v => keys.Contains(v.Key)), ct)).ToDictionary(v => v.Key);
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.Vocabulary is null) continue;
            if (!vocabs.TryGetValue(f.Vocabulary, out var v)) errors[$"fields[{i}].vocabulary"] = [$"Vocabulary '{f.Vocabulary}' does not exist."];
            else if (f.Type == FieldType.TermTree && !v.Hierarchical) errors[$"fields[{i}].vocabulary"] = [$"'{f.Vocabulary}' is not hierarchical; use a vocabulary field instead."];
        }
        if (errors.Count > 0) return Error.Validation(errors);

        var current = await q.FirstOrDefaultAsync(schemas.Query().Where(s => s.AssetType == c.AssetType && s.IsCurrent), ct);
        if (current is not null && SameFields(current.Fields, fields))
            return new SchemaUpdateResultDto(ContentMapping.ToDto(current), false, [], [], []);

        var version = current is null ? 1 : current.Version + 1;
        current?.Retire();
        var next = MetadataSchema.Create(tenant.TenantId, c.AssetType, version, fields, c.ChangeNote, me.UserId, clock.UtcNow);
        schemas.Add(next);
        events.Raise(Events.Changed("metadata_schema.updated", "metadata_schema", next.Id));

        var before = current?.Fields.ToDictionary(f => f.Name) ?? [];
        var after = fields.ToDictionary(f => f.Name);
        return new SchemaUpdateResultDto(ContentMapping.ToDto(next), true,
            after.Keys.Except(before.Keys).Order().ToList(), before.Keys.Except(after.Keys).Order().ToList(),
            after.Where(kv => before.TryGetValue(kv.Key, out var old) && JsonSerializer.Serialize(old, Web) != JsonSerializer.Serialize(kv.Value, Web)).Select(kv => kv.Key).Order().ToList());
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static bool SameFields(List<FieldDefinition> a, List<FieldDefinition> b) =>
        JsonSerializer.Serialize(a, Web) == JsonSerializer.Serialize(b, Web);
}

/// <summary>Checks a metadata document against a schema (current or a given version). Invalid means a 422 with field errors.</summary>
public sealed record ValidateMetadataQuery(string AssetType, JsonElement Metadata, int? Version) : IQuery<ValidationResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AssetsRead;
}

public sealed class ValidateMetadataHandler(IRepository<MetadataSchema> schemas, IQueryExecutor q, MetadataValidationService validation)
    : IRequestHandler<ValidateMetadataQuery, ValidationResultDto>
{
    public async Task<Result<ValidationResultDto>> Handle(ValidateMetadataQuery r, CancellationToken ct)
    {
        if (!AssetTypes.IsKnown(r.AssetType)) return Error.NotFound($"Unknown asset type '{r.AssetType}'.");
        var s = await q.FirstOrDefaultAsync(schemas.Query().Where(x => x.AssetType == r.AssetType && (r.Version == null ? x.IsCurrent : x.Version == r.Version)), ct);
        if (s is null) return Error.NotFound("No metadata schema for that asset type" + (r.Version is null ? "." : $" and version {r.Version}."));
        var errors = await validation.ValidateAsync(s, r.Metadata, allowDeprecatedTerms: false, ct);
        return errors.Count > 0 ? Error.Validation(errors) : new ValidationResultDto(true, s.Version);
    }
}
