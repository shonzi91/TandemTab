namespace FinApp.Server.Auth;

/// <summary>
/// Gate for the owner-only admin metrics (OPEN-BETA P2). Admin access is an allowlist of emails (config
/// <c>Admin:Emails</c>, comma- or semicolon-separated — a Cloud Run env var, so it changes without a deploy).
/// <para><b>Fails closed</b>, unlike <see cref="FinApp.Server.BankSync.BankAccessPolicy"/>: an empty or unset
/// allowlist means <b>nobody</b> is an admin. An endpoint that enumerates users is the single highest-value
/// target in the app, so "not configured" must never mean "open to all".</para>
/// </summary>
public sealed class AdminPolicy
{
    private readonly HashSet<string> _admins;

    public AdminPolicy(IConfiguration config) =>
        _admins = (config["Admin:Emails"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

    /// <summary>Whether this email is an admin. False for a null email or when no allowlist is configured.</summary>
    public bool IsAdmin(string? email) =>
        email is not null && _admins.Contains(email.ToLowerInvariant());
}
