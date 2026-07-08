using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Services;

namespace FinApp.Shared.UI.Services;

/// <summary>A motivational milestone or streak derived entirely from account history (BACKLOG #12) — no stored state.
/// <see cref="Earned"/> ones are celebrated on Home; an un-earned one is the next target to chase, with
/// <see cref="Percent"/> progress toward it.</summary>
public sealed record Achievement(string Icon, string Title, string Desc, bool Earned, int? Percent = null);

/// <summary>
/// Computes achievements/streaks for the Home motivation strip. Pure presentation-layer logic over the domain
/// aggregate's public reads (mirrors <see cref="InsightsService"/>); not in DI — the Dashboard news it up.
/// </summary>
public sealed class AchievementsService
{
    private readonly SavingsReportService _savings = new();
    private Func<string, string> _t = s => s;

    public IReadOnlyList<Achievement> Build(Account account, Func<Money, string> fmt, Func<string, string>? translate = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        _t = translate ?? (s => s);
        var earned = new List<Achievement>();
        var next = new List<Achievement>();

        // First money set aside — the starter win.
        var lifetime = _savings.LifetimeSaved(account);
        if (lifetime.Amount > 0m)
            earned.Add(new Achievement("🌱", _t("Saver"),
                string.Format(_t("You've set aside {0} in total. Every bit counts."), fmt(lifetime)), true));

        // Saving streak vs the account's target (consecutive most-recent periods that had income and hit the target).
        var target = account.SavingsRateTarget;
        var streak = CurrentSavingStreak(account, target);
        if (streak >= 3)
            earned.Add(new Achievement("🔥", string.Format(_t("{0}-period saving streak"), streak),
                string.Format(_t("You've hit your {0} savings target {1} periods running. Keep the chain alive."), Pct(target), streak), true));
        else
            next.Add(new Achievement("🔥", _t("Start a saving streak"),
                string.Format(_t("Hit your {0} target 3 periods running to earn this — you're at {1}."), Pct(target), streak),
                false, (int)Math.Round(streak / 3.0 * 100)));

        // First extra payment toward any debt.
        var debts = account.SavingCategories.Where(s => s.IsDebt).ToList();
        if (debts.Any(d => d.DebtPaidOff > 0m))
            earned.Add(new Achievement("💪", _t("First payment down"),
                _t("You've made your first payment toward a debt. That's momentum."), true));

        // Debt payoff milestones — the highest threshold crossed on each debt (25/50/75/100%).
        foreach (var d in debts.Where(d => d.DebtProgressRatio is > 0m))
        {
            var pct = (int)Math.Floor((double)d.DebtProgressRatio!.Value * 100);
            if (pct >= 100)
                earned.Add(new Achievement("🏁", string.Format(_t("{0} paid off!"), d.Name),
                    _t("Cleared in full — outstanding work."), true));
            else if (pct >= 25)
            {
                var tier = pct >= 75 ? 75 : pct >= 50 ? 50 : 25;
                earned.Add(new Achievement("📉", string.Format(_t("{0}% of {1} cleared"), tier, d.Name),
                    string.Format(_t("{0} of {1} paid off so far."),
                        fmt(new Money(d.DebtPaidOff, account.Currency)), fmt(new Money(d.DebtOriginalBalance, account.Currency))), true));
            }
        }

        // Savings goals reached.
        foreach (var s in account.SavingCategories.Where(x => !x.IsDebt && x.HasGoal))
        {
            if (_savings.GoalProgress(account, s.Id).GoalReached)
                earned.Add(new Achievement("🎯", string.Format(_t("{0} goal reached"), s.Name),
                    _t("You hit your savings goal. Time to celebrate — or set the next one."), true));
        }

        // Earned first (celebratory), then the single most-progressed "next" target to aim at.
        var result = new List<Achievement>(earned);
        var bestNext = next.OrderByDescending(a => a.Percent ?? 0).FirstOrDefault();
        if (bestNext is not null && result.Count < 6) result.Add(bestNext);
        return result;
    }

    /// <summary>Consecutive most-recent periods (that had income) whose savings rate met the target. Periods with no
    /// contributions are skipped, not treated as misses, so an idle month doesn't wrongly break the chain.</summary>
    private int CurrentSavingStreak(Account account, decimal target)
    {
        var n = 0;
        for (var i = account.Periods.Count - 1; i >= 0; i--)
        {
            var p = account.Periods[i];
            if (p.ContributionsPaidTotal.Amount <= 0m) continue;
            if ((_savings.PeriodSavingsRate(p) ?? 0m) >= target) n++;
            else break;
        }
        return n;
    }

    private static string Pct(decimal ratio) => $"{decimal.Round(ratio * 100m, 0, MidpointRounding.AwayFromZero)}%";
}
