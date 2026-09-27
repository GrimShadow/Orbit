using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Content;

internal static class CollectionMapping
{
    public static CollectionDto ToDto(Collection c) => new(
        c.Id, c.Name, c.Type.ToString().ToLowerInvariant(), c.Query, c.AssetIds,
        c.Visibility.ToString().ToLowerInvariant(), c.OwnerId, c.CreatedAt, c.UpdatedAt);

    /// <summary>The stored "query jsonb" round-trips through <see cref="SearchQuery"/> with no naming-policy games:
    /// both directions use <see cref="JsonSerializer"/>'s default (PascalCase) options, so what a smart collection saves
    /// is exactly what running it later deserialises - the client-facing camelCase JSON is only ever a request/response
    /// shape at the API boundary, never what is actually stored.</summary>
    public static Dictionary<string, JsonElement> ToStored(SearchQuery query) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.SerializeToElement(query))!;

    public static SearchQuery FromStored(IReadOnlyDictionary<string, JsonElement> stored) =>
        JsonSerializer.Deserialize<SearchQuery>(JsonSerializer.SerializeToElement(stored))!;

    /// <summary>Whether <paramref name="userId"/> may see this collection at all (spec: "share settings"): the owner,
    /// anyone when it is tenant-visible, or a curator who can manage every collection regardless.</summary>
    public static bool Visible(Collection c, Guid userId, bool canManage) =>
        canManage || c.OwnerId == userId || c.Visibility == CollectionVisibility.Tenant;
}

// ------------------------------------------------------------------------------------------------------------- create

public sealed record CreateStaticCollectionCommand(string Name, IReadOnlyList<Guid> AssetIds) : ICommand<CollectionDto>;

public sealed class CreateStaticCollectionValidator : AbstractValidator<CreateStaticCollectionCommand>
{
    public CreateStaticCollectionValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
}

public sealed class CreateStaticCollectionHandler(IRepository<Collection> collections, IRepository<Asset> assets, IQueryExecutor q,
    IAuthorizationService authz, ITenantContext tenant, ICurrentUser me, IClock clock) : IRequestHandler<CreateStaticCollectionCommand, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(CreateStaticCollectionCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var missing = await CollectionAccess.MissingAssetIds(assets, q, c.AssetIds, ct);
        if (missing.Count > 0) return Error.Validation(ContentMapping.Fail("assetIds", $"Unknown asset id(s): {string.Join(", ", missing)}."));

        var collection = Collection.CreateStatic(tenant.TenantId, c.Name, c.AssetIds, me.UserId ?? Guid.Empty, clock.UtcNow);
        collections.Add(collection);
        return CollectionMapping.ToDto(collection);
    }
}

public sealed record CreateSmartCollectionCommand(string Name, SearchQuery Query) : ICommand<CollectionDto>;

public sealed class CreateSmartCollectionValidator : AbstractValidator<CreateSmartCollectionCommand>
{
    public CreateSmartCollectionValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
}

public sealed class CreateSmartCollectionHandler(IRepository<Collection> collections, IAuthorizationService authz, ITenantContext tenant, ICurrentUser me, IClock clock)
    : IRequestHandler<CreateSmartCollectionCommand, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(CreateSmartCollectionCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var collection = Collection.CreateSmart(tenant.TenantId, c.Name, CollectionMapping.ToStored(c.Query), me.UserId ?? Guid.Empty, clock.UtcNow);
        collections.Add(collection);
        return CollectionMapping.ToDto(collection);
    }
}

// -------------------------------------------------------------------------------------------------------------- read

public sealed record GetCollectionQuery(Guid Id) : IQuery<CollectionDto>;

public sealed class GetCollectionHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, ICurrentUser me)
    : IRequestHandler<GetCollectionQuery, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(GetCollectionQuery r, CancellationToken ct)
    {
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(c => c.Id == r.Id), ct);
        var canManage = await authz.CanAsync(Permissions.CollectionsManage, ct: ct);
        if (collection is null || !CollectionMapping.Visible(collection, me.UserId ?? Guid.Empty, canManage)) return Error.NotFound("Collection not found.");
        return CollectionMapping.ToDto(collection);
    }
}

public sealed record ListCollectionsQuery : IQuery<IReadOnlyList<CollectionDto>>;

public sealed class ListCollectionsHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, ICurrentUser me)
    : IRequestHandler<ListCollectionsQuery, IReadOnlyList<CollectionDto>>
{
    public async Task<Result<IReadOnlyList<CollectionDto>>> Handle(ListCollectionsQuery r, CancellationToken ct)
    {
        var canManage = await authz.CanAsync(Permissions.CollectionsManage, ct: ct);
        var userId = me.UserId ?? Guid.Empty;
        var all = await q.ToListAsync(collections.Query().OrderBy(c => c.Name), ct);
        return all.Where(c => CollectionMapping.Visible(c, userId, canManage)).Select(CollectionMapping.ToDto).ToList();
    }
}

/// <summary>Resolves a collection's actual contents through search - a static collection's is a filter on its member
/// ids, a smart one is its saved query - so both go through the exact same access-scoped, paginated path a direct
/// search would (spec: "Smart = saved search").</summary>
public sealed record GetCollectionContentsQuery(Guid Id, string? Cursor, int? Limit) : IQuery<SearchResultDto>;

public sealed class GetCollectionContentsHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, ISearchQueryService search, ICurrentUser me)
    : IRequestHandler<GetCollectionContentsQuery, SearchResultDto>
{
    public async Task<Result<SearchResultDto>> Handle(GetCollectionContentsQuery r, CancellationToken ct)
    {
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(c => c.Id == r.Id), ct);
        var canManage = await authz.CanAsync(Permissions.CollectionsManage, ct: ct);
        var userId = me.UserId ?? Guid.Empty;
        if (collection is null || !CollectionMapping.Visible(collection, userId, canManage)) return Error.NotFound("Collection not found.");

        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return new SearchResultDto([], 0, [], null);

        var query = collection.Type == CollectionType.Static
            ? new SearchQuery(null, new Dictionary<string, string[]> { ["id"] = collection.AssetIds.Select(id => id.ToString()).ToArray() }, null, null, false, null, r.Cursor, r.Limit)
            : CollectionMapping.FromStored(collection.Query!) with { Cursor = r.Cursor, Limit = r.Limit };
        return await search.SearchAsync(query, clauses, profile.UserId, ct);
    }
}

// ------------------------------------------------------------------------------------------------------------ update

public sealed record RenameCollectionCommand(Guid Id, string Name) : ICommand<CollectionDto>;

public sealed class RenameCollectionValidator : AbstractValidator<RenameCollectionCommand>
{
    public RenameCollectionValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
}

public sealed class RenameCollectionHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<RenameCollectionCommand, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(RenameCollectionCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(x => x.Id == c.Id), ct);
        if (collection is null) return Error.NotFound("Collection not found.");
        collection.Rename(c.Name, clock.UtcNow);
        return CollectionMapping.ToDto(collection);
    }
}

public sealed record SetCollectionQueryCommand(Guid Id, SearchQuery Query) : ICommand<CollectionDto>;

public sealed class SetCollectionQueryHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<SetCollectionQueryCommand, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(SetCollectionQueryCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(x => x.Id == c.Id), ct);
        if (collection is null) return Error.NotFound("Collection not found.");
        if (collection.Type != CollectionType.Smart) return Error.Conflict("Only a smart collection has a saved query.");
        collection.SetQuery(CollectionMapping.ToStored(c.Query), clock.UtcNow);
        return CollectionMapping.ToDto(collection);
    }
}

/// <param name="Remove">When true, <see cref="AssetIds"/> are removed instead of added.</param>
public sealed record ChangeCollectionAssetsCommand(Guid Id, IReadOnlyList<Guid> AssetIds, bool Remove) : ICommand<CollectionDto>;

public sealed class ChangeCollectionAssetsValidator : AbstractValidator<ChangeCollectionAssetsCommand>
{
    public ChangeCollectionAssetsValidator() => RuleFor(x => x.AssetIds).NotEmpty();
}

public sealed class ChangeCollectionAssetsHandler(IRepository<Collection> collections, IRepository<Asset> assets, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<ChangeCollectionAssetsCommand, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(ChangeCollectionAssetsCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(x => x.Id == c.Id), ct);
        if (collection is null) return Error.NotFound("Collection not found.");
        if (collection.Type != CollectionType.Static) return Error.Conflict("Only a static collection has explicit members.");

        if (!c.Remove)
        {
            var missing = await CollectionAccess.MissingAssetIds(assets, q, c.AssetIds, ct);
            if (missing.Count > 0) return Error.Validation(ContentMapping.Fail("assetIds", $"Unknown asset id(s): {string.Join(", ", missing)}."));
        }
        collection.ChangeAssets(c.Remove ? [] : c.AssetIds, c.Remove ? c.AssetIds : [], clock.UtcNow);
        return CollectionMapping.ToDto(collection);
    }
}

public sealed record ShareCollectionCommand(Guid Id, string Visibility) : ICommand<CollectionDto>;

public sealed class ShareCollectionValidator : AbstractValidator<ShareCollectionCommand>
{
    public ShareCollectionValidator() => RuleFor(x => x.Visibility).Must(v => Enum.TryParse<CollectionVisibility>(v, true, out _))
        .WithMessage("Visibility must be 'private' or 'tenant'.");
}

public sealed class ShareCollectionHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, IClock clock)
    : IRequestHandler<ShareCollectionCommand, CollectionDto>
{
    public async Task<Result<CollectionDto>> Handle(ShareCollectionCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(x => x.Id == c.Id), ct);
        if (collection is null) return Error.NotFound("Collection not found.");
        collection.SetVisibility(Enum.Parse<CollectionVisibility>(c.Visibility, true), clock.UtcNow);
        return CollectionMapping.ToDto(collection);
    }
}

// ------------------------------------------------------------------------------------------------------------ delete

public sealed record DeleteCollectionCommand(Guid Id) : ICommand<Guid>;

public sealed class DeleteCollectionHandler(IRepository<Collection> collections, IQueryExecutor q, IAuthorizationService authz, IEventCollector events)
    : IRequestHandler<DeleteCollectionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteCollectionCommand c, CancellationToken ct)
    {
        if (!await authz.CanAsync(Permissions.CollectionsManage, ct: ct)) return Error.Forbidden();
        var collection = await q.FirstOrDefaultAsync(collections.Query().Where(x => x.Id == c.Id), ct);
        if (collection is null) return Error.NotFound("Collection not found.");
        // A static collection's members must be re-indexed with this id dropped from their collection_ids - raised
        // explicitly (the aggregate itself is about to be removed, not just mutated) rather than through ChangeAssets.
        if (collection.Type == CollectionType.Static && collection.AssetIds.Count > 0)
            events.Raise(new CollectionAssetsChanged(collection.Id, [], collection.AssetIds));
        collections.Remove(collection);
        return c.Id;
    }
}

internal static class CollectionAccess
{
    public static async Task<List<Guid>> MissingAssetIds(IRepository<Asset> assets, IQueryExecutor q, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        var found = await q.ToListAsync(assets.Query().Where(a => distinct.Contains(a.Id)).Select(a => a.Id), ct);
        return distinct.Except(found).ToList();
    }
}
