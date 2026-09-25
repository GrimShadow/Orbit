using Dam.Domain.Authorization;
using Dam.Domain.Common;

namespace Dam.Domain.Identity;

public enum TenantStatus { Active, Suspended }
public enum UserStatus { Active, Disabled }
public enum GroupSource { Idp, Local }

/// <summary>One customer or brand group. Row-level security lets a session see only its own tenant row.</summary>
public sealed class Tenant : Entity
{
    private Tenant() { }

    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public TenantStatus Status { get; private set; } = TenantStatus.Active;
    /// <summary>Free-form tenant settings as a JSON object.</summary>
    public string SettingsJson { get; private set; } = "{}";
    public string StorageConfigJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }

    public static Tenant Create(Guid id, string name, string slug, DateTimeOffset now) =>
        new() { Id = id, Name = name.Trim(), Slug = slug.Trim().ToLowerInvariant(), CreatedAt = now };

    public void Update(string? name, string? settingsJson, string? storageConfigJson)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (settingsJson is not null) SettingsJson = settingsJson;
        if (storageConfigJson is not null) StorageConfigJson = storageConfigJson;
    }
}

/// <summary>A person or machine known to a tenant. Created on first login (JIT); the IdP stays the source of truth.</summary>
public sealed class User : AggregateRoot
{
    private User() { }

    public string ExternalSubject { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public UserStatus Status { get; private set; } = UserStatus.Active;
    /// <summary>ABAC attributes (region, dealer, brand, channel) as synced from the IdP token.</summary>
    public Dictionary<string, string[]> Attributes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public static User Provision(Guid tenantId, string subject, string email, string displayName,
        Dictionary<string, string[]> attributes, DateTimeOffset now)
    {
        var u = new User
        {
            TenantId = tenantId, ExternalSubject = subject, Email = email, DisplayName = displayName,
            Attributes = Normalize(attributes), CreatedAt = now, LastSeenAt = now,
        };
        u.Raise(new UserProvisioned(u.Id, subject, email));
        return u;
    }

    /// <summary>Applies IdP claims. Returns true when something changed.</summary>
    public bool SyncFromIdp(string email, string displayName, Dictionary<string, string[]> attributes, DateTimeOffset now)
    {
        var changed = false;
        if (Email != email) { Email = email; changed = true; }
        if (DisplayName != displayName) { DisplayName = displayName; changed = true; }
        var normalized = Normalize(attributes);
        if (!AttributesEqual(Attributes, normalized)) { Attributes = normalized; changed = true; }
        LastSeenAt = now;
        return changed;
    }

    public void SetStatus(UserStatus status) => Status = status;

    private static Dictionary<string, string[]> Normalize(Dictionary<string, string[]> a) =>
        a.Where(kv => Dimensions.All.Contains(kv.Key) && kv.Value.Length > 0)
         .ToDictionary(kv => kv.Key, kv => kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().Order().ToArray());

    private static bool AttributesEqual(Dictionary<string, string[]> x, Dictionary<string, string[]> y) =>
        x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));
}

public sealed class Group : TenantEntity
{
    private Group() { }

    public string Name { get; private set; } = "";
    public GroupSource Source { get; private set; }

    public static Group Create(Guid tenantId, string name, GroupSource source) =>
        new() { TenantId = tenantId, Name = name.Trim(), Source = source };

    public void Rename(string name) => Name = name.Trim();
}

public sealed class Role : TenantEntity
{
    private Role() { }

    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public List<string> Permissions { get; private set; } = [];
    public bool IsBuiltIn { get; private set; }
    public bool AttributeRestricted { get; private set; }
    public List<string> RequiredAttributes { get; private set; } = [];

    public static Role Create(Guid tenantId, string name, string description, IEnumerable<string> permissions,
        bool attributeRestricted = false, IEnumerable<string>? requiredAttributes = null, bool isBuiltIn = false) =>
        new()
        {
            TenantId = tenantId, Name = name.Trim(), Description = description, IsBuiltIn = isBuiltIn,
            Permissions = permissions.Distinct().Order().ToList(), AttributeRestricted = attributeRestricted,
            RequiredAttributes = (requiredAttributes ?? []).Distinct().ToList(),
        };

    public static Role FromBuiltIn(Guid tenantId, BuiltInRole b) =>
        Create(tenantId, b.Name, b.Description, b.Permissions, b.AttributeRestricted, b.RequiredAttributes, isBuiltIn: true);

    /// <summary>Built-in roles are defined in code; this brings a stored copy up to the current definition.</summary>
    public void SyncFromBuiltIn(BuiltInRole b)
    {
        Description = b.Description;
        Permissions = b.Permissions.Distinct().Order().ToList();
        AttributeRestricted = b.AttributeRestricted;
        RequiredAttributes = (b.RequiredAttributes ?? []).ToList();
    }

    /// <summary>Built-in roles keep their permission sets; only custom roles may change them.</summary>
    public void Update(string? description, IEnumerable<string>? permissions, bool? attributeRestricted, IEnumerable<string>? requiredAttributes)
    {
        if (description is not null) Description = description;
        if (IsBuiltIn) return;
        if (permissions is not null) Permissions = permissions.Distinct().Order().ToList();
        if (attributeRestricted is not null) AttributeRestricted = attributeRestricted.Value;
        if (requiredAttributes is not null) RequiredAttributes = requiredAttributes.Distinct().ToList();
    }
}

/// <summary>ABAC layer on top of roles (spec A4.1): grants permissions on a scope, optionally narrowed by attributes.</summary>
public sealed class AccessRule : TenantEntity
{
    private AccessRule() { }

    public PrincipalType PrincipalType { get; private set; }
    public Guid PrincipalId { get; private set; }
    public ScopeType ScopeType { get; private set; }
    public string? ScopeValue { get; private set; }
    public Dictionary<string, string[]> Attributes { get; private set; } = [];
    public List<string> Permissions { get; private set; } = [];
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AccessRule Create(Guid tenantId, PrincipalType principalType, Guid principalId, ScopeType scopeType,
        string? scopeValue, Dictionary<string, string[]>? attributes, IEnumerable<string> permissions, Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, PrincipalType = principalType, PrincipalId = principalId, ScopeType = scopeType,
            ScopeValue = scopeValue, Attributes = attributes ?? [], Permissions = permissions.Distinct().Order().ToList(),
            CreatedBy = createdBy, CreatedAt = now,
        };

    public void Update(ScopeType scopeType, string? scopeValue, Dictionary<string, string[]>? attributes, IEnumerable<string> permissions)
    {
        ScopeType = scopeType;
        ScopeValue = scopeValue;
        Attributes = attributes ?? [];
        Permissions = permissions.Distinct().Order().ToList();
    }

    public RuleGrant ToGrant() => new(Id, new ScopeSpec(ScopeType, ScopeValue), Attributes, Permissions);
}

// Join rows carry tenant_id so they are protected by row-level security like everything else.
public sealed class UserGroup : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
}

public sealed class UserRole : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class GroupRole : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid GroupId { get; set; }
    public Guid RoleId { get; set; }
}
