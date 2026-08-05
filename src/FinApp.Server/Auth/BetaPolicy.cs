using System.Globalization;

namespace FinApp.Server.Auth;

/// <summary>
/// How many people the free beta lets in, and who doesn't count against that.
/// <para>
/// <b>Why a cap at all.</b> An open beta that grows faster than it can be supported produces churn, not signal —
/// the point of this phase is a group small enough that every report gets read and acted on. Thirty is the
/// number the owner set; it is config (<c>Beta__Cap</c>, a Cloud Run env var), so raising it is a revision
/// update rather than a deploy.
/// </para>
/// <para>
/// <b>Counted from a date.</b> The cap arrived mid-beta, so it counts sign-ups from <c>Beta__CountFrom</c>
/// onward. Counting from zero would have let existing users eat the entire allowance and slam the door
/// immediately — a cap that is already full the moment it ships isn't a cap, it's an outage.
/// </para>
/// <para>
/// <b>Test accounts are excluded by construction, not by a filter at read time.</b> An email matching
/// <c>Beta__TestEmailPatterns</c> is stamped <see cref="SignupService.TestCohort"/> at registration, so it never
/// occupies a seat and never inflates the tester count. Doing it at the source means no later query can forget
/// to apply the exclusion. Patterns are plain case-insensitive substrings (e.g. <c>+test@</c>,
/// <c>@example.com</c>) — kept out of the repo since this one is public.
/// </para>
/// </summary>
public sealed class BetaPolicy
{
    /// <summary>Zero or below disables the cap entirely (the pre-cap behaviour).</summary>
    public int Cap { get; }
    public DateTimeOffset CountFrom { get; }
    private readonly string[] _testPatterns;

    public BetaPolicy(IConfiguration config)
    {
        Cap = config.GetValue("Beta:Cap", 30);
        CountFrom = DateTimeOffset.TryParse(config["Beta:CountFrom"], CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var from)
            ? from
            : new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);   // the day the cap was introduced
        _testPatterns = (config["Beta:TestEmailPatterns"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToArray();
    }

    public bool Enabled => Cap > 0;

    /// <summary>Whether this address is one of ours. Matching addresses skip the cap and are stamped as the test
    /// cohort. Empty pattern list = nothing is a test account, which is the safe default (a mistake here would
    /// let real sign-ups bypass the cap).</summary>
    public bool IsTestEmail(string? email) =>
        email is not null && _testPatterns.Length > 0 &&
        _testPatterns.Any(p => email.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The cohort to stamp on a new account, given how many lifetime-Pro seats are already taken.
    /// <para>Our own addresses are always <see cref="SignupService.TestCohort"/>. Otherwise the first
    /// <see cref="Cap"/> real sign-ups get <see cref="SignupService.BetaCohort"/> (grandfathered to Pro for life);
    /// everyone after joins on <see cref="SignupService.FreeCohort"/>. Registration is never refused — the cap
    /// decides <em>which tier</em> you land on, not <em>whether</em> you may join.</para>
    /// </summary>
    public string CohortFor(string? email, int seatsTaken) =>
        IsTestEmail(email) ? SignupService.TestCohort
        : IsFull(seatsTaken) ? SignupService.FreeCohort
        : SignupService.BetaCohort;

    public int Remaining(int taken) => Enabled ? Math.Max(0, Cap - taken) : int.MaxValue;
    public bool IsFull(int taken) => Enabled && taken >= Cap;
}
