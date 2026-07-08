using System.Globalization;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Shared.UI.Services;

/// <summary>How a delta should read: <c>Up</c> = spending/cost rose (bad, red), <c>Down</c> = fell (good, green), <c>Flat</c> = neutral.</summary>
public enum DeltaDir { Up, Down, Flat }

/// <summary>Health band the score falls into; drives gauge colour + verdict copy.</summary>
public enum HealthBand { AtRisk, Average, Healthy }

/// <summary>Kind of signal card (matches the template's warn / good / info icon tints).</summary>
public enum SignalKind { Warn, Good, Info }

public sealed record Signal(SignalKind Kind, string Title, string Desc, string Delta, DeltaDir Dir);

public sealed record CategorySpend(string Name, string Icon, Money Amount, decimal BarFraction, DeltaDir Dir, string ColorHex);

public sealed record TrendPoint(string Label, Money Outgoings, decimal BarFraction, bool IsCurrent);

/// <summary>A labelled mini-trend across recent periods for the Insights "trends over time" strip (#9). <see cref="Points"/>
/// are chronological raw values (percentage points for rates, currency amounts otherwise). <see cref="Dir"/> is already
/// framed as a sentiment colour — <c>Down</c> = good/green, <c>Up</c> = bad/red — matching the signal cards.</summary>
public sealed record TrendSeries(string Label, string Icon, IReadOnlyList<decimal> Points, string CurrentText, string DeltaNote, DeltaDir Dir);

public sealed record QuickWin(string Text);

/// <summary>
/// A read-only financial-health/insights view model for one period of an account, derived entirely from
/// existing domain reads (no stored state). Built by <see cref="InsightsService"/>.
/// </summary>
public sealed record FinancialHealthReport(
    bool HasData,
    string PeriodLabel,
    int Score,
    int? ScoreDelta,
    HealthBand Band,
    string Verdict,
    string Summary,
    decimal? SavingsRate,
    decimal SavingsTarget,
    Money? SavingsShortfall,
    string SavingsCritique,
    bool TrendUp,
    string TrendNote,
    Money TrendAverage,
    decimal TrendAvgFraction,
    IReadOnlyList<Signal> Signals,
    IReadOnlyList<CategorySpend> Breakdown,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<TrendSeries> MiniTrends,
    IReadOnlyList<QuickWin> QuickWins);

/// <summary>
/// Computes the Insights tab's financial-health report for a given period. Pure presentation-layer logic over
/// the domain aggregate's public reads — it adds no domain concepts and stores nothing. The savings-rate target
/// is a fixed default (<see cref="DefaultSavingsTarget"/>) until/unless it becomes a per-account setting.
/// </summary>
public sealed class InsightsService
{
    public const decimal DefaultSavingsTarget = 0.20m;

    // A harmonious mint-family palette for the spending breakdown (cycled).
    private static readonly string[] Palette =
        ["#13a06e", "#0e7c55", "#f5a623", "#ff7a59", "#4da6ff", "#a78bfa", "#e0608a", "#36c5c0"];

    private readonly SavingsReportService _savings = new();

    // Translator (Localizer.T): English text is the key, so an absent translation falls back to English.
    private Func<string, string> _t = s => s;

    public FinancialHealthReport Build(Account account, int periodIndex, Func<Money, string> fmt, Func<string, string>? translate = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        _t = translate ?? (s => s);
        var periods = account.Periods;
        var currency = account.Currency;

        if (periodIndex < 0 || periodIndex >= periods.Count)
            return Empty(currency);

        var p = periods[periodIndex];
        var label = p.From.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        var target = account.SavingsRateTarget;

        var income = p.ContributionsPaidTotal;
        var spend = p.ExpensesTotal;
        var hasData = spend.Amount > 0 || income.Amount > 0 || p.BudgetedTotal.Amount > 0;
        if (!hasData)
            return Empty(currency) with { PeriodLabel = label, SavingsTarget = target };

        var savingsRate = _savings.PeriodSavingsRate(p);

        var breakdown = BuildBreakdown(account, periods, periodIndex);
        var trend = BuildTrend(periods, periodIndex, currency, fmt);

        var score = ComputeScore(account, periodIndex, target);
        int? delta = periodIndex > 0 ? score - ComputeScore(account, periodIndex - 1, target) : null;
        var band = BandFor(score);

        var (verdict, summary) = Narrative(band, delta);

        // Savings shortfall vs target (per period), only when below target and there's income to measure against.
        Money? shortfall = null;
        if (income.Amount > 0 && (savingsRate ?? 0m) < target)
        {
            var gap = (target - (savingsRate ?? 0m)) * income.Amount;
            if (gap > 0m) shortfall = new Money(decimal.Round(gap, 2), currency);
        }
        var savingsCritique = SavingsCritique(savingsRate, shortfall, target, fmt);

        var signals = BuildSignals(account, periods, periodIndex, savingsRate, target, fmt);
        var miniTrends = BuildMiniTrends(account, periods, periodIndex, currency, fmt);
        var wins = BuildQuickWins(account, p, savingsRate, income, target, fmt);

        return new FinancialHealthReport(
            HasData: true,
            PeriodLabel: label,
            Score: score,
            ScoreDelta: delta,
            Band: band,
            Verdict: verdict,
            Summary: summary,
            SavingsRate: savingsRate,
            SavingsTarget: target,
            SavingsShortfall: shortfall,
            SavingsCritique: savingsCritique,
            TrendUp: trend.Up,
            TrendNote: trend.Note,
            TrendAverage: trend.Average,
            TrendAvgFraction: trend.AvgFraction,
            Signals: signals,
            Breakdown: breakdown,
            Trend: trend.Points,
            MiniTrends: miniTrends,
            QuickWins: wins);
    }

    private static FinancialHealthReport Empty(string currency) => new(
        HasData: false, PeriodLabel: "", Score: 0, ScoreDelta: null, Band: HealthBand.Average,
        Verdict: "", Summary: "", SavingsRate: null, SavingsTarget: DefaultSavingsTarget,
        SavingsShortfall: null, SavingsCritique: "", TrendUp: false, TrendNote: "",
        TrendAverage: Money.Zero(currency), TrendAvgFraction: 0m,
        Signals: [], Breakdown: [], Trend: [], MiniTrends: [], QuickWins: []);

    // --- Score (0..100, four equally-weighted 25-pt components) ----------------------------------

    private int ComputeScore(Account account, int idx, decimal target)
    {
        var p = account.Periods[idx];

        // 1) Savings: blend "hit your target" with the absolute rate (0..100%) so it's not too forgiving —
        //    reaching a 20% target no longer maxes the component; high absolute rates are still rewarded.
        var rate = _savings.PeriodSavingsRate(p) ?? 0m;
        if (rate < 0m) rate = 0m;
        var towardTarget = Math.Min(1m, target <= 0m ? 1m : rate / target);
        var absolute = Math.Min(1m, rate);
        var sav = (0.6m * towardTarget + 0.4m * absolute) * 25m;

        // 2) Budget adherence (neutral when nothing is budgeted — can't assess).
        decimal adh;
        if (p.BudgetedTotal.Amount > 0m)
        {
            var over = OverspendTotal(account, p).Amount;
            var frac = Math.Clamp(over / p.BudgetedTotal.Amount, 0m, 1m);
            adh = (1m - frac) * 25m;
        }
        else adh = 15m;

        // 3) Living within means (no deficit, non-negative closing).
        decimal wm;
        if (p.Deficit.Amount <= 0m && p.ExpectedClosingBalance.Amount >= 0m)
            wm = 25m;
        else
        {
            var baseAmt = p.ContributionsPaidTotal.Amount > 0m
                ? p.ContributionsPaidTotal.Amount
                : Math.Max(1m, p.ExpensesTotal.Amount);
            var d = Math.Clamp(p.Deficit.Amount / baseAmt, 0m, 1m);
            wm = (1m - d) * 25m;
        }

        // 4) Spending trend vs trailing average (neutral when no history).
        decimal tr;
        var priorAvg = TrailingAverageOutgoings(account.Periods, idx, 3);
        if (priorAvg is not { } avg) tr = 15m;
        else
        {
            var cur = p.ExpensesTotal.Amount;
            var ratio = avg <= 0m ? (cur > 0m ? 1.5m : 0m) : cur / avg;
            tr = Math.Clamp(1.5m - ratio, 0m, 1m) * 25m;
        }

        return (int)Math.Round(sav + adh + wm + tr, MidpointRounding.AwayFromZero);
    }

    private static decimal? TrailingAverageOutgoings(IReadOnlyList<Period> periods, int idx, int count)
    {
        var start = Math.Max(0, idx - count);
        if (start >= idx) return null;
        decimal sum = 0m;
        var n = 0;
        for (var i = start; i < idx; i++) { sum += periods[i].ExpensesTotal.Amount; n++; }
        return n == 0 ? null : sum / n;
    }

    private Money OverspendTotal(Account account, Period p)
    {
        var total = Money.Zero(account.Currency);
        foreach (var b in p.Budgets)
        {
            var spent = SpentInTree(account, p, b.CategoryId);
            if (spent > b.Allocated) total += spent - b.Allocated;
        }
        return total;
    }

    private static Money SpentInTree(Account account, Period p, Guid rootCategoryId)
    {
        var ids = account.CategoryWithDescendantIds(rootCategoryId).ToHashSet();
        return p.Expenses.Where(e => ids.Contains(e.CategoryId))
            .Select(e => e.Amount)
            .Aggregate(Money.Zero(p.Currency), (acc, m) => acc + m);
    }

    private static HealthBand BandFor(int score) =>
        score >= 70 ? HealthBand.Healthy : score >= 40 ? HealthBand.Average : HealthBand.AtRisk;

    private (string Verdict, string Summary) Narrative(HealthBand band, int? delta)
    {
        var move = delta switch
        {
            > 0 => " " + string.Format(_t("You're up {0} points from last month."), delta),
            < 0 => " " + string.Format(_t("You're down {0} points from last month."), -delta),
            _ => ""
        };
        return band switch
        {
            HealthBand.Healthy => (_t("Looking healthy"),
                _t("Your habits are solid — saving steadily, spending within plan.") + move),
            HealthBand.Average => (_t("Getting there"),
                _t("Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different.") + move),
            _ => (_t("Needs attention"),
                _t("A few things need a look this period — overspending or thin savings. Small fixes add up fast.") + move),
        };
    }

    // --- Spending breakdown by root category ----------------------------------------------------

    private static IReadOnlyList<CategorySpend> BuildBreakdown(Account account, IReadOnlyList<Period> periods, int idx)
    {
        var p = periods[idx];
        var prev = idx > 0 ? periods[idx - 1] : null;

        var rows = new List<(Category Cat, Money Cur, Money Prev)>();
        foreach (var root in account.RootCategories)
        {
            var cur = SpentInTree(account, p, root.Id);
            if (cur.Amount <= 0m) continue;
            var prevAmt = prev is null ? Money.Zero(account.Currency) : SpentInTree(account, prev, root.Id);
            rows.Add((root, cur, prevAmt));
        }

        rows.Sort((a, b) => b.Cur.Amount.CompareTo(a.Cur.Amount));
        var max = rows.Count > 0 ? rows.Max(r => r.Cur.Amount) : 0m;

        var result = new List<CategorySpend>();
        for (var i = 0; i < rows.Count; i++)
        {
            var (cat, cur, prevAmt) = rows[i];
            var dir = DeltaDirection(cur.Amount, prevAmt.Amount);
            var bar = max > 0m ? cur.Amount / max : 0m;
            result.Add(new CategorySpend(cat.Name, CategoryIcons.Effective(cat), cur, bar, dir, Palette[i % Palette.Length]));
        }
        return result;
    }

    private static DeltaDir DeltaDirection(decimal cur, decimal prev)
    {
        if (prev <= 0m) return cur > 0m ? DeltaDir.Up : DeltaDir.Flat;
        var change = (cur - prev) / prev;
        return change > 0.05m ? DeltaDir.Up : change < -0.05m ? DeltaDir.Down : DeltaDir.Flat;
    }

    // --- Outgoings trend --------------------------------------------------------------------------
    // Uses each period's actual outgoings (so the current bar matches the "Spent" figure shown elsewhere),
    // and compares the current period against the average of the PRIOR periods only — an average that
    // included the current period couldn't be meaningfully "above" or "below". This mirrors how the score's
    // spending-trend component computes its trailing average (see TrailingAverageOutgoings).

    private (IReadOnlyList<TrendPoint> Points, Money Average, decimal AvgFraction, bool Up, string Note)
        BuildTrend(IReadOnlyList<Period> periods, int idx, string currency, Func<Money, string> fmt)
    {
        var start = Math.Max(0, idx - 5);
        var series = new List<(string Label, decimal Amt, bool Cur)>();
        for (var i = start; i <= idx; i++)
            series.Add((periods[i].From.ToString("MMM", CultureInfo.InvariantCulture),
                decimal.Round(periods[i].ExpensesTotal.Amount, 2), i == idx));

        var max = series.Count > 0 ? series.Max(x => x.Amt) : 0m;
        var priorCount = series.Count - 1;   // periods before the current one, in the window
        var avg = priorCount > 0 ? series.Take(priorCount).Average(x => x.Amt) : 0m;
        var avgMoney = new Money(decimal.Round(avg, 2), currency);

        var points = series.Select(x => new TrendPoint(
            x.Label, new Money(x.Amt, currency), max > 0m ? x.Amt / max : 0m, x.Cur)).ToList();
        var avgFraction = max > 0m ? avg / max : 0m;

        var current = series.Count > 0 ? series[^1].Amt : 0m;
        var diff = current - avg;
        bool up; string note;
        if (priorCount < 1)
            (up, note) = (false, _t("Not enough history yet to spot a trend."));
        else if (Math.Abs(diff) < 1m)
            (up, note) = (false, string.Format(_t("This month is right around your {0}-month average of {1}."), priorCount, fmt(avgMoney)));
        else if (diff > 0m)
            (up, note) = (true, string.Format(_t("This month is {0} above your {1}-month average of {2}."), fmt(new Money(decimal.Round(diff, 2), currency)), priorCount, fmt(avgMoney)));
        else
            (up, note) = (false, string.Format(_t("This month is {0} below your {1}-month average of {2}."), fmt(new Money(decimal.Round(-diff, 2), currency)), priorCount, fmt(avgMoney)));

        return (points, avgMoney, avgFraction, up, note);
    }

    // --- Cross-period mini-trends (#9) ----------------------------------------------------------
    // Savings rate, total debt owed, and the top category's spend, each as a short chronological series
    // for a sparkline. Direction is pre-framed as sentiment colour (Down = good/green, Up = bad/red).

    private IReadOnlyList<TrendSeries> BuildMiniTrends(
        Account account, IReadOnlyList<Period> periods, int idx, string currency, Func<Money, string> fmt)
    {
        var start = Math.Max(0, idx - 5);
        var priorK = idx - start;              // periods before the current one, in the window
        if (priorK < 1) return [];             // need at least two points to be a trend
        var result = new List<TrendSeries>();

        // 1) Savings rate per period (percentage points) — higher is better.
        var rates = new List<decimal>();
        for (var i = start; i <= idx; i++)
            rates.Add(decimal.Round(Math.Max(0m, _savings.PeriodSavingsRate(periods[i]) ?? 0m) * 100m, 1));
        {
            var cur = rates[^1];
            var avg = rates.Take(priorK).DefaultIfEmpty(0m).Average();
            var diff = cur - avg;
            var dir = diff > 0.5m ? DeltaDir.Down : diff < -0.5m ? DeltaDir.Up : DeltaDir.Flat;
            var note = Math.Abs(diff) <= 0.5m
                ? string.Format(_t("Steady around your {0}-period average of {1}%."), priorK, decimal.Round(avg, 0))
                : diff > 0m
                    ? string.Format(_t("Up {0} pts vs your {1}-period average of {2}%."), decimal.Round(diff, 0), priorK, decimal.Round(avg, 0))
                    : string.Format(_t("Down {0} pts vs your {1}-period average of {2}%."), decimal.Round(-diff, 0), priorK, decimal.Round(avg, 0));
            result.Add(new TrendSeries(_t("Savings rate"), "💰", rates, $"{decimal.Round(cur, 0)}%", note, dir));
        }

        // 2) Total debt owed per period, reconstructed from payment (disbursement) history — lower is better.
        var debts = account.SavingCategories.Where(s => s.IsDebt && s.DebtOriginalBalance > 0m).ToList();
        if (debts.Count > 0)
        {
            var owed = new List<decimal>();
            for (var i = start; i <= idx; i++)
            {
                var total = 0m;
                foreach (var b in debts)
                    total += Math.Max(0m, b.DebtOriginalBalance - DisbursedThroughPeriod(account, periods, b.Id, i));
                owed.Add(decimal.Round(total, 2));
            }
            var cur = owed[^1];
            var diff = cur - owed[0];
            var dir = diff < -0.01m ? DeltaDir.Down : diff > 0.01m ? DeltaDir.Up : DeltaDir.Flat;
            var firstLabel = periods[start].From.ToString("MMM", CultureInfo.InvariantCulture);
            var note = Math.Abs(diff) <= 0.01m
                ? _t("No change over this window.")
                : diff < 0m
                    ? string.Format(_t("Down {0} since {1}."), fmt(new Money(decimal.Round(-diff, 2), currency)), firstLabel)
                    : string.Format(_t("Up {0} since {1}."), fmt(new Money(decimal.Round(diff, 2), currency)), firstLabel);
            result.Add(new TrendSeries(_t("Debt owed"), "💳", owed, fmt(new Money(cur, currency)), note, dir));
        }

        // 3) The biggest spending category this period, tracked across the window — lower is better.
        var top = account.RootCategories
            .Select(c => (Cat: c, Amt: SpentInTree(account, periods[idx], c.Id).Amount))
            .Where(x => x.Amt > 0m)
            .OrderByDescending(x => x.Amt)
            .FirstOrDefault();
        if (top.Cat is not null)
        {
            var spend = new List<decimal>();
            for (var i = start; i <= idx; i++)
                spend.Add(decimal.Round(SpentInTree(account, periods[i], top.Cat.Id).Amount, 2));
            var cur = spend[^1];
            var avg = spend.Take(priorK).DefaultIfEmpty(0m).Average();
            var diff = cur - avg;
            var tol = 0.05m * avg;
            var dir = avg <= 0m ? DeltaDir.Flat : diff > tol ? DeltaDir.Up : diff < -tol ? DeltaDir.Down : DeltaDir.Flat;
            var note = avg <= 0m
                ? _t("First period with spend here.")
                : Math.Abs(diff) <= tol
                    ? string.Format(_t("About your {0}-period average of {1}."), priorK, fmt(new Money(decimal.Round(avg, 2), currency)))
                    : diff > 0m
                        ? string.Format(_t("Up {0} vs your {1}-period average of {2}."), fmt(new Money(decimal.Round(diff, 2), currency)), priorK, fmt(new Money(decimal.Round(avg, 2), currency)))
                        : string.Format(_t("Down {0} vs your {1}-period average of {2}."), fmt(new Money(decimal.Round(-diff, 2), currency)), priorK, fmt(new Money(decimal.Round(avg, 2), currency)));
            result.Add(new TrendSeries(top.Cat.Name, CategoryIcons.Effective(top.Cat), spend, fmt(new Money(cur, currency)), note, dir));
        }

        return result;
    }

    /// <summary>Cumulative payments (disbursements out) made to a debt bucket through <paramref name="throughIdx"/>.</summary>
    private static decimal DisbursedThroughPeriod(Account account, IReadOnlyList<Period> periods, Guid bucketId, int throughIdx)
    {
        var ids = account.SavingCategoryWithDescendantIds(bucketId).ToHashSet();
        var paid = 0m;
        for (var i = 0; i <= throughIdx; i++)
            paid += periods[i].SavingAllocations
                .Where(a => ids.Contains(a.SavingCategoryId) && a.IsDisbursement)
                .Sum(a => -a.Amount.Amount);
        return paid;
    }

    // --- Signals --------------------------------------------------------------------------------

    private IReadOnlyList<Signal> BuildSignals(
        Account account, IReadOnlyList<Period> periods, int idx,
        decimal? savingsRate, decimal target, Func<Money, string> fmt)
    {
        var p = periods[idx];
        var prev = idx > 0 ? periods[idx - 1] : null;
        var warn = new List<Signal>();
        var good = new List<Signal>();
        var info = new List<Signal>();

        // Category running materially above its usual pace (budget-aware; ignores within-budget spend and small amounts).
        var spike = TopSpikingCategory(account, periods, idx);
        if (spike is { } s)
            warn.Add(new Signal(SignalKind.Warn, string.Format(_t("{0} is running high"), s.Name),
                string.Format(_t("You've spent {0} on {1} — {2} ({3}%) above your recent average of {4}."), fmt(s.Cur), s.Name, fmt(s.Delta), s.Pct, fmt(s.Avg)),
                $"+{s.Pct}%", DeltaDir.Up));

        // (Overspent budgets are surfaced as always-visible rings in the Overview, not as a signal here.)

        // No savings set aside.
        if (p.SavingsNetTotal.Amount <= 0m)
            warn.Add(new Signal(SignalKind.Warn, _t("No savings set aside"),
                _t("You haven't moved anything into savings this period. Even a small amount keeps the habit alive."),
                "—", DeltaDir.Flat));

        // Savings rate above target.
        if (savingsRate is { } r && r >= target)
            good.Add(new Signal(SignalKind.Good, _t("Savings on track"),
                string.Format(_t("You set aside {0} of what came in — at or above your {1} goal."), Pct(r), Pct(target)),
                $"{Pct(r)}", DeltaDir.Down));

        // A category that fell vs last month.
        if (prev is not null)
        {
            var drop = TopDroppingCategory(account, p, prev);
            if (drop is { } d)
                good.Add(new Signal(SignalKind.Good, string.Format(_t("{0} spend down"), d.Name),
                    string.Format(_t("{0} vs {1} last month. Keep it up."), fmt(d.Cur), fmt(d.Prev)),
                    $"−{d.Pct}%", DeltaDir.Down));
        }

        // End-of-period runway (only for the latest, still-open period).
        if (idx == periods.Count - 1 && p.Status == PeriodStatus.Open)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (today <= p.To && today >= p.From)
            {
                var daysLeft = p.To.DayNumber - today.DayNumber;
                info.Add(new Signal(SignalKind.Info, _t("Days left in the period"),
                    string.Format(_t("You have {0} on hand with {1} days to go."), fmt(p.ExpectedClosingBalance), daysLeft),
                    string.Format(_t("{0}d left"), daysLeft), DeltaDir.Flat));
            }
        }

        // Deficit: spending beyond the fresh cash that came in. If there's a savings earmark it eats into that; with
        // nothing saved it's simply spending more than came in — don't claim a savings earmark that doesn't exist.
        if (p.Deficit.Amount > 0m)
        {
            var hasSavings = _savings.AccumulatedTotal(account).Amount > 0m;
            warn.Add(hasSavings
                ? new Signal(SignalKind.Warn, _t("Spending dipped into savings"),
                    string.Format(_t("{0} of this period's spend isn't backed by fresh cash — it leans on your savings earmark."), fmt(p.Deficit)),
                    "deficit", DeltaDir.Up)
                : new Signal(SignalKind.Warn, _t("Spending outran your income"),
                    string.Format(_t("{0} of this period's spend isn't backed by fresh cash that came in this period."), fmt(p.Deficit)),
                    "deficit", DeltaDir.Up));
        }

        // Priority: warnings first, then a positive, then info — capped at 5.
        var ordered = new List<Signal>();
        ordered.AddRange(warn);
        ordered.AddRange(good);
        ordered.AddRange(info);
        return ordered.Take(5).ToList();
    }

    /// <summary>
    /// The category most materially above its recent average — but only when the jump is "concerning":
    /// it must be over its budget (if it has one), be a meaningful share of the month's spend, and the
    /// <i>absolute</i> jump must matter (not just a big % off a tiny base). Ranked by money, not by %.
    /// </summary>
    private (string Name, Money Cur, Money Avg, Money Delta, int Pct)? TopSpikingCategory(
        Account account, IReadOnlyList<Period> periods, int idx)
    {
        if (idx == 0) return null;
        var p = periods[idx];
        var totalSpend = p.ExpensesTotal.Amount;
        if (totalSpend <= 0m) return null;

        (string Name, Money Cur, Money Avg, Money Delta, int Pct)? best = null;
        foreach (var root in account.RootCategories)
        {
            var cur = SpentInTree(account, p, root.Id).Amount;
            if (cur <= 0m) continue;

            var start = Math.Max(0, idx - 3);
            decimal sum = 0m; var n = 0;
            for (var i = start; i < idx; i++) { sum += SpentInTree(account, periods[i], root.Id).Amount; n++; }
            if (n == 0) continue;
            var avg = sum / n;
            if (avg <= 0m) continue;

            var delta = cur - avg;
            var pct = (int)Math.Round(delta / avg * 100m, MidpointRounding.AwayFromZero);

            // Filters that keep this honest:
            if (pct < 40) continue;                       // a real jump, not noise
            if (delta < 0.10m * totalSpend) continue;     // the jump is a meaningful slice of the month (not a low-base illusion)
            if (cur < 0.15m * totalSpend) continue;       // the category itself is a meaningful chunk of spending
            if (p.FindBudget(root.Id) is { } b && cur <= b.Allocated.Amount) continue; // within its plan — not concerning

            // Rank by the absolute money jump, so we surface the biggest real overspend, not the biggest percentage.
            if (best is null || delta > best.Value.Delta.Amount)
                best = (root.Name, new Money(cur, account.Currency), new Money(decimal.Round(avg, 2), account.Currency),
                    new Money(decimal.Round(delta, 2), account.Currency), pct);
        }
        return best;
    }

    private (string Name, Money Cur, Money Prev, int Pct)? TopDroppingCategory(Account account, Period p, Period prev)
    {
        (string Name, Money Cur, Money Prev, int Pct)? best = null;
        foreach (var root in account.RootCategories)
        {
            var cur = SpentInTree(account, p, root.Id).Amount;
            var pr = SpentInTree(account, prev, root.Id).Amount;
            if (pr <= 0m || cur >= pr) continue;
            var pct = (int)Math.Round((pr - cur) / pr * 100m, MidpointRounding.AwayFromZero);
            if (pct < 10) continue;
            if (best is null || pct > best.Value.Pct)
                best = (root.Name, new Money(cur, account.Currency), new Money(pr, account.Currency), pct);
        }
        return best;
    }

    // --- Quick wins -----------------------------------------------------------------------------

    private IReadOnlyList<QuickWin> BuildQuickWins(
        Account account, Period p, decimal? savingsRate, Money income, decimal target, Func<Money, string> fmt)
    {
        var wins = new List<QuickWin>();

        // Worst overspent budget.
        (Budget B, Money Over)? worst = null;
        foreach (var b in p.Budgets)
        {
            var spent = SpentInTree(account, p, b.CategoryId);
            if (spent > b.Allocated)
            {
                var over = spent - b.Allocated;
                if (worst is null || over > worst.Value.Over) worst = (b, over);
            }
        }
        if (worst is { } w)
        {
            var name = account.FindCategory(w.B.CategoryId)?.Name ?? _t("that category");
            wins.Add(new QuickWin(string.Format(_t("Rein in {0}: you're {1} over budget this month."), name, fmt(w.Over))));
        }

        // Below savings target.
        if (income.Amount > 0m && (savingsRate ?? 0m) < target)
        {
            var gap = (target - (savingsRate ?? 0m)) * income.Amount;
            if (gap > 0m)
                wins.Add(new QuickWin(string.Format(_t("Set aside {0} more to hit your {1} savings goal."), fmt(new Money(decimal.Round(gap, 2), account.Currency)), Pct(target))));
        }

        // A meaningful category with spend but no budget.
        foreach (var root in account.RootCategories)
        {
            if (p.FindBudget(root.Id) is not null) continue;
            var spent = SpentInTree(account, p, root.Id);
            if (spent.Amount > 0m)
            {
                wins.Add(new QuickWin(string.Format(_t("Give {0} a budget — you've spent {1} with no plan in place."), root.Name, fmt(spent))));
                break;
            }
        }

        return wins.Take(3).ToList();
    }

    private string SavingsCritique(decimal? rate, Money? shortfall, decimal target, Func<Money, string> fmt)
    {
        if (rate is null)
            return _t("No contributions recorded this period, so there's no savings rate to measure yet.");
        if (rate.Value >= target)
            return string.Format(_t("You saved {0} this period — at or above your {1} goal. Keep that rhythm."), Pct(rate.Value), Pct(target));
        var tail = shortfall is { } s ? " " + string.Format(_t("That's about {0} short of your goal this period."), fmt(s)) : "";
        return string.Format(_t("You saved {0} this period — better than nothing, but short of your {1} goal."), Pct(rate.Value), Pct(target)) + tail;
    }

    private static string Pct(decimal ratio) =>
        $"{decimal.Round(ratio * 100m, 0, MidpointRounding.AwayFromZero)}%";
}
