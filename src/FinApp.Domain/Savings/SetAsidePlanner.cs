namespace FinApp.Domain.Savings;

/// <summary>
/// Pure per-period set-aside suggestion for a scheduled savings bucket ("plan"). Numbers in, number out — it never
/// touches the money model: the app shows the figure and the user chooses whether to set it aside. This keeps the
/// "put a bit aside each period toward a future cost" behaviour honest and advisory, not an automatic reservation.
/// </summary>
public static class SetAsidePlanner
{
    /// <summary>How much to set aside this period, or null when there's nothing to suggest (no schedule, or the goal
    /// is already met and nothing more is needed).</summary>
    /// <param name="rule">The bucket's schedule rule.</param>
    /// <param name="installment">The fixed per-period amount (used only for <see cref="SetAsideRule.Installment"/>).</param>
    /// <param name="goal">The bucket's goal/target (used only for <see cref="SetAsideRule.SplitEvenly"/>).</param>
    /// <param name="saved">How much is already accumulated in the bucket.</param>
    /// <param name="dueDate">Fund-by date (used only for <see cref="SetAsideRule.SplitEvenly"/>).</param>
    /// <param name="periodFrom">The start date of the period being funded.</param>
    public static decimal? Suggest(SetAsideRule rule, decimal installment, decimal? goal, decimal saved, DateOnly? dueDate, DateOnly periodFrom)
    {
        switch (rule)
        {
            case SetAsideRule.Installment:
                return installment > 0m ? installment : null;

            case SetAsideRule.SplitEvenly:
                if (goal is not { } g || g <= saved) return null;   // no target, or already funded
                var remaining = g - saved;
                var periods = PeriodsRemaining(dueDate, periodFrom);
                return periods <= 1 ? decimal.Round(remaining, 2) : decimal.Round(remaining / periods, 2);

            default:
                return null;
        }
    }

    /// <summary>Whole periods (months, including the current one) from <paramref name="periodFrom"/> up to and
    /// including the month of <paramref name="dueDate"/>. Returns 1 when the due date is in the current period or
    /// already past, so the whole remainder is suggested now.</summary>
    public static int PeriodsRemaining(DateOnly? dueDate, DateOnly periodFrom)
    {
        if (dueDate is not { } d) return 1;
        var months = (d.Year - periodFrom.Year) * 12 + (d.Month - periodFrom.Month) + 1;
        return Math.Max(1, months);
    }
}
