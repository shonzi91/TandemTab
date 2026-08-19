package com.tandemtab.app.ui

import com.tandemtab.app.data.InsightArgDto
import com.tandemtab.app.data.InsightMessageDto
import kotlin.math.roundToLong

/**
 * Renders the domain's language-independent insight messages (code + args) into English strings — the Android
 * counterpart of the web's `InsightNarrator`, keyed by the same stable `InsightCodes`. Templates match the web
 * verbatim so the two clients read identically; an unknown code falls back to showing the code (defensive).
 * BG (or any other) translations would swap this table; English is the baseline.
 */
object InsightNarrator {

    fun render(m: InsightMessageDto, money: (Double) -> String): String {
        val template = template(m.code)
        if (m.args.isEmpty()) return template
        val parts = m.args.map { arg(it, money) }.toMutableList()
        // Orphaned-budget fallback: an empty category name in "Rein in {0}…" becomes "that category".
        if (m.code == "win.rein_in" && parts.isNotEmpty() && parts[0] == "") parts[0] = template("win.that_category")
        return format(template, parts)
    }

    /** Join ordered fragments (e.g. a summary + its score-movement clause) with a space. */
    fun render(parts: List<InsightMessageDto>, money: (Double) -> String): String =
        parts.joinToString(" ") { render(it, money) }

    private fun arg(a: InsightArgDto, money: (Double) -> String): String = when (a.kind) {
        "money" -> money(a.number)
        "percent" -> "${(a.number * 100.0).roundToLong()}%"
        "int" -> a.number.roundToLong().toString()
        else -> a.text ?: ""
    }

    /** Minimal positional {0}/{1}/… substitution (the templates only use indexed placeholders). */
    private fun format(template: String, parts: List<String>): String {
        var out = template
        parts.forEachIndexed { i, p -> out = out.replace("{$i}", p) }
        return out
    }

    private fun template(code: String): String = when (code) {
        "verdict.healthy" -> "Looking healthy"
        "verdict.average" -> "Getting there"
        "verdict.at_risk" -> "Needs attention"

        "summary.healthy" -> "Your habits are solid — saving steadily, spending within plan."
        "summary.average" -> "Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different."
        "summary.at_risk" -> "A few things need a look this period — overspending or thin savings. Small fixes add up fast."

        "move.up" -> "You're up {0} points from last month."
        "move.down" -> "You're down {0} points from last month."

        "signal.cat_high.title" -> "{0} is running high"
        "signal.cat_high.desc" -> "You've spent {0} on {1} — {2} ({3}%) above your recent average of {4}."
        "signal.no_savings.title" -> "No savings set aside"
        "signal.no_savings.desc" -> "You haven't moved anything into savings this period. Even a small amount keeps the habit alive."
        "signal.savings_ok.title" -> "Savings on track"
        "signal.savings_ok.desc" -> "You set aside {0} of what came in — at or above your {1} goal."
        "signal.cat_down.title" -> "{0} spend down"
        "signal.cat_down.desc" -> "{0} vs {1} last month. Keep it up."
        "signal.days_left.title" -> "Days left in the period"
        "signal.days_left.desc" -> "You have {0} on hand with {1} days to go."
        "signal.deficit_savings.title" -> "Spending dipped into savings"
        "signal.deficit_savings.desc" -> "{0} of this period's spend isn't backed by fresh cash — it leans on your savings earmark."
        "signal.deficit_income.title" -> "Spending outran your income"
        "signal.deficit_income.desc" -> "{0} of this period's spend isn't backed by fresh cash that came in this period."

        "badge.pct_up" -> "+{0}%"
        "badge.pct_down" -> "−{0}%"
        "badge.value" -> "{0}"
        "badge.days_left" -> "{0}d left"
        "badge.deficit" -> "deficit"
        "badge.dash" -> "—"

        "critique.no_contrib" -> "No contributions recorded this period, so there's no savings rate to measure yet."
        "critique.aside_no_income" -> "You set aside {0} this period. Nothing has come in yet, so there's no rate to measure it against."
        "critique.at_target" -> "You saved {0} this period — at or above your {1} goal. Keep that rhythm."
        "critique.none_yet" -> "You haven't set anything aside this period yet — your goal is {0}."
        "critique.short" -> "You saved {0} this period — a start, but short of your {1} goal."
        "critique.tail_short" -> "That's about {0} short of your goal this period."

        "win.rein_in" -> "Rein in {0}: you're {1} over budget this month."
        "win.set_aside" -> "Set aside {0} more to hit your {1} savings goal."
        "win.give_budget" -> "Give {0} a budget — you've spent {1} with no plan in place."
        "win.that_category" -> "that category"

        "trend.none" -> "Not enough history yet to spot a trend."
        "trend.around" -> "This month is right around your {0}-month average of {1}."
        "trend.above" -> "This month is {0} above your {1}-month average of {2}."
        "trend.below" -> "This month is {0} below your {1}-month average of {2}."

        "mt.label.savings" -> "Savings rate"
        "mt.label.debt" -> "Debt owed"
        "mt.label.raw" -> "{0}"
        "mt.cur.pct" -> "{0}%"
        "mt.cur.money" -> "{0}"
        "mt.sav.steady" -> "Steady around your {0}-period average of {1}%."
        "mt.sav.up" -> "Up {0} pts vs your {1}-period average of {2}%."
        "mt.sav.down" -> "Down {0} pts vs your {1}-period average of {2}%."
        "mt.debt.no_change" -> "No change over this window."
        "mt.debt.down" -> "Down {0} since {1}."
        "mt.debt.up" -> "Up {0} since {1}."
        "mt.cat.first" -> "First period with spend here."
        "mt.cat.about" -> "About your {0}-period average of {1}."
        "mt.cat.up" -> "Up {0} vs your {1}-period average of {2}."
        "mt.cat.down" -> "Down {0} vs your {1}-period average of {2}."

        else -> code
    }
}
