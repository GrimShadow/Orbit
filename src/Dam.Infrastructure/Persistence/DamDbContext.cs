using Dam.Application.Abstractions;
using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Domain.Assets;
using Dam.Domain.Comments;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Domain.Notifications;
using Dam.Domain.Webhooks;
using Dam.Domain.Workflow;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Dam.Infrastructure.Persistence;

public sealed class DamDbContext(DbContextOptions<DamDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AccessRule> AccessRules => Set<AccessRule>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<GroupRole> GroupRoles => Set<GroupRole>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Vocabulary> Vocabularies => Set<Vocabulary>();
    public DbSet<Term> Terms => Set<Term>();
    public DbSet<MetadataSchema> MetadataSchemas => Set<MetadataSchema>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<AssetVersion> AssetVersions => Set<AssetVersion>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<Rendition> Renditions => Set<Rendition>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<AssetUsage> AssetUsages => Set<AssetUsage>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowBinding> WorkflowBindings => Set<WorkflowBinding>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<SiemCursor> SiemCursors => Set<SiemCursor>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentMention> CommentMentions => Set<CommentMention>();

    // Referenced by the compiled query filter; EF re-evaluates it per context instance.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt).HasFilter("published_at IS NULL");
        });
        b.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("processed_messages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Before).HasColumnType("jsonb");
            e.Property(x => x.After).HasColumnType("jsonb");
            e.Property(x => x.ChainNo).ValueGeneratedOnAdd();
            e.Property(x => x.PrevHash).ValueGeneratedOnAdd();
            e.Property(x => x.Hash).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.TenantId, x.ChainNo }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.HasIndex(x => new { x.TenantId, x.Ts });
        });

        ConfigureIdentity(b);
        ConfigureContent(b);
        ConfigureAssets(b);
        ConfigureVersions(b);
        ConfigureRenditions(b);
        ConfigureRetention(b);
        ConfigureCollections(b);
        ConfigureUsage(b);
        ConfigureWorkflow(b);
        ConfigureNotifications(b);
        ConfigureComments(b);

        // Defence in depth: EF filter here, Postgres RLS underneath.
        foreach (var t in b.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)))
            b.Entity(t.ClrType).HasQueryFilter(TenantFilter(t.ClrType));
    }

    private void ConfigureIdentity(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.SettingsJson).HasColumnType("jsonb");
            e.Property(x => x.StorageConfigJson).HasColumnType("jsonb");
            // A tenant row is visible only to its own tenant (Postgres RLS enforces the same rule).
            e.HasQueryFilter(t => t.Id == CurrentTenantId);
        });
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Attributes).AsJsonDictionary();
            e.Property(x => x.IdpRoles).HasColumnType("text[]");
            e.HasIndex(x => new { x.TenantId, x.ExternalSubject }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Email });
        });
        b.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasConversion<string>();
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Permissions).HasColumnType("text[]");
            e.Property(x => x.RequiredAttributes).HasColumnType("text[]");
        });
        b.Entity<AccessRule>(e =>
        {
            e.ToTable("access_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.PrincipalType).HasConversion<string>();
            e.Property(x => x.ScopeType).HasConversion<string>();
            e.Property(x => x.Permissions).HasColumnType("text[]");
            e.Property(x => x.Attributes).AsJsonDictionary();
            e.HasIndex(x => new { x.TenantId, x.PrincipalType, x.PrincipalId });
        });
        b.Entity<UserGroup>(e => { e.ToTable("user_groups"); e.HasKey(x => new { x.UserId, x.GroupId }); e.HasIndex(x => x.GroupId); });
        b.Entity<UserRole>(e => { e.ToTable("user_roles"); e.HasKey(x => new { x.UserId, x.RoleId }); e.HasIndex(x => x.RoleId); });
        b.Entity<GroupRole>(e => { e.ToTable("group_roles"); e.HasKey(x => new { x.GroupId, x.RoleId }); e.HasIndex(x => x.RoleId); });
    }

    private const string PathPattern = "'^[a-z0-9_]{1,64}(\\.[a-z0-9_]{1,64}){0,11}$'";

    private static void ConfigureContent(ModelBuilder b)
    {
        b.Entity<Folder>(e =>
        {
            e.ToTable("folders", t => t.HasCheckConstraint("ck_folders_path", $"path ~ {PathPattern}"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).HasMaxLength(800);
            e.Property(x => x.Label).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.InheritedMetadata).AsJson();
            e.HasIndex(x => new { x.TenantId, x.Path }).IsUnique();
            // Labels are unique among siblings. NULL parents never compare equal, so roots get their own index.
            e.HasIndex(x => new { x.TenantId, x.ParentId, x.Label }).IsUnique().HasFilter("parent_id IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.Label }).IsUnique().HasFilter("parent_id IS NULL").HasDatabaseName("ux_folders_root_label");
        });
        b.Entity<Vocabulary>(e =>
        {
            e.ToTable("vocabularies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Levels).HasColumnType("text[]");
            e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        });
        b.Entity<Term>(e =>
        {
            e.ToTable("terms", t => t.HasCheckConstraint("ck_terms_path", $"path ~ {PathPattern}"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Path).HasMaxLength(800);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Labels).AsJson();
            e.Property(x => x.Attributes).AsJson();
            e.HasIndex(x => new { x.TenantId, x.VocabularyId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.VocabularyId, x.ParentId });
        });
        b.Entity<MetadataSchema>(e =>
        {
            e.ToTable("metadata_schemas", t => t.HasCheckConstraint("ck_metadata_schemas_type",
                "asset_type IN ('image','video','audio','document','manual','3d','html5','other')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.AssetType).HasMaxLength(16);
            e.Property(x => x.Fields).AsJson();
            e.HasIndex(x => new { x.TenantId, x.AssetType, x.Version }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.AssetType }).IsUnique().HasFilter("is_current").HasDatabaseName("ux_metadata_schemas_current");
        });
    }

    private static void ConfigureAssets(ModelBuilder b)
    {
        b.Entity<Asset>(e =>
        {
            e.ToTable("assets", t =>
            {
                t.HasCheckConstraint("ck_assets_type", "type IN ('image','video','audio','document','manual','3d','html5','other')");
                t.HasCheckConstraint("ck_assets_status", "status IN ('draft','in_review','approved','published','expired','archived','deleted','blocked')");
            });
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.Property(x => x.ChecksumSha256).HasColumnType("char(64)");
            e.Property(x => x.Phash).HasColumnType("char(16)");
            e.Property(x => x.Metadata).AsJson();
            e.Property(x => x.Attributes).AsJsonDictionary();
            e.Property(x => x.TaxonomyTerms).HasColumnType("text[]");
            e.Property(x => x.Tags).HasColumnType("text[]");
            // A folder that still holds assets cannot be removed, even by a bug: the database refuses.
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
            // A retention policy still assigned to assets cannot be removed either; unassign first.
            e.HasOne<RetentionPolicy>().WithMany().HasForeignKey(x => x.RetentionPolicyId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.FolderId });
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.ChecksumSha256 }).HasFilter("checksum_sha256 IS NOT NULL");
            // The scheduled-publish/expire and retention jobs poll across every tenant (spec Step 1.6b), so these are plain
            // (not tenant-prefixed) partial indexes on exactly what each poll filters by.
            e.HasIndex(x => new { x.Status, x.PublishAt }).HasFilter("publish_at IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.ExpireAt }).HasFilter("expire_at IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.RetentionPolicyId }).HasFilter("retention_policy_id IS NOT NULL");
        });
        b.Entity<Upload>(e =>
        {
            e.ToTable("uploads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.Bucket).HasMaxLength(100);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.S3UploadId).HasMaxLength(500);
            e.Property(x => x.Sha256).HasColumnType("char(64)");
            e.Property(x => x.Error).HasMaxLength(500);
            e.Property(x => x.IngestStage).HasMaxLength(16);
            e.Property(x => x.DetectedMime).HasMaxLength(100);
            e.Property(x => x.TechnicalMetadata).AsJson();
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.AssetId });
            e.HasIndex(x => new { x.TenantId, x.BatchId }).HasFilter("batch_id IS NOT NULL");
            e.HasIndex(x => x.StorageKey).IsUnique();
        });
    }

    private static void ConfigureVersions(ModelBuilder b) =>
        b.Entity<AssetVersion>(e =>
        {
            e.ToTable("asset_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Bucket).HasMaxLength(100);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.Mime).HasMaxLength(100);
            e.Property(x => x.Sha256).HasColumnType("char(64)");
            e.Property(x => x.ChangeNote).HasMaxLength(500);
            e.Property(x => x.TechnicalMetadata).AsJson();
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.VersionNo }).IsUnique();
            e.HasIndex(x => x.UploadId).IsUnique();    // one upload produces at most one version
            e.HasIndex(x => x.StorageKey).IsUnique();
        });

    private static void ConfigureRenditions(ModelBuilder b) =>
        b.Entity<Rendition>(e =>
        {
            e.ToTable("renditions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Profile).HasMaxLength(64);
            e.Property(x => x.Bucket).HasMaxLength(100);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.Mime).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne<AssetVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.VersionId, x.Profile, x.PageNo }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.AssetId });
        });

    private static void ConfigureRetention(ModelBuilder b) =>
        b.Entity<RetentionPolicy>(e =>
        {
            e.ToTable("retention_policies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

    private static void ConfigureCollections(ModelBuilder b) =>
        b.Entity<Collection>(e =>
        {
            e.ToTable("collections");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Query!).AsJson(); // Query is nullable (Static collections have none); AsJson<T>'s "class" constraint just needs the ! to compile
            // AssetIds itself has no setter (only Collection.ChangeAssets may change membership); EF reads and writes
            // the private backing field directly instead of going through the property.
            e.Property<List<Guid>>("_assetIds").HasColumnName("asset_ids").HasColumnType("uuid[]").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasIndex(x => new { x.TenantId, x.Name });
        });

    private static void ConfigureUsage(ModelBuilder b)
    {
        b.Entity<UsageEvent>(e =>
        {
            e.ToTable("usage_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(16);
            e.Property(x => x.Channel).HasMaxLength(UsageRules.MaxChannelLength);
            e.Property(x => x.Rendition).HasMaxLength(UsageRules.MaxRenditionLength);
            e.Property(x => x.ClientEventId).HasMaxLength(UsageRules.MaxClientEventIdLength);
            e.Property(x => x.Context!).AsJson(); // nullable: AsJson<T>'s "class" constraint just needs the ! to compile
            // Events go with their asset if it is ever hard-deleted (the app soft-deletes, so this is a safety net).
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.OccurredAt }).IsDescending(false, false, true);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            // A re-sent event is recognised by its reporter-supplied id; events without one are never deduplicated.
            e.HasIndex(x => new { x.TenantId, x.ClientEventId }).IsUnique().HasFilter("client_event_id IS NOT NULL");
        });
        b.Entity<AssetUsage>(e =>
        {
            e.ToTable("asset_usage");
            e.HasKey(x => x.AssetId);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.LastUsedAt });
        });
    }

    private static void ConfigureWorkflow(ModelBuilder b)
    {
        b.Entity<WorkflowDefinition>(e =>
        {
            e.ToTable("workflow_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Json).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.Name });
        });
        b.Entity<WorkflowBinding>(e =>
        {
            e.ToTable("workflow_bindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.AssetType).HasMaxLength(16);
            e.Property(x => x.Region).HasMaxLength(64);
            e.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Restrict);
            // One binding per (type, region); NULL means "any", and two NULLs must clash, so nulls are treated as equal.
            e.HasIndex(x => new { x.TenantId, x.AssetType, x.Region }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(x => x.WorkflowId);
        });
        b.Entity<WorkflowInstance>(e =>
        {
            e.ToTable("workflow_instances", t => t.HasCheckConstraint("ck_workflow_instances_status", "status IN ('running','approved','rejected','cancelled')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.DefinitionName).HasMaxLength(200);
            e.Property(x => x.DefinitionJson).HasColumnType("jsonb");   // the frozen snapshot this instance runs by
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.OutcomeReason).HasMaxLength(500);
            e.Property(x => x.OutcomeStatus).HasMaxLength(16);
            // Deleting a definition keeps the history of what ran: the snapshot above is all an instance needs.
            e.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Dam.Domain.Assets.Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.StartedAt });
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.DefinitionId);
            // An asset is in at most one workflow at a time; two submits racing cannot both start one.
            e.HasIndex(x => x.AssetId).IsUnique().HasFilter("status = 'running'").HasDatabaseName("ux_workflow_instances_running_per_asset");
        });
        b.Entity<WorkflowTask>(e =>
        {
            e.ToTable("workflow_tasks", t => t.HasCheckConstraint("ck_workflow_tasks_status", "status IN ('pending','approved','rejected','cancelled')"));
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.StepId).HasMaxLength(40);
            e.Property(x => x.StepName).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.Property(x => x.AssigneeRoles).HasColumnType("text[]");
            e.Property(x => x.AssigneeUserIds).HasColumnType("uuid[]");
            e.Property(x => x.EscalatedRoles).HasColumnType("text[]");
            e.Property(x => x.EscalatedUserIds).HasColumnType("uuid[]");
            e.HasOne<WorkflowInstance>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
            // Inbox: open tasks of a tenant, found by assignee (GIN on the arrays) and listed newest first.
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.AssigneeRoles).HasMethod("gin");
            e.HasIndex(x => x.AssigneeUserIds).HasMethod("gin");
            e.HasIndex(x => new { x.TenantId, x.DecidedBy }).HasFilter("decided_by IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.AssetId });
            e.HasIndex(x => new { x.InstanceId, x.StepId, x.Round });
            // The SLA job sweeps every tenant for open, not-yet-breached tasks past their due time.
            e.HasIndex(x => new { x.Status, x.DueAt }).HasFilter("status = 'pending' AND sla_breached_at IS NULL AND due_at IS NOT NULL").HasDatabaseName("ix_workflow_tasks_sla_sweep");
        });
    }

    private static void ConfigureNotifications(ModelBuilder b)
    {
        b.Entity<Webhook>(e =>
        {
            e.ToTable("webhooks");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.SecretCipher).HasMaxLength(500);
            e.Property(x => x.SecretLast4).HasMaxLength(8);
            e.Property(x => x.Events).HasColumnType("text[]");
            e.HasIndex(x => new { x.TenantId, x.Active });
        });
        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries", t => t.HasCheckConstraint("ck_webhook_deliveries_status", "status IN ('pending','succeeded','failed')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.ResponseExcerpt).HasMaxLength(4096);
            e.HasOne<Webhook>().WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.WebhookId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Status, x.Id });
            e.HasIndex(x => x.ReplayOf).HasFilter("replay_of IS NOT NULL");
            // One delivery per event per webhook; replays are extra deliveries of the same event and are exempt.
            e.HasIndex(x => new { x.WebhookId, x.EventId }).IsUnique().HasFilter("replay_of IS NULL").HasDatabaseName("ux_webhook_deliveries_event");
            // The retry job sweeps every tenant for pending deliveries that are due.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("status = 'pending'").HasDatabaseName("ix_webhook_deliveries_due");
        });
        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Category).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.LinkUrl).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.UserId }).HasFilter("read_at IS NULL").HasDatabaseName("ix_notifications_unread");
        });
        b.Entity<NotificationPreference>(e =>
        {
            e.ToTable("notification_preferences");
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).HasMaxLength(40);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.Category }).IsUnique();
        });
        b.Entity<NotificationChannel>(e =>
        {
            e.ToTable("notification_channels", t => t.HasCheckConstraint("ck_notification_channels_kind", "kind IN ('slack','teams')"));
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.UrlCipher).HasMaxLength(4000);
            e.Property(x => x.UrlHint).HasMaxLength(300);
            e.Property(x => x.Events).HasColumnType("text[]");
            e.HasIndex(x => new { x.TenantId, x.Active });
        });
        b.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("notification_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Property(x => x.Recipient).HasMaxLength(64);
            e.HasIndex(x => new { x.TenantId, x.EventId, x.Channel, x.Recipient }).IsUnique();
        });
        b.Entity<SiemCursor>(e =>
        {
            e.ToTable("siem_cursors");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId).IsUnique();
        });
    }

    private static void ConfigureComments(ModelBuilder b)
    {
        b.Entity<Comment>(e =>
        {
            e.ToTable("comments", t => t.HasCheckConstraint("ck_comments_anchor_type", "anchor_type IS NULL OR anchor_type IN ('region','point','timecode')"));
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property(x => x.Body).HasColumnType("text");
            e.Property(x => x.AnchorJson).HasColumnName("anchor").HasColumnType("jsonb");
            e.Property(x => x.AnchorType).HasMaxLength(16);
            // Comments go with their asset if it is ever hard-deleted (the app soft-deletes, so this is a safety net); a version can never be removed.
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AssetVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Comment>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.Resolved });
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.AnchorType }).HasFilter("anchor_type IS NOT NULL");
            e.HasIndex(x => x.ParentId).HasFilter("parent_id IS NOT NULL");
        });
        b.Entity<CommentMention>(e =>
        {
            e.ToTable("comment_mentions");
            e.HasKey(x => new { x.CommentId, x.UserId });
            e.HasOne<Comment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.UserId });
        });
    }

    private LambdaExpression TenantFilter(Type entityType)
    {
        var e = Expression.Parameter(entityType, "e");
        var body = Expression.Equal(
            Expression.Property(e, nameof(ITenantScoped.TenantId)),
            Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));
        return Expression.Lambda(body, e);
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<ITenantScoped>().Where(x => x.State == EntityState.Added))
            if (entry.Property(nameof(ITenantScoped.TenantId)).CurrentValue is Guid g && g == Guid.Empty)
                entry.Property(nameof(ITenantScoped.TenantId)).CurrentValue = tenant.TenantId;
        return base.SaveChangesAsync(ct);
    }
}

internal static class JsonDictionaryExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string ToJson<T>(T v) => JsonSerializer.Serialize(v, Options);

    /// <summary>Stores a Dictionary&lt;string, string[]&gt; as jsonb, with a comparer so EF detects in-place edits.</summary>
    public static PropertyBuilder<Dictionary<string, string[]>> AsJsonDictionary(this PropertyBuilder<Dictionary<string, string[]>> p) => p.AsJson();

    /// <summary>Stores any JSON-serialisable value as jsonb and compares by serialised form, so in-place edits are saved.</summary>
    public static PropertyBuilder<T> AsJson<T>(this PropertyBuilder<T> p) where T : class, new() =>
        p.HasColumnType("jsonb")
         .HasConversion(
             v => ToJson(v),
             s => JsonSerializer.Deserialize<T>(s, Options) ?? new T(),
             new ValueComparer<T>(
                 (a, b) => ToJson(a!) == ToJson(b!),
                 v => ToJson(v).GetHashCode(StringComparison.Ordinal),
                 v => JsonSerializer.Deserialize<T>(ToJson(v), Options)!));
}
