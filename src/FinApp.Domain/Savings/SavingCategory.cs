using FinApp.Domain.Common;

namespace FinApp.Domain.Savings;

/// <summary>What a savings bucket is for. <see cref="Common"/> is an ordinary goal/earmark; <see cref="Debt"/>
/// is a payoff envelope carrying the loan's balance/rate/installment so payoff can be projected; <see cref="Investment"/>
/// carries a rate/term/compounding so future value can be projected. All accumulate real money the same way — the
/// debt/investment fields are projection metadata only and never affect the money model.</summary>
/// <remarks>Value 3 was a short-lived "PlannedExpense" kind (removed in favour of a set-aside schedule on a common
/// bucket). Legacy snapshots carrying kind 3 deserialize to an unknown enum value, match neither Debt nor Investment,
/// and so restore as a <see cref="Common"/> bucket (keeping their goal) — then re-save as Common. Don't reuse value 3.</remarks>
public enum SavingKind { Common = 0, Debt = 1, Investment = 2 }

/// <summary>How a bucket's per-period set-aside is suggested. <see cref="None"/> = no schedule; <see cref="Installment"/>
/// = a fixed amount each period; <see cref="SplitEvenly"/> = what's left to the goal divided across the periods left
/// until the due date, so it lands funded on time. Suggestion only — it never moves money by itself.</summary>
public enum SetAsideRule { None = 0, Installment = 1, SplitEvenly = 2 }

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

    /// <summary>Investment buckets only: the expected annual growth rate (%), the horizon in years, and how many times
    /// a year interest compounds (12 = monthly, 4 = quarterly, 1 = annual...). Pure projection inputs — they never touch
    /// balances, budgets or the savings rate. The present value used in the projection is the bucket's accumulated
    /// balance (initial + allocations), not a separate field. Body data (snapshot, not EF).</summary>
    public decimal InvestmentAnnualRatePercent { get; private set; }
    public decimal InvestmentTermYears { get; private set; }
    public int InvestmentCompoundsPerYear { get; private set; } = 12;

    public bool IsDebt => Kind == SavingKind.Debt;
    public bool IsInvestment => Kind == SavingKind.Investment;

    /// <summary>Optional set-aside schedule — a "plan" to fund this bucket toward its goal. Body data. Suggestion only:
    /// the app proposes an amount each period (see <c>SetAsidePlanner</c>); nothing is reserved automatically.</summary>
    public SetAsideRule Rule { get; private set; } = SetAsideRule.None;

    /// <summary><see cref="SetAsideRule.Installment"/>: the fixed amount to set aside each period. 0 otherwise.</summary>
    public decimal SetAsideAmount { get; private set; }

    /// <summary><see cref="SetAsideRule.SplitEvenly"/>: fund the goal by this date (drives the per-period split).</summary>
    public DateOnly? SetAsideDueDate { get; private set; }

    /// <summary>The fund this bucket's money is earmarked in ("held in"). A tag only — no money physically moves; it
    /// defaults the disburse/payment fund and shows where the bucket's money lives. Optional. Body data.</summary>
    public Guid? FundId { get; private set; }

    public bool HasSchedule => Rule != SetAsideRule.None;

    /// <summary>Optional free-text group tag (e.g. "Car") that rolls this bucket up with related recurring items and
    /// debts into a single total-cost view. Body data.</summary>
    public string? Group { get; private set; }

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
        ClearInvestmentFields();
    }

    /// <summary>Mark this bucket as an investment envelope with its (projection-only) growth figures.</summary>
    public void ConfigureInvestment(decimal annualRatePercent, decimal termYears, int compoundsPerYear)
    {
        if (annualRatePercent < 0m) throw new ArgumentException("Interest rate cannot be negative.", nameof(annualRatePercent));
        if (termYears < 0m) throw new ArgumentException("Term cannot be negative.", nameof(termYears));
        if (compoundsPerYear <= 0) throw new ArgumentException("Compounding frequency must be positive.", nameof(compoundsPerYear));
        Kind = SavingKind.Investment;
        InvestmentAnnualRatePercent = annualRatePercent;
        InvestmentTermYears = termYears;
        InvestmentCompoundsPerYear = compoundsPerYear;
        ClearDebtFields();
    }

    /// <summary>Revert to an ordinary (common) savings bucket, clearing the debt figures.</summary>
    public void ClearDebt()
    {
        Kind = SavingKind.Common;
        ClearDebtFields();
    }

    /// <summary>Revert to an ordinary (common) savings bucket, clearing the investment figures.</summary>
    public void ClearInvestment()
    {
        Kind = SavingKind.Common;
        ClearInvestmentFields();
    }

    private void ClearDebtFields()
    {
        DebtBalance = 0m;
        DebtAnnualRatePercent = 0m;
        DebtInstallment = 0m;
        DebtOriginalBalance = 0m;
    }

    private void ClearInvestmentFields()
    {
        InvestmentAnnualRatePercent = 0m;
        InvestmentTermYears = 0m;
        InvestmentCompoundsPerYear = 12;
    }

    /// <summary>Set (or with <see cref="SetAsideRule.None"/> clear) this bucket's set-aside schedule. Only the fields
    /// relevant to the rule are kept — a fixed amount for <see cref="SetAsideRule.Installment"/>, a due date for
    /// <see cref="SetAsideRule.SplitEvenly"/>.</summary>
    public void SetSchedule(SetAsideRule rule, decimal amount, DateOnly? dueDate)
    {
        if (amount < 0m) throw new ArgumentException("Set-aside amount cannot be negative.", nameof(amount));
        Rule = rule;
        SetAsideAmount = rule == SetAsideRule.Installment ? amount : 0m;
        SetAsideDueDate = rule == SetAsideRule.SplitEvenly ? dueDate : null;
    }

    public void ClearSchedule() => SetSchedule(SetAsideRule.None, 0m, null);

    /// <summary>Attach this bucket to a fund (an earmark tag — no money moves), or clear with null/empty.</summary>
    public void SetFund(Guid? fundId) => FundId = fundId is { } f && f != Guid.Empty ? f : null;

    /// <summary>Set or clear the free-text group tag. Blank clears it.</summary>
    public void SetGroup(string? group) => Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();

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
