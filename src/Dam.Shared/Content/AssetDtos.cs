using System.Text.Json;

namespace Dam.Shared.Content;

public sealed record AssetDto(
    Guid Id, Guid? FolderId, string Type, string Status, string Title, IReadOnlyDictionary<string, JsonElement> Metadata,
    int MetadataSchemaVersion, IReadOnlyList<string> TaxonomyTerms, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string[]> Attributes, Guid? OwnerId, Guid? CurrentVersionId, string? ChecksumSha256,
    string StatusReason, DateTimeOffset? PublishAt, DateTimeOffset? ExpireAt, Guid? RetentionPolicyId, bool LegalHold,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="ArchiveAfterDays">Days after an asset expires before the retention job archives it. Null: never auto-archived.</param>
/// <param name="DeleteAfterDays">Days after an asset is archived before the retention job purges it (deletes its file bytes).
/// Null: never auto-purged.</param>
/// <param name="LegalHoldExempt">When true, this policy purges on schedule even through a legal hold.</param>
public sealed record RetentionPolicyDto(Guid Id, string Name, int? ArchiveAfterDays, int? DeleteAfterDays, bool LegalHoldExempt, DateTimeOffset CreatedAt);

public sealed record PartUrlDto(int PartNumber, string Url, DateTimeOffset ExpiresAt);

public sealed record ReceivedPartDto(int PartNumber, long Size, string ETag);

/// <summary>State of one resumable transfer. After a dropped connection, read this and send only the missing parts.</summary>
public sealed record UploadDto(
    Guid Id, Guid AssetId, Guid? BatchId, string Status, string FileName, long DeclaredSize, string ContentType, int PartSize, int PartCount,
    string? Sha256, long? SizeBytes, Guid? DuplicateOfAssetId, string Error, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<ReceivedPartDto>? ReceivedParts);

/// <summary>An open upload with its first presigned part URLs. Ask for more with POST /uploads/{id}/parts.</summary>
public sealed record UploadStartDto(UploadDto Upload, IReadOnlyList<PartUrlDto> Parts);

/// <param name="AssetType">Optional: the type to create this file as. Omitted, it is guessed from the extension - which cannot
/// tell an HTML5 bundle from a 3D package or a manual (all .zip), or a document from a manual (both .pdf).</param>
/// <param name="Title">Optional: the asset's title. Omitted, it is the file name without its extension.</param>
public sealed record BulkFileRequest(string FileName, long Size, string? ContentType, string? AssetType = null, string? Title = null);

public sealed record BulkUploadItemDto(string FileName, Guid? AssetId, UploadStartDto? Upload, string? Error);

public sealed record BulkUploadResultDto(Guid BatchId, int Created, int Failed, IReadOnlyList<BulkUploadItemDto> Items);

/// <summary>One stored file of an asset. Where the bytes live is deliberately not exposed.</summary>
public sealed record AssetVersionDto(
    Guid Id, int VersionNo, bool IsCurrent, long SizeBytes, string Mime, string Sha256,
    IReadOnlyDictionary<string, JsonElement> TechnicalMetadata, string ChangeNote, DateTimeOffset CreatedAt);

/// <param name="Distance">How many of the 64 perceptual-hash bits differ: 0 is visually identical, small numbers are near-duplicates.</param>
public sealed record SimilarAssetDto(Guid AssetId, string Title, string Type, string Status, Guid? FolderId, int Distance);

/// <param name="Before">Missing when the field only exists on <see cref="VersionDiffDto.OtherVersionNo"/>'s side.</param>
/// <param name="After">Missing when the field only exists on <see cref="VersionDiffDto.VersionNo"/>'s side.</param>
public sealed record FieldDiffDto(JsonElement? Before, JsonElement? After);

/// <param name="Fields">Every field (size, mime, sha256, and each top-level technical-metadata key) that differs between the
/// two versions, keyed by name; fields that are the same on both are left out.</param>
public sealed record VersionDiffDto(int VersionNo, int OtherVersionNo, IReadOnlyDictionary<string, FieldDiffDto> Fields);

/// <param name="Fit">cover (fill the box and crop), contain (fit inside), or fill (stretch).</param>
/// <param name="Focal">Where to keep in frame when cropping, as "x,y" between 0 and 1 (default "0.5,0.5", the centre).</param>
public sealed record TransformRequest(int? W, int? H, string? Fit, string? Fmt, int? Q, string? Focal);

/// <param name="Renditions">Named renditions to allow ("thumb", "preview", "web", "web_jpeg", "mobile", "original"). Ignored when Transform is given.</param>
/// <param name="TtlSeconds">How long the link works. Defaults to one hour; the server caps it.</param>
/// <param name="Ip">Only this client address may use the link.</param>
public sealed record CreateLinkRequest(IReadOnlyList<string>? Renditions, int? TtlSeconds, string? Ip, TransformRequest? Transform);

/// <param name="Watermarked">True when the asset is not published: every image served by these links carries a watermark.</param>
/// <param name="Links">Rendition name (or "transform") to a signed URL on the delivery origin or CDN.</param>
public sealed record DeliveryLinkDto(DateTimeOffset ExpiresAt, bool Watermarked, IReadOnlyDictionary<string, string> Links);

/// <param name="Duration">Seconds, for a video rendition; null for stills.</param>
/// <param name="PageNo">1-based page number for a PDF page rendition; null for everything else.</param>
public sealed record RenditionDto(string Profile, string Mime, int Width, int Height, long SizeBytes, string Status, double? Duration, int? PageNo, DateTimeOffset CreatedAt);
