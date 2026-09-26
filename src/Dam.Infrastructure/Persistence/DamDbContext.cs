using Dam.Application.Abstractions;
using System.Text.Json;
using Dam.Domain.Authorization;
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
