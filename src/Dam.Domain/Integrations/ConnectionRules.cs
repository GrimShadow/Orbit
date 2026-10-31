using System.Text.Json;
using Dam.Domain.Webhooks;

namespace Dam.Domain.Integrations;

/// <summary>Field-level problems, keyed <c>config.&lt;field&gt;</c> or <c>secrets.&lt;field&gt;</c> (and <c>name</c>, <c>connector</c>, <c>clearSecrets</c>).</summary>
public sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool Any => _errors.Count > 0;
    public void Add(string key, string message)
    {
        if (!_errors.TryGetValue(key, out var list)) _errors[key] = list = [];
        if (!list.Contains(message)) list.Add(message);
    }

    public void AddAll(IReadOnlyDictionary<string, string[]> other)
    {
        foreach (var (key, messages) in other) foreach (var m in messages) Add(key, m);
    }

    /// <summary>Adds problems for keys that have none yet (a value that failed its shape check is not also reported as missing).</summary>
    public void AddMissing(IReadOnlyDictionary<string, string[]> other)
    {
        foreach (var (key, messages) in other) if (!_errors.ContainsKey(key)) foreach (var m in messages) Add(key, m);
    }

    public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
}

/// <summary>What an input was reduced to once it passed <see cref="ConnectionRules.CheckInput"/>.</summary>
/// <param name="Config">Normalised non-secret values: trimmed text, empty values removed, defaults filled in.</param>
/// <param name="Secrets">Secrets that were sent (and so replace any stored value), trimmed.</param>
/// <param name="Clear">Secrets to remove.</param>
public sealed record ConnectionInputCheck(Dictionary<string, string[]> Errors, Dictionary<string, JsonElement> Config, Dictionary<string, string> Secrets, HashSet<string> Clear);

/// <summary>Validation of a connection's name and configuration against its connector's field descriptors (ADR-0040 decisions 2 and 3).</summary>
public static class ConnectionRules
{
    public const int MaxPerTenant = 50;
    public const int MaxNameLength = 200;
    public const int MaxTextLength = 500;
    public const int MaxTextareaLength = 4000;
    public const int MaxUrlLength = 2000;
    public const int MaxConfigBytes = 16_000;
    public const int MaxKeyEcho = 64;
    /// <summary>More keys than any connector has fields are refused outright, rather than answered with an error each.</summary>
    public const int MaxKeys = 100;
    /// <summary>Test connection may run at most once in this time for one connection.</summary>
    public static readonly TimeSpan TestInterval = TimeSpan.FromSeconds(5);

    public static string? CheckName(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return "A name is required.";
        if (n.Length > MaxNameLength) return $"A name is at most {MaxNameLength} characters.";
        if (n.Any(char.IsControl)) return "A name cannot contain line breaks or control characters.";
        return null;
    }

    /// <summary>Checks the shape of what was sent: known keys, value types and lengths, secrets only in <c>secrets</c>. Does not check what the chosen strategy needs (see <see cref="CheckComplete"/>).</summary>
    public static ConnectionInputCheck CheckInput(
        ConnectorDescriptor d, IReadOnlyDictionary<string, JsonElement>? config, IReadOnlyDictionary<string, string?>? secrets, IReadOnlyList<string>? clearSecrets)
    {
        var errors = new FieldErrors();
        var normalised = new Dictionary<string, JsonElement>();
        var newSecrets = new Dictionary<string, string>();
        var clear = new HashSet<string>();
        if ((config?.Count ?? 0) > MaxKeys || (secrets?.Count ?? 0) > MaxKeys || (clearSecrets?.Count ?? 0) > MaxKeys)
        {
            errors.Add("config", $"At most {MaxKeys} settings, secrets and cleared secrets can be sent.");
            return new ConnectionInputCheck(errors.ToDictionary(), normalised, newSecrets, clear);
        }

        foreach (var (key, value) in config ?? new Dictionary<string, JsonElement>())
        {
            var field = d.Field(key);
            var at = "config." + Echo(key);
            if (field is null) { errors.Add(at, "This setting is not known to the connector."); continue; }
            if (field.IsSecret) { errors.Add(at, "This is a secret: send it in 'secrets', not in 'config'."); continue; }
            if (Normalise(field, value, errors, at) is { } clean) normalised[key] = clean;
        }

        // Defaults fill what is left out, but only for fields that apply (a field shown for another strategy keeps no default). Fields are in dependency order.
        foreach (var field in d.Fields.Where(f => !f.IsSecret && f.Default is not null && !normalised.ContainsKey(f.Key)))
        {
            var applies = field.ShowWhen is null
                || (normalised.TryGetValue(field.ShowWhen.Field, out var current) && current.ValueKind == JsonValueKind.String && field.ShowWhen.Matches(current.GetString()));
            if (applies) normalised[field.Key] = field.Default!.Value.Clone();
        }

        if (JsonSerializer.Serialize(normalised).Length > MaxConfigBytes) errors.Add("config", $"The settings are too large (at most {MaxConfigBytes:N0} characters).");

        foreach (var (key, value) in secrets ?? new Dictionary<string, string?>())
        {
            var field = d.Field(key);
            var at = "secrets." + Echo(key);
            if (field is not { IsSecret: true }) { errors.Add(at, "This is not a secret field of the connector."); continue; }
            var trimmed = value?.Trim() ?? "";
            if (trimmed.Length == 0) continue;   // left empty: the stored secret (if any) is kept
            if (SecretRules.Check(trimmed) is { } problem) { errors.Add(at, problem); continue; }
            newSecrets[key] = trimmed;
        }

        foreach (var key in clearSecrets ?? [])
        {
            if (d.Field(key) is not { IsSecret: true }) { errors.Add("clearSecrets", $"'{Echo(key)}' is not a secret field of the connector."); continue; }
            if (newSecrets.ContainsKey(key)) { errors.Add("secrets." + key, "A secret cannot be replaced and cleared in the same request."); continue; }
            clear.Add(key);
        }
        return new ConnectionInputCheck(errors.ToDictionary(), normalised, newSecrets, clear);
    }

    private static string Echo(string key)
    {
        var clean = new string(key.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= MaxKeyEcho ? clean : clean[..MaxKeyEcho] + "...";
    }

    private static JsonElement? Normalise(ConnectorField field, JsonElement value, FieldErrors errors, string at)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        switch (field.Type)
        {
            case FieldTypes.Boolean:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.Clone();
                errors.Add(at, "Must be true or false.");
                return null;
            case FieldTypes.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var n)) { errors.Add(at, "Must be a whole number."); return null; }
                if (field.Min is { } min && n < min) { errors.Add(at, $"Must be at least {min:N0}."); return null; }
                if (field.Max is { } max && n > max) { errors.Add(at, $"Must be at most {max:N0}."); return null; }
                return value.Clone();
        }

        if (value.ValueKind != JsonValueKind.String) { errors.Add(at, "Must be text."); return null; }
        var raw = value.GetString()!;
        var text = field.Verbatim ? raw : raw.Trim();
        if (text.Length == 0 && !field.Verbatim) return null;   // empty means "not set"
        var limit = field.MaxLength ?? field.Type switch { FieldTypes.Textarea => MaxTextareaLength, FieldTypes.Url => MaxUrlLength, _ => MaxTextLength };
        if (text.Length > limit) { errors.Add(at, $"At most {limit:N0} characters."); return null; }
        var allowBreaks = field.Type == FieldTypes.Textarea;
        if (text.Any(c => char.IsControl(c) && !(allowBreaks && c is '\n' or '\r' or '\t'))) { errors.Add(at, "Cannot contain line breaks or control characters."); return null; }

        switch (field.Type)
        {
            case FieldTypes.Url:
                if (AddressGuard.CheckSyntax(text) is { } urlProblem) { errors.Add(at, urlProblem); return null; }
                break;
            case FieldTypes.Select:
                if (field.Options is null || !field.Options.Any(o => o.Value == text))
                {
                    errors.Add(at, $"Must be one of: {string.Join(", ", field.Options?.Select(o => o.Value) ?? [])}.");
                    return null;
                }
                break;
        }
        return JsonSerializer.SerializeToElement(text);
    }

    /// <summary>
    /// What the connection needs to be usable: the fields that are always required, then what its authentication strategy needs.
    /// </summary>
    /// <param name="secretSet">Whether a secret field has a value (just sent, or kept from before).</param>
    public static Dictionary<string, string[]> CheckComplete(ConnectorDescriptor d, IReadOnlyDictionary<string, JsonElement> config, Func<string, bool> secretSet)
    {
        var errors = new FieldErrors();
        foreach (var field in d.Fields.Where(f => f.Required))
        {
            var present = field.IsSecret ? secretSet(field.Key) : config.ContainsKey(field.Key);
            if (!present) errors.Add((field.IsSecret ? "secrets." : "config.") + field.Key, "Required.");
        }
        if (d.AuthStrategies.Count > 0)
        {
            var settings = HttpAuthSettings.Read(config);
            if (!d.AuthStrategies.Contains(settings.Strategy)) errors.Add("config." + FieldKeys.AuthStrategy, $"Must be one of: {string.Join(", ", d.AuthStrategies)}.");
            else errors.AddAll(HttpAuthRules.Validate(settings, secretSet));
        }
        return errors.ToDictionary();
    }

    /// <summary>The config keys whose value differs between two configurations (names only, for the audit trail).</summary>
    public static IReadOnlyList<string> ChangedKeys(IReadOnlyDictionary<string, JsonElement> before, IReadOnlyDictionary<string, JsonElement> after) =>
        before.Keys.Union(after.Keys).Where(k => !(before.TryGetValue(k, out var a) && after.TryGetValue(k, out var b) && SameValue(a, b))).Order(StringComparer.Ordinal).ToList();

    /// <summary>Same JSON value, however each was written (the database re-spells escapes, so raw text cannot be compared).</summary>
    private static bool SameValue(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetDecimal() == b.GetDecimal(),
            _ => true,
        };
}

/// <summary>What each authentication strategy needs (ADR-0040 decision 4). Keys are <c>config.&lt;field&gt;</c> / <c>secrets.&lt;field&gt;</c>.</summary>
public static class HttpAuthRules
{
    public static Dictionary<string, string[]> Validate(HttpAuthSettings s, Func<string, bool> secretSet)
    {
        var errors = new FieldErrors();
        void Config(string key, string? problem) { if (problem is not null) errors.Add("config." + key, problem); }
        void NeedSetting(string key, string? value) { if (string.IsNullOrEmpty(value)) errors.Add("config." + key, "Required for this authentication."); }
        void NeedSecret(string key, string why) { if (!secretSet(key)) errors.Add("secrets." + key, why); }

        if (!string.IsNullOrEmpty(s.TestPath)) Config(FieldKeys.TestPath, ConnectorUrls.CheckPath(s.TestPath));

        switch (s.Strategy)
        {
            case AuthStrategies.ApiKey:
                if (string.IsNullOrEmpty(s.ApiKeyName)) NeedSetting(FieldKeys.ApiKeyName, null);
                else Config(FieldKeys.ApiKeyName, s.ApiKeyIn == "query" ? HeaderRules.CheckQueryName(s.ApiKeyName) : HeaderRules.CheckName(s.ApiKeyName));
                NeedSecret(FieldKeys.ApiKey, "Required for this authentication.");
                break;
            case AuthStrategies.Bearer:
                NeedSecret(FieldKeys.BearerToken, "Required for this authentication.");
                break;
            case AuthStrategies.Basic:
                NeedSetting(FieldKeys.Username, s.Username);
                if (s.Username?.Contains(':', StringComparison.Ordinal) == true) errors.Add("config." + FieldKeys.Username, "A user name for basic authentication cannot contain a colon.");
                NeedSecret(FieldKeys.Password, "Required for this authentication.");
                break;
            case AuthStrategies.OAuth2ClientCredentials:
                if (string.IsNullOrEmpty(s.TokenUrl)) NeedSetting(FieldKeys.TokenUrl, null);
                else Config(FieldKeys.TokenUrl, AddressGuard.CheckSyntax(s.TokenUrl));
                NeedSetting(FieldKeys.ClientId, s.ClientId);
                NeedSecret(FieldKeys.ClientSecret, "Required for this authentication.");
                TokenHeader(s, errors);
                break;
            case AuthStrategies.LoginCall:
                LoginCall(s, errors, secretSet);
                break;
        }
        return errors.ToDictionary();
    }

    private static void TokenHeader(HttpAuthSettings s, FieldErrors errors)
    {
        if (HeaderRules.CheckName(s.TokenHeader) is { } header) errors.Add("config." + FieldKeys.TokenHeader, header);
        if (HeaderRules.CheckPrefix(s.TokenPrefix) is { } prefix) errors.Add("config." + FieldKeys.TokenPrefix, prefix);
    }

    private static void LoginCall(HttpAuthSettings s, FieldErrors errors, Func<string, bool> secretSet)
    {
        if (s.LoginMethod is not ("POST" or "PUT")) errors.Add("config." + FieldKeys.LoginMethod, "Must be POST or PUT.");
        if (!LoginBodyTypes.All.Contains(s.LoginBodyType)) errors.Add("config." + FieldKeys.LoginBodyType, $"Must be one of: {string.Join(", ", LoginBodyTypes.All)}.");

        if (string.IsNullOrEmpty(s.LoginUrl)) errors.Add("config." + FieldKeys.LoginUrl, "Required for this authentication.");
        else if (ConnectorUrls.IsAbsolute(s.LoginUrl)) { if (AddressGuard.CheckSyntax(s.LoginUrl) is { } u) errors.Add("config." + FieldKeys.LoginUrl, u); }
        else if (ConnectorUrls.CheckPath(s.LoginUrl) is { } p) errors.Add("config." + FieldKeys.LoginUrl, p);

        foreach (var problem in LoginTemplate.Validate(s.LoginBody, s.LoginBodyType)) errors.Add("config." + FieldKeys.LoginBody, problem);

        // Each placeholder the body uses needs its value.
        foreach (var name in LoginTemplate.Used(s.LoginBody, s.LoginBodyType))
        {
            var have = LoginTemplate.IsSecretPlaceholder(name)
                ? secretSet(name)
                : !string.IsNullOrEmpty(name == FieldKeys.Username ? s.Username : s.ClientId);
            var why = $"Needed by the sign-in body ({{{{{name}}}}}).";
            if (!have) errors.Add((LoginTemplate.IsSecretPlaceholder(name) ? "secrets." : "config.") + name, why);
        }

        if (JsonPointer.Check(s.TokenPointer) is { } tp) errors.Add("config." + FieldKeys.TokenPointer, tp);
        if (!string.IsNullOrWhiteSpace(s.ExpiryPointer) && JsonPointer.Check(s.ExpiryPointer) is { } ep) errors.Add("config." + FieldKeys.ExpiryPointer, ep);
        if (s.ExpirySeconds is { } secs && (secs < TokenLifetime.MinFixedSeconds || secs > TokenLifetime.MaxSeconds))
            errors.Add("config." + FieldKeys.ExpirySeconds, $"Must be between {TokenLifetime.MinFixedSeconds} and {TokenLifetime.MaxSeconds:N0}.");
        TokenHeader(s, errors);
    }
}
