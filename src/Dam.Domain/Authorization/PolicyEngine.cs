namespace Dam.Domain.Authorization;

/// <summary>
/// One allow-clause of a user's access to a permission: "resources in this scope, matching these attributes,
/// in these statuses". A resource is accessible when ANY clause matches. The same clauses drive single-resource
/// checks (<see cref="PolicyEngine.Can"/>) and list filtering (search, collections), so the two cannot disagree.
/// </summary>
public sealed record ScopeClause(
    ScopeSpec Scope,
    IReadOnlyDictionary<string, string[]> Attributes,
    bool OwnerOnly,
    /// <summary>Only for assets.read: which statuses are visible. Null means status does not matter.</summary>
    IReadOnlySet<string>? Statuses,
    bool OwnUnpublished,
    string Source)
{
    public bool Matches(ResourceContext? resource, Guid userId)
    {
        // A check without a resource ("may this user ever do X?") is satisfied only by unscoped grants.
        if (resource is null) return Scope.Type == ScopeType.All && !OwnerOnly;

        var isOwner = resource.OwnerId is { } owner && owner == userId;
        if (OwnerOnly && !isOwner) return false;
        if (!ScopeMatches(resource)) return false;
        if (!PolicyEngine.AttributesAllow(Attributes, resource.Attributes)) return false;

        if (Statuses is not null && resource.Status is { } status && status != AssetStatuses.Published)
            return Statuses.Contains(status) || (OwnUnpublished && isOwner);
        return true;
    }

    private bool ScopeMatches(ResourceContext r) => Scope.Type switch
    {
        ScopeType.All => true,
        ScopeType.Folder => r.FolderPath is { } p && PolicyEngine.IsUnder(Scope.Value!, p),
        ScopeType.Collection => Guid.TryParse(Scope.Value, out var id) && r.CollectionIds.Contains(id),
        ScopeType.AssetType => string.Equals(r.AssetType, Scope.Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}

/// <summary>Pure RBAC + ABAC evaluation (spec A7.2). No I/O, so the whole policy matrix is unit-testable.</summary>
public static class PolicyEngine
{
    public static bool Can(AccessProfile profile, string permission, ResourceContext? resource = null)
    {
        if (!profile.Active) return false;
        if (resource?.TenantId is { } tenant && tenant != profile.TenantId) return false;
        return GetScope(profile, permission, includeRules: resource is not null)
            .Any(c => c.Matches(resource, profile.UserId));
    }

    /// <summary>All clauses through which the user holds <paramref name="permission"/>. Empty means no access at all.</summary>
    public static IReadOnlyList<ScopeClause> GetScope(AccessProfile profile, string permission, bool includeRules = true)
    {
        var clauses = new List<ScopeClause>();
        if (!profile.Active) return clauses;
        var own = permission + Permissions.OwnSuffix;
        var isRead = permission == Permissions.AssetsRead;

        foreach (var role in profile.Roles)
        {
            if (!HasRequiredAttributes(role, profile)) continue; // fail closed: a Dealer with no region gets nothing
            var perms = new HashSet<string>(role.Permissions, StringComparer.Ordinal);
            var everything = perms.Contains(permission);
            if (!everything && !perms.Contains(own)) continue;

            // Restricted roles are limited by the user's own attributes in EVERY dimension: a missing value
            // means "only resources that are global in that dimension".
            var constraint = role.AttributeRestricted
                ? Dimensions.All.ToDictionary(d => d, d => profile.Attributes.GetValueOrDefault(d) ?? [])
                : new Dictionary<string, string[]>();
            clauses.Add(Clause(ScopeSpec.Everything, constraint, !everything, isRead, perms, $"role:{role.Name}"));
        }

        if (includeRules)
        {
            foreach (var rule in profile.Rules)
            {
                var perms = new HashSet<string>(rule.Permissions, StringComparer.Ordinal);
                var everything = perms.Contains(permission);
                if (!everything && !perms.Contains(own)) continue;
                // For a rule, an empty list means "this dimension is not constrained", so drop it.
                var attrs = rule.Attributes.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
                clauses.Add(Clause(rule.Scope, attrs, !everything, isRead, perms, $"rule:{rule.RuleId}"));
            }
        }
        return clauses;
    }

    private static ScopeClause Clause(ScopeSpec scope, IReadOnlyDictionary<string, string[]> attrs, bool ownerOnly,
        bool isRead, HashSet<string> perms, string source)
    {
        IReadOnlySet<string>? statuses = null;
        var ownUnpublished = false;
        if (isRead)
        {
            if (perms.Contains(Permissions.AssetsReadUnpublished)) statuses = null;
            else
            {
                var set = new HashSet<string> { AssetStatuses.Published };
                if (perms.Contains(Permissions.AssetsReadInReview)) set.Add(AssetStatuses.InReview);
                statuses = set;
                ownUnpublished = perms.Contains(Permissions.AssetsReadUnpublishedOwn);
            }
        }
        return new ScopeClause(scope, attrs, ownerOnly, statuses, ownUnpublished, source);
    }

    /// <summary>Roles that actually grant something: an inactive user or a role missing a required attribute grants nothing.</summary>
    public static IReadOnlyList<RoleGrant> RolesInEffect(AccessProfile profile) =>
        profile.Active ? profile.Roles.Where(r => HasRequiredAttributes(r, profile)).ToList() : [];

    /// <summary>Roles the user holds but that are switched off, e.g. Dealer without a region attribute.</summary>
    public static IReadOnlyList<string> RolesBlocked(AccessProfile profile) =>
        profile.Roles.Where(r => !HasRequiredAttributes(r, profile)).Select(r => r.Name).Order().ToList();

    private static bool HasRequiredAttributes(RoleGrant role, AccessProfile profile) =>
        role.RequiredAttributes is null
        || role.RequiredAttributes.All(a => profile.Attributes.TryGetValue(a, out var v) && v.Length > 0);

    /// <summary>
    /// For every dimension the constraint names: the resource passes if it has no value there, is tagged Global,
    /// or shares at least one value with the constraint. Dimensions the constraint does not name are ignored.
    /// </summary>
    public static bool AttributesAllow(IReadOnlyDictionary<string, string[]> constraint, IReadOnlyDictionary<string, string[]> resource)
    {
        foreach (var (dimension, allowed) in constraint)
        {
            var values = resource.GetValueOrDefault(dimension);
            if (values is null || values.Length == 0) continue;
            if (values.Any(v => string.Equals(v, Dimensions.Global, StringComparison.OrdinalIgnoreCase))) continue;
            // An empty allowed-list is a real constraint: "nothing but Global" (used for restricted roles).
            if (!values.Any(v => allowed.Contains(v, StringComparer.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="prefix"/> or lies beneath it (dotted ltree paths).</summary>
    public static bool IsUnder(string prefix, string path) =>
        string.Equals(prefix, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
}
