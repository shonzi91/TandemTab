using System.Data;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// An admin-only switch that pins one account to a specific plan, so the owner can walk the Free → upgrade → Pro
/// journey on demand — the thing you need repeatedly while wiring a real payment provider, and which is otherwise
/// impossible to reach without either flipping the global flag (which would start gating every real beta user) or
/// hand-editing the database.
/// <para>
/// <b>Turning it on also turns the plan surface on for that one account.</b> While <c>Monetization:Enabled</c> is
/// off every account reads "unlimited" and no plan UI exists anywhere, so an override that only set the plan
/// would still show nothing. An override therefore implies "monetization is live <em>for you</em>", leaving the
/// global flag off and every other user completely untouched.
/// </para>
/// <para>
/// <b>Admin-gated at write time, and stored as data.</b> Only an <see cref="AdminPolicy"/> email can set one; the
/// value is a row, not a claim in a token, so revoking admin rights doesn't leave a stale entitlement floating in
/// somebody's session. Clearing it returns the account to whatever the normal rules say.
/// </para>
/// </summary>
public sealed class PlanOverrideService(FinAppDbContext db)
{
    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"PlanOverrides\" (" +
            "\"UserId\" text PRIMARY KEY, \"Plan\" text NOT NULL)", ct);

    /// <summary>Pin this user to "free" or "pro". Any other value clears the override.</summary>
    public async Task SetAsync(Guid userId, string? plan, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            if (plan is "free" or "pro")
            {
                cmd.CommandText =
                    "INSERT INTO \"PlanOverrides\" (\"UserId\", \"Plan\") VALUES (@uid, @plan) " +
                    "ON CONFLICT (\"UserId\") DO UPDATE SET \"Plan\" = @plan";
                AddParam(cmd, "@uid", userId.ToString());
                AddParam(cmd, "@plan", plan);
            }
            else
            {
                cmd.CommandText = "DELETE FROM \"PlanOverrides\" WHERE \"UserId\" = @uid";
                AddParam(cmd, "@uid", userId.ToString());
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>The pinned plan, or null. Never throws — a lookup failure must degrade to "no override", which is
    /// the normal path, rather than 500 every page load.</summary>
    public async Task<string?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Plan\" FROM \"PlanOverrides\" WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch { return null; }
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
