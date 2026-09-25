using Dam.Application.Abstractions;
using Dam.Domain.Common;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Dam.Infrastructure.Persistence;

public sealed class DamDbContext(DbContextOptions<DamDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

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

        // Defence in depth: EF filter here, Postgres RLS underneath.
        foreach (var t in b.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)))
            b.Entity(t.ClrType).HasQueryFilter(TenantFilter(t.ClrType));
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
