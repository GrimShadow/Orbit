using Dam.Application.Abstractions;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Dam.IntegrationTests;

public sealed class TestTenant(Guid id) : ITenantContext
{
    public Guid TenantId { get; set; } = id;
}

/// <summary>Real Postgres 16, migrations applied as owner, app connects as a non-superuser member of dam_app.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string OwnerConnection => _pg.GetConnectionString();
    public string AppConnection { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await using (var db = NewContext(OwnerConnection, new TestTenant(Guid.Empty)))
            await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE ROLE app_login LOGIN PASSWORD 'app_pw' NOSUPERUSER NOBYPASSRLS IN ROLE dam_app";
        await cmd.ExecuteNonQueryAsync();
        AppConnection = new NpgsqlConnectionStringBuilder(OwnerConnection)
            { Username = "app_login", Password = "app_pw", Pooling = false }.ConnectionString;
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    public DamDbContext AppContext(TestTenant tenant) => NewContext(AppConnection, tenant);

    public NpgsqlConnection RawAppConnection(Guid? tenant)
    {
        var c = new NpgsqlConnection(AppConnection);
        c.Open();
        using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", c);
        cmd.Parameters.AddWithValue("t", tenant?.ToString() ?? "");
        cmd.ExecuteNonQuery();
        return c;
    }

    private static DamDbContext NewContext(string cs, TestTenant tenant)
    {
        var o = new DbContextOptionsBuilder<DamDbContext>()
            .UseNpgsql(cs).UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantConnectionInterceptor(tenant)).Options;
        return new DamDbContext(o, tenant);
    }
}

[CollectionDefinition("pg")]
public sealed class PgCollection : ICollectionFixture<PostgresFixture>;
