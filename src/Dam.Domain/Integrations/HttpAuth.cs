using System.Text;
using System.Text.Json;
using Dam.Domain.Webhooks;

namespace Dam.Domain.Integrations;

/// <summary>Paths and addresses a connection may call (ADR-0040 decisions 2 and 5). Pure: whether an address is public is the outbound guard's job, on save and on every call.</summary>
public static class ConnectorUrls
{
    public const int MaxPathLength = 500;

    /// <summary>Null when the text is an acceptable request path (with or without a leading slash, optionally with a query).</summary>
    public static string? CheckPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A path is required, such as /health.";
        if (path.Length > MaxPathLength) return $"A path is at most {MaxPathLength} characters.";
        if (path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "A path cannot contain spaces or control characters.";
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
            return "Enter a path on the connection's address, such as /health, not a full URL.";
        if (path.Contains('#', StringComparison.Ordinal)) return "A path cannot contain a fragment (#).";
        if (path.Contains("{{", StringComparison.Ordinal)) return "Placeholders are only available in the sign-in body.";
        var beforeQuery = path.Split('?', 2)[0];
        if (beforeQuery.Split('/').Any(s => s == "..")) return "A path cannot contain '..'.";
        return null;
    }

    public static bool IsAbsolute(string? value) =>
        value is not null && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>The address a path means on a base address (<c>https://api.example.test/v1</c> + <c>/ping</c>). Null when it cannot be built; a full URL is not a path and gives null.</summary>
    public static Uri? Resolve(string? baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = path.Trim();
        if (CheckPath(text) is not null || AddressGuard.CheckSyntax(baseUrl) is not null) return null;
        var joined = baseUrl!.Trim().TrimEnd('/') + "/" + text.TrimStart('/');
        return Uri.TryCreate(joined, UriKind.Absolute, out var uri) && uri.Host == new Uri(baseUrl.Trim()).Host ? uri : null;
    }

    /// <summary>Like <see cref="Resolve"/>, but a full http(s) address is returned as it is (the sign-in call may live on another host).</summary>
    public static Uri? ResolveUrlOrPath(string? baseUrl, string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return null;
        var text = urlOrPath.Trim();
        return IsAbsolute(text) ? (AddressGuard.CheckSyntax(text) is null ? new Uri(text) : null) : Resolve(baseUrl, text);
    }

    /// <summary>The address with the given query parameters added (percent-encoded).</summary>
    public static Uri WithQuery(Uri url, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var extra = string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        if (extra.Length == 0) return url;
        var builder = new UriBuilder(url) { Query = url.Query.Length > 1 ? url.Query.TrimStart('?') + "&" + extra : extra };
        return builder.Uri;
    }

    /// <summary>What is recorded about a call: the host (with a non-default port) and the path. Never the query string, user information or fragment.</summary>
    public static string Target(Uri url)
    {
        var host = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        var path = url.AbsolutePath;
        var target = host + path;
        return target.Length <= 500 ? target : target[..500];
    }
}

/// <summary>The authentication-related settings of an HTTP connection, read from its configuration (field keys in <see cref="FieldKeys"/>).</summary>
public sealed record HttpAuthSettings(
    string Strategy, string? BaseUrl, string? TestPath, string ApiKeyIn, string? ApiKeyName, string? Username, string? TokenUrl, string? ClientId, string? Scope,
    string LoginMethod, string? LoginUrl, string LoginBodyType, string? LoginBody, string TokenPointer, string? ExpiryPointer, int? ExpirySeconds,
    string TokenHeader, string TokenPrefix)
{
    public const string DefaultTokenHeader = "Authorization";
    public const string DefaultTokenPrefix = "Bearer ";
    public const string OAuthTokenPointer = "/access_token";
    public const string OAuthExpiryPointer = "/expires_in";

    public bool IsTokenBased => AuthStrategies.IsTokenBased(Strategy);

    public static HttpAuthSettings Read(IReadOnlyDictionary<string, JsonElement> config)
    {
        string? Text(string key) => config.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : null;
        int? Whole(string key) => config.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;
        return new HttpAuthSettings(
            Text(FieldKeys.AuthStrategy) ?? AuthStrategies.None, Text(FieldKeys.BaseUrl), Text(FieldKeys.TestPath), Text(FieldKeys.ApiKeyIn) ?? "header", Text(FieldKeys.ApiKeyName),
            Text(FieldKeys.Username), Text(FieldKeys.TokenUrl), Text(FieldKeys.ClientId), Text(FieldKeys.Scope),
            Text(FieldKeys.LoginMethod) ?? "POST", Text(FieldKeys.LoginUrl), Text(FieldKeys.LoginBodyType) ?? LoginBodyTypes.Json, Text(FieldKeys.LoginBody),
            Text(FieldKeys.TokenPointer) ?? OAuthTokenPointer, Text(FieldKeys.ExpiryPointer), Whole(FieldKeys.ExpirySeconds),
            Text(FieldKeys.TokenHeader) ?? DefaultTokenHeader, config.TryGetValue(FieldKeys.TokenPrefix, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : DefaultTokenPrefix);
    }
}

/// <summary>A request the framework will send for a connector: sign-in or token calls built here, and (by the runtime) the actual API calls.</summary>
public sealed record OutboundRequest(string Method, Uri Url, string? Body, string? ContentType);

/// <summary>Credentials to add to an outgoing request.</summary>
public sealed record AuthAddition(IReadOnlyList<KeyValuePair<string, string>> Headers, IReadOnlyList<KeyValuePair<string, string>> Query)
{
    public static readonly AuthAddition Empty = new([], []);
}

/// <summary>Turns a connection's settings and secrets into authenticated requests (ADR-0040 decision 4). Pure: no network, no clock, no storage.</summary>
public static class HttpAuth
{
    /// <summary>Credentials for the strategies that send a stored credential with every request. Fails (naming the setting, never a value) when one is missing.</summary>
    public static (AuthAddition? Addition, string? Error) ForStatic(HttpAuthSettings s, Func<string, string?> secret)
    {
        switch (s.Strategy)
        {
            case AuthStrategies.None:
                return (AuthAddition.Empty, null);
            case AuthStrategies.ApiKey:
            {
                if (string.IsNullOrEmpty(secret(FieldKeys.ApiKey))) return (null, "The API key is not set.");
                if (string.IsNullOrEmpty(s.ApiKeyName)) return (null, "The header or parameter name for the API key is not set.");
                var pair = new KeyValuePair<string, string>(s.ApiKeyName, secret(FieldKeys.ApiKey)!);
                return s.ApiKeyIn == "query" ? (new AuthAddition([], [pair]), null) : (new AuthAddition([pair], []), null);
            }
            case AuthStrategies.Bearer:
                return string.IsNullOrEmpty(secret(FieldKeys.BearerToken))
                    ? (null, "The bearer token is not set.")
                    : (new AuthAddition([new("Authorization", "Bearer " + secret(FieldKeys.BearerToken))], []), null);
            case AuthStrategies.Basic:
            {
                if (string.IsNullOrEmpty(s.Username)) return (null, "The user name is not set.");
                if (string.IsNullOrEmpty(secret(FieldKeys.Password))) return (null, "The password is not set.");
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Username + ":" + secret(FieldKeys.Password)));
                return (new AuthAddition([new("Authorization", "Basic " + credentials)], []), null);
            }
            default:
                return (null, $"{AuthStrategies.Label(s.Strategy)} fetches a token first; it has no stored credential to send.");
        }
    }

    /// <summary>The header that carries a fetched token: the configured name (default <c>Authorization</c>) and prefix (default <c>Bearer </c>).</summary>
    public static AuthAddition ForToken(HttpAuthSettings s, string token) => new([new(s.TokenHeader, s.TokenPrefix + token)], []);

    /// <summary>The request that fetches a token: an OAuth 2 client-credentials call, or the configured sign-in call with its body template filled in.</summary>
    public static (OutboundRequest? Request, string? Error) BuildTokenRequest(HttpAuthSettings s, Func<string, string?> secret)
    {
        // The placeholder values: settings by their key, secrets by theirs.
        string? Value(string name) => LoginTemplate.IsSecretPlaceholder(name) ? secret(name) : name switch
        {
            FieldKeys.Username => s.Username,
            FieldKeys.ClientId => s.ClientId,
            _ => null,
        };

        if (s.Strategy == AuthStrategies.OAuth2ClientCredentials)
        {
            if (AddressGuard.CheckSyntax(s.TokenUrl) is not null) return (null, "The token URL is not set or not valid.");
            if (string.IsNullOrEmpty(s.ClientId) || string.IsNullOrEmpty(secret(FieldKeys.ClientSecret))) return (null, "The client ID or client secret is not set.");
            var fields = new List<string>
            {
                "grant_type=client_credentials",
                "client_id=" + Uri.EscapeDataString(s.ClientId),
                "client_secret=" + Uri.EscapeDataString(secret(FieldKeys.ClientSecret)!),
            };
            if (!string.IsNullOrWhiteSpace(s.Scope)) fields.Add("scope=" + Uri.EscapeDataString(s.Scope));
            return (new OutboundRequest("POST", new Uri(s.TokenUrl!.Trim()), string.Join('&', fields), "application/x-www-form-urlencoded"), null);
        }

        if (s.Strategy != AuthStrategies.LoginCall) return (null, $"{AuthStrategies.Label(s.Strategy)} does not fetch a token.");
        var url = ConnectorUrls.ResolveUrlOrPath(s.BaseUrl, s.LoginUrl);
        if (url is null) return (null, "The sign-in address is not set or not valid.");
        var (body, error) = LoginTemplate.Render(s.LoginBody, s.LoginBodyType, Value);
        if (error is not null) return (null, error);
        var contentType = s.LoginBodyType switch { LoginBodyTypes.Json => "application/json", LoginBodyTypes.Form => "application/x-www-form-urlencoded", _ => null };
        return (new OutboundRequest(s.LoginMethod, url, body, contentType), null);
    }

    /// <summary>Reads the token and its lifetime from the response to <see cref="BuildTokenRequest"/>.</summary>
    public static TokenResponse ReadTokenResponse(HttpAuthSettings s, string body) =>
        s.Strategy == AuthStrategies.OAuth2ClientCredentials
            ? TokenExtraction.Read(body, HttpAuthSettings.OAuthTokenPointer, HttpAuthSettings.OAuthExpiryPointer, s.ExpirySeconds)
            : TokenExtraction.Read(body, s.TokenPointer, s.ExpiryPointer, s.ExpirySeconds);
}
