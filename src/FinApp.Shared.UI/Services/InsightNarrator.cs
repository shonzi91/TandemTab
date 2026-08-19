using System.Globalization;
using FinApp.Domain.Services;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Renders the domain's language-independent <see cref="InsightMessage"/>s (code + args) into localized strings for the
/// Blazor UI. It owns the <b>English</b> template for each <see cref="InsightCodes"/> code; the English text doubles as
/// the <see cref="Localizer"/> lookup key, so a Bulgarian (or any other) translation is picked up automatically and an
/// untranslated code falls back to English. A future native client keeps its own template table keyed by the same codes.
/// The English templates here must match the canonical strings documented on <see cref="InsightCodes"/> verbatim.
/// </summary>
public static class InsightNarrator
{
    /// <summary>Render one message: localize its template and fill the args (money via <paramref name="money"/>).</summary>
    public static string Render(InsightMessage m, Localizer loc, Func<decimal, string> money)
    {
        var template = loc.T(Template(m.Code));
        if (m.Args.Count == 0) return template;

        var parts = new object[m.Args.Count];
        for (var i = 0; i < m.Args.Count; i++) parts[i] = Arg(m.Args[i], money);

        // Orphaned-budget fallback: an empty category name in "Rein in {0}…" becomes a localized "that category".
        if (m.Code == InsightCodes.WinReinIn && parts.Length > 0 && parts[0] is "")
            parts[0] = loc.T(Template(InsightCodes.WinThatCategory));

        return string.Format(CultureInfo.InvariantCulture, template, parts);
    }

    /// <summary>Render ordered fragments (e.g. a summary + its score-movement clause) joined by a space.</summary>
    public static string Render(IReadOnlyList<InsightMessage> parts, Localizer loc, Func<decimal, string> money) =>
        string.Join(" ", parts.Select(p => Render(p, loc, money)));

    private static string Arg(InsightArg a, Func<decimal, string> money) => a.Kind switch
    {
        InsightArgKind.Money => money(a.Number),
        InsightArgKind.Percent => $"{decimal.Round(a.Number * 100m, 0, MidpointRounding.AwayFromZero)}%",
        InsightArgKind.Int => a.Number.ToString("0", CultureInfo.InvariantCulture),
        _ => a.Text ?? "",
    };

    /// <summary>The canonical English template for a code (also the <see cref="Localizer"/> key).</summary>
    private static string Template(string code) => code switch
    {
        InsightCodes.VerdictHealthy => "Looking healthy",
        InsightCodes.VerdictAverage => "Getting there",
        InsightCodes.VerdictAtRisk => "Needs attention",

        InsightCodes.SummaryHealthy => "Your habits are solid — saving steadily, spending within plan.",
        InsightCodes.SummaryAverage => "Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different.",
        InsightCodes.SummaryAtRisk => "A few things need a look this period — overspending or thin savings. Small fixes add up fast.",

        InsightCodes.MoveUp => "You're up {0} points from last month.",
        InsightCodes.MoveDown => "You're down {0} points from last month.",

        InsightCodes.SigCatHighTitle => "{0} is running high",
        InsightCodes.SigCatHighDesc => "You've spent {0} on {1} — {2} ({3}%) above your recent average of {4}.",
        // "This period" is load-bearing: the Home header's "Saved" is the standing earmark (this period AND
        // prior), so an unqualified "No savings set aside" sits directly under a four-figure Saved total and
        // reads as a flat contradiction even when both are right. The description always said "this period";
        // the strip on Home shows only the title.
        InsightCodes.SigNoSavingsTitle => "Nothing set aside this period",
        InsightCodes.SigNoSavingsDesc => "You haven't moved anything into savings this period. Even a small amount keeps the habit alive.",
        InsightCodes.SigSavingsOkTitle => "Savings on track",
        InsightCodes.SigSavingsOkDesc => "You set aside {0} of what came in — at or above your {1} goal.",
        InsightCodes.SigCatDownTitle => "{0} spend down",
        InsightCodes.SigCatDownDesc => "{0} vs {1} last month. Keep it up.",
        InsightCodes.SigDaysLeftTitle => "Days left in the period",
        InsightCodes.SigDaysLeftDesc => "You have {0} on hand with {1} days to go.",
        InsightCodes.SigDeficitSavingsTitle => "Spending dipped into savings",
        InsightCodes.SigDeficitSavingsDesc => "{0} of this period's spend isn't backed by fresh cash — it leans on your savings earmark.",
        InsightCodes.SigDeficitIncomeTitle => "Spending outran your income",
        InsightCodes.SigDeficitIncomeDesc => "{0} of this period's spend isn't backed by fresh cash that came in this period.",

        InsightCodes.BadgePctUp => "+{0}%",
        InsightCodes.BadgePctDown => "−{0}%",
        InsightCodes.BadgeValue => "{0}",
        InsightCodes.BadgeDaysLeft => "{0}d left",
        InsightCodes.BadgeDeficit => "deficit",
        InsightCodes.BadgeDash => "—",

        InsightCodes.CritNoContrib => "No contributions recorded this period, so there's no savings rate to measure yet.",
        InsightCodes.CritAsideNoIncome => "You set aside {0} this period. Nothing has come in yet, so there's no rate to measure it against.",
        InsightCodes.CritAtTarget => "You saved {0} this period — at or above your {1} goal. Keep that rhythm.",
        InsightCodes.CritNoneYet => "You haven't set anything aside this period yet — your goal is {0}.",
        InsightCodes.CritShort => "You saved {0} this period — a start, but short of your {1} goal.",
        InsightCodes.CritTailShort => "That's about {0} short of your goal this period.",

        InsightCodes.WinReinIn => "Rein in {0}: you're {1} over budget this month.",
        InsightCodes.WinSetAside => "Set aside {0} more to hit your {1} savings goal.",
        InsightCodes.WinGiveBudget => "Give {0} a budget — you've spent {1} with no plan in place.",
        InsightCodes.WinThatCategory => "that category",

        InsightCodes.TrendNone => "Not enough history yet to spot a trend.",
        InsightCodes.TrendAround => "This month is right around your {0}-month average of {1}.",
        InsightCodes.TrendAbove => "This month is {0} above your {1}-month average of {2}.",
        InsightCodes.TrendBelow => "This month is {0} below your {1}-month average of {2}.",

        InsightCodes.MtLabelSavings => "Savings rate",
        InsightCodes.MtLabelDebt => "Debt owed",
        InsightCodes.MtLabelRaw => "{0}",
        InsightCodes.MtCurPct => "{0}%",
        InsightCodes.MtCurMoney => "{0}",
        InsightCodes.MtSavSteady => "Steady around your {0}-period average of {1}%.",
        InsightCodes.MtSavUp => "Up {0} pts vs your {1}-period average of {2}%.",
        InsightCodes.MtSavDown => "Down {0} pts vs your {1}-period average of {2}%.",
        InsightCodes.MtDebtNoChange => "No change over this window.",
        InsightCodes.MtDebtDown => "Down {0} since {1}.",
        InsightCodes.MtDebtUp => "Up {0} since {1}.",
        InsightCodes.MtCatFirst => "First period with spend here.",
        InsightCodes.MtCatAbout => "About your {0}-period average of {1}.",
        InsightCodes.MtCatUp => "Up {0} vs your {1}-period average of {2}.",
        InsightCodes.MtCatDown => "Down {0} vs your {1}-period average of {2}.",

        _ => code,   // unknown code: show the code rather than throw (defensive; should never happen)
    };
}
