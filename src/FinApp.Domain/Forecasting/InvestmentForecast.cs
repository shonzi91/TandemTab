namespace FinApp.Domain.Forecasting;

/// <summary>
/// Pure compound-growth math for the Investment bucket's projection — estimates only. Like <see cref="LoanForecast"/>
/// it takes numbers and returns numbers, touching nothing in the money model. Steps month by month (so a monthly
/// "extra on top" is intuitive) at the monthly rate equivalent to the stated nominal annual rate + compounding.
/// </summary>
public static class InvestmentForecast
{
    /// <summary>Cap the horizon so a silly term can't loop forever (100 years).</summary>
    public const int MaxMonths = 1200;

    /// <summary>Result of a projection: the projected value, how much of it was fresh contributions vs starting balance,
    /// and how much was growth (interest). <see cref="Months"/> is the horizon actually simulated.</summary>
    public readonly record struct Projection(decimal FutureValue, decimal Contributed, decimal Growth, int Months);

    /// <summary>
    /// Project the value after <paramref name="termYears"/>, starting from <paramref name="present"/> and adding
    /// <paramref name="monthlyContribution"/> at the end of each month. <paramref name="annualRatePercent"/> is the
    /// nominal annual rate compounded <paramref name="compoundsPerYear"/> times a year; growth is applied monthly at
    /// the equivalent monthly factor so 12 months of growth equals one year at the stated rate.
    /// </summary>
    public static Projection Project(decimal present, decimal annualRatePercent, decimal termYears, int compoundsPerYear, decimal monthlyContribution)
    {
        var months = (int)Math.Round((double)termYears * 12, MidpointRounding.AwayFromZero);
        months = Math.Clamp(months, 0, MaxMonths);
        var n = compoundsPerYear <= 0 ? 12 : compoundsPerYear;
        var contribution = Math.Max(0m, monthlyContribution);

        // Monthly growth factor equivalent to the nominal annual rate compounded n times a year.
        var nominalPerCompound = (double)annualRatePercent / 100.0 / n;
        var monthlyFactor = Math.Pow(1.0 + nominalPerCompound, (double)n / 12.0);

        var balance = (double)present;
        for (var m = 0; m < months; m++)
            balance = balance * monthlyFactor + (double)contribution;

        var fv = decimal.Round((decimal)balance, 2);
        var contributed = decimal.Round(contribution * months, 2);
        var growth = decimal.Round(fv - present - contributed, 2);
        return new Projection(fv, contributed, growth, months);
    }
}
