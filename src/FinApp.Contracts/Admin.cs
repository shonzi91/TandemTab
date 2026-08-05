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
    IReadOnlyList<AdminDayPoint> SignupsByDay);

/// <summary>One day's sign-up count, for the little activity sparkline. <see cref="Day"/> is an ISO date (yyyy-MM-dd).</summary>
public sealed record AdminDayPoint(string Day, int Count);
