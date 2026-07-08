using FinApp.Domain.Common;

namespace FinApp.Domain.Savings;

/// <summary>What a savings bucket is for. <see cref="Common"/> is an ordinary goal/earmark; <see cref="Debt"/>
/// is a payoff envelope carrying the loan's balance/rate/installment so payoff can be projected. Both accumulate
/// real money the same way — the debt fields are projection metadata only and never affect the money model.</summary>
public enum SavingKind { Common = 0, Debt = 1 }

/// <summary>
/// A savings bucket (Kids, Vacations, Loan principal...). Like budget categories these form a tree
/// via <see cref="ParentId"/> and are stored flat on the <c>Account</c>. Savings accumulate across
/// periods and stay in the account, earmarked and excluded from budgets until intentionally spent.
/// </summary>
public sealed class SavingCategory : Entity
{
    public string Name { get; private set; }
    public Guid? ParentId { get; private set; }

    /// <summary>Whether this is an ordinary savings goal or a debt-payoff envelope. Body data (snapshot, not EF).</summary>
    public SavingKind Kind { get; private set; } = SavingKind.Common;

    /// <summary>Debt buckets only: the outstanding balance owed, its annual rate (%) and the contractual monthly
    /// installment. Pure projection inputs — they never touch balances, budgets or the savings rate. Body data.</summary>
    public decimal DebtBalance { get; private set; }
    public decimal DebtAnnualRatePercent { get; private set; }
    public decimal DebtInstallment { get; private set; }

    /// <summary>Debt buckets only: the balance owed when the debt was first set up, kept fixed as payments lower
    /// <see cref="DebtBalance"/>. This is the "€Y" in "paid off €X of €Y (Z%)" and the baseline for progress-over-time.
    /// Captured automatically on first <see cref="ConfigureDebt"/>; never drops below the current balance. Body data.</summary>
    public decimal DebtOriginalBalance { get; private set; }

    /// <summary>Optional user-set target amount to add to this bucket each period ("€300/mo"). When set it is used
    /// instead of the app inferring a pace from deposit history, giving stable goal/payoff dates. Null → infer from
    /// history. Applies to both common and debt buckets. Projection metadata only — never touches the money model. Body data.</summary>
    public decimal? PlannedContribution { get; private set; }

    public bool IsDebt => Kind == SavingKind.Debt;

    /// <summary>Debt buckets: how much of the original balance has been paid off (never negative). Zero for common buckets.</summary>
    public decimal DebtPaidOff => IsDebt ? Math.Max(0m, DebtOriginalBalance - DebtBalance) : 0m;

    /// <summary>Debt buckets: fraction (0..1) of the original balance paid off, or null when there's no original to measure against.</summary>
    public decimal? DebtProgressRatio => IsDebt && DebtOriginalBalance > 0m
        ? Math.Clamp(DebtPaidOff / DebtOriginalBalance, 0m, 1m)
        : null;

    /// <summary>Archived buckets (a paid-off debt or a reached goal) are hidden from the main lists but keep their
    /// history. Body data (snapshot, not EF).</summary>
    public bool IsArchived { get; private set; }

    /// <summary>A debt bucket is cleared once nothing is owed on it.</summary>
    public bool IsDebtCleared => IsDebt && DebtBalance <= 0m;

    /// <summary>Optional display icon (emoji). Null → the UI derives one from the name. Body data (in the snapshot, not EF).</summary>
    public string? Icon { get; private set; }

    /// <summary>Optional target amount (in the account currency) for this bucket; null when there's no goal.</summary>
    public decimal? GoalAmount { get; private set; }

    /// <summary>Fraction (0..1) of the goal at which to raise a milestone alert, e.g. 0.80 = warn at 80%.</summary>
    public decimal AlertThreshold { get; private set; } = 0.80m;

    /// <summary>If true, notify when a savings milestone (threshold / goal) is reached. Mirrors a budget's notify flag.</summary>
    public bool NotifyOnMilestone { get; private set; }

    /// <summary>
    /// Money already saved in this bucket before the account started tracking periods (e.g. an existing
    /// balance you bring in on day one). It counts toward the bucket's accumulated balance and goal
    /// progress, but is deliberately <b>excluded from the savings rate</b> — that rate reflects only what
    /// you set aside from contributions, so it stays an honest "how much of what came in did I save".
    /// Set only during initial setup (the first period); see <see cref="SetInitialAmount"/>.
    /// </summary>
    public decimal InitialAmount { get; private set; }

    public SavingCategory(string name, Guid? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Saving category name is required.", nameof(name));
        Name = name.Trim();
        ParentId = parentId;
    }

    public bool IsRoot => ParentId is null;
    public bool HasGoal => GoalAmount is > 0m;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Saving category name is required.", nameof(name));
        Name = name.Trim();
    }

    /// <summary>Set or clear the savings goal and its alert settings. A null or zero amount clears the goal.</summary>
    public void SetGoal(decimal? goalAmount, decimal alertThreshold = 0.80m, bool notifyOnMilestone = false)
    {
        if (goalAmount is < 0m)
            throw new ArgumentException("Goal amount cannot be negative.", nameof(goalAmount));
        if (alertThreshold is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(alertThreshold), "Threshold must be between 0 and 1.");
        GoalAmount = goalAmount is > 0m ? goalAmount : null;
        AlertThreshold = alertThreshold;
        NotifyOnMilestone = notifyOnMilestone;
    }

    public void SetIcon(string? icon) => Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    /// <summary>Mark this bucket as a debt-payoff envelope and set its (projection-only) loan figures. The original
    /// balance (for progress %) is captured the first time and preserved across later edits so paying it down doesn't
    /// reset progress; pass <paramref name="originalBalance"/> to set it explicitly (e.g. round-tripping the snapshot).</summary>
    public void ConfigureDebt(decimal balance, decimal annualRatePercent, decimal installment, decimal? originalBalance = null)
    {
        if (balance < 0m) throw new ArgumentException("Debt balance cannot be negative.", nameof(balance));
        if (annualRatePercent < 0m) throw new ArgumentException("Interest rate cannot be negative.", nameof(annualRatePercent));
        if (installment < 0m) throw new ArgumentException("Installment cannot be negative.", nameof(installment));
        if (originalBalance is < 0m) throw new ArgumentException("Original balance cannot be negative.", nameof(originalBalance));
        Kind = SavingKind.Debt;
        DebtBalance = balance;
        // Capture the original owed once (first config, or when told explicitly); never let it fall below what's still
        // owed, and grow it if the balance is corrected upward (e.g. more was borrowed).
        if (originalBalance is { } orig && orig > 0m) DebtOriginalBalance = orig;
        if (DebtOriginalBalance < balance) DebtOriginalBalance = balance;
        DebtAnnualRatePercent = annualRatePercent;
        DebtInstallment = installment;
    }

    /// <summary>Revert to an ordinary (common) savings bucket, clearing the debt figures.</summary>
    public void ClearDebt()
    {
        Kind = SavingKind.Common;
        DebtBalance = 0m;
        DebtAnnualRatePercent = 0m;
        DebtInstallment = 0m;
        DebtOriginalBalance = 0m;
    }

    /// <summary>Set or clear the user's planned per-period contribution to this bucket. Null or zero clears it (revert
    /// to inferring pace from history). Cannot be negative.</summary>
    public void SetPlannedContribution(decimal? amount)
    {
        if (amount is < 0m) throw new ArgumentException("Planned contribution cannot be negative.", nameof(amount));
        PlannedContribution = amount is > 0m ? amount : null;
    }

    /// <summary>Record a payment against a debt bucket: lower the outstanding balance (never below zero). No-op for a
    /// common bucket. The full payment is applied to the balance — an approximation the user can correct by editing.</summary>
    public void RecordDebtPayment(decimal amount)
    {
        if (!IsDebt || amount <= 0m) return;
        DebtBalance = Math.Max(0m, DebtBalance - amount);
    }

    /// <summary>Hide/show this bucket in the main lists (its history is kept regardless).</summary>
    public void SetArchived(bool archived) => IsArchived = archived;

    /// <summary>Set the pre-existing balance carried into the bucket at setup time. Cannot be negative.</summary>
    public void SetInitialAmount(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("Initial amount cannot be negative.", nameof(amount));
        InitialAmount = amount;
    }
}
