using Dam.Application.Abstractions;
using Dam.Application.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure.Integrations;

public static class IntegrationsRegistration
{
    /// <summary>
    /// The connector runtime and the connectors that talk HTTP (<c>http</c>, <c>pads4</c>). Needs <c>AddDamNotifications</c> (the guarded outbound client and the secret protector).
    /// Env: DAM_INTEGRATIONS_TIMEOUT_SECONDS, and the webhook address rules (DAM_WEBHOOK_ALLOW_PRIVATE).
    /// </summary>
    public static IServiceCollection AddDamIntegrations(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton(IntegrationSettings.Read(cfg));
        services.AddScoped<IConnectionStore, EfConnectionStore>();
        services.AddScoped<ConnectionRuntime>();
        services.AddScoped<IConnector>(sp => new HttpApiConnector(ShippedConnectors.Http, sp.GetRequiredService<ConnectionRuntime>()));
        services.AddScoped<IConnector>(sp => new HttpApiConnector(ShippedConnectors.Pads4, sp.GetRequiredService<ConnectionRuntime>()));
        return services;
    }
}
