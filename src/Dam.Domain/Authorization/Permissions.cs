namespace Dam.Domain.Authorization;

/// <summary>
/// The permission catalog. A permission is a plain string so custom roles can be stored in the database.
/// A trailing ".own" variant (e.g. assets.update.own) grants the permission only on resources the user owns.
/// </summary>
public static class Permissions
{
    // Assets: read is split by status. `assets.read` alone sees published assets only.
    public const string AssetsRead = "assets.read";
    public const string AssetsReadUnpublished = "assets.read.unpublished";
    public const string AssetsReadInReview = "assets.read.in_review";
    public const string AssetsReadUnpublishedOwn = "assets.read.unpublished.own";
    public const string AssetsCreate = "assets.create";
    public const string AssetsUpdate = "assets.update";
    public const string AssetsUpdateOwn = "assets.update.own";
    public const string AssetsDelete = "assets.delete";
    public const string AssetsMove = "assets.move";
    public const string AssetsSubmit = "assets.submit";
    public const string AssetsSubmitOwn = "assets.submit.own";
    public const string AssetsWithdraw = "assets.withdraw";
    public const string AssetsWithdrawOwn = "assets.withdraw.own";
    public const string AssetsApprove = "assets.approve";
    public const string AssetsPublish = "assets.publish";
    public const string AssetsDownloadOriginal = "assets.download.original";
    public const string AssetsBulk = "assets.bulk";

    public const string FoldersManage = "folders.manage";
    public const string CollectionsManage = "collections.manage";
    public const string MetadataSchemaManage = "metadata.schema.manage";
    public const string TaxonomyManage = "taxonomy.manage";

    public const string WorkflowManage = "workflow.manage";
    public const string WorkflowTasksDecide = "workflow.tasks.decide";
    public const string CommentsWrite = "comments.write";

    public const string RightsManage = "rights.manage";
    public const string KnowledgeManage = "knowledge.manage";
    public const string QrManage = "qr.manage";
    public const string ShareManage = "share.manage";
    public const string PortalsManage = "portals.manage";

    public const string AnalyticsRead = "analytics.read";
    public const string UsageWrite = "usage.write";

    public const string TenantsManage = "tenants.manage";
    public const string UsersManage = "users.manage";
    public const string GroupsManage = "groups.manage";
    public const string RolesManage = "roles.manage";
    public const string AccessRulesManage = "access_rules.manage";
    public const string WebhooksManage = "webhooks.manage";
    public const string ApiClientsManage = "api_clients.manage";
    public const string RetentionManage = "retention.manage";
    public const string AuditRead = "audit.read";
    public const string MigrationManage = "migration.manage";

    public const string SystemPing = "system.ping";

    public const string OwnSuffix = ".own";

    /// <summary>Every permission a role may hold. New permissions must be added here AND to the policy matrix test.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        AssetsRead, AssetsReadUnpublished, AssetsReadInReview, AssetsReadUnpublishedOwn, AssetsCreate, AssetsUpdate,
        AssetsUpdateOwn, AssetsDelete, AssetsMove, AssetsSubmit, AssetsSubmitOwn, AssetsWithdraw, AssetsWithdrawOwn,
        AssetsApprove, AssetsPublish, AssetsDownloadOriginal, AssetsBulk,
        FoldersManage, CollectionsManage, MetadataSchemaManage, TaxonomyManage,
        WorkflowManage, WorkflowTasksDecide, CommentsWrite,
        RightsManage, KnowledgeManage, QrManage, ShareManage, PortalsManage,
        AnalyticsRead, UsageWrite,
        TenantsManage, UsersManage, GroupsManage, RolesManage, AccessRulesManage, WebhooksManage, ApiClientsManage,
        RetentionManage, AuditRead, MigrationManage,
        SystemPing,
    ];

    public static bool IsKnown(string permission) => All.Contains(permission);
}
