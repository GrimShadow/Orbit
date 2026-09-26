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
}
