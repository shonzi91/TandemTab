using System.Data;
using System.Globalization;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Stores user feedback — a star rating, a comment, or both (OPEN-BETA B2). Backed by a standalone table created
/// idempotently, the same migration-free pattern as <see cref="ConsentService"/> and <see cref="AvatarService"/>.
/// <para>
/// <b>Why a table and not just a log line</b> (unlike client errors, which are logged only): feedback is content
/// we want to keep, come back to, and — with explicit per-item consent — eventually quote on the landing page
/// (OPEN-BETA P1). A log has retention limits and no consent flag; this needs both.
/// </para>
/// </summary>
public sealed class FeedbackService(FinAppDbContext db)
{
    /// <summary>Longest comment we store. Generous enough for a real complaint, bounded so the endpoint isn't a
    /// free write-anything channel.</summary>
    public const int MaxCommentLength = 4000;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"Feedback\" (" +
            "\"Id\" text PRIMARY KEY, \"UserId\" text NULL, \"Rating\" integer NULL, \"Comment\" text NULL, " +
            // Consent to show this publicly, captured at submission time. Default 0 — a review is never
            // publishable unless the person ticked the box for that specific review (OPEN-BETA P1).
            "\"PublicConsent\" text NOT NULL, \"Source\" text NOT NULL, " +
            "\"AppVersion\" text NULL, \"UserAgent\" text NULL, \"At\" text NOT NULL)", ct);

        // Added after the table shipped, so it arrives as an ALTER on the existing (live Postgres) table rather
        // than in the CREATE above.
        // ⚠️ Deliberately NOT "ADD COLUMN IF NOT EXISTS": Postgres accepts that, **SQLite does not** — and SQLite
        // is what dev and the MAUI client run on, so the IF NOT EXISTS form threw there, the catch swallowed it,
        // and the column was silently never created. Every read then failed instead of filtering, which looks
        // exactly like "no approved reviews" and would have hidden the carousel forever. A plain ALTER works on
        // both engines; on a rerun it throws "duplicate column", which is the one thing the catch should absorb.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Feedback\" ADD COLUMN \"Approved\" text NOT NULL DEFAULT '0'", ct);
        }
        catch { /* already present — the only expected failure here */ }
    }

    /// <summary>
    /// Reviews cleared for the landing page. <b>Two independent gates, both required:</b> the author ticked
    /// "you may show this" (<c>PublicConsent</c>) <em>and</em> a human approved it (<c>Approved</c>).
    /// <para>Consent alone is not enough and this is the whole point of the second column: <c>/feedback</c> is an
    /// <b>anonymous, unauthenticated</b> endpoint, so anyone on the internet can POST a five-star review with
    /// consent set — publishing on consent alone would put a stranger's text, unreviewed, on the marketing page
    /// of a product about trust. Approval defaults to 0, so the carousel is empty until someone deliberately
    /// promotes a row.</para>
    /// </summary>
    public async Task<IReadOnlyList<(int? Rating, string Comment, string At)>> PublicReviewsAsync(
        int limit = 12, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT \"Rating\", \"Comment\", \"At\" FROM \"Feedback\" " +
                "WHERE \"PublicConsent\" = '1' AND \"Approved\" = '1' AND \"Comment\" IS NOT NULL " +
                "ORDER BY \"At\" DESC";
            var list = new List<(int?, string, string)>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct) && list.Count < limit)
            {
                var rating = await r.IsDBNullAsync(0, ct) ? (int?)null : Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                var comment = r.GetString(1);
                if (string.IsNullOrWhiteSpace(comment)) continue;
                list.Add((rating, comment, r.GetString(2)));
            }
            return list;
        }
        catch { return Array.Empty<(int?, string, string)>(); }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Record one piece of feedback. <paramref name="userId"/> is null when it came from the landing
    /// page (someone who hasn't signed up — often the most useful feedback there is).</summary>
    public async Task RecordAsync(Guid? userId, int? rating, string? comment, bool publicConsent,
        string source, string? appVersion, string? userAgent, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"Feedback\" (\"Id\", \"UserId\", \"Rating\", \"Comment\", \"PublicConsent\", \"Source\", " +
                "\"AppVersion\", \"UserAgent\", \"At\") " +
                "VALUES (@id, @uid, @rating, @comment, @consent, @source, @app, @ua, @at)";
            AddParam(cmd, "@id", Guid.NewGuid().ToString());
            AddParam(cmd, "@uid", (object?)userId?.ToString() ?? DBNull.Value);
            AddParam(cmd, "@rating", rating is { } r ? r : (object)DBNull.Value);
            AddParam(cmd, "@comment", (object?)Trim(comment, MaxCommentLength) ?? DBNull.Value);
            AddParam(cmd, "@consent", publicConsent ? "1" : "0");
            AddParam(cmd, "@source", Trim(source, 20) ?? "app");
            AddParam(cmd, "@app", (object?)Trim(appVersion, 60) ?? DBNull.Value);
            AddParam(cmd, "@ua", (object?)Trim(userAgent, 300) ?? DBNull.Value);
            AddParam(cmd, "@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Everything with public consent, approved or not — the admin moderation queue. Without this the
    /// approval gate had no door: the column existed and defaulted to 0, so the carousel could never fill.</summary>
    public async Task<IReadOnlyList<(string Id, int? Rating, string? Comment, bool Consent, bool Approved, string Source, string At)>>
        ModerationQueueAsync(int limit = 50, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT \"Id\", \"Rating\", \"Comment\", \"PublicConsent\", \"Approved\", \"Source\", \"At\" " +
                "FROM \"Feedback\" WHERE \"PublicConsent\" = '1' ORDER BY \"At\" DESC";
            var list = new List<(string, int?, string?, bool, bool, string, string)>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct) && list.Count < limit)
            {
                list.Add((
                    r.GetString(0),
                    await r.IsDBNullAsync(1, ct) ? null : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                    await r.IsDBNullAsync(2, ct) ? null : r.GetString(2),
                    r.GetString(3) == "1",
                    r.GetString(4) == "1",
                    r.GetString(5),
                    r.GetString(6)));
            }
            return list;
        }
        catch { return Array.Empty<(string, int?, string?, bool, bool, string, string)>(); }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Approve (or un-approve) one review for public display.</summary>
    public async Task SetApprovedAsync(string id, bool approved, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE \"Feedback\" SET \"Approved\" = @a WHERE \"Id\" = @id";
            AddParam(cmd, "@a", approved ? "1" : "0");
            AddParam(cmd, "@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>How many pieces of feedback exist (used by the tests; the real read path is SQL for now — see
    /// OPEN-BETA P2, which argues for a query before a dashboard).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"Feedback\"";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

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
