using Dam.Api.Auth;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Application.Ping;
using Dam.Domain.Common;

namespace Dam.Api.Endpoints;

public sealed record PingRequest(string Message);
public sealed record PingResponse(string Reply);

public sealed record MeResponse(Guid? UserId, string? Email, string? DisplayName, Guid TenantId, IReadOnlyCollection<string> Roles);

public static class ApiEndpoints
{
    public static void MapDamEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").RequireAuthorization();

        v1.MapGet("/me", (HttpCurrentUser user, ITenantContext tenant) =>
            tenant.TenantId == Guid.Empty
                ? Results.Problem(statusCode: 403, title: "No tenant", detail: "Token has no valid 'tenant' claim.")
                : Results.Ok(new MeResponse(user.UserId, user.Email, user.DisplayName, tenant.TenantId, user.Roles)))
            .WithName("GetMe")
            .WithSummary("Current user, tenant and roles")
            .Produces<MeResponse>();

        v1.MapPost("/system/ping", async (PingRequest req, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var r = await dispatcher.Send(new PingCommand(req.Message), ct);
                return r.IsSuccess ? Results.Ok(new PingResponse(r.Value)) : r.Error.ToProblem();
            })
            .WithName("SystemPing")
            .WithSummary("Emits a system.ping event through the outbox (Admin only); consumed by the worker")
            .Produces<PingResponse>();
    }

    /// <summary>Maps a failed application Result to RFC 9457 problem+json.</summary>
    public static IResult ToProblem(this Error e) => e.Type switch
    {
        ErrorType.Validation => Results.ValidationProblem(
            e.FieldErrors!.ToDictionary(k => k.Key, k => k.Value), title: e.Message, statusCode: 422),
        ErrorType.Unauthorized => Results.Problem(statusCode: 401, title: e.Message),
        ErrorType.Forbidden => Results.Problem(statusCode: 403, title: e.Message),
        ErrorType.NotFound => Results.Problem(statusCode: 404, title: e.Message),
        ErrorType.Conflict => Results.Problem(statusCode: 409, title: e.Message),
        _ => Results.Problem(statusCode: 500, title: e.Message),
    };
}
