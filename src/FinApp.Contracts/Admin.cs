namespace FinApp.Contracts;

/// <summary>
/// Owner-only usage metrics (OPEN-BETA P2). Deliberately <b>counts and timestamps only</b> — never any other
/// person's financial data. It answers "is anyone signing up, and are they coming back", which is the whole
/// point of watching a beta; reading a stranger's budget would contradict the product's privacy stance.
/// </summary>
public sealed record AdminMetricsDto(
    int TotalUsers,
    int TotalAccounts,
    int BetaCohort,
    int NewUsers7d,
    int NewUsers30d,
    int Active7d,
    int Active30d,
    IReadOnlyList<AdminDayPoint> SignupsByDay,
    // --- Monetization (added Session 92) ------------------------------------------------------------------
    // Both read 0 until their machinery ships, and that is the correct answer rather than a broken one: no trial
    // is modelled yet (R5, with the payment provider) and every subscription row so far is Sandbox = 1.
    /// <summary>How many people have ever started a Pro trial — cumulative, counting expired and cancelled ones,
    /// because "did the trial get taken up" is a question about starts, not about who is still inside one.</summary>
    int TrialsStarted = 0,
    /// <summary>How many are inside a trial right now (started, not expired, not converted).</summary>
    int TrialsActive = 0,
    /// <summary>⚠️ <b>Paying subscribers, not payment events.</b> <c>Subscriptions</c> holds one row per user and a
    /// renewal <i>upserts</i> it, so a customer in their third year is one row, not three payments. Counting real
    /// payments needs a payment/webhook event log, which lands with the provider — see MONETIZATION.md. Until then
    /// this is the honest figure: distinct users on a real (non-sandbox, non-trial) paid plan.</summary>
    int PayingSubscribers = 0,
    // --- Cohorts -------------------------------------------------------------------------------------------
    /// <summary>Every cohort with a head count, biggest first. <see cref="BetaCohort"/> above is the beta row of
    /// this list and stays for the tile that reads it; this is the whole picture beside it.</summary>
    IReadOnlyList<AdminCohortPoint>? Cohorts = null,
    /// <summary>The configured free-beta seat cap (<c>Beta__Cap</c>). 0 means the beta is closed/disabled.</summary>
    int BetaCap = 0,
    /// <summary>Seats left before new sign-ups stop being grandfathered to Pro. Never negative.</summary>
    int BetaSeatsLeft = 0);

/// <summary>One day's sign-up count, for the little activity sparkline. <see cref="Day"/> is an ISO date (yyyy-MM-dd).</summary>
public sealed record AdminDayPoint(string Day, int Count);

/// <summary>
/// One sign-up cohort and how many people are in it. <see cref="Cohort"/> is the stored key (<c>beta</c>,
/// <c>free</c>, <c>test</c>) rather than a display name, so an unrecognised value that somehow reached the
/// column still shows up here instead of being silently folded into another row.
/// </summary>
public sealed record AdminCohortPoint(string Cohort, int Count);

/// <summary>
/// One member of a cohort: who they are and when they joined. Fetched <b>only</b> when an admin opens a cohort,
/// never as part of the metrics payload — the panel stays a counts-only view of the beta until someone asks a
/// question that needs a name, which is almost always "who do I pass to <c>POST /admin/cohort</c>".
/// <para>
/// Identity and join date only. Nothing here touches an account snapshot, so a person's financial data stays as
/// unreachable to the admin console as it was before this existed.
/// </para>
/// </summary>
public sealed record AdminCohortMember(string Username, string Email, string JoinedAt);

/// <summary>A page of cohort members. <see cref="Total"/> is the full head count, so the UI can say how many of
/// them it is actually showing rather than implying the cap is the whole cohort.</summary>
public sealed record AdminCohortMembersDto(string Cohort, int Total, IReadOnlyList<AdminCohortMember> Members);
