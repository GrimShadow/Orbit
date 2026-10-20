using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Shared.Content;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <param name="Type">"static" (explicit <see cref="AssetIds"/>) or "smart" (a saved <see cref="Query"/>).</param>
public sealed record CreateCollectionRequest(string Name, string Type, IReadOnlyList<Guid>? AssetIds, SearchRequestBody? Query);
public sealed record RenameCollectionRequest(string Name);
public sealed record SetCollectionQueryRequest(SearchRequestBody Query);
/// <param name="Remove">True removes <see cref="AssetIds"/> instead of adding them.</param>
public sealed record ChangeCollectionAssetsRequest(IReadOnlyList<Guid> AssetIds, bool Remove = false);
/// <param name="Visibility">"private" (owner only) or "tenant" (anyone who can read assets).</param>
public sealed record ShareCollectionRequest(string Visibility);

/// <summary>Saved searches (spec A4.1 `Collection`, Step 1.7c): CRUD, membership, and sharing.</summary>
public static class CollectionEndpoints
{
    public static void MapCollectionEndpoints(this RouteGroupBuilder v1)
    {
        var collections = v1.MapGroup("/collections").WithTags("Collections");

        collections.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListCollectionsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListCollections").WithSummary("Every collection the caller owns, that is shared tenant-wide, or - for a curator - every one").Produces<IReadOnlyList<CollectionDto>>();

        collections.MapPost("", (CreateCollectionRequest r, IDispatcher d, CancellationToken ct) => r.Type.ToLowerInvariant() switch
            {
                "static" => Run(d, new CreateStaticCollectionCommand(r.Name, r.AssetIds ?? []), v => Task.FromResult(Results.Created($"/api/v1/collections/{v.Id}", v)), ct),
                "smart" => Run(d, new CreateSmartCollectionCommand(r.Name, ToSearchQuery(r.Query ?? new SearchRequestBody(null, null, null, null))),
                    v => Task.FromResult(Results.Created($"/api/v1/collections/{v.Id}", v)), ct),
                _ => Task.FromResult(Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Must be 'static' or 'smart'."] })),
            })
            .WithName("CreateCollection").Produces<CollectionDto>(StatusCodes.Status201Created);

        collections.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetCollectionQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetCollection").Produces<CollectionDto>();

        collections.MapGet("/{id:guid}/assets", (Guid id, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetCollectionContentsQuery(id, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetCollectionContents").WithSummary("A static collection's members, or a smart collection's saved search run now - both filtered to what the caller may read").Produces<SearchResultDto>();

        collections.MapPut("/{id:guid}", (Guid id, RenameCollectionRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new RenameCollectionCommand(id, r.Name), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("RenameCollection").Produces<CollectionDto>();

        collections.MapPut("/{id:guid}/query", (Guid id, SetCollectionQueryRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new SetCollectionQueryCommand(id, ToSearchQuery(r.Query)), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("SetCollectionQuery").WithSummary("Smart collections only").Produces<CollectionDto>();

        collections.MapPost("/{id:guid}/assets", (Guid id, ChangeCollectionAssetsRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new ChangeCollectionAssetsCommand(id, r.AssetIds, r.Remove), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ChangeCollectionAssets").WithSummary("Static collections only").Produces<CollectionDto>();

        collections.MapPost("/{id:guid}/share", (Guid id, ShareCollectionRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new ShareCollectionCommand(id, r.Visibility), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ShareCollection").Produces<CollectionDto>();

        collections.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteCollectionCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteCollection");
    }

    private static SearchQuery ToSearchQuery(SearchRequestBody r) => new(r.Query, r.Filters, r.Facets, r.Sort, r.Highlight, r.SimilarTo, null, null, r.Semantic);
}
