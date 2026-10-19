using Dam.Application.Abstractions;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dam.Infrastructure.Notifications;

public static class NotificationsRegistration
{
    public static WebhookSettings ReadWebhookSettings(this IConfiguration cfg)
    {
        var settings = new WebhookSettings
        {
            AllowPrivate = string.Equals(cfg["DAM_WEBHOOK_ALLOW_PRIVATE"], "true", StringComparison.OrdinalIgnoreCase),
            Timeout = TimeSpan.FromSeconds(int.TryParse(cfg["DAM_WEBHOOK_TIMEOUT_SECONDS"], out var t) && t is > 0 and <= 30 ? t : 5),
        };
        if (int.TryParse(cfg["DAM_WEBHOOK_MAX_ATTEMPTS"], out var m) && m is >= 1 and <= 20)
            settings.Retry = WebhookRetryPolicy.Default with { MaxAttempts = m };
        return settings;
    }

    /// <summary>
    /// What every host needs to read and manage notifications, webhooks and the audit log, and (in the API) to make a delivery attempt on
    /// request: secrets, SMTP, the guarded outbound HTTP client, the deliverer, the audit reader. Env: DAM_SECRETS_KEY, DAM_SMTP_*,
    /// DAM_CONSOLE_PUBLIC_URL, DAM_WEBHOOK_*.
    /// </summary>
    public static IServiceCollection AddDamNotifications(this IServiceCollection services, IConfiguration cfg)
    {
        var webhook = cfg.ReadWebhookSettings();
        services.AddSingleton(webhook);
        services.AddSingleton(new NotificationSettings { ConsolePublicUrl = cfg["DAM_CONSOLE_PUBLIC_URL"] });
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(cfg["DAM_SECRETS_KEY"]));
        services.AddSingleton<IEmailSender>(SmtpOptions.Read(cfg) is { } smtp ? new SmtpEmailSender(smtp) : new NoOpEmailSender());
        services.AddSingleton<OutboundHttp>();
        services.AddSingleton<IOutboundUrlGuard, OutboundUrlGuard>();
        services.AddSingleton<IChatSender, ChatSender>();
        services.AddScoped<IWebhookDeliverer, WebhookDeliverer>();
        services.AddScoped<IAuditReader, EfAuditReader>();
        return services;
    }

    /// <summary>The worker side: the webhook and notification consumers and the dispatcher that makes webhook attempts right after commit.</summary>
    public static IServiceCollection AddDamNotificationConsumers(this IServiceCollection services)
    {
        services.AddScoped<Dam.Application.Workflow.WorkflowTaskAccess>();
        services.AddScoped<Dam.Application.Notifications.UserProfiles>();
        services.AddScoped<Dam.Application.Comments.AssetReaders>();
        services.AddScoped<Dam.Application.Notifications.NotificationPlanner>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebhookDispatcher>());
        services.AddEventConsumer<WebhookConsumer>();
        services.AddEventConsumer<NotificationConsumer>();
        return services;
    }
}
