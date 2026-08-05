using System.Data;
using System.Globalization;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Who is on a paid plan, and until when. Backed by a standalone table created idempotently — the same
/// migration-free pattern as <see cref="SignupService"/> / <see cref="FeedbackService"/>, and for the same reason
/// (no EF migration on SQLite, no raw ALTER on the live Postgres <c>Users</c> table).
/// <para>
/// <b>The record is ours, not the provider's.</b> Entitlement is decided by this table, never by calling Stripe at
/// request time: a provider outage must not downgrade paying users mid-session, and a plan check sits on the hot
/// path of every gated action. The provider's job is to tell us when this table should change (webhook /
/// completed checkout); its job is not to be the source of truth for a page render.
/// </para>
/// <para>
/// <b>Expiry is stored, not inferred.</b> A cancelled subscription stays entitled until the period it was paid
/// for ends — <see cref="IsActiveAsync"/> compares against <c>ExpiresAt</c>, so lapsing needs no cron job.
/// </para>
/// </summary>
public sealed class SubscriptionService(FinAppDbContext db)
{
    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"Subscriptions\" (" +
            "\"UserId\" text PRIMARY KEY, \"Plan\" text NOT NULL, \"Interval\" text NOT NULL, " +
            "\"Provider\" text NOT NULL, \"ProviderRef\" text NULL, \"Sandbox\" text NOT NULL, " +
            "\"StartedAt\" text NOT NULL, \"ExpiresAt\" text NOT NULL, \"Status\" text NOT NULL)", ct);

    /// <summary>Record (or renew) a paid subscription. Upsert — a renewal moves the expiry rather than adding a
    /// second row, since entitlement is a single yes/no per user.</summary>
    public async Task ActivateAsync(Guid userId, string interval, string provider, string? providerRef,
        bool sandbox, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"Subscriptions\" (\"UserId\", \"Plan\", \"Interval\", \"Provider\", \"ProviderRef\", " +
                "\"Sandbox\", \"StartedAt\", \"ExpiresAt\", \"Status\") " +
                "VALUES (@uid, 'pro', @interval, @provider, @ref, @sandbox, @started, @expires, 'active') " +
                "ON CONFLICT (\"UserId\") DO UPDATE SET \"Plan\" = 'pro', \"Interval\" = @interval, " +
                "\"Provider\" = @provider, \"ProviderRef\" = @ref, \"Sandbox\" = @sandbox, " +
                "\"ExpiresAt\" = @expires, \"Status\" = 'active'";
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@interval", interval);
            AddParam(cmd, "@provider", provider);
            AddParam(cmd, "@ref", (object?)providerRef ?? DBNull.Value);
            AddParam(cmd, "@sandbox", sandbox ? "1" : "0");
            AddParam(cmd, "@started", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            AddParam(cmd, "@expires", expiresAt.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Whether this user currently holds a paid plan. Never throws — a monetization lookup that fails
    /// must not 500 a page load; it degrades to "not subscribed", which the beta-cohort grandfather then covers
    /// for every current user anyway.</summary>
    public async Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT \"ExpiresAt\" FROM \"Subscriptions\" WHERE \"UserId\" = @uid AND \"Status\" = 'active'";
            AddParam(cmd, "@uid", userId.ToString());
            var raw = await cmd.ExecuteScalarAsync(ct) as string;
            return raw is not null
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expires)
                && expires > DateTimeOffset.UtcNow;
        }
        catch { return false; }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Mark a subscription cancelled. Deliberately keeps <c>ExpiresAt</c> — you keep what you paid for
    /// until it runs out.</summary>
    public async Task CancelAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE \"Subscriptions\" SET \"Status\" = 'cancelled' WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
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
