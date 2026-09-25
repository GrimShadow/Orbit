using Dam.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dam.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef`. Set DAM_MIGRATION_CONNECTION to the owner/superuser connection.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DamDbContext>
{
    private sealed class NoTenant : ITenantContext { public Guid TenantId => Guid.Empty; }

    public DamDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("DAM_MIGRATION_CONNECTION")
                 ?? "Host=localhost;Port=15432;Database=dam;Username=dam;Password=dam";
        var o = new DbContextOptionsBuilder<DamDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options;
        return new DamDbContext(o, new NoTenant());
    }
}
