namespace FinApp.Server.Assistant;

/// <summary>
/// Experimental-feature gate for the assistant (R3), deliberately the same shape as
/// <see cref="FinApp.Server.BankSync.BankAccessPolicy"/> — an allowlist of account emails (config
/// <c>Assistant:AllowedEmails</c>, comma- or semicolon-separated). It is the same two people today, and keeping
/// the two gates identical means one habit covers both.
/// <para>The reason is not cost: the measured bill is ~$0.0016 a question and the per-user cap already bounds it.
/// It is that the assistant sends a masked question to a <b>third party</b>, and 15 of the first 21 model answers
/// in production came back <c>unknown</c>. Both of those are things to be sure about with a known handful of
/// users before they are true for everyone.</para>
/// <para>An <b>empty</b> allowlist means "enabled for everyone" — so widening the rollout is an env var
/// (<c>Assistant__AllowedEmails</c>), not a deploy of new code. ⚠️ That direction is the risky one: clearing the
/// value by accident opens the feature rather than closing it. It matches BankSync on purpose, because two gates
/// that fail in opposite directions is how one of them gets misread.</para>
/// <para>⚠️ This gate must be read by <b>/assistant/status</b> as well as by the ask endpoint, and status is the
/// one that matters for the promise made here. The client hides every entrance when status says unavailable, and
/// the on-device matcher answers only from inside that UI — so a gated-out user gets no assistant at all, rather
/// than a working local one that fails the moment a question is hard.</para>
/// </summary>
public sealed class AssistantAccessPolicy
{
    private readonly HashSet<string> _allowed;

    public AssistantAccessPolicy(IConfiguration config) =>
        _allowed = (config["Assistant:AllowedEmails"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

    /// <summary>True when an allowlist is configured (so access is restricted to it).</summary>
    public bool Restricted => _allowed.Count > 0;

    /// <summary>Whether this email may use the assistant. Always true when no allowlist is configured.</summary>
    public bool IsAllowed(string? email) =>
        !Restricted || (email is not null && _allowed.Contains(email.ToLowerInvariant()));
}
