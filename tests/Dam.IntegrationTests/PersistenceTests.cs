using Dam.Application.Abstractions;
using Dam.Domain.Common;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class PersistenceTests(PostgresFixture fx)
{
    private static readonly Guid A = Uuid7.NewGuid();
    private static readonly Guid B = Uuid7.NewGuid();

    private sealed record Ev : DomainEvent, IEntityEvent
    {
        public override string Name => "test.happened";
        public string EntityType => "thing";
        public Guid EntityId { get; init; } = Uuid7.NewGuid();
    }
    private sealed class Anon : ICurrentUser
    {
        public bool IsAuthenticated => false;
        public Guid? UserId => null;
        public IReadOnlyCollection<string> Roles => [];
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private static long Scalar(NpgsqlConnection c, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private async Task SeedOutbox(Guid tenant, int n)
    {
        await using var db = fx.AppContext(new TestTenant(tenant));
        for (var i = 0; i < n; i++)
            db.Outbox.Add(new OutboxMessage { EventType = "seed", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); // TenantId is stamped from the tenant context
    }

    [Fact]
    public async Task Tenant_cannot_read_other_tenants_rows_even_with_raw_sql()
    {
        await SeedOutbox(A, 2);
        await SeedOutbox(B, 3);

        await using (var db = fx.AppContext(new TestTenant(A)))
            Assert.Equal(2, await db.Outbox.CountAsync(x => x.EventType == "seed"));

        // Bypass EF entirely: only Postgres RLS is left protecting the data.
        using var a = fx.RawAppConnection(A);
        Assert.Equal(2, Scalar(a, "SELECT count(*) FROM outbox WHERE event_type = 'seed'"));
        Assert.Equal(0, Scalar(a, $"SELECT count(*) FROM outbox WHERE tenant_id = '{B}'"));

        using var none = fx.RawAppConnection(null);
        Assert.Equal(0, Scalar(none, "SELECT count(*) FROM outbox"));
    }

    [Fact]
    public async Task Tenant_cannot_write_rows_for_another_tenant()
    {
        using var a = fx.RawAppConnection(A);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO outbox (id, tenant_id, event_type, payload, created_at, attempts) VALUES ('{Uuid7.NewGuid()}', '{B}', 'x', '{{}}', now(), 0)", a);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // row-level security violation
    }

    [Fact]
    public void App_role_is_not_a_superuser_and_cannot_bypass_rls()
    {
        using var a = fx.RawAppConnection(A);
        Assert.Equal(0, Scalar(a, "SELECT count(*) FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)"));
    }

    [Fact]
    public async Task Every_tenant_scoped_table_has_forced_rls()
    {
        await using var c = new NpgsqlConnection(fx.OwnerConnection);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT c.relname FROM pg_class c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
            WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """, c);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.False(await r.ReadAsync(), "Table without forced RLS: " + (r.HasRows ? r.GetString(0) : ""));
    }

    [Fact]
    public async Task Audit_chain_verifies_and_detects_tampering()
    {
        var t = Uuid7.NewGuid();
        await using (var db = fx.AppContext(new TestTenant(t)))
        {
            var box = new EfOutbox(db, new Anon(), new Clock());
            await box.EnqueueAsync([new Ev(), new Ev(), new Ev()], default);
            await db.SaveChangesAsync();
        }

        using var app = fx.RawAppConnection(t);
        Assert.Equal(3, Scalar(app, "SELECT count(*) FROM audit_log"));
        using (var v = new NpgsqlCommand($"SELECT audit_verify('{t}')", app))
            Assert.Equal(DBNull.Value, v.ExecuteScalar());

        // Append-only for the app role, and even the owner is blocked by the trigger.
        using (var u = new NpgsqlCommand("UPDATE audit_log SET actor = 'evil'", app))
            Assert.Throws<PostgresException>(() => u.ExecuteNonQuery());
        await using var owner = new NpgsqlConnection(fx.OwnerConnection);
        await owner.OpenAsync();
        await using (var d = new NpgsqlCommand("DELETE FROM audit_log", owner))
            await Assert.ThrowsAsync<PostgresException>(() => d.ExecuteNonQueryAsync());

        // A privileged attacker who disables the trigger is still caught by verification.
        foreach (var sql in new[] {
            "ALTER TABLE audit_log DISABLE TRIGGER audit_log_no_update",
            $"UPDATE audit_log SET actor = 'evil' WHERE tenant_id = '{t}' AND chain_no = 2",
            "ALTER TABLE audit_log ENABLE TRIGGER audit_log_no_update" })
        {
            await using var x = new NpgsqlCommand(sql, owner);
            await x.ExecuteNonQueryAsync();
        }
        await using var check = new NpgsqlCommand($"SELECT audit_verify('{t}')", owner);
        // The owner is subject to FORCE RLS too, so bind the tenant for the check.
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{t}', false)", owner))
            await set.ExecuteNonQueryAsync();
        Assert.Equal(2L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Unit_of_work_writes_audit_and_outbox_atomically()
    {
        var t = Uuid7.NewGuid();
        await using var db = fx.AppContext(new TestTenant(t));
        IUnitOfWork uow = new EfUnitOfWork(db);
        await uow.BeginAsync(default);
        await new EfOutbox(db, new Anon(), new Clock()).EnqueueAsync([new Ev()], default);
        await uow.CommitAsync(default);
        Assert.Equal(1, await db.Outbox.CountAsync(x => x.EventType == "test.happened"));
        Assert.Equal(1, await db.AuditLog.CountAsync(x => x.Action == "test.happened" && x.EntityType == "thing"));

        var t2 = Uuid7.NewGuid();
        await using var db2 = fx.AppContext(new TestTenant(t2));
        uow = new EfUnitOfWork(db2);
        await uow.BeginAsync(default);
        await new EfOutbox(db2, new Anon(), new Clock()).EnqueueAsync([new Ev()], default);
        await uow.RollbackAsync(default);
        Assert.Equal(0, await db2.Outbox.CountAsync());
    }
}
