using System.Text.Json;

namespace Dam.Shared.Content;

/// <param name="Type">"static" or "smart".</param>
/// <param name="Query">The saved search (spec: "query jsonb") - present only for a smart collection.</param>
/// <param name="AssetIds">Explicit members - present (possibly empty) only for a static collection.</param>
/// <param name="Visibility">"private" (owner only) or "tenant" (anyone who can read assets).</param>
public sealed record CollectionDto(
    Guid Id, string Name, string Type, IReadOnlyDictionary<string, JsonElement>? Query, IReadOnlyList<Guid> AssetIds,
    string Visibility, Guid OwnerId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
