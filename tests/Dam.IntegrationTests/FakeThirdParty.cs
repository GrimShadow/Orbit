using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

/// <summary>
/// A pretend third-party system for the integrations tests (ADR-0040): it signs people in ("login then call"), hands out tokens, accepts the credential styles the framework
/// can send, and answers the way it is told to. Listens on 127.0.0.1 (the test API allows private targets). Everything it knows is fictional.
/// </summary>
public sealed class FakeThirdParty : IAsyncDisposable
{
    public const string User = "ann@acme.example.test";
    public const string Password = "pa55word-for-ann";
    public const string ApiKey = "api-key-0001-acme";
    public const string BearerToken = "static-bearer-0001-acme";
    public const string ClientId = "client-acme";
    public const string ClientSecret = "client-secret-0001-acme";

    public sealed record Hit(string Method, string Path, string Query, string Body, IReadOnlyDictionary<string, string> Headers, int Status);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Hit> _hits = new();
    private readonly HashSet<string> _valid = [];
    private readonly object _lock = new();
    private int _issued;

    private FakeThirdParty(WebApplication app) => _app = app;

    public string BaseUrl => _app.Urls.First().TrimEnd('/');
    public IReadOnlyList<Hit> Hits => [.. _hits];

    /// <summary>Seconds the sign-in endpoints say a token lasts.</summary>
    public int ExpiresIn { get; set; } = 3600;
    public int LoginDelayMs { get; set; }
    /// <summary>When set, the plain sign-in answers with this body instead of a token (use {TOKEN} for a fresh token).</summary>
    public string? LoginBody { get; set; }
    public int LoginStatus { get; set; } = 200;
    /// <summary>Answer every API call 401, whatever credential it carries.</summary>
    public bool RejectApiCalls { get; set; }
    /// <summary>Error bodies repeat everything the request carried (headers, query, body), as a careless server might.</summary>
    public bool EchoInErrors { get; set; }

    /// <summary>Sign-in requests that have arrived, answered or not yet.</summary>
    public int LoginsStarted => Volatile.Read(ref _loginsStarted);
    private int _loginsStarted;

    public int Logins => _hits.Count(h => h.Path is "/login" or "/login-form" or "/nested-login" or "/oauth/token");
    public IReadOnlyList<Hit> Api => [.. _hits.Where(h => h.Path is "/things" or "/health" or "/boom")];

    /// <summary>After this every token handed out so far is refused (a server-side revocation or restart).</summary>
    public void RevokeTokens()
    {
        lock (_lock) _valid.Clear();
    }

    public static async Task<FakeThirdParty> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var fake = new FakeThirdParty(app);
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        return fake;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var path = ctx.Request.Path.Value ?? "/";
        var (status, response, location) = await RouteAsync(ctx, path, body, headers);
        if (status >= 400 && EchoInErrors)
            response = $"rejected {path}: headers={string.Join(';', headers.Select(h => h.Key + '=' + h.Value))} query={ctx.Request.QueryString} body={body}";
        _hits.Enqueue(new Hit(ctx.Request.Method, path, ctx.Request.QueryString.Value ?? "", body, headers, status));
        ctx.Response.StatusCode = status;
        if (location is not null) ctx.Response.Headers.Location = location;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(response);
    }

    private async Task<(int Status, string Body, string? Location)> RouteAsync(HttpContext ctx, string path, string body, Dictionary<string, string> headers)
    {
        switch (path)
        {
            case "/login":
            case "/nested-login":
            {
                Interlocked.Increment(ref _loginsStarted);
                if (LoginDelayMs > 0) await Task.Delay(LoginDelayMs);
                if (LoginStatus != 200) return (LoginStatus, """{"error":"sign-in refused"}""", null);
                if (!JsonCredentialsOk(body)) return (401, """{"error":"bad credentials"}""", null);
                var token = Issue();
                if (path == "/nested-login") return (200, JsonSerializer.Serialize(new { data = new { jwt = token }, ttl = ExpiresIn }), null);
                return (200, LoginBody?.Replace("{TOKEN}", token, StringComparison.Ordinal) ?? JsonSerializer.Serialize(new { access_token = token, expires_in = ExpiresIn }), null);
            }
            case "/login-form":
            {
                if (LoginDelayMs > 0) await Task.Delay(LoginDelayMs);
                var form = System.Web.HttpUtility.ParseQueryString(body);
                return form["username"] == User && form["password"] == Password
                    ? (200, JsonSerializer.Serialize(new { access_token = Issue(), expires_in = ExpiresIn }), null)
                    : (401, """{"error":"bad credentials"}""", null);
            }
            case "/oauth/token":
            {
                var form = System.Web.HttpUtility.ParseQueryString(body);
                return form["grant_type"] == "client_credentials" && form["client_id"] == ClientId && form["client_secret"] == ClientSecret
                    ? (200, JsonSerializer.Serialize(new { access_token = Issue(), expires_in = ExpiresIn, token_type = "Bearer" }), null)
                    : (401, """{"error":"invalid_client"}""", null);
            }
            case "/things":
            case "/health":
                return !RejectApiCalls && Authorised(ctx, headers) ? (200, """{"items":[]}""", null) : (401, """{"error":"unauthorised"}""", null);
            case "/boom":
                return (500, """{"error":"internal"}""", null);
            case "/moved":
                return (302, "", "/things");
            default:
                return (404, """{"error":"not found"}""", null);
        }
    }

    private static bool JsonCredentialsOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("username", out var u) && u.GetString() == User
                && doc.RootElement.TryGetProperty("password", out var p) && p.GetString() == Password;
        }
        catch (JsonException) { return false; }
    }

    private string Issue()
    {
        var token = $"tok-{Interlocked.Increment(ref _issued)}-{Guid.NewGuid():N}"[..28];
        lock (_lock) _valid.Add(token);
        return token;
    }

    private bool IsValid(string token)
    {
        lock (_lock) return _valid.Contains(token);
    }

    private bool Authorised(HttpContext ctx, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue("Authorization", out var auth))
        {
            if (auth.StartsWith("Bearer ", StringComparison.Ordinal) && (auth[7..] == BearerToken || IsValid(auth[7..]))) return true;
            if (auth.StartsWith("Basic ", StringComparison.Ordinal) && Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])) == $"{User}:{Password}") return true;
        }
        if (headers.TryGetValue("X-API-Key", out var key) && key == ApiKey) return true;
        if (ctx.Request.Query["api_key"] == ApiKey) return true;
        return headers.TryGetValue("X-Auth", out var custom) && custom.StartsWith("Token ", StringComparison.Ordinal) && IsValid(custom[6..]);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
