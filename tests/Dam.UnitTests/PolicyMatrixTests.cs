using Dam.Domain.Authorization;
using static Dam.Domain.Authorization.Permissions;

namespace Dam.UnitTests;

public sealed class PolicyMatrixTests
{
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid Me = Guid.Parse("0197a000-0000-7000-8000-0000000000aa");
    private static readonly Guid Someone = Guid.Parse("0197a000-0000-7000-8000-0000000000bb");

    private static AccessProfile Profile(string[] roles, Dictionary<string, string[]>? attrs = null, RuleGrant[]? rules = null,
        bool active = true, IEnumerable<RoleGrant>? custom = null)
    {
        var grants = roles.Select(r => BuiltInRoles.Find(r) is { } b
            ? new RoleGrant(b.Name, b.Permissions, b.AttributeRestricted, b.RequiredAttributes)
            : throw new ArgumentException(r)).Concat(custom ?? []).ToList();
        return new AccessProfile(Tenant, Me, active, attrs ?? new Dictionary<string, string[]>(), grants, rules ?? []);
    }

    private static Dictionary<string, string[]> Attrs(string? region = null, string? dealer = null, string? channel = null, string? brand = null)
    {
        var d = new Dictionary<string, string[]>();
        if (region is not null) d[Dimensions.Region] = [region];
        if (dealer is not null) d[Dimensions.Dealer] = [dealer];
        if (channel is not null) d[Dimensions.Channel] = [channel];
        if (brand is not null) d[Dimensions.Brand] = [brand];
        return d;
    }

    private static RuleGrant Rule(ScopeSpec scope, string[] permissions, Dictionary<string, string[]>? attrs = null) =>
        new(Guid.NewGuid(), scope, attrs ?? new Dictionary<string, string[]>(), permissions);

    // ---- 1. Role x permission matrix (explicit, independent of BuiltInRoles) ----------------------------------

    private static readonly Dictionary<string, string[]> Expected = new()
    {
        [BuiltInRoles.Admin] = [.. All],
        [BuiltInRoles.Librarian] =
        [
            AssetsRead, AssetsReadUnpublished, AssetsCreate, AssetsUpdate, AssetsDelete, AssetsMove, AssetsSubmit, AssetsWithdraw,
            AssetsPublish, AssetsDownloadOriginal, AssetsBulk, FoldersManage, CollectionsManage, MetadataSchemaManage, TaxonomyManage,
            RightsManage, KnowledgeManage, QrManage, ShareManage, PortalsManage, CommentsWrite, AnalyticsRead,
        ],
        [BuiltInRoles.Editor] =
        [
            AssetsRead, AssetsReadUnpublished, AssetsCreate, AssetsUpdate, AssetsSubmit, AssetsWithdraw, AssetsDownloadOriginal,
            AssetsBulk, CollectionsManage, ShareManage, CommentsWrite,
        ],
        [BuiltInRoles.Approver] = [AssetsRead, AssetsReadUnpublished, CommentsWrite], // approve/decide only via access rules
        [BuiltInRoles.Contributor] =
        [
            AssetsRead, AssetsCreate, AssetsReadUnpublishedOwn, AssetsUpdateOwn, AssetsSubmitOwn, AssetsWithdrawOwn, CommentsWrite,
        ],
        [BuiltInRoles.Viewer] = [AssetsRead],
        [BuiltInRoles.Dealer] = [AssetsRead],
        [BuiltInRoles.ApiClient] = [AssetsRead, UsageWrite],
    };

    public static IEnumerable<object[]> RolePermissionCells() =>
        from role in Expected.Keys from perm in All select new object[] { role, perm, Expected[role].Contains(perm) };

    [Theory]
    [MemberData(nameof(RolePermissionCells))]
    public void Role_holds_exactly_the_expected_permissions(string role, string permission, bool expected)
    {
        var attrs = Attrs(region: "North", channel: "signage"); // satisfies Dealer / ApiClient required attributes
        Assert.Equal(expected, PolicyEngine.Can(Profile([role], attrs), permission));
    }

    [Fact]
    public void Matrix_covers_every_builtin_role_and_every_permission()
    {
        Assert.Equal(BuiltInRoles.All.Select(r => r.Name).Order(), Expected.Keys.Order());
        Assert.Equal(All.Count, All.Distinct().Count());
        // The matrix theory enumerates role x All, so a permission added to All is automatically exercised;
        // this guards that every listed expectation is itself a known permission.
        Assert.All(Expected.Values.SelectMany(x => x), p => Assert.True(IsKnown(p), p));
        foreach (var role in BuiltInRoles.All)
            Assert.All(role.Permissions, p => Assert.True(IsKnown(p), $"{role.Name} holds unknown permission {p}"));
    }

    [Fact]
    public void Scoped_powers_are_never_in_a_builtin_role_except_admin()
    {
        foreach (var r in BuiltInRoles.All.Where(r => r.Name != BuiltInRoles.Admin))
        {
            Assert.DoesNotContain(AssetsApprove, r.Permissions);
            Assert.DoesNotContain(WorkflowTasksDecide, r.Permissions);
        }
    }

    // ---- 2. Dealer in region North (acceptance criterion) ----------------------------------------------------

    public static IEnumerable<object[]> DealerNorthCases() =>
    [
        ["published, North", AssetStatuses.Published, new[] { "North" }, true],
        ["published, South", AssetStatuses.Published, new[] { "South" }, false],
        ["published, Global", AssetStatuses.Published, new[] { "Global" }, true],
        ["published, no region tag", AssetStatuses.Published, null!, true],
        ["published, North+South", AssetStatuses.Published, new[] { "North", "South" }, true],
        ["published, lower-case north", AssetStatuses.Published, new[] { "north" }, true],
        ["draft, North", AssetStatuses.Draft, new[] { "North" }, false],
        ["in_review, North", AssetStatuses.InReview, new[] { "North" }, false],
        ["approved, North", AssetStatuses.Approved, new[] { "North" }, false],
        ["expired, North", AssetStatuses.Expired, new[] { "North" }, false],
        ["archived, Global", AssetStatuses.Archived, new[] { "Global" }, false],
    ];

    [Theory]
    [MemberData(nameof(DealerNorthCases))]
    public void Dealer_north_sees_published_north_or_global_only(string _, string status, string[]? region, bool expected)
    {
        var dealer = Profile([BuiltInRoles.Dealer], Attrs(region: "North"));
        Assert.Equal(expected, PolicyEngine.Can(dealer, AssetsRead, ResourceContext.Asset(status, region: region)));
    }

    [Fact]
    public void Dealer_without_a_region_attribute_sees_nothing_fail_closed()
    {
        var dealer = Profile([BuiltInRoles.Dealer], attrs: []);
        Assert.False(PolicyEngine.Can(dealer, AssetsRead, ResourceContext.Asset(AssetStatuses.Published, region: ["Global"])));
        Assert.False(PolicyEngine.Can(dealer, AssetsRead));
        Assert.Empty(PolicyEngine.GetScope(dealer, AssetsRead));
    }

    [Fact]
    public void Roles_missing_a_required_attribute_are_reported_as_blocked_and_grant_nothing()
    {
        var noRegion = Profile([BuiltInRoles.Dealer, BuiltInRoles.Viewer], attrs: []);
        Assert.Equal([BuiltInRoles.Dealer], PolicyEngine.RolesBlocked(noRegion));
        Assert.Equal([BuiltInRoles.Viewer], PolicyEngine.RolesInEffect(noRegion).Select(r => r.Name));
        var north = Profile([BuiltInRoles.Dealer], Attrs(region: "North"));
        Assert.Empty(PolicyEngine.RolesBlocked(north));
        Assert.Empty(PolicyEngine.RolesInEffect(Profile([BuiltInRoles.Admin], active: false)));
    }

    [Fact]
    public void Dealer_specific_assets_need_a_matching_dealer_attribute()
    {
        var asset = ResourceContext.Asset(AssetStatuses.Published, region: ["North"], dealer: ["D-001"]);
        Assert.True(PolicyEngine.Can(Profile([BuiltInRoles.Dealer], Attrs(region: "North", dealer: "D-001")), AssetsRead, asset));
        Assert.False(PolicyEngine.Can(Profile([BuiltInRoles.Dealer], Attrs(region: "North", dealer: "D-002")), AssetsRead, asset));
        Assert.False(PolicyEngine.Can(Profile([BuiltInRoles.Dealer], Attrs(region: "North")), AssetsRead, asset)); // no dealer attr
        Assert.True(PolicyEngine.Can(Profile([BuiltInRoles.Dealer], Attrs(region: "North")), AssetsRead,
            ResourceContext.Asset(AssetStatuses.Published, region: ["North"], dealer: ["Global"])));
    }

    [Fact]
    public void Dealer_cannot_do_anything_but_read_even_on_their_own_region()
    {
        var dealer = Profile([BuiltInRoles.Dealer], Attrs(region: "North"));
        var north = ResourceContext.Asset(AssetStatuses.Published, region: ["North"]);
        foreach (var p in All.Where(p => p != AssetsRead))
            Assert.False(PolicyEngine.Can(dealer, p, north), p);
    }

    [Fact]
    public void Dealer_scope_is_a_single_clause_limited_to_region_and_published()
    {
        var clause = Assert.Single(PolicyEngine.GetScope(Profile([BuiltInRoles.Dealer], Attrs(region: "North")), AssetsRead));
        Assert.Equal(["North"], clause.Attributes[Dimensions.Region]);
        Assert.Empty(clause.Attributes[Dimensions.Dealer]); // restricted roles constrain every dimension
        Assert.Equal([AssetStatuses.Published], clause.Statuses!);
    }

    // ---- 3. Approver only in assigned folders (acceptance criterion) -----------------------------------------

    private static readonly RuleGrant TharoadsterFolder = Rule(ScopeSpec.Folder("brand.roadster"), [AssetsApprove, WorkflowTasksDecide]);

    public static IEnumerable<object[]> ApproverFolderCases() =>
    [
        ["the assigned folder itself", "brand.roadster", true],
        ["a sub-folder", "brand.roadster.hero", true],
        ["a deep sub-folder", "brand.roadster.hero.stills.2026", true],
        ["a sibling folder", "brand.trail", false],
        ["the parent folder", "brand", false],
        ["a folder that merely shares the prefix", "brand.roadsterx", false],
        ["a folder that merely shares the prefix, deeper", "brand.roadsterx.hero", false],
        ["case differences in the path", "Brand.Roadster.Hero", true],
        ["no folder at all", null!, false],
    ];

    [Theory]
    [MemberData(nameof(ApproverFolderCases))]
    public void Approver_can_approve_only_in_assigned_folders(string _, string? folder, bool expected)
    {
        var approver = Profile([BuiltInRoles.Approver], rules: [TharoadsterFolder]);
        var asset = ResourceContext.Asset(AssetStatuses.InReview, folderPath: folder);
        Assert.Equal(expected, PolicyEngine.Can(approver, AssetsApprove, asset));
        Assert.Equal(expected, PolicyEngine.Can(approver, WorkflowTasksDecide, asset));
    }

    [Fact]
    public void Approver_role_alone_can_approve_nothing_and_a_permission_only_check_is_false()
    {
        var approver = Profile([BuiltInRoles.Approver]);
        Assert.False(PolicyEngine.Can(approver, AssetsApprove, ResourceContext.Asset(AssetStatuses.InReview, "brand.roadster")));
        var withRule = Profile([BuiltInRoles.Approver], rules: [TharoadsterFolder]);
        Assert.False(PolicyEngine.Can(withRule, AssetsApprove)); // scoped grants need a concrete target
    }

    [Fact]
    public void Approver_reads_unpublished_everywhere_but_approves_only_in_scope()
    {
        var approver = Profile([BuiltInRoles.Approver], rules: [TharoadsterFolder]);
        Assert.True(PolicyEngine.Can(approver, AssetsRead, ResourceContext.Asset(AssetStatuses.InReview, "brand.trail")));
        Assert.True(PolicyEngine.Can(approver, AssetsRead, ResourceContext.Asset(AssetStatuses.Draft, "brand.trail")));
    }

    [Fact]
    public void A_rule_grants_only_the_permissions_it_lists()
    {
        var approver = Profile([BuiltInRoles.Approver], rules: [Rule(ScopeSpec.Folder("brand.roadster"), [AssetsApprove])]);
        var asset = ResourceContext.Asset(AssetStatuses.InReview, "brand.roadster");
        Assert.True(PolicyEngine.Can(approver, AssetsApprove, asset));
        Assert.False(PolicyEngine.Can(approver, WorkflowTasksDecide, asset));
        Assert.False(PolicyEngine.Can(approver, AssetsPublish, asset));
    }

    [Fact]
    public void Several_assigned_folders_combine()
    {
        var approver = Profile([BuiltInRoles.Approver], rules:
            [Rule(ScopeSpec.Folder("brand.roadster"), [AssetsApprove]), Rule(ScopeSpec.Folder("brand.trail"), [AssetsApprove])]);
        Assert.True(PolicyEngine.Can(approver, AssetsApprove, ResourceContext.Asset(AssetStatuses.InReview, "brand.trail.x")));
        Assert.True(PolicyEngine.Can(approver, AssetsApprove, ResourceContext.Asset(AssetStatuses.InReview, "brand.roadster")));
        Assert.False(PolicyEngine.Can(approver, AssetsApprove, ResourceContext.Asset(AssetStatuses.InReview, "brand.sedan")));
    }

    // ---- 4. Ownership (.own) ---------------------------------------------------------------------------------

    [Fact]
    public void Contributor_edits_and_reads_drafts_only_on_their_own_assets()
    {
        var c = Profile([BuiltInRoles.Contributor]);
        var mine = ResourceContext.Asset(AssetStatuses.Draft, ownerId: Me);
        var theirs = ResourceContext.Asset(AssetStatuses.Draft, ownerId: Someone);
        var unowned = ResourceContext.Asset(AssetStatuses.Draft);

        Assert.True(PolicyEngine.Can(c, AssetsUpdate, mine));
        Assert.False(PolicyEngine.Can(c, AssetsUpdate, theirs));
        Assert.False(PolicyEngine.Can(c, AssetsUpdate, unowned));
        Assert.False(PolicyEngine.Can(c, AssetsUpdate)); // no resource: cannot prove ownership
        Assert.True(PolicyEngine.Can(c, AssetsSubmit, mine));
        Assert.False(PolicyEngine.Can(c, AssetsSubmit, theirs));
        Assert.True(PolicyEngine.Can(c, AssetsRead, mine));
        Assert.False(PolicyEngine.Can(c, AssetsRead, theirs)); // someone else's draft
        Assert.True(PolicyEngine.Can(c, AssetsRead, ResourceContext.Asset(AssetStatuses.Published, ownerId: Someone)));
        Assert.False(PolicyEngine.Can(c, AssetsDelete, mine));
        Assert.False(PolicyEngine.Can(c, AssetsPublish, mine));
    }

    // ---- 5. Custom review-stage role: sees "ready for review" and "reviewed" assets, never drafts ------------------------

    [Fact]
    public void A_review_stage_role_sees_in_review_and_published_but_not_drafts()
    {
        var reviewer = new RoleGrant("Reviewer", [AssetsRead, AssetsReadInReview]);
        var p = Profile([], custom: [reviewer]);
        Assert.True(PolicyEngine.Can(p, AssetsRead, ResourceContext.Asset(AssetStatuses.Published)));
        Assert.True(PolicyEngine.Can(p, AssetsRead, ResourceContext.Asset(AssetStatuses.InReview)));
        Assert.False(PolicyEngine.Can(p, AssetsRead, ResourceContext.Asset(AssetStatuses.Draft)));
        Assert.False(PolicyEngine.Can(p, AssetsRead, ResourceContext.Asset(AssetStatuses.Approved)));
    }

    // ---- 6. API client channel -------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "signage" }, true)]
    [InlineData(new[] { "web" }, false)]
    [InlineData(new[] { "web", "signage" }, true)]
    [InlineData(new[] { "Global" }, true)]
    [InlineData(null, true)]
    public void Api_client_sees_only_assets_for_its_channel(string[]? channels, bool expected)
    {
        var plugin = Profile([BuiltInRoles.ApiClient], Attrs(channel: "signage"));
        Assert.Equal(expected, PolicyEngine.Can(plugin, AssetsRead, ResourceContext.Asset(AssetStatuses.Published, channel: channels)));
    }

    [Fact]
    public void Api_client_without_a_channel_attribute_is_locked_out()
    {
        Assert.False(PolicyEngine.Can(Profile([BuiltInRoles.ApiClient], attrs: []), AssetsRead,
            ResourceContext.Asset(AssetStatuses.Published, channel: ["Global"])));
    }

    // ---- 7. Rule attributes and other scope types ------------------------------------------------------------

    [Fact]
    public void Rule_attributes_narrow_what_the_rule_grants()
    {
        var editor = Profile([], rules:
        [
            Rule(ScopeSpec.Everything, [AssetsUpdate], new() { [Dimensions.Region] = ["North"], [Dimensions.Brand] = ["Acme"] }),
        ]);
        Assert.True(PolicyEngine.Can(editor, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, region: ["North"], brand: ["Acme"])));
        Assert.False(PolicyEngine.Can(editor, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, region: ["South"], brand: ["Acme"])));
        Assert.False(PolicyEngine.Can(editor, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, region: ["North"], brand: ["Other"])));
        Assert.True(PolicyEngine.Can(editor, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, region: ["Global"], brand: ["Acme"])));
    }

    [Fact]
    public void An_empty_attribute_list_on_a_rule_means_unconstrained()
    {
        var p = Profile([], rules: [Rule(ScopeSpec.Everything, [AssetsUpdate], new() { [Dimensions.Region] = [] })]);
        Assert.True(PolicyEngine.Can(p, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, region: ["South"])));
    }

    [Fact]
    public void Collection_and_asset_type_scopes()
    {
        var col = Guid.NewGuid();
        var p = Profile([], rules: [Rule(ScopeSpec.Collection(col), [AssetsUpdate]), Rule(ScopeSpec.OfAssetType("video"), [AssetsDelete])]);
        Assert.True(PolicyEngine.Can(p, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, collections: [col])));
        Assert.False(PolicyEngine.Can(p, AssetsUpdate, ResourceContext.Asset(AssetStatuses.Draft, collections: [Guid.NewGuid()])));
        Assert.True(PolicyEngine.Can(p, AssetsDelete, ResourceContext.Asset(AssetStatuses.Draft, assetType: "VIDEO")));
        Assert.False(PolicyEngine.Can(p, AssetsDelete, ResourceContext.Asset(AssetStatuses.Draft, assetType: "image")));
    }

    // ---- 8. Tenant isolation, disabled users, unions ---------------------------------------------------------

    [Fact]
    public void Nobody_reaches_another_tenants_resources_not_even_an_admin()
    {
        var admin = Profile([BuiltInRoles.Admin]);
        Assert.True(PolicyEngine.Can(admin, AssetsDelete, ResourceContext.Asset(AssetStatuses.Draft, tenantId: Tenant)));
        Assert.False(PolicyEngine.Can(admin, AssetsDelete, ResourceContext.Asset(AssetStatuses.Draft, tenantId: Guid.NewGuid())));
    }

    [Fact]
    public void A_disabled_user_can_do_nothing()
    {
        var admin = Profile([BuiltInRoles.Admin], active: false);
        Assert.All(All, p => Assert.False(PolicyEngine.Can(admin, p), p));
        Assert.Empty(PolicyEngine.GetScope(admin, AssetsRead));
    }

    [Fact]
    public void Roles_union_and_a_restricted_role_does_not_limit_an_unrestricted_one()
    {
        var both = Profile([BuiltInRoles.Dealer, BuiltInRoles.Editor], Attrs(region: "North"));
        var south = ResourceContext.Asset(AssetStatuses.Draft, region: ["South"]);
        Assert.True(PolicyEngine.Can(both, AssetsRead, south)); // Editor is not attribute-restricted
        Assert.True(PolicyEngine.Can(both, AssetsUpdate, south));
    }

    [Fact]
    public void Unknown_permissions_are_denied_for_everyone_but_no_role_holds_them()
    {
        Assert.False(PolicyEngine.Can(Profile([BuiltInRoles.Admin]), "does.not.exist"));
    }

    [Fact]
    public void Path_prefix_uses_label_boundaries()
    {
        Assert.True(PolicyEngine.IsUnder("a.b", "a.b"));
        Assert.True(PolicyEngine.IsUnder("a.b", "a.b.c"));
        Assert.False(PolicyEngine.IsUnder("a.b", "a.bc"));
        Assert.False(PolicyEngine.IsUnder("a.b", "a"));
    }
}
