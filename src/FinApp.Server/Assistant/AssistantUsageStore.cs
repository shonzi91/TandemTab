using System.Data;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Assistant;

/// <summary>
/// How many model calls a user has spent, per calendar month and per day. Backed by a standalone table created
/// idempotently — the same migration-free pattern as <see cref="Auth.ConsentService"/>.
/// <para>
/// ⚠️⚠️ <b>This replaces an in-memory counter, and the reason is that the in-memory one was not a limit.</b> It
/// lived in one process, so the real ceiling was the cap times the instance count — a number nobody controls,
/// since Cloud Run decides it — and it reset to zero on every deploy. A spend limit that a restart clears is a
/// suggestion. This one is shared by every instance and survives anything short of dropping the table.
/// </para>
/// <para>
/// ★ <b>Only calls that actually reach the model are counted.</b> Questions answered by the local matcher, by the
/// suggestion chips, or from the answer cache cost nothing and consume nothing — so a user who exhausts the
/// month still has a working assistant for everything the device can answer, which is most of it.
/// </para>
/// </summary>
public sealed class AssistantUsageStore(FinAppDbContext db)
{
    /// <summary>The month a moment falls in, e.g. <c>2026-08</c>. UTC, deliberately: a per-user local month would
    /// make the reset time depend on where someone is standing, and the bill is not measured that way.</summary>
    public static string MonthBucket(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM");
    public static string DayBucket(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd");

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"AssistantUsage\" (" +
            "\"UserId\" text NOT NULL, \"Bucket\" text NOT NULL, \"Calls\" integer NOT NULL, " +
            "PRIMARY KEY (\"UserId\", \"Bucket\"))", ct);

    /// <summary>
    /// Add one to a bucket and return the new total, atomically.
    /// <para>⚠️ <b>The one piece of dialect-sensitive SQL in this file.</b> <c>ON CONFLICT … DO UPDATE SET
    /// "Calls" = "Calls" + 1</c> is written unqualified because that form means "the existing row" on both SQLite
    /// and Postgres; a qualified reference is accepted by one and not reliably by the other. The increment must
    /// be a single statement — read-then-write would let two instances hand out the same last call.</para>
    /// </summary>
    public async Task<int> BumpAsync(Guid userId, string bucket, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO \"AssistantUsage\" (\"UserId\", \"Bucket\", \"Calls\") VALUES (@uid, @bucket, 1) " +
                    "ON CONFLICT (\"UserId\", \"Bucket\") DO UPDATE SET \"Calls\" = \"Calls\" + 1";
                AddParam(cmd, "@uid", userId.ToString());
                AddParam(cmd, "@bucket", bucket);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return await ReadAsync(conn, userId, bucket, ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    public async Task<int> GetAsync(Guid userId, string bucket, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try { return await ReadAsync(conn, userId, bucket, ct); }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static async Task<int> ReadAsync(System.Data.Common.DbConnection conn, Guid userId, string bucket, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"Calls\" FROM \"AssistantUsage\" WHERE \"UserId\" = @uid AND \"Bucket\" = @bucket";
        AddParam(cmd, "@uid", userId.ToString());
        AddParam(cmd, "@bucket", bucket);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task<bool> OpenAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (conn.State == ConnectionState.Open) return false;
        await conn.OpenAsync(ct);
        return true;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
