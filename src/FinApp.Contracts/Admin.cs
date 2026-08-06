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
    int PayingSubscribers = 0);

/// <summary>One day's sign-up count, for the little activity sparkline. <see cref="Day"/> is an ISO date (yyyy-MM-dd).</summary>
public sealed record AdminDayPoint(string Day, int Count);
