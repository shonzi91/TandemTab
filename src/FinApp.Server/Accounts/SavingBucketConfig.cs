using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Savings;

namespace FinApp.Server.Accounts;

/// <summary>
/// Applies a <see cref="SaveSavingBucketRequest"/> to a savings bucket that already exists on the aggregate — the
/// shared body of the create and update endpoints, mirroring <c>BudgetingState.SaveSavingBucket</c> so a native
/// bucket ends up configured exactly like a web one. Kept in one place so the two endpoints can't drift.
/// </summary>
public static class SavingBucketConfig
{
    /// <summary>Configure <paramref name="bucketId"/> (already added) from <paramref name="req"/>. <paramref name="today"/>
    /// anchors a debt bucket's stated balance. All domain guards (unique name, valid ids) surface as thrown exceptions.</summary>
    public static void Apply(Account account, Guid bucketId, SaveSavingBucketRequest req, DateOnly today)
    {
        account.RenameSavingCategory(bucketId, req.Name);
        account.SetSavingCategoryIcon(bucketId, req.Icon);

        // Kind, in the same priority order the web modal uses. Debt/investment carry their own figures, so any goal
        // is cleared; the ordinary branch clears debt/investment and (re)sets the goal.
        if (req.IsDebt)
        {
            account.ConfigureSavingDebt(bucketId, req.DebtBalance, req.DebtRate, req.DebtInstallment,
                originalBalance: req.DebtOriginalBalance, balanceAsOf: today,
                installmentDay: req.DebtInstallmentDay, startDate: req.DebtStartDate);
            // Set explicitly (not keep-on-null) so the edit form can CLEAR the due-day / origination date.
            account.SetSavingDebtInstallmentDay(bucketId, req.DebtInstallmentDay);
            account.SetSavingDebtStartDate(bucketId, req.DebtStartDate);
            // After ConfigureSavingDebt, which has just re-anchored the stated balance to today — so the snapshot
            // this takes on a mode change is today's stated figure, and flipping the switch moves no money.
            account.SetSavingDebtPaymentDriven(bucketId, req.DebtPaymentDriven, today);
            account.ConfigureSavingGoal(bucketId, null);
        }
        else if (req.IsInvestment)
        {
            account.ConfigureSavingInvestment(bucketId, req.InvRate, req.InvTermYears, req.InvCompounds);
            account.ConfigureSavingGoal(bucketId, null);
        }
        else
        {
            account.ClearSavingDebt(bucketId);
            account.ClearSavingInvestment(bucketId);
            account.ConfigureSavingGoal(bucketId, req.GoalAmount is > 0m ? req.GoalAmount : null, req.ThresholdPercent / 100m, req.NotifyOnMilestone);
        }

        account.SetSavingPlannedContribution(bucketId, req.PlannedContribution);
        account.SetSavingFund(bucketId, req.FundId);
        account.SetSavingCosts(bucketId, MapCosts(req.Costs));
        // Applied last so it wins over the goal branch: switching to a sinking fund clears any stale goal.
        if (req.IsExpensesFund) account.ConfigureSavingExpensesFund(bucketId);
        // Setup-time only: a pre-existing opening balance may be set while the account still has just one period.
        if (account.Periods.Count == 1) account.SetSavingInitialAmount(bucketId, req.InitialAmount);
    }

    private static IEnumerable<PlannedCost> MapCosts(IReadOnlyList<PlannedCostDto>? costs) =>
        costs is null ? [] : costs.Select(c => new PlannedCost(c.Label, c.Amount, Cadence(c.Cadence), c.DueDate));

    private static CostCadence Cadence(string? cadence) => (cadence ?? "").Trim().ToLowerInvariant() switch
    {
        "monthly" => CostCadence.Monthly,
        "quarterly" => CostCadence.Quarterly,
        "yearly" => CostCadence.Yearly,
        "one-off" or "oneoff" or "one_off" or "" => CostCadence.OneOff,
        _ => throw new InvalidOperationException($"Unknown cost cadence “{cadence}”."),
    };
}
