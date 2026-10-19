namespace Dam.Shared.Licensing;

/// <summary>A rights record as a rights manager sees it (ADR-0033). Dates are calendar dates (UTC); <c>validTo</c> is the last valid day, inclusive.</summary>
/// <param name="LicenceType">owned, licensed, royalty_free, rights_managed, editorial, creative_commons or other.</param>
/// <param name="Channels">Channel codes the licence covers; empty means every channel.</param>
/// <param name="Regions">Region codes the licence covers; empty means worldwide.</param>
/// <param name="Status">valid, expiring (ends within 30 days), expired or not_yet_valid, as of today (UTC).</param>
/// <param name="DaysRemaining">Days until validTo (0 on the last day, negative once expired); null without an end date.</param>
/// <param name="AssetCount">Assets currently assigned to this record.</param>
/// <param name="DocumentCount">Release documents attached.</param>
/// <param name="PendingAssets">Only after an update that changed channels or regions: assigned assets beyond the first 500, whose access attributes the hourly rights job re-derives instead of this request. Normally 0.</param>
public sealed record RightsDto(
    Guid Id, string Name, string LicenceType, string Licensor, IReadOnlyList<string> Channels, IReadOnlyList<string> Regions,
    DateOnly? ValidFrom, DateOnly? ValidTo, bool WatermarkRequired, string Notes, string Status, int? DaysRemaining,
    int AssetCount, int DocumentCount, Guid? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int PendingAssets = 0);

/// <summary>Create or replace a rights record (PUT replaces every field; omitted lists become empty). Every member is optional on the wire; name and
/// licenceType are required in effect (422 otherwise).</summary>
public sealed record RightsInput
{
    public string? Name { get; init; }
    public string? LicenceType { get; init; }
    public string? Licensor { get; init; }
    public IReadOnlyList<string>? Channels { get; init; }
    public IReadOnlyList<string>? Regions { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidTo { get; init; }
    public bool WatermarkRequired { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// The rights of one asset, for anyone who can read it. <c>Licensor</c> and <c>Notes</c> are filled only for callers who hold
/// <c>rights.manage</c> (null otherwise). Same shape for managers and readers.
/// </summary>
/// <param name="DocumentsCount">How many release documents exist; the documents themselves need rights.manage.</param>
public sealed record RightsSummaryDto(
    Guid Id, string Name, string LicenceType, IReadOnlyList<string> Channels, IReadOnlyList<string> Regions, DateOnly? ValidFrom, DateOnly? ValidTo,
    string Status, int? DaysRemaining, bool WatermarkRequired, int DocumentsCount, string? Licensor, string? Notes);

/// <param name="Rights">Null when the asset has no rights assigned.</param>
public sealed record AssetRightsDto(Guid AssetId, RightsSummaryDto? Rights);

/// <summary>Assign or clear (rightsId null) an asset's rights.</summary>
public sealed record SetAssetRightsInput(Guid? RightsId);

/// <summary>Assign one rights record to many assets.</summary>
public sealed record AssignRightsInput
{
    /// <summary>Up to 500 assets.</summary>
    public IReadOnlyList<Guid>? AssetIds { get; init; }
    /// <summary>When false (default), an asset that already has other rights is skipped; when true its rights are replaced.</summary>
    public bool Replace { get; init; }
}

/// <param name="Outcome">assigned, skipped or failed.</param>
/// <param name="Reason">Why an asset was skipped or failed; null when assigned.</param>
public sealed record AssignRightsItemDto(Guid AssetId, string Outcome, string? Reason);

public sealed record AssignRightsResultDto(int Assigned, int Skipped, int Failed, IReadOnlyList<AssignRightsItemDto> Items);

/// <summary>
/// Whether an asset may be used on a channel and in a region, and why not.
/// </summary>
/// <param name="Allowed">True when no reason stands in the way.</param>
/// <param name="Reasons">Sentences fit to show a person; empty when allowed.</param>
/// <param name="Status">The licence's status (valid, expiring, expired, not_yet_valid); null when the asset has no rights.</param>
/// <param name="WatermarkRequired">Deliveries of this asset carry the watermark and withhold the original and video.</param>
/// <param name="ValidTo">The licence's last valid day, if any.</param>
/// <param name="EffectiveChannels">Channels the asset can reach after metadata and licence are combined; empty with <c>NoChannelsAllowed</c> false means every channel.</param>
/// <param name="EffectiveRegions">Regions the asset can reach; empty with <c>NoRegionsAllowed</c> false means worldwide.</param>
/// <param name="NoChannelsAllowed">The metadata and the licence share no channel, so no channel may use the asset.</param>
/// <param name="NoRegionsAllowed">The metadata and the licence share no region.</param>
public sealed record RightsCheckDto(
    bool Allowed, IReadOnlyList<string> Reasons, string? Status, bool WatermarkRequired, DateOnly? ValidTo,
    IReadOnlyList<string> EffectiveChannels, IReadOnlyList<string> EffectiveRegions, bool NoChannelsAllowed, bool NoRegionsAllowed);

/// <param name="FileName">The name to show and download as; its extension must match the content type.</param>
public sealed record RightsDocumentUploadInput(string? FileName, string? ContentType, long Size);

/// <summary>PUT the file's bytes to <c>UploadUrl</c> (with the headers listed), then call complete with the same name, type and size.</summary>
/// <param name="Headers">Headers the PUT must send exactly (the content type is part of the signature).</param>
public sealed record RightsDocumentUploadDto(Guid DocumentId, string UploadUrl, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt, long MaxBytes);

/// <param name="Size">The size the upload was declared with; it must equal the stored object's size.</param>
public sealed record RightsDocumentCompleteInput(string? FileName, string? ContentType, long Size);

public sealed record RightsDocumentDto(Guid Id, Guid RightsId, string Name, string ContentType, long SizeBytes, Guid? UploadedBy, DateTimeOffset CreatedAt);

/// <summary>A short-lived download URL; the response forces an attachment download.</summary>
public sealed record RightsDocumentLinkDto(string Url, DateTimeOffset ExpiresAt);
