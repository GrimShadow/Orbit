using Dam.Application.Abstractions;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Requires ITenantContext, ICurrentUser and IAuthorizationService from the host.</summary>
    public static IServiceCollection AddDamInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<DamDbContext>((sp, o) => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<EfDomainEventSource>();
        services.AddScoped<IDomainEventSource>(sp => sp.GetRequiredService<EfDomainEventSource>());
        services.AddScoped<IEventCollector>(sp => sp.GetRequiredService<EfDomainEventSource>());
        services.AddScoped<IOutbox, EfOutbox>();
        return services;
    }
}
