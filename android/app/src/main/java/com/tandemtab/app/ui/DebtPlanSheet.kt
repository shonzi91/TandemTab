package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.DebtPlanDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * The whole-stack payoff plan: one spare amount thrown at every debt each month, with each cleared debt's
 * installment rolling onto the next.
 *
 * ★ **Every figure is the server's** ([DebtPlanDto]) — including the clearing order, which is why the strategy
 * chips and the extra-amount chips re-fetch instead of re-sorting a list here. A debt-free date computed twice is
 * a debt-free date that will eventually be computed two ways.
 *
 * ★ **Two answers, kept apart on purpose.** The plan ("if you put £X spare at this") sits above the fold; the
 * forecast ("at the pace you've actually kept up") sits below it with its own wording. They are different
 * questions, and showing one under the other's heading would turn a hypothetical into a promise.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun DebtPlanSheet(
    plan: DebtPlanDto?,
    loading: Boolean,
    extra: Double,
    strategy: String,
    onSet: (Double?, String?) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val money = moneyFormatter(plan?.currency.orEmpty())

    SheetScaffold(
        title = "Payoff plan",
        saving = false,
        canSave = false,
        onDismiss = onDismiss,
        onSave = {},
        sheetState = sheetState,
        saveLabel = "",
    ) {
        if (plan == null) {
            Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }
            return@SheetScaffold
        }

        Text(
            "One spare amount goes at your debts each month, on top of every installment. As each one clears, " +
                "its installment rolls onto the next.",
            fontSize = 12.sp, color = tandem.muted,
        )
        Spacer(Modifier.height(14.dp))

        // ── The two things this screen exists to change ──────────────────────────────────────────
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            PickChip("Avalanche", null, selected = plan.strategy == "avalanche") { onSet(null, "avalanche") }
            PickChip("Snowball", null, selected = plan.strategy == "snowball") { onSet(null, "snowball") }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            if (plan.strategy == "avalanche") "Attacking the highest-rate debt first — least interest overall."
            else "Attacking the smallest balance first — a quicker first win.",
            fontSize = 12.sp, color = tandem.muted,
        )

        Spacer(Modifier.height(14.dp))
        FieldLabel("Extra each month, across all debts")
        // ⚠️ Chips rather than a free field or a drag-slider, and the reason is the round trip: every change
        // re-runs an amortisation per debt on the server, so a control that fires on every keystroke or every
        // drag pixel would be a request storm. The ladder SCALES with what is being paid already — the same rule
        // the per-bucket payoff curve uses, so a step means the same thing on a card and on a mortgage.
        // ⚠️ ROUNDED UP TO A HUMAN NUMBER, and this is not cosmetic. A tenth of €450 of installments is €39, and a
        // row of chips reading "+39 +78 +156 +390" is a set of amounts nobody would ever choose — it looks like
        // the output of a formula, which is exactly what it was. Nudging to the next 25 gives +50 +100 +200 +500.
        val symbol = currencySymbol(plan.currency)
        val step = Math.ceil(maxOf(25.0, plan.totalInstallments / 10.0) / 25.0) * 25.0
        FlowRow(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            listOf(0.0, step, step * 2, step * 4, step * 10).forEach { amount ->
                PickChip(
                    // The symbol is not decoration either: "+50" beside a euro balance is a figure with no unit,
                    // and this is the one control on the screen that states an amount rather than showing one.
                    label = if (amount == 0.0) "None" else "+$symbol" + trimAmount(amount),
                    icon = null,
                    selected = kotlin.math.abs(extra - amount) < 0.005,
                ) { onSet(amount, null) }
            }
        }

        Spacer(Modifier.height(16.dp))
        if (loading) {
            Box(Modifier.fillMaxWidth().height(60.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }
        } else if (!plan.available) {
            // The honest "we can't say". Installments that cannot out-run the interest never clear the stack, and
            // a date fifty years out would be a fiction rather than a forecast.
            Text(
                "At these installments the stack never clears — the minimums don't cover the interest. " +
                    "Add an extra amount above to see a date.",
                fontSize = 13.sp, color = tandem.warn,
            )
            Spacer(Modifier.height(10.dp))
            FigureLine("Owed altogether", money(plan.totalOwed), strong = true)
            FigureLine("Installments", money(plan.totalInstallments) + " / month")
        } else {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(TandemIcons.Flag, null, tint = tandem.positive, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text(
                    "Debt-free " + monthOf(plan.debtFreeOn),
                    fontSize = 19.sp, fontWeight = FontWeight.ExtraBold,
                    color = MaterialTheme.colorScheme.onBackground,
                )
            }
            Text(monthsText(plan.months) + " · total interest " + money(plan.totalInterest),
                fontSize = 12.sp, color = tandem.muted)

            if (plan.monthsSaved > 0 || plan.interestSaved > 0.0) {
                Spacer(Modifier.height(8.dp))
                Text(
                    "That extra clears you ${monthsText(plan.monthsSaved)} sooner and saves " +
                        "${money(plan.interestSaved)} in interest.",
                    fontSize = 12.sp, color = tandem.positive,
                )
            }

            Spacer(Modifier.height(16.dp))
            SectionTitle("The order")
            plan.order.forEachIndexed { i, o ->
                Row(Modifier.fillMaxWidth().padding(vertical = 5.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        Modifier.size(22.dp).clip(RoundedCornerShape(999.dp))
                            .background(if (i == 0) tandem.positive else MaterialTheme.colorScheme.surface)
                            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(999.dp)),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            "${i + 1}", fontSize = 11.sp, fontWeight = FontWeight.Bold,
                            color = if (i == 0) MaterialTheme.colorScheme.onPrimary else tandem.muted,
                        )
                    }
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text(o.name, fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.onSurface)
                        Text(
                            money(o.balance) + " at " + maskPct(trimAmount(o.annualRatePercent) + "%"),
                            fontSize = 11.sp, color = tandem.muted,
                        )
                    }
                    Text("clear " + monthOf(o.clearedOn), fontSize = 12.sp, color = tandem.muted)
                }
            }
        }

        // ── The other question, and it is deliberately not folded into the figures above ─────────
        if (plan.paceMonths != null) {
            Spacer(Modifier.height(18.dp))
            SectionTitle("If nothing changes")
            // ⚠️ This date is often LATER than the plan's even with no extra, and the reason must be on the screen
            // or the two figures look like a contradiction: the plan rolls each cleared debt's installment onto
            // the next one, and this doesn't. It is each debt running its own installment out, on its own, plus
            // whatever you have actually been setting aside — which is what happens if the freed-up money simply
            // gets spent. Found by reading both against one account, not by reasoning about either alone.
            Text(
                "Each debt running its own installment out, plus what you've actually been setting aside. " +
                    "Unlike the plan above, it doesn't assume a cleared debt's payment rolls onto the next one.",
                fontSize = 12.sp, color = tandem.muted,
            )
            Spacer(Modifier.height(6.dp))
            FigureLine("Debt-free", monthOf(plan.paceDebtFreeOn) + " · " + monthsText(plan.paceMonths), strong = true)
            if (plan.paceInterestSaved > 0.0) {
                FigureLine("Interest that pace saves", money(plan.paceInterestSaved), good = true)
            }
        }
        Spacer(Modifier.height(8.dp))
    }
}

/**
 * The Goals-tab card that opens the plan. Shown only with **two or more** debts — with one, the plan IS that
 * loan's own payoff and the per-bucket drawer already answers it better. The server states `debtCount` rather
 * than applying this rule, so each client keeps its own threshold.
 */
@Composable
fun DebtPlanCard(plan: DebtPlanDto?, debtCount: Int, onOpen: () -> Unit) {
    if (debtCount < 2) return
    val tandem = LocalTandemColors.current
    val money = moneyFormatter(plan?.currency.orEmpty())
    Column(
        Modifier.fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(MaterialTheme.colorScheme.surface)
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable(onClick = onOpen)
            .padding(14.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(TandemIcons.Target, null, tint = tandem.positive, modifier = Modifier.size(17.dp))
            Spacer(Modifier.width(8.dp))
            Text("Payoff plan", fontSize = 15.sp, fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
            OpenChip()
        }
        Spacer(Modifier.height(6.dp))
        Text(
            // ⚠️ The card shows the PACE date, not the plan's. The plan's depends on an extra amount nobody has
            // chosen until the sheet is open, so putting it here would announce a date for a decision the reader
            // has not made. The pace date is a fact about what they are already doing.
            when {
                plan == null -> "$debtCount debts — see when they clear"
                plan.paceMonths != null -> "Debt-free " + monthOf(plan.paceDebtFreeOn) + " at your current pace"
                else -> money(plan.totalOwed) + " owed across $debtCount debts"
            },
            fontSize = 12.sp, color = tandem.muted,
        )
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(
        text, fontSize = 14.sp, fontWeight = FontWeight.Bold,
        color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(bottom = 4.dp),
    )
}

@Composable
private fun FigureLine(label: String, value: String, strong: Boolean = false, good: Boolean = false) {
    val tandem = LocalTandemColors.current
    Row(Modifier.fillMaxWidth().padding(vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, fontSize = 13.sp, color = tandem.muted, modifier = Modifier.weight(1f))
        Text(
            value,
            fontSize = if (strong) 15.sp else 13.sp,
            fontWeight = if (strong) FontWeight.ExtraBold else FontWeight.SemiBold,
            color = if (good) tandem.saved else MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** "2029-04-01" → "Apr 2029". An em-dash when the server had no date to give, which is a real answer here. */
private fun monthOf(iso: String?): String = iso?.let {
    runCatching { LocalDate.parse(it).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault())) }
        .getOrNull()
} ?: "—"

/** Mirrors the web's MonthsText: years and months once it runs past a year, because "63mo" is not a duration
 *  anybody feels. */
private fun monthsText(months: Int): String = when {
    months <= 0 -> "0mo"
    months >= 12 -> "${months / 12}y ${months % 12}mo"
    else -> "${months}mo"
}
