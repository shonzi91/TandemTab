namespace FinApp.Domain.Forecasting;

/// <summary>
/// Pure loan amortization math for the Forecasts tab — projections and "what-if" simulations only. It touches
/// nothing in the money model (no <see cref="Periods.Period"/>, funds, savings or balances): it just takes numbers
/// and returns numbers, so a loan/goal can be forecast without ever affecting the account's actual money flow.
/// </summary>
public static class LoanForecast
{
    /// <summary>Cap the month-by-month simulation so a too-small payment can't loop forever (100 years).</summary>
    public const int MaxMonths = 1200;

    /// <summary>Result of paying a loan off at a fixed monthly payment.</summary>
    /// <param name="Months">Whole months until the balance reaches zero.</param>
    /// <param name="TotalInterest">Total interest paid over the life of the loan.</param>
    public readonly record struct Payoff(int Months, decimal TotalInterest);

    /// <summary>
    /// Simulate clearing <paramref name="balance"/> at <paramref name="annualRatePercent"/> APR paying
    /// <paramref name="monthlyPayment"/> each month (interest compounds monthly). Returns null when the payment
    /// can't cover the monthly interest (it would never clear) or the balance/payment is non-positive.
    /// </summary>
    public static Payoff? PayOff(decimal balance, decimal annualRatePercent, decimal monthlyPayment)
    {
        if (balance <= 0m) return new Payoff(0, 0m);
        if (monthlyPayment <= 0m) return null;

        var monthlyRate = annualRatePercent / 100m / 12m;
        var remaining = balance;
        var interest = 0m;

        for (var month = 1; month <= MaxMonths; month++)
        {
            var monthInterest = remaining * monthlyRate;
            if (monthlyPayment <= monthInterest) return null;   // payment doesn't even dent the principal
            interest += monthInterest;
            remaining = remaining + monthInterest - monthlyPayment;
            if (remaining <= 0m) return new Payoff(month, decimal.Round(interest, 2));
        }
        return null;   // still not cleared after the cap → treat as "never" at this pace
    }

    /// <summary>What paying <paramref name="extraPerMonth"/> more each month buys you: months and interest saved
    /// versus the base payment. Null when either scenario never clears (so there's nothing meaningful to compare).</summary>
    public readonly record struct Simulation(Payoff Base, Payoff WithExtra, int MonthsSaved, decimal InterestSaved);

    public static Simulation? SimulateExtra(decimal balance, decimal annualRatePercent, decimal monthlyPayment, decimal extraPerMonth)
    {
        if (PayOff(balance, annualRatePercent, monthlyPayment) is not { } baseline) return null;
        if (PayOff(balance, annualRatePercent, monthlyPayment + extraPerMonth) is not { } faster) return null;
        return new Simulation(baseline, faster,
            Math.Max(0, baseline.Months - faster.Months),
            decimal.Round(Math.Max(0m, baseline.TotalInterest - faster.TotalInterest), 2));
    }
}
