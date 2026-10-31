using Dam.Application.Abstractions;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Integrations;

public sealed class EfConnectionStore(DamDbContext db, ITenantContext tenant) : IConnectionStore
{
    public async Task<bool> TryClaimTestAsync(Guid connectionId, DateTimeOffset now, TimeSpan interval, CancellationToken ct)
    {
        var at = now.ToUniversalTime();
        var earliest = (now - interval).ToUniversalTime();
        // One conditional UPDATE: of two requests (or two API instances) at the same moment exactly one changes the row.
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE integration_connections SET test_claimed_at = {at}
            WHERE id = {connectionId} AND tenant_id = {tenant.TenantId} AND (test_claimed_at IS NULL OR test_claimed_at <= {earliest})
            """, ct);
        return claimed == 1;
    }
}
