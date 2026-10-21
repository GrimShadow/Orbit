using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Integrations;

namespace Dam.Application.Integrations;

/// <summary>The connectors this build knows, as DI registers them. Scoped, because connectors use the request's tenant and database.</summary>
public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _byKey;

    public ConnectorRegistry(IEnumerable<IConnector> connectors)
    {
        _byKey = connectors.ToDictionary(c => c.Descriptor.Key, StringComparer.Ordinal);
        All = _byKey.Values.OrderBy(c => Array.IndexOf(ConnectorStatuses.All.ToArray(), c.Descriptor.Status)).ThenBy(c => c.Descriptor.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Available connectors first, then preview, then planned.</summary>
    public IReadOnlyList<IConnector> All { get; }

    public IConnector? Find(string? key) => key is not null && _byKey.TryGetValue(key, out var c) ? c : null;
}

/// <summary>A connector that is listed but cannot be used yet (status <c>planned</c>): creating a connection to it is refused.</summary>
public sealed class PlannedConnector(ConnectorDescriptor descriptor) : IConnector
{
    public ConnectorDescriptor Descriptor => descriptor;

    public Task<ConnectorTestResult> TestAsync(IntegrationConnection connection, CancellationToken ct) =>
        Task.FromResult(new ConnectorTestResult(false, [new ConnectorStep("Test", false, $"{descriptor.Name} is not available yet.", 0)]));
}

/// <summary>The descriptors of the connectors that ship now (ADR-0040 decision 1).</summary>
public static class ShippedConnectors
{
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static readonly Dictionary<string, string> StrategyLabels = new Dictionary<string, string>
    {
        [AuthStrategies.None] = "None",
        [AuthStrategies.ApiKey] = "API key",
        [AuthStrategies.Bearer] = "Bearer token",
        [AuthStrategies.Basic] = "Basic (user name and password)",
        [AuthStrategies.OAuth2ClientCredentials] = "OAuth 2 client credentials",
        [AuthStrategies.LoginCall] = "Sign-in call",
    };

    private static ShowWhen When(params string[] strategies) => new(FieldKeys.AuthStrategy, strategies);

    /// <summary>The settings every HTTP-based connector shares: where the API is, how to authenticate and how to test.</summary>
    private static List<ConnectorField> HttpFields(IReadOnlyList<string> strategies, string defaultStrategy, string baseUrlHelp, string? baseUrlPlaceholder)
    {
        var fields = new List<ConnectorField>
        {
            new(FieldKeys.BaseUrl, "Base URL", FieldTypes.Url, Required: true, Help: baseUrlHelp, Placeholder: baseUrlPlaceholder, Group: FieldGroups.Connection),
            new(FieldKeys.TestPath, "Test path", FieldTypes.Text, Help: "A path on the base URL that Test connection requests with GET after signing in, such as /health. Leave empty to test only the sign-in.",
                Placeholder: "/health", Group: FieldGroups.Connection),
            new(FieldKeys.AuthStrategy, "Authentication", FieldTypes.Select, Required: true, Help: "How Orbit proves who it is to the other system.", Group: FieldGroups.Authentication,
                Options: strategies.Select(s => new FieldOption(s, StrategyLabels[s])).ToList(), Default: Json(defaultStrategy)),
        };
        if (strategies.Contains(AuthStrategies.ApiKey))
        {
            fields.Add(new(FieldKeys.ApiKeyIn, "Send the key in", FieldTypes.Select, Group: FieldGroups.Authentication, ShowWhen: When(AuthStrategies.ApiKey),
                Options: [new("header", "A header"), new("query", "A query parameter")], Default: Json("header")));
            fields.Add(new(FieldKeys.ApiKeyName, "Header or parameter name", FieldTypes.Text, Help: "For example X-API-Key.", Placeholder: "X-API-Key", Group: FieldGroups.Authentication,
                ShowWhen: When(AuthStrategies.ApiKey), Default: Json("X-API-Key"), MaxLength: HeaderRules.MaxHeaderNameLength));
        }
        if (strategies.Contains(AuthStrategies.ApiKey) || strategies.Contains(AuthStrategies.LoginCall))
        {
            var keyOn = strategies.Where(s => s is AuthStrategies.ApiKey or AuthStrategies.LoginCall).ToArray();
            fields.Add(new(FieldKeys.ApiKey, "API key", FieldTypes.Secret, Group: FieldGroups.Authentication, ShowWhen: When(keyOn),
                Help: strategies.Contains(AuthStrategies.LoginCall) ? "Also available to the sign-in body as {{apiKey}}." : null));
        }
        if (strategies.Contains(AuthStrategies.Bearer))
            fields.Add(new(FieldKeys.BearerToken, "Token", FieldTypes.Secret, Help: "Sent as Authorization: Bearer <token>.", Group: FieldGroups.Authentication, ShowWhen: When(AuthStrategies.Bearer)));
        if (strategies.Contains(AuthStrategies.Basic) || strategies.Contains(AuthStrategies.LoginCall))
        {
            var userOn = strategies.Where(s => s is AuthStrategies.Basic or AuthStrategies.LoginCall).ToArray();
            fields.Add(new(FieldKeys.Username, "User name", FieldTypes.Text, Group: FieldGroups.Authentication, ShowWhen: When(userOn),
                Help: strategies.Contains(AuthStrategies.LoginCall) ? "Also available to the sign-in body as {{username}}." : null));
            fields.Add(new(FieldKeys.Password, "Password", FieldTypes.Secret, Group: FieldGroups.Authentication, ShowWhen: When(userOn),
                Help: strategies.Contains(AuthStrategies.LoginCall) ? "Also available to the sign-in body as {{password}}." : null));
        }
        if (strategies.Contains(AuthStrategies.OAuth2ClientCredentials))
            fields.Add(new(FieldKeys.TokenUrl, "Token URL", FieldTypes.Url, Placeholder: "https://auth.example.test/oauth/token", Group: FieldGroups.Authentication,
                ShowWhen: When(AuthStrategies.OAuth2ClientCredentials)));
        if (strategies.Contains(AuthStrategies.OAuth2ClientCredentials) || strategies.Contains(AuthStrategies.LoginCall))
        {
            var clientOn = strategies.Where(s => s is AuthStrategies.OAuth2ClientCredentials or AuthStrategies.LoginCall).ToArray();
            fields.Add(new(FieldKeys.ClientId, "Client ID", FieldTypes.Text, Group: FieldGroups.Authentication, ShowWhen: When(clientOn),
                Help: strategies.Contains(AuthStrategies.LoginCall) ? "Also available to the sign-in body as {{clientId}}." : null));
            fields.Add(new(FieldKeys.ClientSecret, "Client secret", FieldTypes.Secret, Group: FieldGroups.Authentication, ShowWhen: When(clientOn),
                Help: strategies.Contains(AuthStrategies.LoginCall) ? "Also available to the sign-in body as {{clientSecret}}." : null));
        }
        if (strategies.Contains(AuthStrategies.OAuth2ClientCredentials))
            fields.Add(new(FieldKeys.Scope, "Scope", FieldTypes.Text, Help: "Optional. Space-separated scopes to ask for.", Group: FieldGroups.Authentication,
                ShowWhen: When(AuthStrategies.OAuth2ClientCredentials), MaxLength: 200));
        if (strategies.Contains(AuthStrategies.LoginCall))
        {
            var login = When(AuthStrategies.LoginCall);
            fields.Add(new(FieldKeys.LoginMethod, "Sign-in method", FieldTypes.Select, Group: FieldGroups.Authentication, ShowWhen: login,
                Options: [new("POST", "POST"), new("PUT", "PUT")], Default: Json("POST")));
            fields.Add(new(FieldKeys.LoginUrl, "Sign-in URL or path", FieldTypes.Text, Group: FieldGroups.Authentication, ShowWhen: login, Placeholder: "/auth/login",
                Help: "A path on the base URL, or a full http(s) address when signing in happens elsewhere."));
            fields.Add(new(FieldKeys.LoginBodyType, "Sign-in body", FieldTypes.Select, Group: FieldGroups.Authentication, ShowWhen: login,
                Options: [new("json", "JSON"), new("form", "Form (URL-encoded)"), new("none", "No body")], Default: Json("json")));
            fields.Add(new(FieldKeys.LoginBody, "Body template", FieldTypes.Textarea, Group: FieldGroups.Authentication, ShowWhen: login,
                Help: "Placeholders {{username}}, {{password}}, {{apiKey}}, {{clientId}} and {{clientSecret}} are filled from this connection. In a JSON body put each inside a string.",
                Default: Json("{\"username\": \"{{username}}\", \"password\": \"{{password}}\"}")));
            fields.Add(new(FieldKeys.TokenPointer, "Token location", FieldTypes.Text, Group: FieldGroups.Authentication, ShowWhen: login, Placeholder: "/access_token",
                Help: "Where the token is in the JSON response, as a JSON pointer.", Default: Json(HttpAuthSettings.OAuthTokenPointer), MaxLength: JsonPointer.MaxLength));
            fields.Add(new(FieldKeys.ExpiryPointer, "Lifetime location", FieldTypes.Text, Group: FieldGroups.Authentication, ShowWhen: login, Placeholder: "/expires_in",
                Help: "Optional. Where the token's lifetime in seconds is in the response, as a JSON pointer.", MaxLength: JsonPointer.MaxLength));
        }
        if (strategies.Any(AuthStrategies.IsTokenBased))
        {
            var tokenOn = strategies.Where(AuthStrategies.IsTokenBased).ToArray();
            if (strategies.Contains(AuthStrategies.LoginCall))
                fields.Add(new(FieldKeys.ExpirySeconds, "Fixed lifetime (seconds)", FieldTypes.Number, Group: FieldGroups.Advanced, ShowWhen: When(AuthStrategies.LoginCall),
                    Help: $"60 to 86,400. Used when the response gives no lifetime; without it a token is reused for {TokenLifetime.DefaultSeconds / 60} minutes.",
                    Min: TokenLifetime.MinFixedSeconds, Max: TokenLifetime.MaxSeconds));
            fields.Add(new(FieldKeys.TokenHeader, "Token header", FieldTypes.Text, Group: FieldGroups.Advanced, ShowWhen: When(tokenOn), Placeholder: HttpAuthSettings.DefaultTokenHeader,
                Help: "The header that carries the token on every request.", Default: Json(HttpAuthSettings.DefaultTokenHeader), MaxLength: HeaderRules.MaxHeaderNameLength));
            fields.Add(new(FieldKeys.TokenPrefix, "Token prefix", FieldTypes.Text, Group: FieldGroups.Advanced, ShowWhen: When(tokenOn), Verbatim: true, MaxLength: HeaderRules.MaxPrefixLength,
                Help: "Text put before the token, including any trailing space.", Default: Json(HttpAuthSettings.DefaultTokenPrefix)));
        }
        return fields;
    }

    public static readonly ConnectorDescriptor Http = new(
        "http", "Generic HTTP API", "Connect to any web API that uses an API key, a bearer token, basic authentication, OAuth 2 client credentials or a sign-in call.", "General",
        ConnectorStatuses.Available,
        [AuthStrategies.None, AuthStrategies.ApiKey, AuthStrategies.Bearer, AuthStrategies.Basic, AuthStrategies.OAuth2ClientCredentials, AuthStrategies.LoginCall],
        AuthStrategies.ApiKey, ["test"],
        HttpFields([AuthStrategies.None, AuthStrategies.ApiKey, AuthStrategies.Bearer, AuthStrategies.Basic, AuthStrategies.OAuth2ClientCredentials, AuthStrategies.LoginCall],
            AuthStrategies.ApiKey, "The address of the API, such as https://api.example.test/v1. Paths below are added to it.", "https://api.example.test"));

    public static readonly ConnectorDescriptor Pads4 = new(
        "pads4", "PADS4", "Connect to a PADS4 server by its own sign-in call. Adding content from the library to PADS4 comes with the next step; for now the connection can be configured and tested.",
        "Digital signage", ConnectorStatuses.Preview,
        [AuthStrategies.LoginCall, AuthStrategies.Bearer, AuthStrategies.ApiKey], AuthStrategies.LoginCall, ["test"],
        // next step: content-library actions. Add them to the capabilities here and implement them on the connector (see IConnector).
        HttpFields([AuthStrategies.LoginCall, AuthStrategies.Bearer, AuthStrategies.ApiKey], AuthStrategies.LoginCall,
            "The address of the PADS4 server's API, such as https://pads4.example.test/api. Paths below are added to it.", "https://pads4.example.test/api"));

    public static readonly ConnectorDescriptor Canva = new(
        "canva", "Canva", "Send designs and assets to and from Canva. Needs the OAuth authorisation-code flow, which arrives with its connector step.", "Design",
        ConnectorStatuses.Planned, [], null, [], []);
}
