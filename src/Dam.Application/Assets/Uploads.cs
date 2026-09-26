using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

internal static class Hex
{
    public static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
}

/// <summary>Everything needed to open a resumable upload for an asset; shared by single and bulk upload.</summary>
public sealed class UploadStarter(
    IRepository<Upload> uploads, IRepository<Asset> assets, IQueryExecutor q, IObjectStore store, UploadSettings settings,
    ITenantContext tenant, ICurrentUser me, IClock clock)
{
    public async Task<Result<UploadStartDto>> StartAsync(Asset asset, string fileName, long size, string? contentType, bool allowDuplicate,
        string? sha256Hint, Guid? batchId, int initialParts, CancellationToken ct)
    {
        var name = UploadPolicy.CleanFileName(fileName);
        var errors = new Dictionary<string, string[]>();
        if (name.Length == 0) errors["fileName"] = ["A file name is required."];
        else if (UploadPolicy.IsBlocked(name)) errors["fileName"] = [$"Files of type '.{UploadPolicy.Extension(name)}' are not accepted."];
        else if (!UploadPolicy.IsAllowed(asset.Type, name)) errors["fileName"] = [$"'.{UploadPolicy.Extension(name)}' is not an accepted file type for a '{asset.Type}' asset."];
        if (size <= 0) errors["size"] = ["Size must be greater than zero."];
        else if (size > UploadPolicy.MaxSize(asset.Type)) errors["size"] = [$"Too large: '{asset.Type}' files can be at most {UploadPolicy.MaxSize(asset.Type) / UploadPolicy.Gib} GB."];
        if (contentType is { Length: > 200 }) errors["contentType"] = ["Content type is too long."];
        if (sha256Hint is not null && !Hex.IsSha256(sha256Hint)) errors["sha256"] = ["Expected 64 hexadecimal characters."];
        if (errors.Count > 0) return Error.Validation(errors);

        if (asset.Status == AssetStatuses.Deleted) return Error.Conflict("The asset is deleted.");
        if (asset.Status == AssetStatuses.Blocked) return Error.Conflict("The asset is blocked because malware was found in an earlier upload. An administrator must review it first.");
        if (await q.AnyAsync(uploads.Query().Where(u => u.AssetId == asset.Id && (u.Status == UploadStatus.Initiated || u.Status == UploadStatus.Processing)), ct))
            return Error.Conflict("This asset already has an upload in progress. Finish or abort it first.");

        if (!allowDuplicate && sha256Hint is not null && await FindDuplicateAsync(sha256Hint.ToLowerInvariant(), asset.Id, ct) is { } dup)
            return DuplicateError(dup);

        var partSize = settings.PartSizeBytes;
        if (Upload.PartsFor(size, partSize) > UploadSettings.MaxParts) partSize = (int)Math.Min(int.MaxValue, (size + UploadSettings.MaxParts - 1) / UploadSettings.MaxParts);

        var id = Uuid7.NewGuid();
        var key = Upload.KeyFor(tenant.TenantId, asset.Id, id);
        var s3UploadId = await store.StartMultipartAsync(store.QuarantineBucket, key, contentType ?? "application/octet-stream", ct);
        var upload = Upload.Start(id, tenant.TenantId, asset.Id, batchId, name, size, contentType ?? "application/octet-stream", store.QuarantineBucket,
            s3UploadId, partSize, allowDuplicate, me.UserId, clock.UtcNow, settings.SessionLifetime);
        uploads.Add(upload);
        return new UploadStartDto(AssetMapping.ToDto(upload), PresignRange(upload, Enumerable.Range(1, Math.Min(upload.PartCount, initialParts))));
    }

    public IReadOnlyList<PartUrlDto> PresignRange(Upload u, IEnumerable<int> partNumbers)
    {
        var expires = clock.UtcNow + settings.PresignTtl;
        return partNumbers.Select(n => new PartUrlDto(n, store.PresignPart(u.Bucket, u.StorageKey, u.S3UploadId, n, settings.PresignTtl), expires)).ToList();
    }

    /// <summary>The asset (in this tenant) that already holds content with this hash, if any.</summary>
    public async Task<Asset?> FindDuplicateAsync(string sha256, Guid exceptAssetId, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(assets.Query().Where(a => a.ChecksumSha256 == sha256 && a.Status != AssetStatuses.Deleted && a.Id != exceptAssetId), ct);

    public static Error DuplicateError(Asset existing) =>
        new("duplicate", "Identical content already exists.", ErrorType.Conflict,
            new Dictionary<string, string[]> { ["duplicateOfAssetId"] = [existing.Id.ToString()] });
}

internal static class UploadAccess
{
    /// <summary>Loads an upload and its asset; the caller must be able to update the asset (own or any).</summary>
    public static async Task<Result<(Upload Upload, Asset Asset, ResourceContext Resource)>> LoadAsync(
        IRepository<Upload> uploads, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz,
        Guid uploadId, CancellationToken ct)
    {
        var upload = await q.FirstOrDefaultAsync(uploads.Query().Where(u => u.Id == uploadId), ct);
        if (upload is null) return Error.NotFound("Upload not found.");
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, upload.AssetId, ct);
        if (asset is null) return Error.NotFound("Upload not found.");
        var resource = asset.ToResource(path);
        if (!await authz.CanAsync(Permissions.AssetsRead, resource, ct)) return Error.NotFound("Upload not found.");
        if (!await authz.CanAsync(Permissions.AssetsUpdate, resource, ct)) return Error.Forbidden();
        return (upload, asset, resource);
    }
}

// ------------------------------------------------------------------------------------------------- start / resume

public sealed record InitiateUploadCommand(Guid AssetId, string FileName, long Size, string? ContentType, bool AllowDuplicate, string? Sha256)
    : ICommand<UploadStartDto>;

public sealed class InitiateUploadValidator : AbstractValidator<InitiateUploadCommand>
{
    public InitiateUploadValidator() => RuleFor(x => x.FileName).NotEmpty().MaximumLength(1000);
}

public sealed class InitiateUploadHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, UploadStarter starter, UploadSettings settings)
    : IRequestHandler<InitiateUploadCommand, UploadStartDto>
{
    public async Task<Result<UploadStartDto>> Handle(InitiateUploadCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.AssetsUpdate, asset.ToResource(path), ct)) return Error.Forbidden();
        return await starter.StartAsync(asset, c.FileName, c.Size, c.ContentType, c.AllowDuplicate, c.Sha256, null, settings.InitialPartUrls, ct);
    }
}

/// <summary>Current state of a transfer. For an open upload it lists the parts storage has, which is what a client resumes from.</summary>
public sealed record GetUploadQuery(Guid UploadId) : IQuery<UploadDto>;

public sealed class GetUploadHandler(
    IRepository<Upload> uploads, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IObjectStore store)
    : IRequestHandler<GetUploadQuery, UploadDto>
{
    public async Task<Result<UploadDto>> Handle(GetUploadQuery r, CancellationToken ct)
    {
        var loaded = await UploadAccess.LoadAsync(uploads, assets, folders, q, authz, r.UploadId, ct);
        if (loaded.IsFailure) return loaded.Error;
        var u = loaded.Value.Upload;
        IReadOnlyList<ReceivedPartDto>? received = null;
        if (u.IsOpen) received = (await store.ListPartsAsync(u.Bucket, u.StorageKey, u.S3UploadId, ct)).Select(p => new ReceivedPartDto(p.PartNumber, p.Size, p.ETag)).ToList();
        return AssetMapping.ToDto(u, received);
    }
}

/// <summary>Fresh presigned URLs for specific parts. URLs are short-lived, so a long upload asks for more as it goes.</summary>
public sealed record PresignPartsQuery(Guid UploadId, IReadOnlyList<int> PartNumbers) : IQuery<IReadOnlyList<PartUrlDto>>;

public sealed class PresignPartsHandler(
    IRepository<Upload> uploads, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz,
    UploadStarter starter, UploadSettings settings) : IRequestHandler<PresignPartsQuery, IReadOnlyList<PartUrlDto>>
{
    public async Task<Result<IReadOnlyList<PartUrlDto>>> Handle(PresignPartsQuery r, CancellationToken ct)
    {
        var loaded = await UploadAccess.LoadAsync(uploads, assets, folders, q, authz, r.UploadId, ct);
        if (loaded.IsFailure) return loaded.Error;
        var u = loaded.Value.Upload;
        if (!u.IsOpen) return Error.Conflict($"The upload is {u.Status.ToString().ToLowerInvariant()}; no more parts can be sent.");
        var numbers = r.PartNumbers.Distinct().Order().ToList();
        if (numbers.Count == 0 || numbers.Count > settings.MaxPartUrlsPerRequest)
            return Error.Validation(ContentMapping.Fail("partNumbers", $"Ask for between 1 and {settings.MaxPartUrlsPerRequest} parts at a time."));
        if (numbers.Any(n => n < 1 || n > u.PartCount)) return Error.Validation(ContentMapping.Fail("partNumbers", $"Part numbers run from 1 to {u.PartCount}."));
        if (u.ExpiresAt < DateTimeOffset.UtcNow) return Error.Conflict("The upload session has expired. Start a new upload.");
        return starter.PresignRange(u, numbers).ToList();
    }
}

// ------------------------------------------------------------------------------------------------- complete / abort

public sealed record PartEtag(int PartNumber, string ETag);

/// <summary>
/// Assembles the parts into one object and hands it to the worker for hashing. Returns as soon as that is queued
/// (hashing a 20 GB file takes time): poll the upload for `ready`, `duplicate` or `failed`.
/// </summary>
public sealed record CompleteUploadCommand(Guid UploadId, IReadOnlyList<PartEtag>? Parts, string? Sha256) : ICommand<UploadDto>;

public sealed class CompleteUploadHandler(
    IRepository<Upload> uploads, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz,
    IObjectStore store, UploadStarter starter, IClock clock, IEventCollector events) : IRequestHandler<CompleteUploadCommand, UploadDto>
{
    public async Task<Result<UploadDto>> Handle(CompleteUploadCommand c, CancellationToken ct)
    {
        var loaded = await UploadAccess.LoadAsync(uploads, assets, folders, q, authz, c.UploadId, ct);
        if (loaded.IsFailure) return loaded.Error;
        var (u, asset, _) = loaded.Value;
        if (u.Status is UploadStatus.Processing or UploadStatus.Ready or UploadStatus.Duplicate) return AssetMapping.ToDto(u); // repeating a completed call is harmless
        if (!u.IsOpen) return Error.Conflict($"The upload is {u.Status.ToString().ToLowerInvariant()}.");
        if (c.Sha256 is not null && !Hex.IsSha256(c.Sha256)) return Error.Validation(ContentMapping.Fail("sha256", "Expected 64 hexadecimal characters."));

        // A part that was sent twice (a resume) can be listed twice by some stores. Timestamps only have one-second resolution, so they
        // cannot decide it alone: prefer the copy the client names by ETag, then one of the right size, then the newest.
        var claimedTags = (c.Parts ?? []).ToDictionary(x => x.PartNumber, x => x.ETag.Trim('"'), EqualityComparer<int>.Default);
        var stored = (await store.ListPartsAsync(u.Bucket, u.StorageKey, u.S3UploadId, ct))
            .GroupBy(p => p.PartNumber).ToDictionary(g => g.Key, g => g
                .OrderBy(p => claimedTags.TryGetValue(p.PartNumber, out var tag) && string.Equals(p.ETag.Trim('"'), tag, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(p => p.Size == ExpectedSize(u, p.PartNumber) ? 1 : 0)
                .ThenBy(p => p.LastModified ?? DateTimeOffset.MinValue)
                .Last());
        var missing = Enumerable.Range(1, u.PartCount).Where(n => !stored.ContainsKey(n)).ToList();
        var wrongSize = Enumerable.Range(1, u.PartCount).Where(n => stored.TryGetValue(n, out var p) && p.Size != ExpectedSize(u, n)).ToList();
        if (missing.Count > 0 || wrongSize.Count > 0)
            return new Error("upload_incomplete", "Some parts are missing or have the wrong size. Send them and complete again.", ErrorType.Conflict,
                new Dictionary<string, string[]> { ["missingParts"] = [string.Join(",", missing)], ["wrongSizeParts"] = [string.Join(",", wrongSize)] });

        // If the client says which ETags it got, make sure storage agrees: catches a part that was overwritten or corrupted.
        foreach (var claimed in c.Parts ?? [])
            if (!stored.TryGetValue(claimed.PartNumber, out var p) || !string.Equals(p.ETag.Trim('"'), claimed.ETag.Trim('"'), StringComparison.OrdinalIgnoreCase))
                return Error.Validation(ContentMapping.Fail("parts", $"ETag for part {claimed.PartNumber} does not match what storage received."));

        // The client already knows the content's hash and it matches an existing asset: stop here and discard the parts.
        // The outcome is recorded (so it survives the request) and the API answers 409 from the "duplicate" status.
        if (!u.AllowDuplicate && c.Sha256 is not null && await starter.FindDuplicateAsync(c.Sha256.ToLowerInvariant(), asset.Id, ct) is { } dup)
        {
            await store.AbortMultipartAsync(u.Bucket, u.StorageKey, u.S3UploadId, ct);
            u.MarkDuplicate(c.Sha256.ToLowerInvariant(), u.DeclaredSize, dup.Id);
            if (asset.CurrentVersionId is null && asset.ChecksumSha256 is null) asset.ChangeStatus(AssetStatuses.Deleted, $"Duplicate of {dup.Id}", clock.UtcNow);
            return AssetMapping.ToDto(u);
        }

        await store.CompleteMultipartAsync(u.Bucket, u.StorageKey, u.S3UploadId, stored.Values.OrderBy(p => p.PartNumber), ct);
        u.MarkProcessing(clock.UtcNow);
        events.Raise(new UploadCompleted(u.Id, u.AssetId));
        return AssetMapping.ToDto(u);
    }

    private static long ExpectedSize(Upload u, int part) =>
        part < u.PartCount ? u.PartSize : u.DeclaredSize - (long)u.PartSize * (u.PartCount - 1);
}

public sealed record AbortUploadCommand(Guid UploadId) : ICommand<UploadDto>;

public sealed class AbortUploadHandler(
    IRepository<Upload> uploads, IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IObjectStore store)
    : IRequestHandler<AbortUploadCommand, UploadDto>
{
    public async Task<Result<UploadDto>> Handle(AbortUploadCommand c, CancellationToken ct)
    {
        var loaded = await UploadAccess.LoadAsync(uploads, assets, folders, q, authz, c.UploadId, ct);
        if (loaded.IsFailure) return loaded.Error;
        var u = loaded.Value.Upload;
        if (u.Status == UploadStatus.Aborted) return AssetMapping.ToDto(u);
        if (!u.IsOpen) return Error.Conflict($"The upload is {u.Status.ToString().ToLowerInvariant()} and can no longer be aborted.");
        await store.AbortMultipartAsync(u.Bucket, u.StorageKey, u.S3UploadId, ct);
        u.MarkAborted();
        return AssetMapping.ToDto(u);
    }
}

// ------------------------------------------------------------------------------------------------------- bulk upload

/// <summary>
/// Creates one draft asset per file, all in the same folder with the same batch metadata and tags, and opens an upload
/// for each. Failures are reported per file, so 199 good files are not lost to one bad one.
/// </summary>
public sealed record BulkUploadCommand(Guid? FolderId, Guid? BatchId, JsonElement? Metadata, IReadOnlyList<string>? Tags, IReadOnlyList<BulkFileRequest> Files)
    : ICommand<BulkUploadResultDto>;

public sealed class BulkUploadValidator : AbstractValidator<BulkUploadCommand>
{
    public const int MaxFiles = 500;
    public BulkUploadValidator()
    {
        RuleFor(x => x.Files).NotEmpty().Must(f => f.Count <= MaxFiles).WithMessage($"At most {MaxFiles} files per request; send several requests with the same batchId for more.");
    }
}

public sealed class BulkUploadHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, AssetMetadataBuilder builder,
    UploadStarter starter, ITenantContext tenant, IClock clock) : IRequestHandler<BulkUploadCommand, BulkUploadResultDto>
{
    private const int FirstPartUrls = 4;

    public async Task<Result<BulkUploadResultDto>> Handle(BulkUploadCommand c, CancellationToken ct)
    {
        Folder? folder = null;
        if (c.FolderId is { } fid)
        {
            folder = await q.FirstOrDefaultAsync(folders.Query().Where(f => f.Id == fid), ct);
            if (folder is null) return Error.Validation(ContentMapping.Fail("folderId", "Folder not found."));
        }
        var tags = AssetMapping.TagErrors(c.Tags, out var tagError);
        if (tagError is not null) return Error.Validation(ContentMapping.Fail("tags", tagError));

        var profile = await authz.GetProfileAsync(ct);
        var batchId = c.BatchId ?? Uuid7.NewGuid();
        var built = new Dictionary<string, Result<BuiltMetadata>>(); // batch metadata validated once per asset type
        var items = new List<BulkUploadItemDto>();
        var now = clock.UtcNow;

        foreach (var file in c.Files)
        {
            var name = UploadPolicy.CleanFileName(file.FileName);
            var type = UploadPolicy.InferType(name);
            if (type is null) { items.Add(new(file.FileName, null, null, "This file type is not accepted.")); continue; }

            if (!await authz.CanAsync(Permissions.AssetsCreate, new ResourceContext { Type = "asset", FolderPath = folder?.Path, AssetType = type, OwnerId = profile.UserId, Status = AssetStatuses.Draft }, ct))
                return Error.Forbidden();

            if (!built.TryGetValue(type, out var meta))
                built[type] = meta = await builder.BuildAsync(type, folder, c.Metadata, requireFields: false, ct);
            if (meta.IsFailure) { items.Add(new(file.FileName, null, null, $"Batch metadata is not valid for {type} assets: {Describe(meta.Error)}")); continue; }

            var title = Path.GetFileNameWithoutExtension(name);
            var asset = Asset.CreateShell(tenant.TenantId, folder?.Id, type, title.Length == 0 ? name : title[..Math.Min(title.Length, 300)], profile.UserId, now);
            asset.SetMetadata(meta.Value.Metadata, meta.Value.SchemaVersion, meta.Value.Projection, now);
            if (tags is not null) asset.SetTags(tags, now);

            var started = await starter.StartAsync(asset, name, file.Size, file.ContentType, allowDuplicate: false, null, batchId, FirstPartUrls, ct);
            if (started.IsFailure) { items.Add(new(file.FileName, null, null, Describe(started.Error))); continue; }
            assets.Add(asset);
            items.Add(new(file.FileName, asset.Id, started.Value, null));
        }
        var created = items.Count(i => i.AssetId is not null);
        return new BulkUploadResultDto(batchId, created, items.Count - created, items);
    }

    private static string Describe(Error e) =>
        e.FieldErrors is { Count: > 0 } f ? string.Join("; ", f.Select(kv => $"{kv.Key}: {string.Join(" ", kv.Value)}")) : e.Message;
}
