using System.Data;
using System.Globalization;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Records, once, that a user account was created — when, and under which cohort (OPEN-BETA B4). Backed by a
/// standalone table created idempotently, the same migration-free pattern as <see cref="FeedbackService"/> and
/// <see cref="ConsentService"/>.
/// <para>
/// <b>Why this exists and why it's its own table.</b> The <c>User</c> row carries no creation timestamp, so
/// today there is <em>no</em> record of who joined when. That is the one fact B4 calls out as impossible to
/// backfill: if we promise "people who joined during beta" grandfathered terms and never wrote down who that
/// was, we either reconstruct it from whatever timestamps happen to exist or break the promise. Kept off the
/// EF-mapped <c>User</c> entity deliberately — every other per-user concern (consent, avatars, 2FA, deletion,
/// feedback) is a side table, and a side table needs no EF migration (SQLite) or raw ALTER on the existing
/// Postgres <c>Users</c> table (which <c>EnsureCreated</c> would not evolve).
/// </para>
/// </summary>
public sealed class SignupService(FinAppDbContext db)
{
    /// <summary>The cohort stamped on everyone who registers before public launch. Post-launch this becomes a
    /// config value (or is simply derived from <c>JoinedAt</c> against the launch date); the point right now is
    /// that the fact is captured at the source rather than inferred later.</summary>
    public const string BetaCohort = "beta";

    /// <summary>Cohort for accounts we create ourselves to test with. They are stamped separately so they neither
    /// consume a capped beta seat nor inflate "how many real testers do we have" — and, usefully, they are NOT
    /// grandfathered to Pro, so a test account is the natural way to see the Free tier.</summary>
    public const string TestCohort = "test";

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"UserSignups\" (" +
            "\"UserId\" text PRIMARY KEY, \"JoinedAt\" text NOT NULL, \"Cohort\" text NOT NULL)", ct);

    /// <summary>Stamp a freshly created user. First write wins — a re-run never moves <c>JoinedAt</c> or the
    /// cohort. Never throws: a signup must not fail because its analytics row couldn't be written.</summary>
    public async Task RecordAsync(Guid userId, string cohort, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            // A brand-new user id can't collide, but guard anyway so a retry/replay is a no-op rather than a
            // 500. ON CONFLICT DO NOTHING is understood by both Postgres and SQLite (3.24+).
            cmd.CommandText =
                "INSERT INTO \"UserSignups\" (\"UserId\", \"JoinedAt\", \"Cohort\") VALUES (@uid, @at, @cohort) " +
                "ON CONFLICT (\"UserId\") DO NOTHING";
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@cohort", string.IsNullOrWhiteSpace(cohort) ? BetaCohort : cohort.Trim());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* stamping is best-effort; never block a sign-up on it */ }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Whether this user joined during the beta (stamped <c>Cohort = "beta"</c>). Used to grandfather
    /// beta-cohort accounts to Pro when monetization is switched on (OPEN-BETA P4).</summary>
    public async Task<bool> IsBetaCohortAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"UserSignups\" WHERE \"UserId\" = @uid AND \"Cohort\" = 'beta'";
            AddParam(cmd, "@uid", userId.ToString());
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>
    /// How many real beta seats have been taken since <paramref name="countFrom"/>. Counts <c>Cohort='beta'</c>
    /// only, so accounts stamped <see cref="TestCohort"/> never consume a seat.
    /// <para><b>Counted from a date, not from zero</b> — the cap was introduced mid-beta ("30 users from now
    /// on"), and making people who already signed up retroactively consume the new allowance would close the
    /// door before anyone could walk through it.</para>
    /// </summary>
    public async Task<int> BetaSeatsTakenAsync(DateTimeOffset countFrom, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            // JoinedAt is an ISO-8601 round-trip string, which sorts lexicographically in the same order as
            // chronologically, so a plain string comparison is a correct date filter on both engines.
            cmd.CommandText =
                "SELECT COUNT(*) FROM \"UserSignups\" WHERE \"Cohort\" = 'beta' AND \"JoinedAt\" >= @from";
            AddParam(cmd, "@from", countFrom.ToString("O", CultureInfo.InvariantCulture));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        catch { return 0; }   // never let a counting failure block sign-ups
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>How many accounts have been created (used by the tests; the real read path is SQL — see
    /// OPEN-BETA P2, which argues for a query before a dashboard).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"UserSignups\"";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally { if (opened) await conn.CloseAsync(); }
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
