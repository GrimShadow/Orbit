using System.Text.Json;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Content;

namespace Dam.Infrastructure.Search;

/// <summary>
/// Projects one asset (plus its folder and resolved taxonomy) into the JSON document stored in its tenant's index. Every
/// event in <see cref="Dam.Infrastructure.Consumers.IndexConsumer"/>'s routing keys carries only ids, so the asset is
/// always freshly loaded before this runs - the document below is the full, current picture, never a delta.
/// </summary>
public static class SearchDocumentBuilder
{
    /// <param name="Ai">What AI enrichment produced for the asset's current version, if anything (ADR-0035). An asset with none is indexed exactly as before.</param>
    public static Dictionary<string, object?> Build(
        Asset asset, Folder? folder, TenantSchemaIndex schemas, TenantTaxonomyIndex taxonomy, TenantCollectionIndex collections, AiIndexData? ai = null)
    {
        var fields = schemas.FieldsFor(asset.Type);
        var doc = new Dictionary<string, object?>
        {
            ["id"] = asset.Id,
            ["tenant_id"] = asset.TenantId,
            ["type"] = asset.Type,
            ["status"] = asset.Status,
            ["title"] = asset.Title,
            ["folder_id"] = asset.FolderId,
            ["folder_path"] = folder?.Path,
            ["owner_id"] = asset.OwnerId,
            // Only static collections (spec Step 1.7c / ADR-0013): a smart collection's membership is never stored.
            ["collection_ids"] = collections.For(asset.Id),
            ["tags"] = asset.Tags,
            ["taxonomy_terms"] = asset.TaxonomyTerms,
            ["attributes"] = Dimensions.All.ToDictionary(d => d, d => (object?)asset.Attributes.GetValueOrDefault(d, [])),
            ["current_version_id"] = asset.CurrentVersionId,
            ["checksum_sha256"] = asset.ChecksumSha256,
            ["publish_at"] = asset.PublishAt,
            ["expire_at"] = asset.ExpireAt,
            ["retention_policy_id"] = asset.RetentionPolicyId,
            ["legal_hold"] = asset.LegalHold,
            ["created_at"] = asset.CreatedAt,
            ["updated_at"] = asset.UpdatedAt,
            ["metadata"] = BuildMetadata(asset, fields),
            ["terms"] = BuildTerms(asset, fields, taxonomy),
        };
        if (ai is not null) AddAi(doc, ai);
        return doc;
    }

    /// <summary>Extracted text and the transcript are analysed text; the vectors go in as plain number lists. Anything the asset does not have is left out.</summary>
    private static void AddAi(Dictionary<string, object?> doc, AiIndexData ai)
    {
        if (!string.IsNullOrEmpty(ai.OcrText)) doc[IndexMappingBuilder.OcrTextField] = ai.OcrText;
        if (!string.IsNullOrEmpty(ai.TranscriptText)) doc[IndexMappingBuilder.TranscriptTextField] = ai.TranscriptText;
        if (ai.Segments.Count > 0)
            doc[IndexMappingBuilder.TranscriptSegmentsField] = ai.Segments.Select(s => new Dictionary<string, object?> { ["start_ms"] = s.StartMs, ["end_ms"] = s.EndMs, ["text"] = s.Text }).ToList();
        if (ai.TextVector is not null) doc[IndexMappingBuilder.VectorTextField] = ai.TextVector;
        if (ai.ImageVector is not null) doc[IndexMappingBuilder.VectorImageField] = ai.ImageVector;
    }

    private static Dictionary<string, JsonElement> BuildMetadata(Asset asset, IReadOnlyList<FieldDefinition> fields)
    {
        var known = fields.Select(f => f.Name).ToHashSet();
        // Only fields the asset's *current* schema still knows about: a retired field stays in Asset.Metadata (old
        // versions keep validating against their own schema version) but is never a mapped, searchable property.
        return asset.Metadata.Where(kv => known.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static List<Dictionary<string, object?>> BuildTerms(Asset asset, IReadOnlyList<FieldDefinition> fields, TenantTaxonomyIndex taxonomy)
    {
        var entries = new List<Dictionary<string, object?>>();
        foreach (var f in fields)
        {
            if (f.Type is not (FieldType.Vocabulary or FieldType.TermTree) || f.Vocabulary is not { } vocabularyKey) continue;
            if (!asset.Metadata.TryGetValue(f.Name, out var value)) continue;
            foreach (var code in Codes(value))
            {
                var term = taxonomy.Resolve(vocabularyKey, code);
                var path = term?.Path ?? code;
                entries.Add(new Dictionary<string, object?>
                {
                    ["field"] = f.Name,
                    ["vocabulary"] = vocabularyKey,
                    ["code"] = code,
                    ["path"] = path,
                    ["ancestor_codes"] = path.Split('.'),
                    ["label"] = term?.LabelFor("en") ?? code,
                });
            }
        }
        return entries;
    }

    private static IEnumerable<string> Codes(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!),
        JsonValueKind.String => [value.GetString()!],
        _ => [],
    };
}

/// <summary>The AI-derived parts of an asset's search document.</summary>
public sealed record AiIndexData(string? OcrText, string? TranscriptText, IReadOnlyList<TranscriptSegment> Segments, float[]? TextVector, float[]? ImageVector)
{
    public static readonly AiIndexData Empty = new(null, null, [], null, null);
}
