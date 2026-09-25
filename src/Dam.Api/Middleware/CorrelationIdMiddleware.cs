using System.Diagnostics;

namespace Dam.Api.Middleware;

/// <summary>Accepts or creates X-Correlation-ID, echoes it, and stamps it on the trace and log scope.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> log)
{
    public const string Header = "X-Correlation-ID";

    public async Task Invoke(HttpContext ctx)
    {
        var id = ctx.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            id = Guid.NewGuid().ToString("N");

        ctx.Items[Header] = id;
        ctx.Response.OnStarting(() => { ctx.Response.Headers[Header] = id; return Task.CompletedTask; });
        Activity.Current?.SetTag("correlation_id", id);
        Activity.Current?.AddBaggage("correlation_id", id);
        using (log.BeginScope(new Dictionary<string, object> { ["correlation_id"] = id }))
            await next(ctx);
    }
}
