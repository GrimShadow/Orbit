using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Common;
using FluentValidation;

namespace Dam.Application.Behaviors;

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, HandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators)
            failures.AddRange((await v.ValidateAsync(request, ct)).Errors);
        if (failures.Count == 0) return await next();
        var fields = failures.GroupBy(f => f.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
        return Error.Validation(fields);
    }
}

public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser user, IAuthorizationService authz)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public Task<Result<TResponse>> Handle(TRequest request, HandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not IAuthorizedRequest ar) return next();
        if (!user.IsAuthenticated) return Task.FromResult<Result<TResponse>>(Error.Unauthorized());
        return authz.Can(user, ar.Permission, request)
            ? next()
            : Task.FromResult<Result<TResponse>>(Error.Forbidden());
    }
}

/// <summary>Wraps commands only; queries skip the transaction.</summary>
public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork uow)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, HandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not ICommand<TResponse>) return await next();
        await uow.BeginAsync(ct);
        try
        {
            var result = await next();
            if (result.IsSuccess) await uow.CommitAsync(ct); else await uow.RollbackAsync(ct);
            return result;
        }
        catch
        {
            await uow.RollbackAsync(ct);
            throw;
        }
    }
}

/// <summary>Moves domain events into the outbox inside the same transaction (decision D15).</summary>
public sealed class OutboxBehavior<TRequest, TResponse>(IDomainEventSource events, IOutbox outbox)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> Handle(TRequest request, HandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var result = await next();
        if (request is ICommand<TResponse> && result.IsSuccess)
        {
            var drained = events.Drain();
            if (drained.Count > 0) await outbox.EnqueueAsync(drained, ct);
        }
        return result;
    }
}
