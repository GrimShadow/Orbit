using Dam.Application.Abstractions;
using Dam.Application.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure.Integrations;

public static class IntegrationsRegistration
{
    /// <summary>
    /// The connector runtime, the HTTP connector (<c>http</c>) and the stored-credentials connector (<c>pads4</c>, which makes no calls yet). Needs <c>AddDamNotifications</c> (the guarded outbound client and the secret protector).
    /// Env: DAM_INTEGRATIONS_TIMEOUT_SECONDS, and the webhook address rules (DAM_WEBHOOK_ALLOW_PRIVATE).
    /// </summary>
    public static IServiceCollection AddDamIntegrations(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton(IntegrationSettings.Read(cfg));
        services.AddScoped<IConnectionStore, EfConnectionStore>();
        services.AddScoped<ConnectionRuntime>();
        services.AddScoped<IConnector>(sp => new HttpApiConnector(ShippedConnectors.Http, sp.GetRequiredService<ConnectionRuntime>()));
        services.AddScoped<IConnector>(_ => new StoredCredentialsConnector(ShippedConnectors.Pads4));   // next step: replace with PADS4's own connector
        return services;
    }
}
