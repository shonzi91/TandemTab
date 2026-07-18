namespace FinApp.Domain.Savings;

/// <summary>How often a planned cost recurs. <see cref="OneOff"/> is a single future payment (a lease residual, a
/// one-time bill); the rest repeat, so their per-month share is the amount annualised ÷ 12.</summary>
public enum CostCadence { OneOff = 0, Monthly = 1, Quarterly = 2, Yearly = 3 }

/// <summary>
/// One future cost a savings bucket is meant to cover (e.g. "Insurance €400 yearly", "Road tax €180 yearly",
/// "Residual €3,000 one-off by Jun 2027"). A pure planning value — it never moves money; it only feeds the average
/// per-period set-aside a sinking fund needs.
/// </summary>
/// <param name="Label">What the cost is.</param>
/// <param name="Amount">The amount per occurrence (for a recurring cost) or the total (for a one-off).</param>
/// <param name="Cadence">How often it falls due.</param>
/// <param name="DueDate">For a one-off, when it's needed — the amount is spread evenly across the months until then.</param>
public sealed record PlannedCost(string Label, decimal Amount, CostCadence Cadence, DateOnly? DueDate = null)
{
    /// <summary>
    /// True when this cost <b>completes</b> — a dated one-off is a target you can finish funding. Recurring costs
    /// never complete: next year's insurance follows this year's, so they are a <i>rate</i> to sustain, not a
    /// balance to reach. That distinction is what decides whether money already saved reduces the ask
    /// (see <see cref="MonthlyAmount(DateOnly, decimal)"/>).
    /// </summary>
    public bool IsTarget => Cadence == CostCadence.OneOff && DueDate is not null;

    /// <summary>This cost's share of a monthly set-aside as of <paramref name="asOf"/>, ignoring anything already
    /// saved. Recurring costs annualise (amount × times-per-year ÷ 12); a dated one-off spreads across the whole
    /// months until due (at least 1, so a due-now or overdue one-off asks for the full amount this month); an
    /// undated one-off contributes nothing to the running average (it's just a lump target).</summary>
    public decimal MonthlyAmount(DateOnly asOf) => MonthlyAmount(asOf, 0m);

    /// <summary>
    /// This cost's share of a monthly set-aside, given <paramref name="alreadySaved"/> attributed to it.
    /// <para>
    /// Only a <see cref="IsTarget">target</see> is reduced by what's saved, and that is the point: asking for the
    /// full amount when you've already put part of it away over-charges you for money you've got. A €3,000 residual
    /// due in 44 months with €1,000 saved needs €2,000 ÷ 44, not €3,000 ÷ 44.
    /// </para>
    /// <para>
    /// A recurring cost is deliberately unaffected. It is a rate that recurs forever, so "already saved" is float
    /// for the next occurrence, not progress toward finishing — discounting it would drop the ask to zero the month
    /// a bucket happens to be full and spike it right after the bill lands.
    /// </para>
    /// Never negative: an over-funded target asks for nothing.
    /// </summary>
    public decimal MonthlyAmount(DateOnly asOf, decimal alreadySaved) => Cadence switch
    {
        CostCadence.Monthly => Amount,
        CostCadence.Quarterly => Amount / 3m,     // paid every 3 months
        CostCadence.Yearly => Amount / 12m,
        CostCadence.OneOff => DueDate is { } d
            ? Math.Max(0m, Amount - Math.Max(0m, alreadySaved)) / MonthsUntil(d, asOf)
            : 0m,
        _ => 0m,
    };

    /// <summary>What's still to find for a target, given what's attributed to it. Recurring costs and undated
    /// one-offs return their full amount — nothing about them "completes".</summary>
    public decimal Remaining(decimal alreadySaved) =>
        IsTarget ? Math.Max(0m, Amount - Math.Max(0m, alreadySaved)) : Amount;

    /// <summary>
    /// The monthly set-aside a whole set of costs needs, given a shared <paramref name="saved"/> pot. Savings go to
    /// targets in due-date order, soonest first — how a sinking fund really drains, since the nearest obligation is
    /// what the money is for. Recurring costs keep their steady rate and consume none of the pot.
    /// <para>
    /// Lives here rather than only on <c>SavingCategory</c> so an editor can preview the figure for rows that aren't
    /// saved to a bucket yet, without a second copy of the attribution rule to drift out of step.
    /// </para>
    /// </summary>
    public static decimal MonthlySetAsideFor(IEnumerable<PlannedCost> costs, DateOnly asOf, decimal saved)
    {
        var all = costs as IReadOnlyList<PlannedCost> ?? costs.ToList();
        var pot = Math.Max(0m, saved);
        var total = 0m;

        foreach (var cost in all.Where(c => !c.IsTarget))
            total += cost.MonthlyAmount(asOf);

        foreach (var target in all.Where(c => c.IsTarget).OrderBy(c => c.DueDate))
        {
            var applied = Math.Min(pot, target.Amount);
            pot -= applied;
            total += target.MonthlyAmount(asOf, applied);
        }

        return decimal.Round(total, 2);
    }

    /// <summary>What a set of dated one-offs still needs beyond <paramref name="saved"/>. Recurring costs excluded —
    /// they never complete.</summary>
    public static decimal TargetShortfallFor(IEnumerable<PlannedCost> costs, decimal saved) =>
        decimal.Round(Math.Max(0m, costs.Where(c => c.IsTarget).Sum(c => c.Amount) - Math.Max(0m, saved)), 2);

    private static int MonthsUntil(DateOnly due, DateOnly asOf)
    {
        var months = (due.Year - asOf.Year) * 12 + (due.Month - asOf.Month);
        return Math.Max(1, months);
    }
}
