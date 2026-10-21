using System.Text.RegularExpressions;

namespace Dam.Domain.Integrations;

/// <summary>The ways a connection can prove who it is to the other system (ADR-0040 decision 4).</summary>
public static class AuthStrategies
{
    public const string None = "none";
    public const string ApiKey = "api_key";
    public const string Bearer = "bearer";
    public const string Basic = "basic";
    public const string OAuth2ClientCredentials = "oauth2_client_credentials";
    public const string LoginCall = "login_call";

    public static readonly IReadOnlyList<string> All = [None, ApiKey, Bearer, Basic, OAuth2ClientCredentials, LoginCall];

    /// <summary>Strategies that fetch a token first (and cache it) rather than sending a stored credential with each request.</summary>
    public static bool IsTokenBased(string? strategy) => strategy is OAuth2ClientCredentials or LoginCall;

    public static string Label(string strategy) => strategy switch
    {
        None => "no authentication",
        ApiKey => "an API key",
        Bearer => "a bearer token",
        Basic => "basic authentication",
        OAuth2ClientCredentials => "OAuth 2 client credentials",
        LoginCall => "a sign-in call",
        _ => strategy,
    };
}

/// <summary>The keys of the settings the HTTP-based connectors share (ADR-0040). The same keys serve several strategies: for instance
/// <c>username</c> and <c>password</c> are used by basic authentication and by the <c>{{username}}</c> / <c>{{password}}</c> placeholders of a sign-in call.</summary>
public static class FieldKeys
{
    public const string BaseUrl = "baseUrl";
    public const string TestPath = "testPath";
    public const string AuthStrategy = "authStrategy";
    public const string ApiKeyIn = "apiKeyIn";
    public const string ApiKeyName = "apiKeyName";
    public const string ApiKey = "apiKey";
    public const string BearerToken = "bearerToken";
    public const string Username = "username";
    public const string Password = "password";
    public const string TokenUrl = "tokenUrl";
    public const string ClientId = "clientId";
    public const string ClientSecret = "clientSecret";
    public const string Scope = "scope";
    public const string LoginMethod = "loginMethod";
    public const string LoginUrl = "loginUrl";
    public const string LoginBodyType = "loginBodyType";
    public const string LoginBody = "loginBody";
    public const string TokenPointer = "tokenPointer";
    public const string ExpiryPointer = "expiryPointer";
    public const string ExpirySeconds = "expirySeconds";
    public const string TokenHeader = "tokenHeader";
    public const string TokenPrefix = "tokenPrefix";
}

/// <summary>Rules for header names, header prefixes, query parameter names and request paths a tenant administrator may configure.</summary>
public static partial class HeaderRules
{
    public const int MaxHeaderNameLength = 64;
    public const int MaxPrefixLength = 32;

    /// <summary>Headers the HTTP stack, a proxy or the receiving server treats specially. Naming one as a credential header could break or smuggle requests.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "content-encoding", "content-range", "content-disposition", "transfer-encoding", "connection", "keep-alive", "upgrade",
        "te", "trailer", "expect", "proxy-authorization", "proxy-authenticate", "proxy-connection", "cookie", "set-cookie", "via", "forwarded", "x-forwarded-for",
        "x-forwarded-host", "x-forwarded-proto", "x-real-ip", "origin", "referer", "user-agent", "accept", "accept-encoding", "range", "if-match", "if-none-match",
        "if-modified-since", "if-unmodified-since", "if-range", "max-forwards", "date", "dnt", "sec-fetch-site", "sec-fetch-mode", "sec-fetch-dest",
    };

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+\\z")]
    private static partial Regex TokenChars();

    [GeneratedRegex("^[A-Za-z0-9_.\\[\\]-]+\\z")]
    private static partial Regex QueryNameChars();

    /// <summary>Null when <paramref name="name"/> may be sent as a credential header; otherwise a message for the user.</summary>
    public static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A header name is required.";
        if (name.Length > MaxHeaderNameLength) return $"A header name is at most {MaxHeaderNameLength} characters.";
        if (!TokenChars().IsMatch(name)) return "A header name may contain only letters, digits and - _ . ! # $ % & ' * + ^ ` | ~ (no spaces, colons or line breaks).";
        if (name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) || Forbidden.Contains(name))
            return $"'{name}' is controlled by the HTTP stack and cannot carry a credential. Use a name such as X-API-Key or Authorization.";
        return null;
    }

    /// <summary>The text put before a token in the header (for example <c>Bearer </c>). Printable ASCII only, so it can never carry a line break.</summary>
    public static string? CheckPrefix(string? prefix)
    {
        if (prefix is null) return null;
        if (prefix.Length > MaxPrefixLength) return $"A prefix is at most {MaxPrefixLength} characters.";
        if (prefix.Any(c => c is < ' ' or > '~')) return "A prefix may contain only printable ASCII characters (no line breaks).";
        return null;
    }

    /// <summary>Null when <paramref name="name"/> may be a query parameter name.</summary>
    public static string? CheckQueryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A parameter name is required.";
        if (name.Length > MaxHeaderNameLength) return $"A parameter name is at most {MaxHeaderNameLength} characters.";
        return QueryNameChars().IsMatch(name) ? null : "A parameter name may contain only letters, digits and - _ . [ ].";
    }
}
