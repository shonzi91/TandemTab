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
    /// <summary>This cost's share of a monthly set-aside as of <paramref name="asOf"/>. Recurring costs annualise
    /// (amount × times-per-year ÷ 12); a dated one-off spreads across the whole months until due (at least 1, so a
    /// due-now or overdue one-off asks for the full amount this month); an undated one-off contributes nothing to the
    /// running average (it's just a lump target).</summary>
    public decimal MonthlyAmount(DateOnly asOf) => Cadence switch
    {
        CostCadence.Monthly => Amount,
        CostCadence.Quarterly => Amount / 3m,     // paid every 3 months
        CostCadence.Yearly => Amount / 12m,
        CostCadence.OneOff => DueDate is { } d ? Amount / MonthsUntil(d, asOf) : 0m,
        _ => 0m,
    };

    private static int MonthsUntil(DateOnly due, DateOnly asOf)
    {
        var months = (due.Year - asOf.Year) * 12 + (due.Month - asOf.Month);
        return Math.Max(1, months);
    }
}
