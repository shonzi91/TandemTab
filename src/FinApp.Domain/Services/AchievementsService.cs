using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Periods;

namespace FinApp.Domain.Services;

/// <summary>A motivational milestone or streak (BACKLOG #12). Derived from account history but keyed by a <b>stable</b>
/// <see cref="Key"/> so the Dashboard can stamp a "date earned" into the account's achievement log the first time it's
/// seen. <see cref="Earned"/> ones are ticked (with their date); locked ones show <see cref="Percent"/> progress.</summary>
/// <summary>Difficulty tier — sets the earned medal's metal colour (bronze = starter, silver = steady, gold = major).</summary>
public enum AchievementTier { Bronze, Silver, Gold }

public sealed record Achievement(string Key, string Icon, string Title, string Desc, bool Earned, int? Percent = null, AchievementTier Tier = AchievementTier.Bronze);

/// <summary>Milestone tallies for the server-side read: how many are <see cref="Earned"/>, the <see cref="Total"/>
/// in the catalogue, and how many are <see cref="InProgress"/> (locked but above 0% — the Home strip's set).</summary>
public readonly record struct MilestoneCounts(int Earned, int Total, int InProgress);

/// <summary>
/// Computes the full achievement catalogue (earned + locked) for the Achievements modal and Home strip. Moved
/// server-side under the Option-A migration (docs/MOBILE.md): it depends only on the domain aggregate's public reads,
/// so the same computation drives the web client (which passes its <c>fmt</c>/<c>translate</c> for localized copy) and
/// the server-side milestones count (which ignores the copy). Everything counts from the account's
/// <see cref="Account.AchievementsAnchor"/> onward, so an existing account doesn't retroactively unlock its history
/// and back-/forward-dated periods can't farm milestones.
/// </summary>
public sealed class AchievementsService
{
    private readonly SavingsReportService _savings = new();
    private Func<string, string> _t = s => s;

    public IReadOnlyList<Achievement> Build(Account account, Func<Money, string> fmt, Func<string, string>? translate = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        _t = translate ?? (s => s);
        var list = new List<Achievement>();
        void Add(string key, string icon, string title, string desc, bool earned, int? percent = null, AchievementTier tier = AchievementTier.Bronze)
            => list.Add(new Achievement(key, icon, title, desc, earned, earned ? null : percent, tier));

        // --- Anchored view: only periods from the anchor onward count. ---
        var anchor = account.AchievementsAnchor;
        var periods = (anchor is { } an ? account.Periods.Where(p => p.From >= an) : account.Periods).ToList();
        // "Finished a period" achievements must only judge periods whose end date has actually passed — otherwise the
        // current, still-open period unlocks them prematurely (e.g. "no overspends" or "spent under budget" mid-month).
        var today = DateOnly.FromDateTime(DateTime.Today);
        var ended = periods.Where(p => p.To < today).ToList();
        var target = account.SavingsRateTarget;

        var expenseCount = periods.Sum(p => p.Expenses.Count);
        var maxDistinctDays = periods.Count == 0 ? 0 : periods.Max(p => p.Expenses.Select(e => e.Date).Distinct().Count());
        var totalSetAside = periods.Aggregate(0m, (s, p) => s + p.SavingsSetAsideTotal.Amount);
        var avgOutgoings = periods.Count == 0 ? 0m : periods.Average(p => p.ExpensesTotal.Amount);
        var maxBudgetedCats = periods.Count == 0 ? 0 : periods.Max(p => p.Budgets.Select(b => b.CategoryId).Distinct().Count());
        var anyBudget = periods.Any(p => p.Budgets.Count > 0);
        var streak = SavingStreak(periods, target);

        var debts = account.SavingCategories.Where(s => s.IsDebt).ToList();
        var origSum = debts.Sum(d => d.DebtOriginalBalance);
        var paidSum = debts.Sum(d => d.DebtPaidOff);
        var goalsReached = account.SavingCategories.Where(s => !s.IsDebt && s.HasGoal)
            .Count(s => _savings.GoalProgress(account, s.Id).GoalReached);

        // --- Engagement / logging ---
        Add("first_expense", "🧾", _t("First expense"), _t("You logged your first expense. The habit starts here."), expenseCount > 0);
        Add("days_15", "📆", _t("Regular"), _t("Logged expenses on 15+ days in a single period."), maxDistinctDays >= 15,
            Pct(maxDistinctDays, 15));
        Add("expenses_100", "💯", _t("Century"), _t("100 expenses logged. You're really tracking now."), expenseCount >= 100,
            Pct(expenseCount, 100));

        // --- Budgeting ---
        Add("first_budget", "🧰", _t("Budgeter"), _t("You set your first budget."), anyBudget);
        Add("budgets_5", "🗂️", _t("Organised"), _t("Budgets on 5+ categories in a period."), maxBudgetedCats >= 5,
            Pct(maxBudgetedCats, 5));
        Add("clean_sweep", "🧹", _t("Clean sweep"), _t("A whole period with no overspent budgets."), CleanSweep(account, ended));
        Add("under_budget", "🪙", _t("Under budget"), _t("Finished a period spending less than you budgeted overall."),
            ended.Any(p => p.BudgetedTotal.Amount > 0m && p.ExpensesTotal.Amount <= p.BudgetedTotal.Amount));

        // --- Saving ---
        Add("saver", "🌱", _t("Saver"), _t("You set money aside for the first time. Every bit counts."), totalSetAside > 0m);
        Add("first_bucket", "🐷", _t("Piggy"), _t("You created your first savings bucket."),
            account.SavingCategories.Any(s => !s.IsArchived && !s.IsDebt));
        Add("saved_1000", "💶", _t("Grand"), _t("You've set aside over 1,000 in total."), totalSetAside >= 1000m,
            Pct(totalSetAside, 1000m));
        Add("buffer_1mo", "💰", _t("Nest egg"), _t("You've saved at least a month of your average outgoings."),
            avgOutgoings > 0m && totalSetAside >= avgOutgoings, avgOutgoings > 0m ? Pct(totalSetAside, avgOutgoings) : null);
        Add("beat_target_10", "📈", _t("Overperformer"), _t("You beat your savings target by 10+ points in a period."),
            ended.Any(p => p.ContributionsPaidTotal.Amount > 0m && (_savings.PeriodSavingsRate(p) ?? 0m) >= target + 0.10m));
        Add("planned_contrib", "🧭", _t("Planner"), _t("You set a planned monthly contribution."),
            account.SavingCategories.Any(s => s.PlannedContribution > 0m));
        Add("streak_3", "🔥", _t("On a streak"), string.Format(_t("Hit your {0} target 3 periods running."), Pct(target)),
            streak >= 3, Pct(streak, 3));
        Add("streak_6", "🔥", _t("On a roll"), string.Format(_t("Hit your {0} target 6 periods running."), Pct(target)),
            streak >= 6, Pct(streak, 6));
        Add("streak_12", "🗓️", _t("Year of saving"), string.Format(_t("Hit your {0} target 12 periods running."), Pct(target)),
            streak >= 12, Pct(streak, 12));
        Add("goals_1", "🎯", _t("Goal getter"), _t("You reached your first savings goal."), goalsReached >= 1);
        Add("goals_3", "🏝️", _t("Hat-trick"), _t("You've reached three savings goals."), goalsReached >= 3, Pct(goalsReached, 3));

        // --- Debt ---
        Add("first_debt", "💳", _t("Facing it"), _t("You started tracking a debt — the first step to clearing it."), debts.Count > 0);
        Add("first_payment", "💪", _t("First payment down"), _t("You made your first payment toward a debt. Momentum."),
            debts.Any(d => d.DebtPaidOff > 0m));
        Add("overpay", "🚀", _t("Overpayer"), _t("You set aside more than the installment toward a debt in a period."),
            periods.Any(p => debts.Where(d => d.DebtInstallment > 0m).Any(d =>
                p.SavingAllocations.Where(a => a.SavingCategoryId == d.Id && !a.IsDisbursement).Sum(a => a.Amount.Amount) > d.DebtInstallment)));
        Add("debt_half_all", "⛰️", _t("Halfway home"), _t("You've cleared half of all your debt combined."),
            origSum > 0m && paidSum / origSum >= 0.5m, origSum > 0m ? Pct(paidSum, origSum * 0.5m) : null);
        Add("debtfree_12mo", "🏁", _t("In sight"), _t("You're on track to be debt-free within 12 months."), DebtFreeWithin(account, 12));
        Add("comeback", "🌤️", _t("Comeback"), _t("You returned to saving right after a period in the red."), Comeback(periods));

        // Per-debt payoff milestones (25/50/75/100%) — dynamic, only for debts that exist.
        foreach (var d in debts.Where(d => d.DebtProgressRatio is > 0m))
        {
            var pct = (int)Math.Floor((double)d.DebtProgressRatio!.Value * 100);
            var tier = pct >= 100 ? 100 : pct >= 75 ? 75 : pct >= 50 ? 50 : pct >= 25 ? 25 : 0;
            if (tier == 0) continue;
            Add($"debt_{tier}_{d.Id}", tier >= 100 ? "🏁" : "📉",
                tier >= 100 ? string.Format(_t("{0} paid off!"), d.Name) : string.Format(_t("{0}% of {1} cleared"), tier, d.Name),
                tier >= 100 ? _t("Cleared in full — outstanding work.")
                    : string.Format(_t("{0} of {1} paid off so far."),
                        fmt(new Money(d.DebtPaidOff, account.Currency)), fmt(new Money(d.DebtOriginalBalance, account.Currency))),
                true);
        }

        // Per-goal "reached" — dynamic.
        foreach (var s in account.SavingCategories.Where(x => !x.IsDebt && x.HasGoal))
            if (_savings.GoalProgress(account, s.Id).GoalReached)
                Add($"goal_{s.Id}", "🎯", string.Format(_t("{0} goal reached"), s.Name),
                    _t("You hit your savings goal. Time to set the next one."), true);

        // --- Social ---
        Add("shared", "🤝", _t("Better together"), _t("You're sharing this account with someone."), account.Members.Count > 1);

        // Assign each medal a difficulty tier (bronze default → silver steady → gold major) so earned badges vary in metal.
        for (var i = 0; i < list.Count; i++)
            if (TierFor(list[i].Key) is var t && t != AchievementTier.Bronze)
                list[i] = list[i] with { Tier = t };

        return list;
    }

    /// <summary>The milestone tallies for the server-side read (the copy is irrelevant to a count, so a no-op
    /// formatter is passed). Same catalogue the client builds, so the counts can't drift from what's on screen.</summary>
    public MilestoneCounts Counts(Account account)
    {
        var all = Build(account, static _ => string.Empty);
        return new MilestoneCounts(
            all.Count(a => a.Earned),
            all.Count,
            all.Count(a => !a.Earned && a.Percent is > 0));
    }

    private static AchievementTier TierFor(string key)
    {
        if (key is "streak_12" or "goals_3" or "debt_half_all" or "debtfree_12mo") return AchievementTier.Gold;
        if (key.StartsWith("debt_", StringComparison.Ordinal))                       // per-debt payoff milestone
            return key.Contains("_100_", StringComparison.Ordinal) || key.Contains("_75_", StringComparison.Ordinal)
                ? AchievementTier.Gold : AchievementTier.Silver;
        if (key.StartsWith("goal_", StringComparison.Ordinal)) return AchievementTier.Silver;   // per-goal reached
        if (key is "expenses_100" or "budgets_5" or "clean_sweep" or "under_budget" or "saved_1000"
                or "buffer_1mo" or "beat_target_10" or "streak_6" or "first_payment" or "overpay" or "comeback" or "goals_1")
            return AchievementTier.Silver;
        return AchievementTier.Bronze;
    }

    /// <summary>Consecutive most-recent (anchored) periods with income whose savings rate met the target. Periods with
    /// no contributions are skipped, not counted as misses, so an idle month doesn't wrongly break the chain.</summary>
    private int SavingStreak(IReadOnlyList<Period> periods, decimal target)
    {
        var n = 0;
        for (var i = periods.Count - 1; i >= 0; i--)
        {
            var p = periods[i];
            if (p.ContributionsPaidTotal.Amount <= 0m) continue;
            if ((_savings.PeriodSavingsRate(p) ?? 0m) >= target) n++;
            else break;
        }
        return n;
    }

    /// <summary>True when some anchored period had at least one budget and none of them ended overspent (subtree-aware).</summary>
    private static bool CleanSweep(Account account, IReadOnlyList<Period> periods) =>
        periods.Any(p => p.Budgets.Count > 0 &&
            p.Budgets.All(b => SpentInTree(account, p, b.CategoryId) <= b.Allocated.Amount));

    /// <summary>Spend on a category and all its descendants within a period (rolls sub-categories up to their parent).</summary>
    private static decimal SpentInTree(Account account, Period period, Guid categoryId)
    {
        var ids = new HashSet<Guid> { categoryId };
        var frontier = new Queue<Guid>(new[] { categoryId });
        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            foreach (var child in account.ChildrenOfCategory(id))
                if (ids.Add(child.Id)) frontier.Enqueue(child.Id);
        }
        return period.Expenses.Where(e => ids.Contains(e.CategoryId)).Sum(e => e.Amount.Amount);
    }

    private static bool DebtFreeWithin(Account account, int months)
    {
        var inputs = account.SavingCategories.Where(s => s.IsDebt && s.DebtBalance > 0m)
            .Select(s => new LoanForecast.LoanInput(s.Id, s.Name, s.DebtBalance, s.DebtAnnualRatePercent, s.DebtInstallment))
            .ToList();
        if (inputs.Count == 0) return false;
        return LoanForecast.PlanPayoff(inputs, 0m, LoanForecast.Strategy.Avalanche) is { } plan && plan.Months <= months;
    }

    private static bool Comeback(IReadOnlyList<Period> periods)
    {
        for (var i = 1; i < periods.Count; i++)
        {
            var prev = periods[i - 1];
            var prevDeficit = prev.SavingsSetAsideTotal.Amount < 0m || prev.ExpensesTotal.Amount > prev.ContributionsPaidTotal.Amount;
            if (prevDeficit && periods[i].SavingsSetAsideTotal.Amount > 0m) return true;
        }
        return false;
    }

    private static int Pct(decimal have, decimal need) =>
        need <= 0m ? 0 : (int)Math.Clamp(Math.Round(have / need * 100m), 0, 99);

    private string Pct(decimal ratio) => $"{decimal.Round(ratio * 100m, 0, MidpointRounding.AwayFromZero)}%";
}
