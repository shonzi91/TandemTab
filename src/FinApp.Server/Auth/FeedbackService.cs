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

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"Feedback\" (" +
            "\"Id\" text PRIMARY KEY, \"UserId\" text NULL, \"Rating\" integer NULL, \"Comment\" text NULL, " +
            // Consent to show this publicly, captured at submission time. Default 0 — a review is never
            // publishable unless the person ticked the box for that specific review (OPEN-BETA P1).
            "\"PublicConsent\" text NOT NULL, \"Source\" text NOT NULL, " +
            "\"AppVersion\" text NULL, \"UserAgent\" text NULL, \"At\" text NOT NULL)", ct);

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
