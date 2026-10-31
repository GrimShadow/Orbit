using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dam.Shared.Integrations;

/// <param name="Values">The values of <paramref name="Field"/> for which the field is shown. Called <c>equals</c> on the wire.</param>
public sealed record ConnectorShowWhenDto(string Field, [property: JsonPropertyName("equals")] IReadOnlyList<string> Values);

public sealed record ConnectorFieldOptionDto(string Value, string Label);

/// <param name="Type">text, url, secret, select, boolean, number or textarea.</param>
/// <param name="Group">connection, authentication or advanced.</param>
/// <param name="Default">A JSON value of the field's type, applied when a configuration leaves the field out.</param>
/// <param name="ShowWhen">Shows the field only while another field (the authentication strategy) holds one of the listed values.</param>
public sealed record ConnectorFieldDto(
    string Key, string Label, string Type, bool Required, string? Help, string? Placeholder, string Group, IReadOnlyList<ConnectorFieldOptionDto> Options, JsonElement? Default,
    ConnectorShowWhenDto? ShowWhen);

/// <param name="Category">A short grouping label for the catalogue.</param>
/// <param name="Status">available (works now), preview (can be configured and tested; its actions are still to come) or planned (cannot be created yet).</param>
/// <param name="AuthStrategies">none, api_key, bearer, basic, oauth2_client_credentials, login_call.</param>
/// <param name="Capabilities">What the connector can do now (for example test).</param>
public sealed record ConnectorDto(
    string Key, string Name, string Description, string Category, string Status, IReadOnlyList<string> AuthStrategies, string? DefaultAuthStrategy,
    IReadOnlyList<string> Capabilities, IReadOnlyList<ConnectorFieldDto> Fields);

/// <param name="Set">Whether the secret has a value.</param>
/// <param name="Hint">The last four characters, to tell secrets apart; null when unset or too short to give any away. The secret itself is never returned.</param>
public sealed record SecretStateDto(bool Set, string? Hint);

/// <param name="Config">Non-secret values by field key (text, numbers and booleans as the field's type says).</param>
/// <param name="Secrets">One entry per secret field of the connector.</param>
/// <param name="Status">new (never tested), ok or error.</param>
/// <param name="StatusDetail">Why the last test failed.</param>
/// <param name="TokenExpiresAt">When the cached access token ends; null when no token is cached.</param>
public sealed record ConnectionDto(
    Guid Id, string Connector, string Name, bool Enabled, IReadOnlyDictionary<string, object?> Config, IReadOnlyDictionary<string, SecretStateDto> Secrets,
    string Status, string? StatusDetail, DateTimeOffset? LastTestedAt, DateTimeOffset? LastOkAt, DateTimeOffset? TokenExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Connector">The connector's key. Required when creating; a connection's connector cannot be changed.</param>
/// <param name="Config">Non-secret values by field key.</param>
/// <param name="Secrets">Secrets to set, by field key. A secret you send replaces the stored one; one you leave out (or send empty) is kept.</param>
/// <param name="ClearSecrets">Secrets to remove, by field key.</param>
public sealed record ConnectionInput(
    string? Connector, string Name, bool Enabled = true, Dictionary<string, object?>? Config = null, Dictionary<string, string?>? Secrets = null,
    IReadOnlyList<string>? ClearSecrets = null);

public sealed record TestStepDto(string Name, bool Ok, string Detail, int DurationMs);

public sealed record TestResultDto(bool Ok, int DurationMs, DateTimeOffset TestedAt, IReadOnlyList<TestStepDto> Steps);

/// <param name="Kind">auth, test or action.</param>
/// <param name="Target">Host and path of the call; never the query string.</param>
/// <param name="Error">A sanitised message when the call failed.</param>
public sealed record ActivityDto(Guid Id, string Kind, string Method, string Target, int? StatusCode, bool Ok, int DurationMs, string? Error, DateTimeOffset CreatedAt);
