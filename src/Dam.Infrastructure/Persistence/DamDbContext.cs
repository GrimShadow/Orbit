using Dam.Application.Abstractions;
using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Domain.Assets;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
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
        });

        ConfigureIdentity(b);
        ConfigureContent(b);
        ConfigureAssets(b);
        ConfigureVersions(b);
        ConfigureRenditions(b);
        ConfigureRetention(b);
        ConfigureCollections(b);
        ConfigureUsage(b);

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
