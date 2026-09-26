using System.Text.Json;

namespace Dam.Shared.Content;

public sealed record AssetDto(
    Guid Id, Guid? FolderId, string Type, string Status, string Title, IReadOnlyDictionary<string, JsonElement> Metadata,
    int MetadataSchemaVersion, IReadOnlyList<string> TaxonomyTerms, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string[]> Attributes, Guid? OwnerId, Guid? CurrentVersionId, string? ChecksumSha256,
    string StatusReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record PartUrlDto(int PartNumber, string Url, DateTimeOffset ExpiresAt);

public sealed record ReceivedPartDto(int PartNumber, long Size, string ETag);

/// <summary>State of one resumable transfer. After a dropped connection, read this and send only the missing parts.</summary>
public sealed record UploadDto(
    Guid Id, Guid AssetId, Guid? BatchId, string Status, string FileName, long DeclaredSize, string ContentType, int PartSize, int PartCount,
    string? Sha256, long? SizeBytes, Guid? DuplicateOfAssetId, string Error, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<ReceivedPartDto>? ReceivedParts);

/// <summary>An open upload with its first presigned part URLs. Ask for more with POST /uploads/{id}/parts.</summary>
public sealed record UploadStartDto(UploadDto Upload, IReadOnlyList<PartUrlDto> Parts);

public sealed record BulkFileRequest(string FileName, long Size, string? ContentType);

public sealed record BulkUploadItemDto(string FileName, Guid? AssetId, UploadStartDto? Upload, string? Error);

public sealed record BulkUploadResultDto(Guid BatchId, int Created, int Failed, IReadOnlyList<BulkUploadItemDto> Items);
