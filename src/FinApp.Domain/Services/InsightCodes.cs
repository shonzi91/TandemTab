namespace FinApp.Domain.Services;

/// <summary>
/// The stable catalogue of <see cref="InsightMessage"/> codes the <see cref="InsightsService"/> emits. Each code names
/// one narrative template; clients map the code to a localized template string and fill it with the message's args.
/// The comment on each constant is the canonical English template (the placeholders show the expected arg order) — a
/// client's English map should match it verbatim so untranslated languages fall back cleanly.
/// </summary>
public static class InsightCodes
{
    // --- Verdict (by health band) ---------------------------------------------------------------
    public const string VerdictHealthy = "verdict.healthy";   // "Looking healthy"
    public const string VerdictAverage = "verdict.average";   // "Getting there"
    public const string VerdictAtRisk = "verdict.at_risk";    // "Needs attention"

    // --- Summary (by health band) ---------------------------------------------------------------
    public const string SummaryHealthy = "summary.healthy";   // "Your habits are solid — saving steadily, spending within plan."
    public const string SummaryAverage = "summary.average";   // "Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different."
    public const string SummaryAtRisk = "summary.at_risk";    // "A few things need a look this period — overspending or thin savings. Small fixes add up fast."

    // --- Score movement (appended to the summary) -----------------------------------------------
    public const string MoveUp = "move.up";       // "You're up {0} points from last month."  {Int delta}
    public const string MoveDown = "move.down";   // "You're down {0} points from last month." {Int delta}

    // --- Signals: titles / descriptions / delta badges ------------------------------------------
    // Category running high
    public const string SigCatHighTitle = "signal.cat_high.title";   // "{0} is running high"  {Text name}
    public const string SigCatHighDesc = "signal.cat_high.desc";     // "You've spent {0} on {1} — {2} ({3}%) above your recent average of {4}."  {Money cur, Text name, Money delta, Int pct, Money avg}
    // No savings set aside
    public const string SigNoSavingsTitle = "signal.no_savings.title"; // "No savings set aside"
    public const string SigNoSavingsDesc = "signal.no_savings.desc";   // "You haven't moved anything into savings this period. Even a small amount keeps the habit alive."
    // Savings on track
    public const string SigSavingsOkTitle = "signal.savings_ok.title"; // "Savings on track"
    public const string SigSavingsOkDesc = "signal.savings_ok.desc";   // "You set aside {0} of what came in — at or above your {1} goal."  {Percent rate, Percent target}
    // Category spend down
    public const string SigCatDownTitle = "signal.cat_down.title";   // "{0} spend down"  {Text name}
    public const string SigCatDownDesc = "signal.cat_down.desc";     // "{0} vs {1} last month. Keep it up."  {Money cur, Money prev}
    // Days left in period
    public const string SigDaysLeftTitle = "signal.days_left.title"; // "Days left in the period"
    public const string SigDaysLeftDesc = "signal.days_left.desc";   // "You have {0} on hand with {1} days to go."  {Money closing, Int days}
    // Deficit (leans on savings earmark)
    public const string SigDeficitSavingsTitle = "signal.deficit_savings.title"; // "Spending dipped into savings"
    public const string SigDeficitSavingsDesc = "signal.deficit_savings.desc";   // "{0} of this period's spend isn't backed by fresh cash — it leans on your savings earmark."  {Money deficit}
    // Deficit (outran income)
    public const string SigDeficitIncomeTitle = "signal.deficit_income.title";   // "Spending outran your income"
    public const string SigDeficitIncomeDesc = "signal.deficit_income.desc";     // "{0} of this period's spend isn't backed by fresh cash that came in this period."  {Money deficit}

    // Delta badges (the small right-aligned chip on each signal)
    public const string BadgePctUp = "badge.pct_up";       // "+{0}%"  {Int pct}
    public const string BadgePctDown = "badge.pct_down";   // "−{0}%"  {Int pct}
    public const string BadgeValue = "badge.value";        // "{0}"    {Percent rate}
    public const string BadgeDaysLeft = "badge.days_left"; // "{0}d left"  {Int days}
    public const string BadgeDeficit = "badge.deficit";    // "deficit"
    public const string BadgeDash = "badge.dash";          // "—"

    // --- Savings critique (base [+ optional shortfall tail]) ------------------------------------
    public const string CritNoContrib = "critique.no_contrib"; // "No contributions recorded this period, so there's no savings rate to measure yet."
    // ★ The savings rate is a ratio, and a ratio needs a denominator — so a period funded entirely from carried-over
    // cash has NO rate however much was set aside. Reporting that as "nothing to measure" in the savings slot reads
    // as "you saved nothing", next to a Saved card showing what they did save. Name the amount instead.
    public const string CritAsideNoIncome = "critique.aside_no_income"; // "You set aside {0} this period. Nothing has come in yet, so there's no rate to measure it against."  {Money setAside}
    public const string CritAtTarget = "critique.at_target";   // "You saved {0} this period — at or above your {1} goal. Keep that rhythm."  {Percent rate, Percent target}
    public const string CritNoneYet = "critique.none_yet";     // "You haven't set anything aside this period yet — your goal is {0}."  {Percent target}
    public const string CritShort = "critique.short";          // "You saved {0} this period — a start, but short of your {1} goal."  {Percent rate, Percent target}
    public const string CritTailShort = "critique.tail_short"; // "That's about {0} short of your goal this period."  {Money shortfall}

    // --- Quick wins -----------------------------------------------------------------------------
    public const string WinReinIn = "win.rein_in";         // "Rein in {0}: you're {1} over budget this month."  {Text name, Money over}
    public const string WinSetAside = "win.set_aside";     // "Set aside {0} more to hit your {1} savings goal."  {Money suggest, Percent target}
    public const string WinGiveBudget = "win.give_budget"; // "Give {0} a budget — you've spent {1} with no plan in place."  {Text name, Money spent}
    public const string WinThatCategory = "win.that_category"; // "that category" — fallback name for an orphaned budget

    // --- Outgoings trend note -------------------------------------------------------------------
    public const string TrendNone = "trend.none";   // "Not enough history yet to spot a trend."
    public const string TrendAround = "trend.around"; // "This month is right around your {0}-month average of {1}."  {Int months, Money avg}
    public const string TrendAbove = "trend.above";  // "This month is {0} above your {1}-month average of {2}."  {Money diff, Int months, Money avg}
    public const string TrendBelow = "trend.below";  // "This month is {0} below your {1}-month average of {2}."  {Money diff, Int months, Money avg}

    // --- Mini-trends (#9): labels, current value, delta note ------------------------------------
    public const string MtLabelSavings = "mt.label.savings"; // "Savings rate"
    public const string MtLabelDebt = "mt.label.debt";       // "Debt owed"
    public const string MtLabelRaw = "mt.label.raw";         // "{0}"  {Text categoryName}
    public const string MtCurPct = "mt.cur.pct";             // "{0}%"  {Int wholePercent}
    public const string MtCurMoney = "mt.cur.money";         // "{0}"   {Money amount}
    // Savings-rate series notes
    public const string MtSavSteady = "mt.sav.steady"; // "Steady around your {0}-period average of {1}%."  {Int periods, Int avgPct}
    public const string MtSavUp = "mt.sav.up";         // "Up {0} pts vs your {1}-period average of {2}%."  {Int pts, Int periods, Int avgPct}
    public const string MtSavDown = "mt.sav.down";     // "Down {0} pts vs your {1}-period average of {2}%."  {Int pts, Int periods, Int avgPct}
    // Debt-owed series notes
    public const string MtDebtNoChange = "mt.debt.no_change"; // "No change over this window."
    public const string MtDebtDown = "mt.debt.down";          // "Down {0} since {1}."  {Money amount, Text monthLabel}
    public const string MtDebtUp = "mt.debt.up";              // "Up {0} since {1}."  {Money amount, Text monthLabel}
    // Top-category series notes
    public const string MtCatFirst = "mt.cat.first";  // "First period with spend here."
    public const string MtCatAbout = "mt.cat.about";  // "About your {0}-period average of {1}."  {Int periods, Money avg}
    public const string MtCatUp = "mt.cat.up";        // "Up {0} vs your {1}-period average of {2}."  {Money diff, Int periods, Money avg}
    public const string MtCatDown = "mt.cat.down";    // "Down {0} vs your {1}-period average of {2}."  {Money diff, Int periods, Money avg}
}
