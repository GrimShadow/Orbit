using System.Text.Json;
using Dam.Domain.Common;

namespace Dam.Domain.Integrations;

public static class ConnectionStatuses
{
    public const string New = "new";
    public const string Ok = "ok";
    public const string Error = "error";
    public static readonly IReadOnlyList<string> All = [New, Ok, Error];
}

public static class CallKinds
{
    public const string Auth = "auth";
    public const string Test = "test";
    public const string Action = "action";
    public static readonly IReadOnlyList<string> All = [Auth, Test, Action];
}

/// <summary>What a stored secret keeps: the ciphertext, and the last four characters of the secret (null for a short one) to tell secrets apart.</summary>
public sealed record StoredSecret(string Cipher, string? Hint);

/// <summary>What each encrypted value is bound to, so a ciphertext cannot be moved between connections, fields or uses (it is authenticated data of the encryption).</summary>
public static class IntegrationSecretPurposes
{
    public static string Field(Guid connectionId, string fieldKey) => $"integration-secret/{connectionId:N}/{fieldKey}";
    public static string Token(Guid connectionId) => $"integration-token/{connectionId:N}";
}

public sealed record IntegrationConnectionCreated(Guid ConnectionId, string Connector, string ConnectionName, bool Enabled) : DomainEvent, IEntityEvent
{
    public const string EventName = "integration.connection.created";
    public override string Name => EventName;
    public string EntityType => "integration_connection";
    public Guid EntityId => ConnectionId;
}

/// <param name="Changed">Names of the settings that changed (<c>config.baseUrl</c>, <c>secrets.apiKey</c>, <c>name</c>, <c>enabled</c>): names only, never values.</param>
public sealed record IntegrationConnectionUpdated(Guid ConnectionId, string Connector, string ConnectionName, bool Enabled, IReadOnlyList<string> Changed) : DomainEvent, IEntityEvent
{
    public const string EventName = "integration.connection.updated";
    public override string Name => EventName;
    public string EntityType => "integration_connection";
    public Guid EntityId => ConnectionId;
}

public sealed record IntegrationConnectionDeleted(Guid ConnectionId, string Connector, string ConnectionName) : DomainEvent, IEntityEvent
{
    public const string EventName = "integration.connection.deleted";
    public override string Name => EventName;
    public string EntityType => "integration_connection";
    public Guid EntityId => ConnectionId;
}

public sealed record IntegrationConnectionTested(Guid ConnectionId, string Connector, string ConnectionName, bool Ok, int DurationMs) : DomainEvent, IEntityEvent
{
    public const string EventName = "integration.connection.tested";
    public override string Name => EventName;
    public string EntityType => "integration_connection";
    public Guid EntityId => ConnectionId;
}

/// <summary>
/// One configured use of a connector by a tenant (ADR-0040 decision 2): non-secret settings, encrypted secrets, the token cache and the result of the last test.
/// The ciphertexts live here but are only ever decrypted by the connector runtime; nothing in an event, a DTO or a log carries them.
/// </summary>
public sealed class IntegrationConnection : AggregateRoot
{
    private IntegrationConnection() { }

    public string Connector { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool Enabled { get; private set; } = true;
    public Dictionary<string, JsonElement> Config { get; private set; } = [];
    public Dictionary<string, StoredSecret> Secrets { get; private set; } = [];
    public string Status { get; private set; } = ConnectionStatuses.New;
    public string? StatusDetail { get; private set; }
    public DateTimeOffset? LastTestedAt { get; private set; }
    public DateTimeOffset? LastOkAt { get; private set; }
    /// <summary>When the cached token ends (null: none cached).</summary>
    public DateTimeOffset? TokenExpiresAt { get; private set; }
    /// <summary>The cached token, encrypted. Written by the connector runtime, which reads and replaces it with single statements.</summary>
    public string? TokenCipher { get; private set; }
    /// <summary>While a sign-in is under way: until when other callers wait for it instead of signing in too (single-flight, ADR-0040 decision 4).</summary>
    public DateTimeOffset? LoginLeaseUntil { get; private set; }
    /// <summary>When a test last started; at most one test per <see cref="ConnectionRules.TestInterval"/>.</summary>
    public DateTimeOffset? TestClaimedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static IntegrationConnection Create(Guid id, Guid tenantId, string connector, string name, bool enabled, IReadOnlyDictionary<string, JsonElement> config,
        IReadOnlyDictionary<string, StoredSecret> secrets, Guid? createdBy, DateTimeOffset now)
    {
        var c = new IntegrationConnection
        {
            TenantId = tenantId, Connector = connector, Name = name.Trim(), Enabled = enabled, Config = new Dictionary<string, JsonElement>(config),
            Secrets = new Dictionary<string, StoredSecret>(secrets), CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        };
        c.SetId(id);
        c.Raise(new IntegrationConnectionCreated(c.Id, c.Connector, c.Name, c.Enabled));
        return c;
    }

    private void SetId(Guid id) => Id = id;

    /// <summary>Replaces the settings. Any change to settings or secrets drops the cached token (it was fetched with the old ones) and the last test result (it no longer applies).</summary>
    public void Update(string name, bool enabled, IReadOnlyDictionary<string, JsonElement> config, IReadOnlyDictionary<string, StoredSecret> secrets, IReadOnlyList<string> changedSecrets,
        DateTimeOffset now)
    {
        var changed = new List<string>();
        if (!string.Equals(Name, name.Trim(), StringComparison.Ordinal)) changed.Add("name");
        if (Enabled != enabled) changed.Add("enabled");
        changed.AddRange(ConnectionRules.ChangedKeys(Config, config).Select(k => "config." + k));
        changed.AddRange(changedSecrets.Select(k => "secrets." + k));

        Name = name.Trim();
        Enabled = enabled;
        Config = new Dictionary<string, JsonElement>(config);
        Secrets = new Dictionary<string, StoredSecret>(secrets);
        UpdatedAt = now;
        if (changed.Any(k => k.StartsWith("config.", StringComparison.Ordinal) || k.StartsWith("secrets.", StringComparison.Ordinal)))
        {
            TokenCipher = null;
            TokenExpiresAt = null;
            Status = ConnectionStatuses.New;
            StatusDetail = null;
        }
        Raise(new IntegrationConnectionUpdated(Id, Connector, Name, Enabled, changed));
    }

    public void MarkDeleted() => Raise(new IntegrationConnectionDeleted(Id, Connector, Name));

    /// <summary>Records the outcome of a test: the status, the detail of what failed, and when. <paramref name="detail"/> is already free of secrets.</summary>
    public void RecordTest(bool ok, string? detail, int durationMs, DateTimeOffset now)
    {
        Status = ok ? ConnectionStatuses.Ok : ConnectionStatuses.Error;
        StatusDetail = ok ? null : Clip(detail, 500);
        LastTestedAt = now;
        if (ok) LastOkAt = now;
        UpdatedAt = now;
        Raise(new IntegrationConnectionTested(Id, Connector, Name, ok, durationMs));
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>
/// One call a connector made (ADR-0040 decision 5): which connection, what kind, the method, the host and path (never the query string), the status code, how long it took
/// and, when it failed, a sanitised message. No request or response bodies or headers are kept. History: the latest 500 per connection survive the daily cleanup.
/// </summary>
public sealed class IntegrationCall : TenantEntity
{
    private IntegrationCall() { }

    public const int KeepPerConnection = 500;

    public Guid ConnectionId { get; private set; }
    public string Kind { get; private set; } = CallKinds.Action;
    public string Method { get; private set; } = "GET";
    public string Target { get; private set; } = "";
    public int? StatusCode { get; private set; }
    public bool Ok { get; private set; }
    public int DurationMs { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
