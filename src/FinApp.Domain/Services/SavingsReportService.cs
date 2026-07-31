using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;

namespace FinApp.Domain.Services;

/// <summary>Accumulated balance of a savings bucket and how much it moved in a given period.</summary>
public sealed record SavingBucketReport(Guid SavingCategoryId, Money AccumulatedTotal, Money PeriodNet);

/// <summary>Progress of a savings bucket toward its goal — mirrors <see cref="BudgetCoverage"/> for the savings side.</summary>
public sealed record SavingGoalProgress(Money Accumulated, Money? Goal, decimal AlertThreshold)
{
    /// <summary>Fraction of the goal reached (0..1+), or null when there's no goal.</summary>
    public decimal? Ratio => Goal is { } g ? Accumulated.RatioOf(g) : null;

    /// <summary>Percent of the goal reached, rounded, or null when there's no goal.</summary>
    public int? Percent => Ratio is { } r ? (int)decimal.Round(r * 100m, 0, MidpointRounding.AwayFromZero) : null;

    public bool GoalReached => Goal is { } g && Accumulated >= g;
    public bool ThresholdReached => Ratio is { } r && r >= AlertThreshold;
}

/// <summary>
/// Feature 8: reports savings progress — per-bucket accumulated balances across the whole
/// account history, the net set aside in a period, and the savings rate (saved ÷ contributions).
/// </summary>
public sealed class SavingsReportService
{
    /// <summary>Net set aside this period across all buckets, as a fraction of paid contributions (null if no
    /// contributions). Savings <b>disbursed</b> to a goal (e.g. a loan prepayment) are added back — deploying a save
    /// to its purpose is a success, not un-saving — so the rate reflects saving habit, not whether goals were reached.</summary>
    public decimal? PeriodSavingsRate(Period period)
    {
        ArgumentNullException.ThrowIfNull(period);
        // SavingsNetTotal includes disbursement drawdowns (negative); subtracting them (also negative) adds them back.
        var savedForRate = period.SavingsNetTotal - period.SavingsDisbursedTotal;
        return savedForRate.RatioOf(period.ContributionsPaidTotal);
    }

    /// <summary>The money you had to work with this period = fresh income (paid contributions) + free cash carried in.
    /// The carry-in is the opening balance minus what was already earmarked for savings before this period (that
    /// isn't fresh to re-allocate), floored at zero. Used as the savings-rate denominator so that setting aside
    /// carried-over cash isn't measured against this period's income alone.</summary>
    public Money MoneyIn(Account account, Period period)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(period);
        var priorSaved = AccumulatedTotal(account) - period.SavingsNetTotal;   // savings held from before, still earmarked
        var carriedFree = period.InitialTotal - priorSaved;
        if (carriedFree.IsNegative) carriedFree -= carriedFree;                 // deficit carried in adds no spendable money
        return period.ContributionsPaidTotal + carriedFree;
    }

    /// <summary>Net set aside this period as a fraction of <see cref="MoneyIn"/> (null when nothing came in). Preferred
    /// over <see cref="PeriodSavingsRate"/> for display: unlike "% of income", carried-over cash saved this period
    /// can't inflate it past ~100%, because you can't set aside more than came in.</summary>
    public decimal? PeriodMoneyInRate(Account account, Period period)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(period);
        var saved = period.SavingsNetTotal - period.SavingsDisbursedTotal;
        return saved.RatioOf(MoneyIn(account, period));
    }

    /// <summary>
    /// Per-bucket report: balance accumulated across all periods plus the movement in <paramref name="period"/>.
    /// The accumulated balance includes any pre-existing <see cref="SavingCategory.InitialAmount"/>; the
    /// period net is allocations only.
    /// </summary>
    public SavingBucketReport ForBucket(Account account, Period period, Guid savingCategoryId)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(period);

        if (account.FindSavingCategory(savingCategoryId) is null)
            throw new InvalidOperationException("Saving category not found in the account.");

        var bucketIds = account.SavingCategoryWithDescendantIds(savingCategoryId).ToHashSet();

        // As of the END of the period being viewed, not today. Looking back at March must show what the bucket held
        // in March — allocations made in April onward hadn't happened yet. Funds and spending already read this way;
        // savings didn't, so a closed period showed a total that its own numbers couldn't add up to.
        var accumulated = AllocationsFor(account, bucketIds, upToPeriodFrom: period.From) + InitialFor(account, bucketIds);

        var periodNet = period.SavingAllocations
            .Where(a => bucketIds.Contains(a.SavingCategoryId))
            .Select(a => a.Amount)
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);

        return new SavingBucketReport(savingCategoryId, accumulated, periodNet);
    }

    /// <summary>
    /// Total savings balance across every bucket and period — what the user actually has saved, including
    /// pre-existing initial balances. Use <see cref="AllocatedTotal"/> for the rate (which excludes those).
    /// </summary>
    public Money AccumulatedTotal(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return AllocatedTotal(account)
            + account.SavingCategories.Select(s => new Money(s.InitialAmount, account.Currency))
                .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);
    }

    /// <summary>Total set aside from contributions across every bucket and period (excludes initial balances). Includes
    /// disbursement drawdowns — this is the money-model figure (what's actually still reserved as savings).</summary>
    public Money AllocatedTotal(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Periods
            .SelectMany(p => p.SavingAllocations)
            .Select(a => a.Amount)
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);
    }

    /// <summary>Lifetime disbursed to goals (deployed savings) across every period — a negative figure.</summary>
    public Money DisbursedTotal(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Periods
            .SelectMany(p => p.SavingAllocations)
            .Where(a => a.IsDisbursement)
            .Select(a => a.Amount)
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);
    }

    /// <summary>Lifetime <b>saved</b> for display: total accumulated with disbursements added back — deploying a save to
    /// its goal counts as saved, not un-saved. (Use <see cref="AccumulatedTotal"/> for the money model.)</summary>
    public Money LifetimeSaved(Account account) => AccumulatedTotal(account) - DisbursedTotal(account);

    /// <summary>
    /// Savings rate across the whole account history: total set aside ÷ total contributions paid (null if
    /// no contributions). Pre-existing initial balances are excluded so the rate reflects only saving from
    /// what came in.
    /// </summary>
    public decimal? AccountSavingsRate(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var contributed = account.Periods
            .Select(p => p.ContributionsPaidTotal)
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);
        // Disbursements (deployed to goals) are added back — deploying a save isn't un-saving.
        return (AllocatedTotal(account) - DisbursedTotal(account)).RatioOf(contributed);
    }

    /// <summary>Progress of a bucket (and its sub-buckets) toward its goal, across the whole account history.</summary>
    public SavingGoalProgress GoalProgress(Account account, Guid savingCategoryId)
    {
        ArgumentNullException.ThrowIfNull(account);
        var bucket = account.FindSavingCategory(savingCategoryId)
            ?? throw new InvalidOperationException("Saving category not found in the account.");

        var bucketIds = account.SavingCategoryWithDescendantIds(savingCategoryId).ToHashSet();
        var accumulated = AllocationsFor(account, bucketIds) + InitialFor(account, bucketIds);

        Money? goal = bucket.GoalAmount is { } g ? new Money(g, account.Currency) : null;
        return new SavingGoalProgress(accumulated, goal, bucket.AlertThreshold);
    }

    /// <summary>
    /// The demonstrated "saving pace" for a bucket: the average amount <b>added</b> to it per period that had any
    /// deposit (positive allocations only — spends/disbursements/transfers-out are ignored). Used purely for
    /// forecasting a payoff or goal date; it never touches the money model. Null when there's no deposit history yet.
    /// </summary>
    public Money? AverageDepositPace(Account account, Guid savingCategoryId)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.FindSavingCategory(savingCategoryId) is null)
            throw new InvalidOperationException("Saving category not found in the account.");

        var bucketIds = account.SavingCategoryWithDescendantIds(savingCategoryId).ToHashSet();
        var perPeriodDeposits = account.Periods
            .Select(p => p.SavingAllocations
                .Where(a => bucketIds.Contains(a.SavingCategoryId) && a.Amount.Amount > 0m)
                .Sum(a => a.Amount.Amount))
            .Where(sum => sum > 0m)
            .ToList();

        if (perPeriodDeposits.Count == 0) return null;
        return new Money(perPeriodDeposits.Sum() / perPeriodDeposits.Count, account.Currency);
    }

    /// <summary>
    /// Debt buckets: the shrinking remaining-balance series over time — the original owed, then the balance after
    /// each period that made a payment (a disbursement out of the bucket). Feeds a shrinking-balance sparkline (#7).
    /// Reconstructed from payment history; empty for a common bucket or a debt without an original balance.
    /// </summary>
    public IReadOnlyList<decimal> DebtBalanceHistory(Account account, Guid savingCategoryId)
    {
        ArgumentNullException.ThrowIfNull(account);
        var bucket = account.FindSavingCategory(savingCategoryId)
            ?? throw new InvalidOperationException("Saving category not found in the account.");
        if (!bucket.IsDebt || bucket.DebtOriginalBalance <= 0m) return Array.Empty<decimal>();

        var bucketIds = account.SavingCategoryWithDescendantIds(savingCategoryId).ToHashSet();
        var series = new List<decimal> { bucket.DebtOriginalBalance };
        var remaining = bucket.DebtOriginalBalance;
        foreach (var period in account.Periods)   // chronological
        {
            // Disbursements out of the bucket are the payments; they're stored negative, so flip the sign.
            var paid = period.SavingAllocations
                .Where(a => bucketIds.Contains(a.SavingCategoryId) && a.IsDisbursement)
                .Sum(a => -a.Amount.Amount);
            if (paid <= 0m) continue;
            remaining = Math.Max(0m, remaining - paid);
            series.Add(remaining);
        }
        return series;
    }

    /// <param name="upToPeriodFrom">When given, only periods starting on or before this date count — the balance
    /// "as of" that period rather than today. Omit for the all-time total.</param>
    private static Money AllocationsFor(Account account, IReadOnlySet<Guid> bucketIds, DateOnly? upToPeriodFrom = null) =>
        account.Periods
            .Where(p => upToPeriodFrom is not { } cut || p.From <= cut)
            .SelectMany(p => p.SavingAllocations)
            .Where(a => bucketIds.Contains(a.SavingCategoryId))
            .Select(a => a.Amount)
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);

    private static Money InitialFor(Account account, IReadOnlySet<Guid> bucketIds) =>
        account.SavingCategories
            .Where(s => bucketIds.Contains(s.Id))
            .Select(s => new Money(s.InitialAmount, account.Currency))
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);
}
