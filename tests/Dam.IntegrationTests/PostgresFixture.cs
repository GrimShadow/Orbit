using System.Text;
using Amazon.S3;
using Dam.Application.Abstractions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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
    private const string S3Key = "testaccess";
    private const string S3Secret = "testsecret-testsecret";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();

    // The same S3-compatible store the dev stack uses (SeaweedFS), so upload tests exercise real multipart uploads. One instance is
    // shared by the whole "pg" collection (all integration test classes). SeaweedFS caps how many volumes it will ever create
    // (default a handful); once that cap is hit it refuses new writes with a generic "internal error" even though the volume size
    // limit itself has headroom. -volume.max raises that cap (matches the dev compose stack's own fix for the same symptom); the
    // size limit stays modest since it is the volume *count*, not size, that was the real constraint.
    private readonly IContainer _s3 = new ContainerBuilder("chrislusf/seaweedfs:latest")
        .WithCommand("server", "-dir=/data", "-s3", "-s3.port=9000", "-s3.config=/etc/s3.json", "-master.volumeSizeLimitMB=256", "-volume.max=100")
        .WithPortBinding(9000, true)
        .WithResourceMapping(Encoding.UTF8.GetBytes(
            "{\"identities\":[{\"name\":\"test\",\"credentials\":[{\"accessKey\":\"" + S3Key + "\",\"secretKey\":\"" + S3Secret +
            "\"}],\"actions\":[\"Admin\",\"Read\",\"Write\",\"List\",\"Tagging\"]}]}"),
            "/etc/s3.json")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(9000))
        .Build();

    public string S3Endpoint => $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(9000)}";
    public string S3AccessKey => S3Key;
    public string S3SecretKey => S3Secret;

    /// <summary>A client for the test store, for assertions about what is (or is not) in a bucket.</summary>
    public AmazonS3Client S3Client() => new(new Amazon.Runtime.BasicAWSCredentials(S3Key, S3Secret),
        new AmazonS3Config
        {
            ServiceURL = S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public string OwnerConnection => _pg.GetConnectionString();
    public string AppConnection { get; private set; } = "";
    public string SystemConnection { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _s3.StartAsync());
        await CreateBucketsAsync();
        await using (var db = NewContext(OwnerConnection, new TestTenant(Guid.Empty)))
            await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE ROLE app_login LOGIN PASSWORD 'app_pw' NOSUPERUSER NOBYPASSRLS IN ROLE dam_app;
            CREATE ROLE sys_login LOGIN PASSWORD 'sys_pw' NOSUPERUSER NOBYPASSRLS IN ROLE dam_system;
            """;
        await cmd.ExecuteNonQueryAsync();
        AppConnection = new NpgsqlConnectionStringBuilder(OwnerConnection)
            { Username = "app_login", Password = "app_pw", Pooling = false }.ConnectionString;
        SystemConnection = new NpgsqlConnectionStringBuilder(OwnerConnection)
            { Username = "sys_login", Password = "sys_pw" }.ConnectionString;
    }

    private async Task CreateBucketsAsync()
    {
        using var client = S3Client();
        foreach (var bucket in new[] { "dam-quarantine", "dam-originals", "dam-renditions" })
            for (var attempt = 0; ; attempt++)
            {
                try { await client.PutBucketAsync(bucket); break; }
                catch (Exception) when (attempt < 30) { await Task.Delay(500); } // the store needs a moment to allocate volumes
            }
    }

    public async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        await _s3.DisposeAsync();
    }

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
