using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Dam.Infrastructure.Persistence;

/// <summary>Runs hand-written SQL on the context's own connection, so it sees the same tenant (RLS) and transaction as everything else in the scope.</summary>
internal static class RawSql
{
    public static async Task<List<T>> QueryAsync<T>(DamDbContext db, string sql, Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> read, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);   // already open inside a transaction; opening also sets app.tenant_id
        try
        {
            await using var cmd = Command(db, sql, bind);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<T>();
            while (await r.ReadAsync(ct)) rows.Add(read(r));
            return rows;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public static async Task<T> ScalarAsync<T>(DamDbContext db, string sql, Action<NpgsqlParameterCollection> bind, Func<object?, T> convert, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, bind);
            return convert(await cmd.ExecuteScalarAsync(ct));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public static async Task<int> ExecuteAsync(DamDbContext db, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, bind);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static NpgsqlCommand Command(DamDbContext db, string sql, Action<NpgsqlParameterCollection> bind)
    {
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- callers pass compile-time SQL; every value is bound as a parameter.
        var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        if (db.Database.CurrentTransaction?.GetDbTransaction() is NpgsqlTransaction tx) cmd.Transaction = tx;
        bind(cmd.Parameters);
        return cmd;
    }

    public static void Bind(this NpgsqlParameterCollection p, string name, NpgsqlDbType type, object? value) =>
        p.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    public static long Long(object? v) => v is null or DBNull ? 0 : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
}
