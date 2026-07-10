namespace FinApp.Server.BankSync;

/// <summary>
/// MVP gate for external bank sync. Open Banking carries real per-user cost, licensing and compliance obligations,
/// so the feature is limited to an allowlist of account emails (config <c>BankSync:AllowedEmails</c>, comma- or
/// semicolon-separated). For everyone else it's reported as disabled (the client hides all bank UI) and the
/// provider-calling endpoints refuse.
/// <para>An <b>empty</b> allowlist means "enabled for everyone" — the pre-MVP behaviour — so nothing changes until
/// the operator sets the list. Widen or clear it later (an env var) to roll the feature out, no code change needed.</para>
/// </summary>
public sealed class BankAccessPolicy
{
    private readonly HashSet<string> _allowed;

    public BankAccessPolicy(IConfiguration config) =>
        _allowed = (config["BankSync:AllowedEmails"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

    /// <summary>True when an allowlist is configured (so access is restricted to it).</summary>
    public bool Restricted => _allowed.Count > 0;

    /// <summary>Whether this email may use external bank sync. Always true when no allowlist is configured.</summary>
    public bool IsAllowed(string? email) =>
        !Restricted || (email is not null && _allowed.Contains(email.ToLowerInvariant()));
}
