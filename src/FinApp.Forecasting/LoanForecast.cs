namespace FinApp.Forecasting;

/// <summary>
/// Pure loan amortization math for the Forecasts tab — projections and "what-if" simulations only. It touches
/// nothing in the money model (no periods, funds, savings or balances): it just takes numbers and returns numbers,
/// so a loan/goal can be forecast without ever affecting the account's actual money flow.
/// </summary>
public static class LoanForecast
{
    /// <summary>Cap the month-by-month simulation so a too-small payment can't loop forever (100 years).</summary>
    public const int MaxMonths = 1200;

    /// <summary>The interest charged in one month on <paramref name="balance"/> at <paramref name="annualRatePercent"/>
    /// APR with monthly compounding (balance × APR ÷ 12). Zero for a non-positive balance or rate. This is what a
    /// partial payment reduces straight away: pay <c>P</c> off the principal and next month's interest falls by
    /// <c>P × APR ÷ 12</c> — the concrete "what changes the moment you pay" figure.</summary>
    public static decimal MonthlyInterest(decimal balance, decimal annualRatePercent)
    {
        if (balance <= 0m || annualRatePercent <= 0m) return 0m;
        return decimal.Round(balance * (annualRatePercent / 100m / 12m), 2);
    }

    /// <summary>Result of paying a loan off at a fixed monthly payment.</summary>
    /// <param name="Months">Whole months until the balance reaches zero.</param>
    /// <param name="TotalInterest">Total interest paid over the life of the loan.</param>
    public readonly record struct Payoff(int Months, decimal TotalInterest);

    /// <summary>
    /// Simulate clearing <paramref name="balance"/> at <paramref name="annualRatePercent"/> APR paying
    /// <paramref name="monthlyPayment"/> each month (interest compounds monthly). Returns null when the payment
    /// can't cover the monthly interest (it would never clear) or the balance/payment is non-positive.
    /// </summary>
    /// <param name="residual">
    /// A final lump the schedule amortises <b>down to</b> rather than through — a lease's residual / balloon /
    /// buy-out value. Zero (the default) is an ordinary loan, which finishes at nothing.
    /// <para>
    /// This exists because a lease is not a loan that happens to end early. Its instalments are sized to leave a
    /// stated sum outstanding on the last scheduled date, and amortising it to zero overshoots the contract's own
    /// end by months — reporting a payoff date the lessee will never see, and interest they will never pay.
    /// </para>
    /// </param>
    public static Payoff? PayOff(decimal balance, decimal annualRatePercent, decimal monthlyPayment, decimal residual = 0m)
    {
        var target = Math.Max(0m, residual);
        if (balance <= target) return new Payoff(0, 0m);
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
            // The month the balance reaches the residual is the last SCHEDULED month. The residual itself is paid
            // then too, but it is not amortised and carries no further interest, so it doesn't extend the term.
            // ★ Compared to the cent when there IS a residual: that figure is *stated* (from a contract, or from
            // BalanceAfter, which rounds) while `remaining` is a raw running total, so the two agree only to two
            // decimals — and without this a fraction of a cent bought a whole extra month. A zero target needs no
            // tolerance: nothing owed is exact, and loosening it there could end an ordinary loan a month early.
            var reached = target > 0m ? decimal.Round(remaining, 2, MidpointRounding.AwayFromZero) <= target : remaining <= 0m;
            if (reached) return new Payoff(month, decimal.Round(interest, 2));
        }
        return null;   // still not cleared after the cap → treat as "never" at this pace
    }

    /// <summary>
    /// Walk <paramref name="balance"/> forward <paramref name="months"/> scheduled installments — the balance a
    /// lender's amortization table would show after that many payments. Each month charges interest on what's left,
    /// then applies the payment, so only <c>installment − interest</c> comes off the principal.
    /// <para>
    /// This is what makes a debt bucket's balance <b>derivable</b> rather than something the app must witness: given
    /// the terms and how long it's been, the position is determined. It matters because the installment is often paid
    /// from an account this one can't see — nothing has to observe the payment for the balance to be right.
    /// </para>
    /// Never returns below zero. A payment that can't cover the interest is a growing debt: the balance rises, which
    /// is the truth, so it's reported rather than clamped.
    /// </summary>
    public static decimal BalanceAfter(decimal balance, decimal annualRatePercent, decimal installment, int months)
    {
        if (balance <= 0m || months <= 0) return Math.Max(0m, balance);
        if (installment <= 0m) return balance;

        var monthlyRate = annualRatePercent / 100m / 12m;
        var remaining = balance;
        for (var m = 0; m < Math.Min(months, MaxMonths); m++)
        {
            remaining = remaining + (remaining * monthlyRate) - installment;
            if (remaining <= 0m) return 0m;
        }
        return decimal.Round(remaining, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How many scheduled installments bring <paramref name="from"/> down to <paramref name="target"/> — the inverse
    /// of <see cref="BalanceAfter"/>. Used to <b>estimate</b> how long a loan has been running (original → current
    /// balance) when no origination date was recorded, so "interest paid so far" can be reconstructed on the
    /// assumption payments were on schedule. Returns 0 when already at/below the target, or when the installment
    /// can't cover the interest (the balance isn't amortizing, so there's no honest month count to give).
    /// </summary>
    public static int MonthsToReach(decimal from, decimal annualRatePercent, decimal installment, decimal target)
    {
        if (from <= target || installment <= 0m) return 0;
        var monthlyRate = annualRatePercent / 100m / 12m;
        var remaining = from;
        for (var m = 1; m <= MaxMonths; m++)
        {
            var next = remaining + (remaining * monthlyRate) - installment;
            if (next >= remaining) return 0;      // not amortizing — payment ≤ interest
            remaining = next;
            if (remaining <= target) return m;
        }
        return MaxMonths;
    }

    /// <summary>
    /// The interest portion accrued over <paramref name="months"/> scheduled installments from <paramref name="balance"/>
    /// — the sum of each month's interest as the balance amortizes. This is the <b>self-consistent</b> way to reconstruct
    /// "interest paid so far": principal cleared + interest accrued always equals what was paid, because both come from
    /// the same walk. Never mix a typed principal figure with a separately-counted number of months — that double-counts
    /// or collapses the interest. Interest stops accruing once the loan clears (a short over-long month count is safe).
    /// </summary>
    public static decimal InterestAccrued(decimal balance, decimal annualRatePercent, decimal installment, int months)
    {
        if (balance <= 0m || months <= 0 || installment <= 0m) return 0m;
        var monthlyRate = annualRatePercent / 100m / 12m;
        var remaining = balance;
        var interest = 0m;
        for (var m = 0; m < Math.Min(months, MaxMonths); m++)
        {
            var monthInterest = remaining * monthlyRate;
            interest += monthInterest;
            remaining = remaining + monthInterest - installment;
            if (remaining <= 0m) break;   // cleared — no more interest accrues
        }
        return decimal.Round(interest, 2);
    }

    /// <summary>
    /// The level monthly payment that clears <paramref name="balance"/> in exactly <paramref name="months"/> at
    /// <paramref name="annualRatePercent"/> APR — the standard annuity payment. This is the other half of
    /// <see cref="PayOff"/>: that one fixes the payment and solves for the term, this one fixes the term and solves
    /// for the payment. Both are needed because a lender given a lump-sum overpayment offers exactly this choice —
    /// keep the installment and shorten the term, or keep the term and lower the installment.
    /// Zero-rate loans divide evenly. Null when the inputs can't describe a loan.
    /// </summary>
    public static decimal? PaymentFor(decimal balance, decimal annualRatePercent, int months)
    {
        if (balance <= 0m) return 0m;
        if (months <= 0) return null;

        var monthlyRate = annualRatePercent / 100m / 12m;
        if (monthlyRate <= 0m) return decimal.Round(balance / months, 2, MidpointRounding.AwayFromZero);

        // pmt = P·r / (1 − (1+r)^−n). Computed in double for the power, then rounded — a projection, not a ledger.
        var discount = 1 - Math.Pow(1 + (double)monthlyRate, -months);
        if (discount <= 0) return null;
        var payment = (double)balance * (double)monthlyRate / discount;
        if (double.IsNaN(payment) || double.IsInfinity(payment)) return null;
        return decimal.Round((decimal)payment, 2, MidpointRounding.AwayFromZero);
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

    /// <summary>
    /// What a one-off overpayment buys. <see cref="MonthsSaved"/>/<see cref="InterestSaved"/> assume you carry on
    /// paying the same installment (the loan simply finishes early); <see cref="LoweredInstallment"/> is the other
    /// thing a lender will do with the same money — keep the original end date and reduce the monthly payment.
    /// </summary>
    /// <remarks>
    /// Both are reported because they are genuinely different goods and the money can only buy one: finishing
    /// sooner is worth more in interest, lowering the payment is worth more in monthly breathing room. Presenting
    /// only the interest saved would quietly recommend the first.
    /// </remarks>
    public readonly record struct LumpSum(
        Payoff Before, Payoff AfterKeepingInstallment, decimal? LoweredInstallment,
        int MonthsSaved, decimal InterestSaved, bool ClearsTheLoan);

    /// <summary>
    /// Model paying <paramref name="lumpSum"/> off <paramref name="balance"/> in one go. Null when the loan has no
    /// schedule to compare against (no installment, or one that can't out-run the interest) — there is nothing
    /// honest to claim in that case.
    /// </summary>
    public static LumpSum? PayLumpSum(decimal balance, decimal annualRatePercent, decimal installment, decimal lumpSum)
    {
        if (lumpSum <= 0m) return null;
        if (PayOff(balance, annualRatePercent, installment) is not { } before) return null;

        var remaining = Math.Max(0m, balance - lumpSum);
        if (PayOff(remaining, annualRatePercent, installment) is not { } after) return null;

        // Keeping the ORIGINAL term and dropping the payment — the alternative use of the same money. Null when the
        // loan is cleared outright (there is no payment left to lower).
        var lowered = remaining <= 0m ? 0m : PaymentFor(remaining, annualRatePercent, before.Months);

        return new LumpSum(
            before, after, lowered,
            Math.Max(0, before.Months - after.Months),
            decimal.Round(Math.Max(0m, before.TotalInterest - after.TotalInterest), 2),
            remaining <= 0m);
    }

    // ---- Multi-loan payoff strategies (avalanche / snowball) ----

    /// <summary>One loan as an input to a multi-loan plan. Pure numbers — decoupled from any money-model entity.</summary>
    public readonly record struct LoanInput(Guid Id, string Name, decimal Balance, decimal AnnualRatePercent, decimal MinPayment);

    /// <summary>Which loan the spare cash attacks first.</summary>
    /// <remarks><see cref="Avalanche"/> targets the highest interest rate (cheapest overall); <see cref="Snowball"/>
    /// targets the smallest balance (fastest first win).</remarks>
    public enum Strategy { Avalanche, Snowball }

    /// <summary>When a given loan is fully cleared under a plan.</summary>
    public readonly record struct LoanCleared(Guid Id, string Name, int ClearedInMonth);

    /// <summary>Result of running a multi-loan plan: when you're debt-free, what it cost, and the order loans fall.</summary>
    public readonly record struct StrategyPlan(int Months, decimal TotalInterest, IReadOnlyList<LoanCleared> Order);

    /// <summary>
    /// Roll every loan's minimum payment plus a shared <paramref name="extraPerMonth"/> at the debt stack, always
    /// funnelling spare cash (and any freed-up minimums from cleared loans) at the <paramref name="strategy"/>'s
    /// current target. Interest compounds monthly. Returns null when the combined budget can't out-run the interest
    /// (the stack never clears) or there are no positive-balance loans to pay.
    /// </summary>
    public static StrategyPlan? PlanPayoff(IReadOnlyList<LoanInput> loans, decimal extraPerMonth, Strategy strategy)
    {
        var active = loans.Where(l => l.Balance > 0m).ToList();
        if (active.Count == 0) return null;

        var bal = active.Select(l => l.Balance).ToArray();
        var rate = active.Select(l => l.AnnualRatePercent / 100m / 12m).ToArray();
        var min = active.Select(l => Math.Max(0m, l.MinPayment)).ToArray();
        var cleared = new int[active.Count];      // 0 = still open
        var budget = active.Sum(l => Math.Max(0m, l.MinPayment)) + Math.Max(0m, extraPerMonth);
        if (budget <= 0m) return null;

        var order = new List<LoanCleared>();
        var totalInterest = 0m;

        for (var month = 1; month <= MaxMonths; month++)
        {
            // 1) Accrue this month's interest on every open loan.
            for (var i = 0; i < bal.Length; i++)
            {
                if (bal[i] <= 0m) continue;
                var mi = bal[i] * rate[i];
                totalInterest += mi;
                bal[i] += mi;
            }

            // 2) Pay each open loan its minimum (capped at the balance).
            var pool = budget;
            for (var i = 0; i < bal.Length; i++)
            {
                if (bal[i] <= 0m) continue;
                var pay = Math.Min(min[i], bal[i]);
                bal[i] -= pay;
                pool -= pay;
            }

            // 3) Funnel whatever's left at the target loans, in strategy order.
            foreach (var i in PriorityOrder(active, bal, strategy))
            {
                if (pool <= 0m) break;
                if (bal[i] <= 0m) continue;
                var pay = Math.Min(pool, bal[i]);
                bal[i] -= pay;
                pool -= pay;
            }

            // 4) Record any loans that just cleared.
            for (var i = 0; i < bal.Length; i++)
            {
                if (cleared[i] == 0 && bal[i] <= 0m)
                {
                    cleared[i] = month;
                    order.Add(new LoanCleared(active[i].Id, active[i].Name, month));
                }
            }

            if (cleared.All(c => c != 0))
                return new StrategyPlan(month, decimal.Round(totalInterest, 2), order);
        }
        return null;   // budget never out-ran the interest within the cap
    }

    /// <summary>Indices of the still-open loans ordered by which the strategy attacks first.</summary>
    private static IEnumerable<int> PriorityOrder(IReadOnlyList<LoanInput> loans, decimal[] bal, Strategy strategy)
    {
        var open = Enumerable.Range(0, loans.Count).Where(i => bal[i] > 0m);
        return strategy == Strategy.Avalanche
            ? open.OrderByDescending(i => loans[i].AnnualRatePercent).ThenBy(i => bal[i])   // priciest debt first
            : open.OrderBy(i => bal[i]).ThenByDescending(i => loans[i].AnnualRatePercent);  // smallest balance first
    }
}
