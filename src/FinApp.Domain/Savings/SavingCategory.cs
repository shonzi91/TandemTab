using FinApp.Domain.Common;

namespace FinApp.Domain.Savings;

/// <summary>What a savings bucket is for. <see cref="Common"/> is an ordinary goal/earmark; <see cref="Debt"/>
/// is a payoff envelope carrying the loan's balance/rate/installment so payoff can be projected; <see cref="Investment"/>
/// carries a rate/term/compounding so future value can be projected. All accumulate real money the same way — the
/// debt/investment fields are projection metadata only and never affect the money model.</summary>
/// <remarks>Value 3 was a short-lived "PlannedExpense" kind (removed in favour of a set-aside schedule on a common
/// bucket). Legacy snapshots carrying kind 3 deserialize to an unknown enum value, match neither Debt nor Investment,
/// and so restore as a <see cref="Common"/> bucket (keeping their goal) — then re-save as Common. Don't reuse value 3.</remarks>
/// <summary>
/// What a bucket is for. <see cref="Expenses"/> is a sinking fund — money topped up to meet irregular costs
/// (insurance, tax, a lease residual) rather than to reach a goal.
/// <para>
/// <b>Value 3 is permanently reserved</b> and must never be reused: a short-lived "PlannedExpense" kind shipped
/// with that number and was reverted the same day, so snapshots in the wild still encode it. It deserializes to an
/// unrecognised value and restores as <see cref="Common"/> — see the serializer's legacy test. <see cref="Expenses"/>
/// therefore takes 4.
/// </para>
/// <para>
/// That earlier revert was about presentation, not the concept: back then every kind bought its own section on the
/// savings tab, which made it overwhelming. The tab is now one filtered grid, so a kind costs a filter chip instead
/// — which is why this distinction can finally be a real kind rather than something inferred from which field
/// happened to be filled in.
/// </para>
/// </summary>
public enum SavingKind { Common = 0, Debt = 1, Investment = 2, Expenses = 4 }

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
    /// installment. Pure projection inputs — they never touch balances, budgets or the savings rate. Body data.
    /// <para>
    /// <b>This is the balance as of <see cref="DebtBalanceAsOf"/>, not necessarily today</b> — read it through
    /// <see cref="DebtBalanceOn"/>, which carries it forward over the installments due since. See that method for why.
    /// </para></summary>
    public decimal DebtBalance { get; private set; }
    public decimal DebtAnnualRatePercent { get; private set; }
    public decimal DebtInstallment { get; private set; }

    /// <summary>
    /// Debt buckets only: the date <see cref="DebtBalance"/> was last known to be true — set whenever the balance is
    /// stated or corrected (setup, an edit, a payment). Null on legacy buckets and on debts with no schedule to walk,
    /// which then behave as before: the balance stays put until something changes it.
    /// <para>
    /// The anchor exists because a loan's position is <b>determined by its terms</b>, not by what the app happens to
    /// observe. The monthly installment is often paid from a different account — one this snapshot cannot see — so
    /// deriving the balance from "what was true then, plus the installments due since" is both more accurate than
    /// waiting to be told, and immune to a payment being missed, duplicated, or logged somewhere else entirely.
    /// A later imported repayment schedule slots in here, replacing the derived rows with the lender's actual ones.
    /// </para>
    /// Body data (snapshot, not EF).
    /// </summary>
    public DateOnly? DebtBalanceAsOf { get; private set; }

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

    /// <summary>A sinking fund: topped up to meet the costs in <see cref="Costs"/>, with no goal to finish.</summary>
    public bool IsExpensesFund => Kind == SavingKind.Expenses;

    /// <summary>The fund this bucket's money is earmarked in ("held in"). A tag only — no money physically moves; it
    /// defaults the disburse/payment fund and shows where the bucket's money lives. Optional. Body data.</summary>
    public Guid? FundId { get; private set; }

    private readonly List<PlannedCost> _costs = new();

    /// <summary>The future costs this bucket is a sinking fund for (insurance, tax, a one-off residual...). Each has an
    /// amount and a cadence; together they give the average per-period set-aside (<see cref="MonthlySetAside"/>). Body
    /// data — planning overlay only, never moves money.</summary>
    public IReadOnlyList<PlannedCost> Costs => _costs;

    public bool HasCosts => _costs.Count > 0;

    /// <summary>
    /// What's owed on <paramref name="asOf"/>: the anchored balance carried forward over the whole installments due
    /// since <see cref="DebtBalanceAsOf"/>. This — not the raw <see cref="DebtBalance"/> field — is the balance to
    /// show and to project from.
    /// <para>
    /// Only <c>installment − interest</c> comes off the principal each month, which is the whole point: subtracting a
    /// whole installment would over-credit by the interest, and the error compounds (a too-low balance charges too
    /// little interest next month, so it over-credits again).
    /// </para>
    /// Falls back to the stored balance when there's no anchor, no schedule, or the date is on/before the anchor —
    /// so legacy buckets and rate-less debts keep their existing behaviour exactly.
    /// </summary>
    public decimal DebtBalanceOn(DateOnly asOf)
    {
        if (!IsDebt || DebtBalanceAsOf is not { } anchor || DebtInstallment <= 0m) return DebtBalance;
        var months = MonthsBetween(anchor, asOf);
        if (months <= 0) return DebtBalance;
        return Forecasting.LoanForecast.BalanceAfter(DebtBalance, DebtAnnualRatePercent, DebtInstallment, months);
    }

    /// <summary>Whole installments due between two dates — a payment lands once a month on the anchor's day-of-month,
    /// so a part-month counts for nothing until its day comes round.</summary>
    private static int MonthsBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to.Day < from.Day) months--;      // the day hasn't come round yet this month
        return Math.Max(0, months);
    }

    /// <summary>Debt buckets: how much of the original balance has been paid off on <paramref name="asOf"/> (never
    /// negative). Zero for common buckets.</summary>
    public decimal DebtPaidOffOn(DateOnly asOf) => IsDebt ? Math.Max(0m, DebtOriginalBalance - DebtBalanceOn(asOf)) : 0m;

    /// <summary>Debt buckets: how much of the original balance has been paid off (never negative). Zero for common buckets.</summary>
    public decimal DebtPaidOff => IsDebt ? Math.Max(0m, DebtOriginalBalance - DebtBalance) : 0m;

    /// <summary>Debt buckets: fraction (0..1) of the original balance paid off on <paramref name="asOf"/>, or null
    /// when there's no original to measure against.</summary>
    public decimal? DebtProgressRatioOn(DateOnly asOf) => IsDebt && DebtOriginalBalance > 0m
        ? Math.Clamp(DebtPaidOffOn(asOf) / DebtOriginalBalance, 0m, 1m)
        : null;

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
    /// <param name="balanceAsOf">The date <paramref name="balance"/> is true on — it anchors the schedule (see
    /// <see cref="DebtBalanceAsOf"/>). Null keeps any existing anchor, which is what snapshot round-tripping wants;
    /// callers stating a fresh balance should pass the day they're stating it for.</param>
    public void ConfigureDebt(decimal balance, decimal annualRatePercent, decimal installment, decimal? originalBalance = null,
                              DateOnly? balanceAsOf = null)
    {
        if (balance < 0m) throw new ArgumentException("Debt balance cannot be negative.", nameof(balance));
        if (annualRatePercent < 0m) throw new ArgumentException("Interest rate cannot be negative.", nameof(annualRatePercent));
        if (installment < 0m) throw new ArgumentException("Installment cannot be negative.", nameof(installment));
        if (originalBalance is < 0m) throw new ArgumentException("Original balance cannot be negative.", nameof(originalBalance));
        Kind = SavingKind.Debt;
        DebtBalance = balance;
        // A stated balance is only true on the day it's stated, so it re-anchors the schedule. Null leaves the anchor
        // alone — the serializer restores the stored one separately, and must not silently re-date a loan on load.
        if (balanceAsOf is { } asOf) DebtBalanceAsOf = asOf;
        // Capture the original owed once (first config, or when told explicitly); never let it fall below what's still
        // owed, and grow it if the balance is corrected upward (e.g. more was borrowed).
        if (originalBalance is { } orig && orig > 0m) DebtOriginalBalance = orig;
        if (DebtOriginalBalance < balance) DebtOriginalBalance = balance;
        DebtAnnualRatePercent = annualRatePercent;
        DebtInstallment = installment;
        ClearInvestmentFields();
    }

    /// <summary>Restore the schedule anchor verbatim (snapshot round-trip only — see <see cref="DebtBalanceAsOf"/>).</summary>
    public void SetDebtBalanceAsOf(DateOnly? asOf) => DebtBalanceAsOf = asOf;

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

    /// <summary>Make this a sinking fund. A goal is cleared: an expenses fund has nothing to finish — next year's
    /// insurance follows this year's — so a goal figure would only give the ring a meaning it can't keep.</summary>
    public void ConfigureExpensesFund()
    {
        Kind = SavingKind.Expenses;
        GoalAmount = null;
        ClearDebtFields();
        ClearInvestmentFields();
    }

    /// <summary>Revert a sinking fund to an ordinary savings bucket. Its cost list is left alone — clearing it
    /// would throw away typed data on a toggle; callers that mean to drop the costs say so explicitly.</summary>
    public void ClearExpensesFund()
    {
        if (Kind == SavingKind.Expenses) Kind = SavingKind.Common;
    }

    private void ClearDebtFields()
    {
        DebtBalance = 0m;
        DebtAnnualRatePercent = 0m;
        DebtInstallment = 0m;
        DebtOriginalBalance = 0m;
        DebtBalanceAsOf = null;   // no schedule left to anchor
    }

    private void ClearInvestmentFields()
    {
        InvestmentAnnualRatePercent = 0m;
        InvestmentTermYears = 0m;
        InvestmentCompoundsPerYear = 12;
    }

    /// <summary>Attach this bucket to a fund (an earmark tag — no money moves), or clear with null/empty.</summary>
    public void SetFund(Guid? fundId) => FundId = fundId is { } f && f != Guid.Empty ? f : null;

    /// <summary>Replace this bucket's list of future costs (the sinking-fund lines). Blank-labelled lines are dropped.</summary>
    public void ReplaceCosts(IEnumerable<PlannedCost> costs)
    {
        _costs.Clear();
        _costs.AddRange(costs.Where(c => !string.IsNullOrWhiteSpace(c.Label) && c.Amount > 0m));
    }

    /// <summary>The average amount to set aside per period (month) to cover all this bucket's future costs, as of
    /// <paramref name="asOf"/>, ignoring anything already saved. Recurring costs annualise; a dated one-off spreads
    /// across the months until it's due.</summary>
    public decimal MonthlySetAside(DateOnly asOf) => decimal.Round(_costs.Sum(c => c.MonthlyAmount(asOf)), 2);

    /// <summary>
    /// The amount to set aside per month once <paramref name="saved"/> — what the bucket actually holds — is taken
    /// into account. Recurring costs keep their steady rate; dated one-offs are reduced by the savings attributed
    /// to them, so a part-funded residual stops asking for money you already have.
    /// <para>
    /// <b>Attribution rule:</b> savings go to targets in due-date order, soonest first, which is how a sinking fund
    /// really drains — the nearest obligation is the one the money is for. Anything left over after every target is
    /// covered is surplus and reduces nothing further (recurring costs are rates, not balances).
    /// </para>
    /// <para>
    /// <b>Known simplification:</b> a bucket holding both a revolving cost and a target shares one balance, and this
    /// treats all of it as available to the targets — so the float sitting there for next quarter's insurance also
    /// counts against the residual. Keep a revolving cost and a long-dated target in separate buckets if that
    /// matters; the bucket is the unit of earmarking precisely so it can be split.
    /// </para>
    /// </summary>
    public decimal MonthlySetAside(DateOnly asOf, decimal saved) =>
        PlannedCost.MonthlySetAsideFor(_costs, asOf, saved);

    /// <summary>
    /// How far behind this bucket's targets are <i>right now</i>: what every dated one-off still needs, less what's
    /// saved. Zero when the targets are fully covered. This is the "you're €X short" read — it answers a different
    /// question from <see cref="MonthlySetAside(DateOnly, decimal)"/>, which spreads that gap over the months left.
    /// </summary>
    public decimal TargetShortfall(decimal saved) => PlannedCost.TargetShortfallFor(_costs, saved);

    /// <summary>Set or clear the user's planned per-period contribution to this bucket. Null or zero clears it (revert
    /// to inferring pace from history). Cannot be negative.</summary>
    public void SetPlannedContribution(decimal? amount)
    {
        if (amount is < 0m) throw new ArgumentException("Planned contribution cannot be negative.", nameof(amount));
        PlannedContribution = amount is > 0m ? amount : null;
    }

    /// <summary>
    /// Record an <b>extra</b> payment against a debt bucket — money paid on top of the schedule, so the whole amount
    /// comes off the principal (unlike a contractual installment, most of which is interest; the schedule handles
    /// those, see <see cref="DebtBalanceOn"/>). No-op for a common bucket.
    /// <para>
    /// Catches the balance up to <paramref name="asOf"/> before subtracting, then re-anchors there: the payment is a
    /// fresh statement of what's owed, and dating it keeps the schedule walking from a point that was actually true.
    /// </para>
    /// </summary>
    public void RecordDebtPayment(decimal amount, DateOnly? asOf = null)
    {
        if (!IsDebt || amount <= 0m) return;
        var on = asOf ?? DebtBalanceAsOf;
        var current = on is { } d ? DebtBalanceOn(d) : DebtBalance;
        DebtBalance = Math.Max(0m, current - amount);
        if (on is { } anchor) DebtBalanceAsOf = anchor;
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
