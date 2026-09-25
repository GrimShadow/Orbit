using Dam.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Application.Messaging;

public interface IRequest<TResponse>;
/// <summary>State-changing request. Goes through the transaction + outbox behaviours.</summary>
public interface ICommand<TResponse> : IRequest<TResponse>;
public interface IQuery<TResponse> : IRequest<TResponse>;
/// <summary>Requests that need a permission check declare it here.</summary>
public interface IAuthorizedRequest { string Permission { get; } }

public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<Result<TResponse>> Handle(TRequest request, CancellationToken ct);
}

public delegate Task<Result<TResponse>> HandlerDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<Result<TResponse>> Handle(TRequest request, HandlerDelegate<TResponse> next, CancellationToken ct);
}

public interface IDispatcher
{
    Task<Result<TResponse>> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
}

internal abstract class Invoker<TResponse>
{
    public abstract Task<Result<TResponse>> Run(IRequest<TResponse> request, IServiceProvider sp, CancellationToken ct);
}

internal sealed class Invoker<TRequest, TResponse> : Invoker<TResponse> where TRequest : IRequest<TResponse>
{
    public override Task<Result<TResponse>> Run(IRequest<TResponse> request, IServiceProvider sp, CancellationToken ct)
    {
        var req = (TRequest)request;
        var handler = sp.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        HandlerDelegate<TResponse> next = () => handler.Handle(req, ct);
        // Registration order = outermost first, so fold from the end.
        foreach (var b in sp.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var inner = next;
            next = () => b.Handle(req, inner, ct);
        }
        return next();
    }
}

public sealed class Dispatcher(IServiceProvider sp) : IDispatcher
{
    public Task<Result<TResponse>> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
    {
        var type = typeof(Invoker<,>).MakeGenericType(request.GetType(), typeof(TResponse));
        var invoker = (Invoker<TResponse>)Activator.CreateInstance(type)!;
        return invoker.Run(request, sp, ct);
    }
}
