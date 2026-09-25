namespace Dam.Domain.Authorization;

public sealed record BuiltInRole(
    string Name, string Description, IReadOnlyList<string> Permissions,
    bool AttributeRestricted = false, IReadOnlyList<string>? RequiredAttributes = null);

/// <summary>
/// Built-in roles (spec A4.1). Scoped powers such as assets.approve are deliberately NOT in a role: they are granted
/// only through AccessRules, so an Approver can approve only in the folders they are assigned to.
/// </summary>
public static class BuiltInRoles
{
    public const string Admin = "Admin";
    public const string Librarian = "Librarian";
    public const string Editor = "Editor";
    public const string Approver = "Approver";
    public const string Contributor = "Contributor";
    public const string Viewer = "Viewer";
    public const string Dealer = "Dealer";
    public const string ApiClient = "ApiClient";

    public static readonly IReadOnlyList<BuiltInRole> All =
    [
        new(Admin, "Full control of the tenant.", Permissions.All),
        new(Librarian, "Curates the library: structure, metadata, taxonomy, rights and publishing.",
        [
            Permissions.AssetsRead, Permissions.AssetsReadUnpublished, Permissions.AssetsCreate, Permissions.AssetsUpdate,
            Permissions.AssetsDelete, Permissions.AssetsMove, Permissions.AssetsSubmit, Permissions.AssetsWithdraw,
            Permissions.AssetsPublish, Permissions.AssetsDownloadOriginal, Permissions.AssetsBulk,
            Permissions.FoldersManage, Permissions.CollectionsManage, Permissions.MetadataSchemaManage, Permissions.TaxonomyManage,
            Permissions.RightsManage, Permissions.KnowledgeManage, Permissions.QrManage, Permissions.ShareManage,
            Permissions.PortalsManage, Permissions.CommentsWrite, Permissions.AnalyticsRead,
        ]),
        new(Editor, "Creates and edits assets and submits them for review.",
        [
            Permissions.AssetsRead, Permissions.AssetsReadUnpublished, Permissions.AssetsCreate, Permissions.AssetsUpdate,
            Permissions.AssetsSubmit, Permissions.AssetsWithdraw, Permissions.AssetsDownloadOriginal, Permissions.AssetsBulk,
            Permissions.CollectionsManage, Permissions.ShareManage, Permissions.CommentsWrite,
        ]),
        new(Approver, "Reviews assets. Approving needs an access rule that assigns the folders.",
        [Permissions.AssetsRead, Permissions.AssetsReadUnpublished, Permissions.CommentsWrite]),
        new(Contributor, "Adds assets and works only on their own.",
        [
            Permissions.AssetsRead, Permissions.AssetsCreate, Permissions.AssetsReadUnpublishedOwn, Permissions.AssetsUpdateOwn,
            Permissions.AssetsSubmitOwn, Permissions.AssetsWithdrawOwn, Permissions.CommentsWrite,
        ]),
        new(Viewer, "Reads published assets.", [Permissions.AssetsRead]),
        new(Dealer, "External dealer: published assets for their own region only.", [Permissions.AssetsRead],
            AttributeRestricted: true, RequiredAttributes: [Dimensions.Region]),
        new(ApiClient, "Machine client (apps, PADS4 plugin): published assets for its channel; writes usage.",
            [Permissions.AssetsRead, Permissions.UsageWrite],
            AttributeRestricted: true, RequiredAttributes: [Dimensions.Channel]),
    ];

    public static BuiltInRole? Find(string name) =>
        All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}
