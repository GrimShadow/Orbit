using Dam.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Net;

namespace Dam.Infrastructure.Search;

public static class SearchRegistration
{
    /// <summary>OpenSearch wiring (env: DAM_OPENSEARCH_URL, decision D4). The low-level client is used directly rather
    /// than the fluent high-level DSL: every mapping and document here is shaped by a tenant's own metadata schema, so
    /// it is built as plain JSON (<see cref="IndexMappingBuilder"/>, <see cref="SearchDocumentBuilder"/>) rather than a
    /// fixed set of POCOs.</summary>
    public static IServiceCollection AddDamSearch(this IServiceCollection services, IConfiguration cfg)
    {
        var options = cfg.ReadSearchOptions();
        services.AddSingleton(options);
        services.AddSingleton<IOpenSearchLowLevelClient>(_ => new OpenSearchLowLevelClient(new ConnectionConfiguration(options.Url)));
        services.AddScoped<OpenSearchIndexService>();
        services.AddScoped<ISearchIndexAdmin>(sp => sp.GetRequiredService<OpenSearchIndexService>());
        services.AddScoped<ISearchIndexWriter>(sp => sp.GetRequiredService<OpenSearchIndexService>());
        services.AddScoped<ISearchQueryService, OpenSearchQueryService>();
        return services;
    }
}
