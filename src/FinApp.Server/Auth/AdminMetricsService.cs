using System.Data;
using System.Globalization;
using FinApp.Contracts;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Computes the owner-only usage metrics (OPEN-BETA P2). <b>Counts and timestamps only</b> — it never opens an
/// encrypted account snapshot, so no other person's financial data is ever read. Sign-ups come from
/// <c>UserSignups</c> (<see cref="SignupService"/>), activity from when refresh tokens were last issued
/// (a login/refresh is the cheapest "came back" signal we already store).
/// </summary>
public sealed class AdminMetricsService(FinAppDbContext db)
{
    public async Task<AdminMetricsDto> BuildAsync(CancellationToken ct = default)
    {
        var totalUsers = await db.Users.CountAsync(ct);
        var totalAccounts = await db.Accounts.CountAsync(ct);

        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var d7 = Iso(now.AddDays(-7));
            var d30 = Iso(now.AddDays(-30));

            var betaCohort = await ScalarAsync(conn,
                "SELECT COUNT(*) FROM \"UserSignups\" WHERE \"Cohort\" = 'beta'", ct);
            var new7 = await ScalarAsync(conn,
                "SELECT COUNT(*) FROM \"UserSignups\" WHERE \"JoinedAt\" >= @cut", ct, ("@cut", d7));
            var new30 = await ScalarAsync(conn,
                "SELECT COUNT(*) FROM \"UserSignups\" WHERE \"JoinedAt\" >= @cut", ct, ("@cut", d30));
            // A distinct user who was issued a refresh token recently = someone who signed in / kept a session
            // alive. It's a proxy for "active", stored already, and never touches financial data.
            var active7 = await ScalarAsync(conn,
                "SELECT COUNT(DISTINCT \"UserId\") FROM \"RefreshTokens\" WHERE \"CreatedAt\" >= @cut", ct, ("@cut", d7));
            var active30 = await ScalarAsync(conn,
                "SELECT COUNT(DISTINCT \"UserId\") FROM \"RefreshTokens\" WHERE \"CreatedAt\" >= @cut", ct, ("@cut", d30));

            var byDay = await SignupsByDayAsync(conn, d30, ct);

            return new AdminMetricsDto(totalUsers, totalAccounts, betaCohort, new7, new30, active7, active30, byDay);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static async Task<IReadOnlyList<AdminDayPoint>> SignupsByDayAsync(
        System.Data.Common.DbConnection conn, string since, CancellationToken ct)
    {
        // The date part (yyyy-MM-dd) is the first 10 chars of the ISO timestamp; substr works on both providers.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT substr(\"JoinedAt\", 1, 10) AS d, COUNT(*) AS c FROM \"UserSignups\" " +
            "WHERE \"JoinedAt\" >= @cut GROUP BY d ORDER BY d";
        AddParam(cmd, "@cut", since);
        var rows = new List<AdminDayPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new AdminDayPoint(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));
        return rows;
    }

    private static async Task<int> ScalarAsync(
        System.Data.Common.DbConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) AddParam(cmd, name, value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

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
