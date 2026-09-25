using System.Data.Common;
using Dam.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dam.Infrastructure.Persistence;

/// <summary>Sets app.tenant_id on every opened connection so Postgres RLS policies can see it.</summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('app.tenant_id', @t, false)";

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = Build(connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = Build(connection);
        cmd.ExecuteNonQuery();
    }

    private DbCommand Build(DbConnection c)
    {
        var cmd = c.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- Sql is a compile-time constant; the tenant id is bound as @t.
        cmd.CommandText = Sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "t";
        // Empty string = "no tenant": RLS policies then match nothing.
        p.Value = tenant.TenantId == Guid.Empty ? "" : tenant.TenantId.ToString();
        cmd.Parameters.Add(p);
        return cmd;
    }
}
