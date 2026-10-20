using System.Globalization;
using Dam.Application.Abstractions;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>
/// The usage rollup migration (ADR-0037) on a database that ALREADY holds usage events: the old table is copied into the month-partitioned one, nothing is lost, the client event
/// ids become keys, every event day is marked for rollup, first-publication times are back-filled from the audit trail, and going back down restores the old table with its rows.
/// Runs in a scratch database of the fixture's server, so the shared one is untouched.
/// </summary>
[Collection("pg")]
public sealed class UsageMigrationTests(PostgresFixture pg)
{
    private const string Before = "20261020143410_TranslationAndGlossary";
    private sealed class NoTenant : ITenantContext { public Guid TenantId => Guid.Empty; }

    private static NpgsqlConnection Open(string cs)
    {
        var c = new NpgsqlConnection(cs);
        c.Open();
        return c;
    }

    private static void Run(string cs, string sql)
    {
        using var c = Open(cs);
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string cs, string sql)
    {
        using var c = Open(cs);
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Text(string cs, string sql)
    {
        using var c = Open(cs);
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private static DamDbContext Context(string cs) =>
        new(new DbContextOptionsBuilder<DamDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options, new NoTenant());

    [Fact]
    public async Task Existing_usage_events_survive_the_conversion_to_a_partitioned_table_and_back()
    {
        var name = "mig_" + Guid.NewGuid().ToString("N")[..10];
        Run(pg.OwnerConnection, $"CREATE DATABASE {name}");
        var cs = new NpgsqlConnectionStringBuilder(pg.OwnerConnection) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            await using (var db = Context(cs)) await db.Database.MigrateAsync(Before);
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pg_class WHERE relname = 'usage_event_keys'"));   // the old schema: no keys table...
            Assert.Equal("r", Text(cs, "SELECT relkind FROM pg_class WHERE relname = 'usage_events'"));        // ... and an ordinary events table

            var (ta, tb) = (Guid.NewGuid(), Guid.NewGuid());
            var (assetA, assetB, assetC, assetD) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Seed(cs, ta, tb, assetA, assetB, assetC, assetD);
            var events = Scalar(cs, "SELECT count(*) FROM usage_events");
            var withKey = Scalar(cs, "SELECT count(*) FROM usage_events WHERE client_event_id IS NOT NULL");
            var days = Scalar(cs, "SELECT count(*) FROM (SELECT DISTINCT tenant_id, (occurred_at AT TIME ZONE 'UTC')::date FROM usage_events) d");
            var fingerprint = Text(cs, "SELECT md5(string_agg(id::text || tenant_id::text || asset_id::text || event_type || coalesce(channel, '~') || occurred_at::text || coalesce(client_event_id, '~') || coalesce(context::text, '~'), ',' ORDER BY id)) FROM usage_events");
            Assert.Equal(120, events);

            await using (var db = Context(cs)) await db.Database.MigrateAsync();

            // every row came across, unchanged, into the partition of its own month
            Assert.Equal("p", Text(cs, "SELECT relkind FROM pg_class WHERE relname = 'usage_events'"));
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pg_class WHERE relname = 'usage_events_old'"));
            Assert.Equal(events, Scalar(cs, "SELECT count(*) FROM usage_events"));
            Assert.Equal(fingerprint, Text(cs, "SELECT md5(string_agg(id::text || tenant_id::text || asset_id::text || event_type || coalesce(channel, '~') || occurred_at::text || coalesce(client_event_id, '~') || coalesce(context::text, '~'), ',' ORDER BY id)) FROM usage_events"));
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM usage_events WHERE tableoid::regclass::text <> 'usage_events_' || to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY_MM')"));
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM usage_events_default"));
            Assert.True(Scalar(cs, "SELECT count(*) FROM usage_events_partition_months()") >= 17);              // 14 months of history, this one and three ahead
            Assert.Equal(1, Scalar(cs, $"SELECT count(*) FROM pg_class WHERE relname = 'usage_events_{DateTime.UtcNow.AddMonths(3):yyyy_MM}'"));

            // keys, dirty days, key and indexes
            Assert.Equal(withKey, Scalar(cs, "SELECT count(*) FROM usage_event_keys"));
            Assert.Equal(days, Scalar(cs, "SELECT count(*) FROM usage_rollup_dirty"));
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM usage_daily"));                                  // history is rolled up by the job, not by the migration
            Assert.Equal("id,occurred_at", Text(cs, "SELECT string_agg(a.attname, ',' ORDER BY k.ord) FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ord) JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum WHERE i.indrelid = 'usage_events'::regclass AND i.indisprimary"));
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pg_indexes WHERE tablename = 'usage_events' AND indexdef ILIKE '%client_event_id%'"));   // no unique index to span partitions
            Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'usage_events'::regclass AND contype = 'f'"));
            Run(cs, $"INSERT INTO usage_event_keys (tenant_id, client_event_id, created_at) VALUES ('{ta}', 'key-2', now()) ON CONFLICT DO NOTHING");
            Assert.Equal(withKey, Scalar(cs, "SELECT count(*) FROM usage_event_keys"));                       // 'key-2' was already recorded: a re-send stays a duplicate after the upgrade

            // RLS on the parent and every partition
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pg_class WHERE relname ~ '^usage_events(_[0-9_]+|_default)?$' AND relkind IN ('r', 'p') AND NOT (relrowsecurity AND relforcerowsecurity)"));
            foreach (var table in new[] { "usage_event_keys", "usage_daily", "usage_rollup_dirty", "report_schedules" })
                Assert.Equal(1, Scalar(cs, $"SELECT count(*) FROM pg_class WHERE relname = '{table}' AND relrowsecurity AND relforcerowsecurity"));

            // first publication, back-filled from the audit trail: the earliest 'published', whatever happened later; never published stays null
            Assert.Equal("2026-01-05T10:00:00", Text(cs, $"SELECT to_char(first_published_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM assets WHERE id = '{assetA}'"));
            Assert.Equal("2026-03-02T08:30:00", Text(cs, $"SELECT to_char(first_published_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM assets WHERE id = '{assetC}'"));
            Assert.Equal(0, Scalar(cs, $"SELECT count(*) FROM assets WHERE id IN ('{assetB}', '{assetD}') AND first_published_at IS NOT NULL"));

            // and back down: the old table, with every row, and the unique client event id index
            await using (var db = Context(cs)) await db.Database.MigrateAsync(Before);
            Assert.Equal("r", Text(cs, "SELECT relkind FROM pg_class WHERE relname = 'usage_events'"));
            Assert.Equal(events, Scalar(cs, "SELECT count(*) FROM usage_events"));
            Assert.Equal(fingerprint, Text(cs, "SELECT md5(string_agg(id::text || tenant_id::text || asset_id::text || event_type || coalesce(channel, '~') || occurred_at::text || coalesce(client_event_id, '~') || coalesce(context::text, '~'), ',' ORDER BY id)) FROM usage_events"));
            Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM pg_indexes WHERE tablename = 'usage_events' AND indexdef ILIKE '%UNIQUE%client_event_id%'"));
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pg_class WHERE relname IN ('usage_event_keys', 'usage_daily', 'usage_rollup_dirty', 'report_schedules')"));

            // and up again from there
            await using (var db = Context(cs)) await db.Database.MigrateAsync();
            Assert.Equal("p", Text(cs, "SELECT relkind FROM pg_class WHERE relname = 'usage_events'"));
            Assert.Equal(events, Scalar(cs, "SELECT count(*) FROM usage_events"));
            Assert.Equal(withKey, Scalar(cs, "SELECT count(*) FROM usage_event_keys"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Run(pg.OwnerConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task A_fresh_database_gets_the_partitioned_table_with_no_rows_to_copy()
    {
        var name = "mig_" + Guid.NewGuid().ToString("N")[..10];
        Run(pg.OwnerConnection, $"CREATE DATABASE {name}");
        var cs = new NpgsqlConnectionStringBuilder(pg.OwnerConnection) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            await using (var db = Context(cs)) await db.Database.MigrateAsync();
            Assert.Equal("p", Text(cs, "SELECT relkind FROM pg_class WHERE relname = 'usage_events'"));
            Assert.Equal(4, Scalar(cs, "SELECT count(*) FROM usage_events_partition_months()"));                // this month and the next three
            Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM usage_events"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Run(pg.OwnerConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    private static void Seed(string cs, Guid ta, Guid tb, Guid assetA, Guid assetB, Guid assetC, Guid assetD)
    {
        foreach (var t in new[] { ta, tb })
            Run(cs, $"INSERT INTO tenants (id, name, slug, status, settings_json, storage_config_json, created_at) VALUES ('{t}', 'Acme {t:N}', 'acme-{t:N}', 'Active', '{{}}', '{{}}', now())");
        void Asset(Guid id, Guid tenant, string status, string title) => Run(cs,
            "INSERT INTO assets (id, tenant_id, type, status, title, metadata, metadata_schema_version, taxonomy_terms, tags, attributes, metadata_attributes, rights_channels, rights_regions, legal_hold, status_reason, created_at, updated_at) "
            + $"VALUES ('{id}', '{tenant}', 'image', '{status}', '{title}', '{{}}', 0, '{{}}', '{{}}', '{{}}', '{{}}', '{{}}', '{{}}', false, '', now() - interval '500 days', now())");
        Asset(assetA, ta, "published", "Hero");
        Asset(assetB, ta, "draft", "Draft");
        Asset(assetC, tb, "published", "Poster");
        Asset(assetD, tb, "published", "Seeded straight into published");   // no audit trail: nothing to back-fill from

        void Audit(Guid tenant, Guid asset, string from, string to, string at) => Run(cs,
            "INSERT INTO audit_log (id, tenant_id, ts, actor, action, entity_type, entity_id, after) "
            + $"VALUES (gen_random_uuid(), '{tenant}', '{at}', 'someone', 'asset.status.changed', 'asset', '{asset}', '{{\"AssetId\":\"{asset}\",\"From\":\"{from}\",\"To\":\"{to}\"}}'::jsonb)");
        Audit(ta, assetA, "draft", "in_review", "2026-01-04T09:00:00Z");
        Audit(ta, assetA, "approved", "published", "2026-01-05T10:00:00Z");
        Audit(ta, assetA, "published", "expired", "2026-02-05T10:00:00Z");
        Audit(ta, assetA, "expired", "published", "2026-04-05T10:00:00Z");     // published again later: the first time counts
        Audit(tb, assetC, "approved", "published", "2026-03-02T08:30:00Z");

        foreach (var (tenant, asset, offset) in new[] { (ta, assetA, 0), (tb, assetC, 3) })
            Run(cs, "INSERT INTO usage_events (id, tenant_id, asset_id, event_type, channel, occurred_at, client_event_id, context, created_at) "
                + $"SELECT gen_random_uuid(), '{tenant}', '{asset}', (ARRAY['view','download','play'])[1 + n % 3], CASE WHEN n % 4 = 0 THEN NULL ELSE 'ch' || (n % 3) END, "
                + $"now() - (n * interval '7 days') - interval '{offset} hours', CASE WHEN n % 2 = 0 THEN 'key-' || n END, CASE WHEN n % 5 = 0 THEN '{{\"k\":1}}'::jsonb END, now() FROM generate_series(0, 59) n");
        // the second tenant reuses the first's client event ids: they are unique per tenant, not globally
    }
}
