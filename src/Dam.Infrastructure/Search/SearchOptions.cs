using Microsoft.Extensions.Configuration;

namespace Dam.Infrastructure.Search;

/// <summary>OpenSearch connection settings (env: DAM_OPENSEARCH_URL, decision D4).</summary>
public sealed class SearchOptions
{
    public required Uri Url { get; init; }
}

public static class SearchOptionsExtensions
{
    public static SearchOptions ReadSearchOptions(this IConfiguration cfg) => new()
    {
        Url = new Uri(cfg["DAM_OPENSEARCH_URL"] ?? throw new InvalidOperationException("DAM_OPENSEARCH_URL is not set.")),
    };
}
