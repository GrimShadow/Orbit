using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Shared.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dam.Infrastructure.Persistence;

/// <summary>
/// Reads the tenant's audit log through its own connection, in one read-only repeatable-read transaction, with the tenant set for row-level
/// security exactly as the ordinary connections do. Rows are streamed one at a time, so exporting a huge range never buffers it. The hash
/// chain is verified by the database's own <c>audit_row_hash</c> function, the one the append trigger uses, so there is a single definition of
/// what a row's hash covers.
/// </summary>
public sealed class EfAuditReader(DamDbContext db, ITenantContext tenant) : IAuditReader
{
    public async Task<IAuditSnapshot> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        try
        {
            await connection.OpenAsync(ct);
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
            {
                set.Parameters.AddWithValue("t", tenant.TenantId == Guid.Empty ? "" : tenant.TenantId.ToString());
                await set.ExecuteNonQueryAsync(ct);
            }
            var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx)) await ro.ExecuteNonQueryAsync(ct);
            return new Snapshot(connection, tx, tenant.TenantId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Snapshot(NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId) : IAuditSnapshot
    {
        private const string Select = """
            SELECT a.id, a.chain_no, a.ts, a.actor, u.id AS actor_user_id,
                   coalesce(nullif(u.display_name, ''), nullif(u.email, ''), a.actor) AS actor_name,
                   a.action, a.entity_type, a.entity_id, a.before::text, a.after::text, a.ip, a.hash, a.prev_hash
            FROM audit_log a LEFT JOIN users u ON u.tenant_id = a.tenant_id AND u.external_subject = a.actor
            """;

        private const string Join = "FROM audit_log a LEFT JOIN users u ON u.tenant_id = a.tenant_id AND u.external_subject = a.actor";

        private NpgsqlCommand Command(string sql, out List<string> where, AuditFilter? filter = null)
        {
            var cmd = new NpgsqlCommand(sql, connection, tx);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            where = ["a.tenant_id = @tenant"];
            if (filter is null) return cmd;
            if (filter.From is { } from) { where.Add("a.ts >= @from"); cmd.Parameters.AddWithValue("from", from.ToUniversalTime()); }
            if (filter.To is { } to) { where.Add("a.ts <= @to"); cmd.Parameters.AddWithValue("to", to.ToUniversalTime()); }
            if (filter.Actor is { } actor)
            {
                // Callers may name the actor by the identity subject that is recorded, or by the Orbit user id.
                where.Add("(a.actor = @actor OR a.actor IN (SELECT x.external_subject FROM users x WHERE x.tenant_id = @tenant AND x.id::text = @actor))");
                cmd.Parameters.AddWithValue("actor", actor);
            }
            if (filter.ActionPrefix is { } action) { where.Add("a.action LIKE @action ESCAPE '\\'"); cmd.Parameters.AddWithValue("action", Like(action) + "%"); }
            if (filter.EntityType is { } et) { where.Add("a.entity_type = @et"); cmd.Parameters.AddWithValue("et", et); }
            if (filter.EntityId is { } eid) { where.Add("a.entity_id = @eid"); cmd.Parameters.AddWithValue("eid", eid); }
            if (filter.Text is { } text)
            {
                where.Add("(a.action ILIKE @q OR a.entity_type ILIKE @q OR a.entity_id::text ILIKE @q OR u.display_name ILIKE @q OR u.email ILIKE @q OR a.actor ILIKE @q)");
                cmd.Parameters.AddWithValue("q", "%" + Like(text) + "%");
            }
            return cmd;
        }

        private static string Like(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

        private static AuditEntryDto Read(NpgsqlDataReader r) => new(
            r.GetGuid(0), r.GetInt64(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetString(5), r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8), Json(r, 9), Json(r, 10), r.IsDBNull(11) ? null : r.GetString(11),
            r.GetString(12), r.GetString(13));

        private static JsonElement? Json(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : JsonDocument.Parse(r.GetString(i)).RootElement.Clone();

        public async Task<AuditPage> ListAsync(AuditFilter filter, long? beforeChainNo, int limit, CancellationToken ct)
        {
            await using var cmd = Command("", out var where, filter);
            if (beforeChainNo is { } before) { where.Add("a.chain_no < @before"); cmd.Parameters.AddWithValue("before", before); }
            cmd.CommandText = $"{Select} WHERE {string.Join(" AND ", where)} ORDER BY a.chain_no DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("limit", limit + 1);
            var items = new List<AuditEntryDto>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) items.Add(Read(r));
            var more = items.Count > limit;
            if (more) items.RemoveAt(limit);
            return new AuditPage(items, more ? items[^1].ChainNo : null);
        }

        public async IAsyncEnumerable<AuditEntryDto> StreamAsync(AuditFilter filter, [EnumeratorCancellation] CancellationToken ct)
        {
            await using var cmd = Command("", out var where, filter);
            cmd.CommandText = $"{Select} WHERE {string.Join(" AND ", where)} ORDER BY a.chain_no";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) yield return Read(r);
        }

        public async Task<IReadOnlyList<AuditEntryDto>> ReadAfterAsync(long afterChainNo, int limit, CancellationToken ct)
        {
            await using var cmd = Command("", out var where);
            where.Add("a.chain_no > @after");
            cmd.Parameters.AddWithValue("after", afterChainNo);
            cmd.CommandText = $"{Select} WHERE {string.Join(" AND ", where)} ORDER BY a.chain_no LIMIT @limit";
            cmd.Parameters.AddWithValue("limit", limit);
            var items = new List<AuditEntryDto>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(Read(r));
            return items;
        }

        public async Task<long> MaxChainNoAsync(CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SELECT coalesce(max(chain_no), 0) FROM audit_log WHERE tenant_id = @tenant", connection, tx);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> CountAsync(AuditFilter filter, CancellationToken ct)
        {
            await using var cmd = Command("", out var where, filter);
            cmd.CommandText = $"SELECT count(*) {Join} WHERE {string.Join(" AND ", where)}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<IReadOnlyList<string>> ActionsAsync(CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SELECT DISTINCT action FROM audit_log WHERE tenant_id = @tenant ORDER BY action LIMIT 500", connection, tx);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            var names = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
            return names;
        }

        public async Task<AuditVerificationDto> VerifyAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        {
            // The rows whose time is in the range, widened to every row between the first and last of them: the chain is by position, and
            // a clock that stepped back a little must not make a healthy chain look as if it has holes.
            long min, max;
            await using (var cmd = Command("", out var where, new AuditFilter(from, to, null, null, null, null, null)))
            {
                cmd.CommandText = $"SELECT min(a.chain_no), max(a.chain_no) FROM audit_log a WHERE {string.Join(" AND ", where)}";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                if (r.IsDBNull(0)) return new AuditVerificationDto(true, 0, null, null, null, null, null, null, null, null, null, from, to);
                min = r.GetInt64(0);
                max = r.GetInt64(1);
            }

            string? expectedPrev = "";
            if (min > 1)
            {
                await using var prev = new NpgsqlCommand("SELECT hash FROM audit_log WHERE tenant_id = @tenant AND chain_no = @n", connection, tx);
                prev.Parameters.AddWithValue("tenant", tenantId);
                prev.Parameters.AddWithValue("n", min - 1);
                expectedPrev = await prev.ExecuteScalarAsync(ct) as string;   // null: the row before the range is gone
            }

            long checkedRows = 0, expectedNo = min;
            Guid? firstId = null, lastId = null, brokenId = null;
            long? firstNo = null, lastNo = null, brokenNo = null;
            string? firstHash = null, lastHash = null, reason = null;
            await using (var cmd = new NpgsqlCommand("""
                SELECT a.id, a.chain_no, a.prev_hash, a.hash, audit_row_hash(a.prev_hash, a)
                FROM audit_log a WHERE a.tenant_id = @tenant AND a.chain_no BETWEEN @min AND @max ORDER BY a.chain_no
                """, connection, tx))
            {
                cmd.Parameters.AddWithValue("tenant", tenantId);
                cmd.Parameters.AddWithValue("min", min);
                cmd.Parameters.AddWithValue("max", max);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetGuid(0);
                    var no = r.GetInt64(1);
                    var prevHash = r.GetString(2);
                    var hash = r.GetString(3);
                    checkedRows++;
                    firstId ??= id; firstNo ??= no; firstHash ??= hash;
                    lastId = id; lastNo = no; lastHash = hash;

                    string? problem = null;
                    if (no != expectedNo) problem = "gap";
                    else if (expectedPrev is null || prevHash != expectedPrev) problem = "prev_hash_mismatch";
                    else if (hash != r.GetString(4)) problem = "hash_mismatch";
                    if (problem is not null) { brokenId = id; brokenNo = no; reason = problem; break; }
                    expectedPrev = hash;
                    expectedNo = no + 1;
                }
            }
            return new AuditVerificationDto(brokenId is null, checkedRows, firstId, lastId, firstNo, lastNo, firstHash, lastHash, brokenId, brokenNo, reason, from, to);
        }

        public async ValueTask DisposeAsync()
        {
            try { await tx.RollbackAsync(); } catch (Exception) { /* read-only: nothing to lose */ }
            await tx.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
