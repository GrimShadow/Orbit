using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using Dam.Shared.Identity;
using FluentValidation;
using Group = Dam.Domain.Identity.Group;

namespace Dam.Application.Assets;

internal static class AssetAccess
{
    /// <summary>The asset plus the path of its folder, which the policy needs.</summary>
    public static async Task<(Asset? Asset, string? FolderPath)> LoadAsync(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, Guid id, CancellationToken ct)
    {
        var a = await q.FirstOrDefaultAsync(assets.Query().Where(x => x.Id == id), ct);
        if (a is null) return (null, null);
        var path = a.FolderId is { } fid ? (await q.FirstOrDefaultAsync(folders.Query().Where(f => f.Id == fid), ct))?.Path : null;
        return (a, path);
    }
}

// ---------------------------------------------------------------------------------------------------------- create

/// <summary>
/// Creates an asset shell (draft, no content yet). Metadata is validated against the type's schema, including required
/// fields unless <paramref name="AllowIncomplete"/> (upload now, describe later). Permission is per folder.
/// </summary>
public sealed record CreateAssetCommand(Guid? FolderId, string Type, string Title, JsonElement? Metadata, IReadOnlyList<string>? Tags, bool AllowIncomplete = false)
    : ICommand<AssetDto>;

public sealed class CreateAssetValidator : AbstractValidator<CreateAssetCommand>
{
    public CreateAssetValidator()
    {
        RuleFor(x => x.Type).Must(AssetTypes.IsKnown).WithMessage($"Type must be one of: {string.Join(", ", AssetTypes.All)}.");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
    }
}

public sealed class CreateAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, AssetMetadataBuilder builder,
    ITenantContext tenant, IClock clock) : IRequestHandler<CreateAssetCommand, AssetDto>
{
    // The response is built from the asset just created. Reading it back through the normal permission check would hide a
    // draft from a user who may create assets but not read other people's drafts.
    public async Task<Result<AssetDto>> Handle(CreateAssetCommand c, CancellationToken ct)
    {
        Folder? folder = null;
        if (c.FolderId is { } fid)
        {
            folder = await q.FirstOrDefaultAsync(folders.Query().Where(f => f.Id == fid), ct);
            if (folder is null) return Error.Validation(ContentMapping.Fail("folderId", "Folder not found."));
        }
        var profile = await authz.GetProfileAsync(ct);
        var resource = new ResourceContext { Type = "asset", FolderPath = folder?.Path, AssetType = c.Type, OwnerId = profile.UserId, Status = AssetStatuses.Draft };
        if (!await authz.CanAsync(Permissions.AssetsCreate, resource, ct)) return Error.Forbidden();

        var tags = AssetMapping.TagErrors(c.Tags, out var tagError);
        if (tagError is not null) return Error.Validation(ContentMapping.Fail("tags", tagError));

        var built = await builder.BuildAsync(c.Type, folder, c.Metadata, requireFields: !c.AllowIncomplete, ct);
        if (built.IsFailure) return built.Error;

        var now = clock.UtcNow;
        var asset = Asset.CreateShell(tenant.TenantId, folder?.Id, c.Type, c.Title, profile.UserId, now);
        asset.SetMetadata(built.Value.Metadata, built.Value.SchemaVersion, built.Value.Projection, now);
        if (tags is not null) asset.SetTags(tags, now);
        assets.Add(asset);
        return AssetMapping.ToDto(asset);
    }
}

// ------------------------------------------------------------------------------------------------------------ read

/// <summary>An asset the caller may not see is reported as not found, so its existence is not revealed.</summary>
public sealed record GetAssetQuery(Guid Id) : IQuery<AssetDto>;

public sealed class GetAssetHandler(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<GetAssetQuery, AssetDto>
{
    public async Task<Result<AssetDto>> Handle(GetAssetQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.Id, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        return AssetMapping.ToDto(asset);
    }
}

/// <summary>
/// Lists the assets the caller may read. Filtering uses the same access clauses as single-asset checks. This is an interim
/// listing (rows are filtered in memory); Step 1.7 replaces it with search, which pushes the clauses into the index.
/// </summary>
public sealed record ListAssetsQuery(Guid? FolderId, bool IncludeSubfolders, string? Status, string? Type, string? Cursor, int? Limit)
    : IQuery<Page<AssetDto>>;

public sealed class ListAssetsHandler(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz)
    : IRequestHandler<ListAssetsQuery, Page<AssetDto>>
{
    private const int MaxRowsScanned = 5000;

    public async Task<Result<Page<AssetDto>>> Handle(ListAssetsQuery r, CancellationToken ct)
    {
        var limit = Math.Clamp(r.Limit ?? 50, 1, 200);
        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return new Page<AssetDto>([], null);

        var allFolders = await q.ToListAsync(folders.Query(), ct);
        var pathById = allFolders.ToDictionary(f => f.Id, f => f.Path);
        var query = assets.Query();
        if (r.Type is not null) query = query.Where(a => a.Type == r.Type);
        // Deleted assets are a trash: hidden from the default listing, and shown only when asked for by status.
        query = r.Status is not null ? query.Where(a => a.Status == r.Status) : query.Where(a => a.Status != AssetStatuses.Deleted);
        if (r.FolderId is { } fid)
        {
            var ids = r.IncludeSubfolders && pathById.TryGetValue(fid, out var root)
                ? allFolders.Where(f => Paths.IsSelfOrDescendant(root, f.Path)).Select(f => f.Id).ToList() : [fid];
            query = query.Where(a => a.FolderId != null && ids.Contains(a.FolderId.Value));
        }

        var cursor = Paging.ParseCursor(r.Cursor);
        var page = new List<Asset>();
        var scanned = 0;
        var more = false;
        while (page.Count <= limit && scanned < MaxRowsScanned)
        {
            // Newest first: ids are UUIDv7 (time-ordered), so descending id is descending creation time - a just-uploaded asset
            // is on the first page rather than buried behind everything older.
            var chunk = cursor is { } after ? query.Where(a => a.Id.CompareTo(after) < 0) : query;
            var rows = await q.ToListAsync(chunk.OrderByDescending(a => a.Id).Take(limit * 4), ct);
            if (rows.Count == 0) break;
            foreach (var a in rows)
            {
                cursor = a.Id;
                scanned++;
                var path = a.FolderId is { } f && pathById.TryGetValue(f, out var p) ? p : null;
                if (!clauses.Any(c => c.Matches(a.ToResource(path), profile.UserId))) continue;
                page.Add(a);
                if (page.Count > limit) { more = true; break; }
            }
            if (more || rows.Count < limit * 4) break;
        }
        if (page.Count > limit) page.RemoveAt(limit);
        return new Page<AssetDto>(page.Select(AssetMapping.ToDto).ToList(), more ? page[^1].Id.ToString() : null);
    }
}

// ---------------------------------------------------------------------------------------------------------- update

/// <summary>
/// Changes title, tags and/or metadata (a full replacement of the metadata). Drafts may stay incomplete; assets past
/// draft must satisfy the schema's required fields.
/// </summary>
public sealed record UpdateAssetCommand(Guid Id, string? Title, JsonElement? Metadata, IReadOnlyList<string>? Tags) : ICommand<Guid>;

public sealed class UpdateAssetValidator : AbstractValidator<UpdateAssetCommand>
{
    public UpdateAssetValidator() => RuleFor(x => x.Title).MaximumLength(300).When(x => x.Title is not null);
}

public sealed class UpdateAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, AssetMetadataBuilder builder, IClock clock)
    : IRequestHandler<UpdateAssetCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateAssetCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.Id, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.AssetsUpdate, asset.ToResource(path), ct)) return Error.Forbidden();
        if (asset.Status == AssetStatuses.Deleted) return Error.Conflict("The asset is deleted.");
        if (c.Title is { Length: 0 }) return Error.Validation(ContentMapping.Fail("title", "Title cannot be empty."));

        var tags = AssetMapping.TagErrors(c.Tags, out var tagError);
        if (tagError is not null) return Error.Validation(ContentMapping.Fail("tags", tagError));

        var now = clock.UtcNow;
        if (c.Metadata is not null)
        {
            // The caller sends the whole document, so folder defaults are not re-applied on top of it.
            var built = await builder.BuildAsync(asset.Type, null, c.Metadata, requireFields: asset.Status != AssetStatuses.Draft, ct);
            if (built.IsFailure) return built.Error;
            asset.SetMetadata(built.Value.Metadata, built.Value.SchemaVersion, built.Value.Projection, now);
        }
        if (c.Title is not null) asset.Rename(c.Title, now);
        if (tags is not null) asset.SetTags(tags, now);
        return asset.Id;
    }
}

public sealed record MoveAssetCommand(Guid Id, Guid? FolderId) : ICommand<Guid>;

public sealed class MoveAssetHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<MoveAssetCommand, Guid>
{
    public async Task<Result<Guid>> Handle(MoveAssetCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.Id, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        Folder? target = null;
        if (c.FolderId is { } fid)
        {
            target = await q.FirstOrDefaultAsync(folders.Query().Where(f => f.Id == fid), ct);
            if (target is null) return Error.Validation(ContentMapping.Fail("folderId", "Folder not found."));
        }
        // Moving needs authority over where the asset is and where it is going.
        if (!await authz.CanAsync(Permissions.AssetsMove, asset.ToResource(path), ct)
            || !await authz.CanAsync(Permissions.AssetsMove, asset.ToResource(target?.Path), ct)) return Error.Forbidden();
        asset.MoveTo(target?.Id, clock.UtcNow);
        return asset.Id;
    }
}

/// <summary>Soft delete: the asset becomes "deleted" and disappears from normal views; retention decides when it is purged.</summary>
public sealed record DeleteAssetCommand(Guid Id) : ICommand<Guid>;

public sealed class DeleteAssetHandler(IRepository<Asset> assets, IRepository<Folder> folders, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<DeleteAssetCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteAssetCommand c, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, c.Id, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");
        if (!await authz.CanAsync(Permissions.AssetsDelete, asset.ToResource(path), ct)) return Error.Forbidden();
        asset.ChangeStatus(AssetStatuses.Deleted, "Deleted by a user", clock.UtcNow);
        return asset.Id;
    }
}
