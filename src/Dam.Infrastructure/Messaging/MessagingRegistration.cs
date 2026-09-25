using Dam.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Dam.Infrastructure.Messaging;

public static class MessagingRegistration
{
    public static MessagingOptions ReadMessagingOptions(this IConfiguration cfg) => new()
    {
        RabbitUrl = cfg["DAM_RABBITMQ_URL"] ?? throw new InvalidOperationException("DAM_RABBITMQ_URL is not set."),
        MaxAttempts = int.TryParse(cfg["DAM_MQ_MAX_ATTEMPTS"], out var m) ? m : 5,
        RetryBaseDelayMs = int.TryParse(cfg["DAM_MQ_RETRY_BASE_MS"], out var d) ? d : 2000,
    };

    /// <summary>Identity for background hosts: tenant is set per message, no interactive user.</summary>
    public static IServiceCollection AddDamWorkerIdentity(this IServiceCollection services)
    {
        services.AddScoped<MutableTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<MutableTenantContext>());
        services.AddScoped<ICurrentUser, SystemUser>();
        return services;
    }

    public static IServiceCollection AddDamMessaging(this IServiceCollection services, MessagingOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<RabbitConnection>();
        return services;
    }

    /// <summary>Register consumers before <see cref="AddOutboxRelay"/> so queues are bound before anything is published.</summary>
    public static IServiceCollection AddEventConsumer<T>(this IServiceCollection services) where T : EventConsumer
    {
        services.AddScoped<T>();
        services.AddSingleton<IHostedService, ConsumerHost<T>>();
        return services;
    }

    public static IServiceCollection AddOutboxRelay(this IServiceCollection services, string systemConnectionString)
    {
        services.AddSingleton(new NpgsqlDataSourceBuilder(systemConnectionString).Build());
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxRelay>());
        return services;
    }
}
