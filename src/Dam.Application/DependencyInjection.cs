using Dam.Application.Abstractions;
using Dam.Application.Behaviors;
using Dam.Application.Messaging;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddDamApplication(this IServiceCollection services)
    {
        var asm = typeof(DependencyInjection).Assembly;
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<IAuthorizationService, Authorization.AuthorizationService>();
        services.AddScoped<Identity.IdentityReader>();
        services.AddScoped<Identity.BuiltInRoleSeeder>();
        services.AddScoped<Content.MetadataValidationService>();
        services.AddScoped<Content.TemplateApplier>();
        services.AddScoped<Assets.AssetMetadataBuilder>();
        services.AddScoped<Assets.UploadStarter>();
        services.AddScoped<IWorkflowGate, Workflow.WorkflowGate>();
        services.AddScoped<Workflow.WorkflowRuntime>();
        services.AddScoped<Workflow.WorkflowTaskAccess>();
        services.AddScoped<Workflow.WorkflowTaskMapper>();
        services.AddScoped<Webhooks.WebhookMapper>();
        services.AddScoped<Webhooks.DeliveryMapper>();
        services.AddScoped<Notifications.UserProfiles>();
        services.AddScoped<Notifications.NotificationPlanner>();
        services.AddScoped<Audit.AuditExporter>();
        services.AddScoped<Comments.AssetReaders>();
        services.AddScoped<Comments.CommentMapper>();
        services.AddScoped<Licensing.RightsVocabularies>();
        services.AddScoped<Licensing.RightsGuard>();
        services.AddScoped<Licensing.RightsMapper>();
        services.AddScoped<Ai.AiSuggestionDecider>();
        services.AddScoped<Ai.AiBackfillService>();
        services.AddScoped<Ai.TranslationAcceptor>();
        services.AddScoped<Knowledge.KnowledgeReader>();
        services.AddDamAnalytics();
        services.AddScoped<Ai.AiReviewReader>();
        services.AddValidatorsFromAssembly(asm, includeInternalTypes: true);

        foreach (var t in asm.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
            foreach (var i in t.GetInterfaces().Where(i => i.IsGenericType &&
                         i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                services.AddScoped(i, t);

        // Order matters: outermost first.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(OutboxBehavior<,>));
        return services;
    }

    /// <summary>The analytics services (ADR-0037): the KPI calculator, the report runner and the scheduled-report mailer. The scheduler host registers the same set
    /// (it has no interactive user), so a scheduled report is built by exactly the code that serves the report API.</summary>
    public static IServiceCollection AddDamAnalytics(this IServiceCollection services)
    {
        services.AddScoped<Assets.TopAssetsReader>();
        services.AddScoped<Assets.UnusedAssetsReader>();
        services.AddScoped<Assets.KpiService>();
        services.AddScoped<Assets.ReportService>();
        services.AddScoped<Assets.ReportMailer>();
        services.AddScoped<Notifications.UserProfiles>();
        return services;
    }
}
