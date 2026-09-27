using Dam.Domain.Authorization;
using Dam.Infrastructure.Search;

namespace Dam.UnitTests;

/// <summary>
/// Verifies <see cref="AccessQueryTranslator"/> produces exactly the filter <see cref="ScopeClause.Matches"/> would have
/// checked in memory - this is the one piece of Step 1.7b a tenant's data isolation actually depends on, so every branch
/// of <c>ScopeClause.Matches</c> gets its own case here.
/// </summary>
public sealed class AccessQueryTranslatorTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static ScopeClause Clause(ScopeSpec? scope = null, IReadOnlyDictionary<string, string[]>? attributes = null,
        bool ownerOnly = false, IReadOnlySet<string>? statuses = null, bool ownUnpublished = false) =>
        new(scope ?? ScopeSpec.Everything, attributes ?? new Dictionary<string, string[]>(), ownerOnly, statuses, ownUnpublished, "test");

    private static Dictionary<string, object?> Bool(Dictionary<string, object?> node) => (Dictionary<string, object?>)node["bool"]!;
    private static object?[] Should(Dictionary<string, object?> node) => (object?[])Bool(node)["should"]!;
    private static object?[] Filter(Dictionary<string, object?> node) => (object?[])Bool(node)["filter"]!;

    /// <summary>The whole scope: one should-branch per clause, OR'd together (any clause matching is enough).</summary>
    [Fact]
    public void Multiple_clauses_become_should_branches_requiring_at_least_one_match()
    {
        var query = AccessQueryTranslator.ToQuery([Clause(), Clause(ScopeSpec.OfAssetType("image"))], UserId);
        var b = Bool(query);
        Assert.Equal(2, ((object?[])b["should"]!).Length);
        Assert.Equal(1, b["minimum_should_match"]);
    }

    [Fact]
    public void An_unconstrained_clause_with_no_conditions_matches_everything()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause()], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        Assert.True(branch.ContainsKey("match_all"));
    }

    [Fact]
    public void A_folder_scope_matches_the_folder_itself_or_anything_beneath_it()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause(ScopeSpec.Folder("brand.roadster"))], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var scopeFilter = (Dictionary<string, object?>)Filter(branch)[0]!;
        var options = Should(scopeFilter);
        var selfTerm = (Dictionary<string, object?>)((Dictionary<string, object?>)options[0]!)["term"]!;
        var descendantPrefix = (Dictionary<string, object?>)((Dictionary<string, object?>)options[1]!)["prefix"]!;
        Assert.Equal("brand.roadster", selfTerm["folder_path"]);
        Assert.Equal("brand.roadster.", descendantPrefix["folder_path"]); // trailing dot: "brand.roadster2" must not match
    }

    [Fact]
    public void An_asset_type_scope_matches_case_insensitively_like_PolicyEngine_does()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause(ScopeSpec.OfAssetType("Image"))], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var term = (Dictionary<string, object?>)((Dictionary<string, object?>)Filter(branch)[0]!)["term"]!;
        var typeMatch = (Dictionary<string, object?>)term["type"]!;
        Assert.Equal("Image", typeMatch["value"]);
        Assert.Equal(true, typeMatch["case_insensitive"]);
    }

    [Fact]
    public void A_collection_scope_matches_membership_by_id()
    {
        var id = Guid.NewGuid();
        var clause = AccessQueryTranslator.ToQuery([Clause(ScopeSpec.Collection(id))], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var term = (Dictionary<string, object?>)((Dictionary<string, object?>)Filter(branch)[0]!)["term"]!;
        Assert.Equal(id.ToString(), term["collection_ids"]);
    }

    [Fact]
    public void Owner_only_filters_to_the_calling_user()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause(ownerOnly: true)], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var term = (Dictionary<string, object?>)((Dictionary<string, object?>)Filter(branch)[0]!)["term"]!;
        Assert.Equal(UserId, term["owner_id"]);
    }

    /// <summary>Mirrors PolicyEngine.AttributesAllow: a resource passes a dimension if it has no value there, is
    /// tagged "Global", or intersects the allowed set.</summary>
    [Fact]
    public void An_attribute_dimension_allows_no_value_global_or_an_allowed_value()
    {
        var attrs = new Dictionary<string, string[]> { ["region"] = ["north", "south"] };
        var clause = AccessQueryTranslator.ToQuery([Clause(attributes: attrs)], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var dimensionFilter = (Dictionary<string, object?>)Filter(branch)[0]!;
        var options = Should(dimensionFilter);
        Assert.Equal(3, options.Length);

        var noValue = (Dictionary<string, object?>)options[0]!;
        var mustNotExists = (Dictionary<string, object?>)((Dictionary<string, object?>)((Dictionary<string, object?>)noValue["bool"]!)["must_not"]!)["exists"]!;
        Assert.Equal("attributes.region", mustNotExists["field"]);

        var globalTerms = (Dictionary<string, object?>)((Dictionary<string, object?>)options[1]!)["terms"]!;
        Assert.Equal(new[] { "Global" }, globalTerms["attributes.region"]);

        var allowedTerms = (Dictionary<string, object?>)((Dictionary<string, object?>)options[2]!)["terms"]!;
        Assert.Equal(attrs["region"], allowedTerms["attributes.region"]);
    }

    [Fact]
    public void An_empty_attribute_dimension_is_not_constrained_and_adds_no_filter()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause(attributes: new Dictionary<string, string[]> { ["region"] = [] })], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        Assert.True(branch.ContainsKey("match_all")); // no attribute condition means no conditions at all here
    }

    /// <summary>published is always visible (Statuses always includes it); anything else needs an explicit grant or
    /// - when the clause also grants assets.read.unpublished.own - ownership.</summary>
    [Fact]
    public void A_status_restricted_clause_allows_the_granted_statuses_or_the_owner_when_own_unpublished_is_granted()
    {
        var statuses = new HashSet<string> { "published", "in_review" };
        var clause = AccessQueryTranslator.ToQuery([Clause(statuses: statuses, ownUnpublished: true)], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var statusFilter = (Dictionary<string, object?>)Filter(branch)[0]!;
        var options = Should(statusFilter);
        Assert.Equal(2, options.Length);
        var terms = (Dictionary<string, object?>)((Dictionary<string, object?>)options[0]!)["terms"]!;
        Assert.Equal(statuses.OrderBy(x => x), ((string[])terms["status"]!).OrderBy(x => x));
        var ownerTerm = (Dictionary<string, object?>)((Dictionary<string, object?>)options[1]!)["term"]!;
        Assert.Equal(UserId, ownerTerm["owner_id"]);
    }

    [Fact]
    public void A_status_restricted_clause_without_own_unpublished_has_no_owner_fallback()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause(statuses: new HashSet<string> { "published" })], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        var statusFilter = (Dictionary<string, object?>)Filter(branch)[0]!;
        Assert.Single(Should(statusFilter));
    }

    [Fact]
    public void A_null_statuses_set_read_unpublished_adds_no_status_filter_at_all()
    {
        var clause = AccessQueryTranslator.ToQuery([Clause(statuses: null)], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        Assert.True(branch.ContainsKey("match_all")); // no scope/owner/attributes/status conditions -> unconstrained
    }

    [Fact]
    public void Every_applicable_condition_on_one_clause_is_AND_ed_together_as_filters()
    {
        var attrs = new Dictionary<string, string[]> { ["brand"] = ["acme"] };
        var clause = AccessQueryTranslator.ToQuery(
            [Clause(ScopeSpec.Folder("brand"), attrs, ownerOnly: true, statuses: new HashSet<string> { "published" })], UserId);
        var branch = (Dictionary<string, object?>)Should(clause)[0]!;
        // scope + owner + one attribute dimension + status = 4 AND-ed filter entries.
        Assert.Equal(4, Filter(branch).Length);
    }
}
