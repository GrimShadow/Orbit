using System.Net;
using System.Text.Json;
using Dam.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>
/// Stops hammering a provider that is down. After <c>threshold</c> transport failures in a row the breaker is open for the cool-down: calls fail at once
/// (the host retries the message later, with its own backoff). When the cool-down ends one call is let through; if it fails the breaker opens again.
/// </summary>
public sealed class CoolDownBreaker(int threshold, TimeSpan coolDown, TimeProvider time)
{
    private readonly object _gate = new();
    private int _failures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public bool IsOpen
    {
        get { lock (_gate) return time.GetUtcNow() < _openUntil; }
    }

    public TimeSpan Remaining
    {
        get { lock (_gate) return _openUntil > time.GetUtcNow() ? _openUntil - time.GetUtcNow() : TimeSpan.Zero; }
    }

    public void Success()
    {
        lock (_gate) { _failures = 0; _openUntil = DateTimeOffset.MinValue; }
    }

    public void Failure()
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= threshold) _openUntil = time.GetUtcNow() + coolDown;
        }
    }
}

/// <summary>
/// The HTTP plumbing both providers share: per-call timeouts, retries only for a busy or unreachable service (and only when the request can be sent again),
/// the cool-down breaker, and turning a failure into the right <see cref="AiException"/>. It never logs a URL's query, a header or a body.
/// </summary>
internal sealed class AiHttp : IDisposable
{
    private readonly HttpClient _http;
    private readonly AiOptions _o;
    private readonly CoolDownBreaker _breaker;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Func<string, string> _redact;
    private readonly string _provider;

    public AiHttp(string provider, AiOptions options, ILogger log, TimeProvider time, Func<string, string> redact, HttpMessageHandler? handler = null)
    {
        _provider = provider;
        _o = options;
        _log = log;
        _time = time;
        _redact = redact;
        _breaker = new CoolDownBreaker(options.FailureThreshold, options.CoolDown, time);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
            UseCookies = false,
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public CoolDownBreaker Breaker => _breaker;

    public void Dispose() => _http.Dispose();

    /// <summary>One request with no retry and no breaker (health probes): the raw response, or null with the reason when it could not be had.</summary>
    public async Task<(HttpResponseMessage? Response, string? Error)> ProbeAsync(Func<HttpRequestMessage> build, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return (await _http.SendAsync(build(), HttpCompletionOption.ResponseContentRead, cts.Token), null); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "The service did not answer in time."); }
        catch (HttpRequestException ex) { return (null, _redact(ex.Message)); }
    }

    /// <summary>
    /// Sends the request <paramref name="build"/> makes. <paramref name="canResend"/> says the build can be called again (a seekable or in-memory body);
    /// when it cannot, the first failure is final. Returns the response only when it is a success.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(string operation, Func<HttpRequestMessage> build, TimeSpan timeout, bool canResend, CancellationToken ct)
    {
        if (_breaker.IsOpen)
            throw new AiUnavailableException($"The {_provider} AI provider was unreachable moments ago; waiting {Math.Ceiling(_breaker.Remaining.TotalSeconds)}s before trying again.");

        var attempts = canResend ? Math.Max(1, _o.Retries) : 1;
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            TimeSpan? retryAfter = null;
            try
            {
                var response = await _http.SendAsync(build(), HttpCompletionOption.ResponseContentRead, cts.Token);
                if (response.IsSuccessStatusCode) { _breaker.Success(); return response; }

                var status = (int)response.StatusCode;
                var detail = await ReadErrorAsync(response);
                if (IsTransientStatus(response.StatusCode))
                {
                    retryAfter = RetryAfter(response);
                    response.Dispose();
                    last = new AiUnavailableException($"The {_provider} AI provider is busy or unavailable ({status}).{Suffix(detail)}");
                }
                else
                {
                    response.Dispose();
                    _breaker.Success();   // it answered: the service is up, the request was the problem
                    throw status is 401 or 403
                        ? new AiRequestException($"The {_provider} AI provider refused the credentials ({status}).{Suffix(detail)}", status)
                        : new AiRequestException($"The {_provider} AI provider rejected {operation} ({status}).{Suffix(detail)}", status);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new AiUnavailableException($"The {_provider} AI provider did not answer {operation} within {timeout.TotalSeconds:0}s.");
            }
            catch (HttpRequestException ex)
            {
                last = new AiUnavailableException($"The {_provider} AI provider could not be reached: {_redact(ex.Message)}", ex);
            }

            if (attempt < attempts)
            {
                var delay = retryAfter is { } ra && ra <= TimeSpan.FromSeconds(10) ? ra : _o.RetryBaseDelay * Math.Pow(2, attempt - 1);
                delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
                _log.LogInformation("AI {Operation} on {Provider} failed (attempt {Attempt}/{Attempts}); trying again in {Delay}ms", operation, _provider, attempt, attempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, _time, ct);
            }
        }
        _breaker.Failure();
        throw last ?? new AiUnavailableException($"The {_provider} AI provider is unavailable.");
    }

    public static bool IsTransientStatus(HttpStatusCode code) =>
        code is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    private static TimeSpan? RetryAfter(HttpResponseMessage r) =>
        r.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null) : null;

    private string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : " " + _redact(detail);

    /// <summary>The provider's own explanation, from {"message"}, {"error":"..."} or {"error":{"message"}}, kept short. Content is never in it.</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage r)
    {
        try
        {
            var text = await r.Content.ReadAsStringAsync();
            if (text.Length == 0) return null;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? message = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                else if (root.TryGetProperty("error", out var e))
                    message = e.ValueKind == JsonValueKind.String ? e.GetString()
                        : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null;
            }
            return message is { Length: > 300 } ? message[..300] : message;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException) { return null; }
    }

    /// <summary>Parses a success body as JSON; an answer that is not JSON is the provider's fault, not worth retrying.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException ex) { throw new AiRequestException($"The {provider} AI provider answered with something that is not JSON.", (int)response.StatusCode, ex); }
    }
}
