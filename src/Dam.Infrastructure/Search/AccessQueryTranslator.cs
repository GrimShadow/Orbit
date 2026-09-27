using Dam.Domain.Authorization;

namespace Dam.Infrastructure.Search;

/// <summary>
/// Translates <see cref="PolicyEngine"/>'s ABAC scope clauses into an OpenSearch filter, so search enforces exactly what
/// <see cref="ScopeClause.Matches"/> enforces in memory today (spec Step 1.7b: "replaces `ListAssetsHandler`'s in-memory
/// filtering with search, which pushes the clauses into the index"). Each clause is one OR-branch (a caller sees an
/// asset if ANY clause matches, mirroring <c>clauses.Any(c => c.Matches(...))</c>); within a clause every condition is
/// AND-ed, mirroring the sequence of early returns in <see cref="ScopeClause.Matches"/>.
/// </summary>
public static class AccessQueryTranslator
{
    /// <summary>The whole scope as one query fragment: "visible if any clause's filter matches".</summary>
    public static Dictionary<string, object?> ToQuery(IReadOnlyList<ScopeClause> clauses, Guid userId) => new()
    {
        ["bool"] = new Dictionary<string, object?> { ["should"] = clauses.Select(c => ClauseQuery(c, userId)).ToArray(), ["minimum_should_match"] = 1 },
    };

    private static Dictionary<string, object?> ClauseQuery(ScopeClause clause, Guid userId)
    {
        var filters = new List<object?>();

        var scope = ScopeFilter(clause.Scope);
        if (scope is not null) filters.Add(scope);

        if (clause.OwnerOnly) filters.Add(Term("owner_id", userId));

        // AttributesAllow: a resource passes a dimension if it has no value there, carries the "Global" marker, or
        // intersects the clause's allowed values - mirrors PolicyEngine.AttributesAllow exactly.
        foreach (var (dimension, allowed) in clause.Attributes)
        {
            if (allowed.Length == 0) continue;
            var field = $"attributes.{dimension}";
            filters.Add(new Dictionary<string, object?>
            {
                ["bool"] = new Dictionary<string, object?>
                {
                    ["should"] = new object?[]
                    {
                        new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["must_not"] = new Dictionary<string, object?> { ["exists"] = new Dictionary<string, object?> { ["field"] = field } } } },
                        Terms(field, [Dimensions.Global]),
                        Terms(field, allowed),
                    },
                    ["minimum_should_match"] = 1,
                },
            });
        }

        // Statuses: null means every status is visible (assets.read.unpublished). Otherwise an asset is visible when its
        // status is in the allowed set (always includes "published", per PolicyEngine.Clause) or, if the clause also
        // grants assets.read.unpublished.own, the caller owns it - matching ScopeClause.Matches's own status check.
        if (clause.Statuses is { } statuses)
        {
            var should = new List<object?> { Terms("status", statuses.ToArray()) };
            if (clause.OwnUnpublished) should.Add(Term("owner_id", userId));
            filters.Add(new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["should"] = should.ToArray(), ["minimum_should_match"] = 1 } });
        }

        return filters.Count == 0
            ? new Dictionary<string, object?> { ["match_all"] = new Dictionary<string, object?>() }
            : new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["filter"] = filters.ToArray() } };
    }

    private static Dictionary<string, object?>? ScopeFilter(ScopeSpec scope) => scope.Type switch
    {
        ScopeType.All => null,
        // Self-or-descendant, exactly like Paths.IsSelfOrDescendant/PolicyEngine.IsUnder: the folder itself, or any path
        // starting with "<value>.".
        ScopeType.Folder => new Dictionary<string, object?>
        {
            ["bool"] = new Dictionary<string, object?>
            {
                ["should"] = new object?[] { Term("folder_path", scope.Value!), Prefix("folder_path", scope.Value! + ".") },
                ["minimum_should_match"] = 1,
            },
        },
        ScopeType.Collection => Term("collection_ids", scope.Value!),
        // PolicyEngine compares asset types case-insensitively (string.Equals(..., OrdinalIgnoreCase)).
        ScopeType.AssetType => new Dictionary<string, object?> { ["term"] = new Dictionary<string, object?> { ["type"] = new Dictionary<string, object?> { ["value"] = scope.Value!, ["case_insensitive"] = true } } },
        _ => new Dictionary<string, object?> { ["match_none"] = new Dictionary<string, object?>() },
    };

    internal static Dictionary<string, object?> Term(string field, object value) => new() { ["term"] = new Dictionary<string, object?> { [field] = value } };
    internal static Dictionary<string, object?> Terms(string field, IReadOnlyList<string> values) => new() { ["terms"] = new Dictionary<string, object?> { [field] = values } };
    private static Dictionary<string, object?> Prefix(string field, string value) => new() { ["prefix"] = new Dictionary<string, object?> { [field] = value } };
}
