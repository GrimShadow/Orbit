using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Domain.Assets;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;

namespace Dam.Application.Assets;

public sealed record BuiltMetadata(Dictionary<string, JsonElement> Metadata, int SchemaVersion, Asset.Projection Projection);

/// <summary>
/// Turns "what the caller supplied" into the metadata an asset stores: folder-inherited values first, then the caller's,
/// validated against the type's current schema, with term codes and access attributes derived from it.
/// </summary>
public sealed class AssetMetadataBuilder(
    IRepository<MetadataSchema> schemas, IRepository<Folder> folders, IQueryExecutor q, MetadataValidationService validation)
{
    public async Task<Result<BuiltMetadata>> BuildAsync(string assetType, Folder? folder, JsonElement? provided, bool requireFields, CancellationToken ct)
    {
        var schema = await q.FirstOrDefaultAsync(schemas.Query().Where(s => s.AssetType == assetType && s.IsCurrent), ct);
        var fields = schema?.Fields ?? [];
        var known = fields.Select(f => f.Name).ToHashSet();

        var merged = new Dictionary<string, JsonElement>();
        // Inherited values are defaults, so keys the schema does not know are simply ignored; supplied values are strict.
        if (folder is not null)
            foreach (var (k, v) in await InheritedAsync(folder, ct)) if (known.Contains(k)) merged[k] = v;

        if (provided is { } p && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.Object) return Error.Validation(ContentMapping.Fail("metadata", "Metadata must be a JSON object."));
            foreach (var prop in p.EnumerateObject()) merged[prop.Name] = prop.Value.Clone();
        }

        var doc = JsonSerializer.SerializeToElement(merged);
        var errors = await validation.ValidateFieldsAsync(fields, doc, allowDeprecatedTerms: false, requireFields, ct);
        if (errors.Count > 0) return Error.Validation(errors);
        return new BuiltMetadata(merged, schema?.Version ?? 0, MetadataProjector.Project(fields, merged));
    }

    /// <summary>The metadata the folder and its ancestors define; deeper folders win, key by key.</summary>
    private async Task<Dictionary<string, JsonElement>> InheritedAsync(Folder folder, CancellationToken ct)
    {
        var labels = folder.Path.Split('.');
        var prefixes = Enumerable.Range(1, labels.Length - 1).Select(n => string.Join('.', labels.Take(n))).ToList();
        var ancestors = prefixes.Count == 0 ? [] : (await q.ToListAsync(folders.Query().Where(x => prefixes.Contains(x.Path)), ct)).OrderBy(x => x.Path.Length).ToList();
        var effective = new Dictionary<string, JsonElement>();
        foreach (var f in ancestors.Append(folder)) foreach (var (k, v) in f.InheritedMetadata) effective[k] = v;
        return effective;
    }
}

internal static class AssetMapping
{
    public static AssetDto ToDto(Asset a) =>
        new(a.Id, a.FolderId, a.Type, a.Status, a.Title, a.Metadata, a.MetadataSchemaVersion, a.TaxonomyTerms, a.Tags, a.Attributes,
            a.OwnerId, a.CurrentVersionId, a.ChecksumSha256, a.StatusReason, a.CreatedAt, a.UpdatedAt);

    public static UploadDto ToDto(Upload u, IReadOnlyList<ReceivedPartDto>? received = null) =>
        new(u.Id, u.AssetId, u.BatchId, u.Status.ToString().ToLowerInvariant(), u.FileName, u.DeclaredSize, u.ContentType, u.PartSize, u.PartCount,
            u.Sha256, u.SizeBytes, u.DuplicateOfAssetId, u.Error, u.CreatedAt, u.ExpiresAt, received);

    public static List<string>? TagErrors(IEnumerable<string>? tags, out string? error)
    {
        error = null;
        if (tags is null) return null;
        var list = Asset.Normalize(tags);
        if (list.Count > 50) { error = "At most 50 tags."; return null; }
        if (list.Any(t => t.Length > 50)) { error = "Each tag is at most 50 characters."; return null; }
        return list;
    }
}
