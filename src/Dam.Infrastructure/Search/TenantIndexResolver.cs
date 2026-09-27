using Dam.Application.Abstractions;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Search;

/// <summary>Resolves the ambient tenant's write alias, shared by every OpenSearch-facing service so a tenant with no
/// slug (should never happen - <c>Tenant.Create</c> requires one) fails the same clear way everywhere.</summary>
internal static class TenantIndexResolver
{
    public static async Task<string> AliasAsync(DamDbContext db, ITenantContext tenant, CancellationToken ct) =>
        IndexNaming.Alias(await SlugAsync(db, tenant, ct));

    public static async Task<string> SlugAsync(DamDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var slug = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Slug).FirstOrDefaultAsync(ct);
        if (slug is not { Length: > 0 }) throw new InvalidOperationException($"Tenant {tenant.TenantId} was not found (or has no slug).");
        return slug;
    }
}
