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

        var accumulated = AllocationsFor(account, bucketIds) + InitialFor(account, bucketIds);

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

    /// <summary>Total set aside from contributions across every bucket and period (excludes initial balances). Drives the savings rate.</summary>
    public Money AllocatedTotal(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Periods
            .SelectMany(p => p.SavingAllocations)
            .Select(a => a.Amount)
            .Aggregate(Money.Zero(account.Currency), (acc, m) => acc + m);
    }

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
        return AllocatedTotal(account).RatioOf(contributed);
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

    private static Money AllocationsFor(Account account, IReadOnlySet<Guid> bucketIds) =>
        account.Periods
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
