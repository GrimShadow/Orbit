using System.Security.Cryptography;
using System.Text;
using Dam.Api.Auth;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Common;
using Microsoft.Extensions.Caching.Memory;

namespace Dam.Api.Middleware;

/// <summary>
/// Just-in-time provisioning: on the first request (and whenever the token's profile changes) mirror the user, their
/// IdP groups and attributes into the tenant. Runs in its own scope so a failed attempt never poisons the request's
/// unit of work, and retries once because two first requests can race on the unique (tenant, subject) index.
/// </summary>
public sealed class UserProvisioningMiddleware(RequestDelegate next, IMemoryCache cache, IServiceScopeFactory scopes, ILogger<UserProvisioningMiddleware> log)
{
    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(5);

    public async Task Invoke(HttpContext ctx)
    {
        var user = ctx.RequestServices.GetRequiredService<HttpCurrentUser>();
        var tenant = ctx.RequestServices.GetRequiredService<Dam.Application.Abstractions.ITenantContext>().TenantId;
        if (!user.IsAuthenticated) { await next(ctx); return; }
        if (user.Subject is null || tenant == Guid.Empty)
        {
            await Results.Problem(statusCode: 403, title: "No tenant", detail: "Token has no valid 'tenant' claim.").ExecuteAsync(ctx);
            return;
        }

        var command = new ProvisionUserCommand(user.Subject, user.Email ?? "", user.DisplayName ?? "", user.Attributes, user.Groups);
        var key = CacheKey(tenant, command);
        if (!cache.TryGetValue(key, out _))
        {
            var result = await ProvisionAsync(command, ctx.RequestAborted);
            if (result.IsFailure)
            {
                if (result.Error.Type == ErrorType.Forbidden)
                {
                    log.LogWarning("Token for unknown tenant {Tenant}", tenant);
                    await Results.Problem(statusCode: 403, title: "Unknown tenant", detail: "This tenant is not set up on this server.").ExecuteAsync(ctx);
                    return;
                }
                throw new InvalidOperationException($"User provisioning failed: {result.Error.Message}");
            }
            cache.Set(key, true, Recheck);
        }
        await next(ctx);
    }

    private async Task<Result<ProvisionedUser>> ProvisionAsync(ProvisionUserCommand command, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command, ct);
            }
            catch (Exception ex) when (attempt == 1 && ex is Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                log.LogInformation("Concurrent first login for {Subject}; retrying provisioning", command.Subject);
            }
        }
    }

    // A change in name, email, groups or attributes changes the key, so the next request re-syncs immediately.
    private static string CacheKey(Guid tenant, ProvisionUserCommand c)
    {
        var raw = string.Join('|', c.Subject, c.Email, c.DisplayName,
            string.Join(',', c.Groups.Order()),
            string.Join(';', c.Attributes.OrderBy(a => a.Key).Select(a => $"{a.Key}={string.Join(',', a.Value.Order())}")));
        return $"prov:{tenant}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))}";
    }
}
