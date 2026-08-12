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
public sealed class AdminMetricsService(FinAppDbContext db, BetaPolicy beta)
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
            var cohorts = await CohortsAsync(conn, ct);

            // Monetization counts. Wrapped so a missing/empty Subscriptions table reports zeros rather than
            // 500-ing the whole panel — the table is created idempotently at startup, but the metrics page must
            // never be the thing that breaks because billing hasn't shipped.
            var (trialsStarted, trialsActive, paying) = await MonetizationCountsAsync(conn, Iso(now), ct);

            // Seats are counted against the SAME rows the cap is enforced on at registration (BetaPolicy takes the
            // beta head count), so the panel can't say "12 left" while the next sign-up is told the beta is full.
            return new AdminMetricsDto(totalUsers, totalAccounts, betaCohort, new7, new30, active7, active30, byDay,
                trialsStarted, trialsActive, paying,
                cohorts, beta.Cap, beta.Remaining(betaCohort) is var left && left == int.MaxValue ? 0 : left);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>
    /// Trial take-up and real paying subscribers, from the <c>Subscriptions</c> table.
    /// <para><b>The trial's shape is fixed here before it is built</b>, so the metric and the mechanics can't
    /// disagree later: a trial is a row with <c>Provider = 'trial'</c> (the column is NOT NULL, so the "null
    /// provider" sketched in MONETIZATION.md becomes this sentinel), and it is <b>never deleted on expiry</b> —
    /// which is what makes "started" countable at all, and what stops a trial being replayable for free forever.</para>
    /// <para>Paying = a real provider and <c>Sandbox = '0'</c>. Sandbox rows are excluded deliberately: every row
    /// in existence today is one, and a metric that counted them would report revenue that never happened.</para>
    /// </summary>
    private static async Task<(int Started, int Active, int Paying)> MonetizationCountsAsync(
        System.Data.Common.DbConnection conn, string nowIso, CancellationToken ct)
    {
        try
        {
            var started = await ScalarAsync(conn,
                "SELECT COUNT(*) FROM \"Subscriptions\" WHERE \"Provider\" = 'trial'", ct);
            var active = await ScalarAsync(conn,
                "SELECT COUNT(*) FROM \"Subscriptions\" WHERE \"Provider\" = 'trial' " +
                "AND \"Status\" = 'active' AND \"ExpiresAt\" > @now", ct, ("@now", nowIso));
            var paying = await ScalarAsync(conn,
                "SELECT COUNT(DISTINCT \"UserId\") FROM \"Subscriptions\" " +
                "WHERE \"Provider\" <> 'trial' AND \"Sandbox\" = '0'", ct);
            return (started, active, paying);
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>
    /// Who is in one cohort — username, email and join date, newest first. Deliberately a <b>separate call</b>
    /// from <see cref="BuildAsync"/>: the metrics panel is a counts-only view and should stay one, so names are
    /// fetched only when an admin opens a cohort to answer "who do I pass to <c>POST /admin/cohort</c>".
    /// <para>
    /// Capped at <paramref name="max"/> rows. The cap is not privacy theatre — it stops a cohort of ten thousand
    /// from being serialized into a popover — and <see cref="AdminCohortMembersDto.Total"/> carries the real count
    /// so the UI can say what it is not showing.
    /// </para>
    /// </summary>
    public async Task<AdminCohortMembersDto> MembersAsync(string cohort, int max = 200, CancellationToken ct = default)
    {
        var key = (cohort ?? "").Trim().ToLowerInvariant();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            var total = await ScalarAsync(conn,
                "SELECT COUNT(*) FROM \"UserSignups\" WHERE \"Cohort\" = @c", ct, ("@c", key));

            // ⚠️ Two steps, not one SQL join. UserSignups stores its UserId as TEXT (the table is created by raw
            // DDL, deliberately migration-free), while Users."Id" is whatever the provider maps a Guid to — TEXT
            // in one case, a real uuid in Postgres. Joining them in SQL matches nothing on SQLite and would need
            // a cast on Postgres, so the id half is handed to EF, which knows the mapping on both.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT \"UserId\", \"JoinedAt\" FROM \"UserSignups\" WHERE \"Cohort\" = @c " +
                "ORDER BY \"JoinedAt\" DESC LIMIT @max";
            AddParam(cmd, "@c", key);
            AddParam(cmd, "@max", max);
            var joined = new List<(Guid Id, string At)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    if (Guid.TryParse(reader.GetString(0), out var uid))
                        joined.Add((uid, reader.GetString(1)));
            }
            if (joined.Count == 0) return new AdminCohortMembersDto(key, total, []);

            var ids = joined.Select(j => j.Id).ToList();
            var users = await db.Users.Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.Email })
                .ToDictionaryAsync(u => u.Id, ct);

            // A signup row whose user has since been deleted drops out rather than rendering as a blank line the
            // admin can do nothing with. The order set by the query above is preserved.
            var rows = joined
                .Where(j => users.ContainsKey(j.Id))
                .Select(j => new AdminCohortMember(users[j.Id].Username, users[j.Id].Email, j.At))
                .ToList();
            return new AdminCohortMembersDto(key, total, rows);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>
    /// Head count per sign-up cohort, biggest first. Grouped in SQL rather than filtered three times so a cohort
    /// value nobody expected still appears — a typo written straight to the column is exactly the thing this
    /// panel should surface, and three COUNT(*)s with hardcoded names would hide it.
    /// </summary>
    private static async Task<IReadOnlyList<AdminCohortPoint>> CohortsAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT \"Cohort\", COUNT(*) AS c FROM \"UserSignups\" GROUP BY \"Cohort\" ORDER BY c DESC";
        var rows = new List<AdminCohortPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new AdminCohortPoint(
                reader.IsDBNull(0) ? "(none)" : reader.GetString(0),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));
        return rows;
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
