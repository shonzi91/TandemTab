package com.tandemtab.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.DebtPayoffDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * What this loan's future looks like: when it ends, what the interest costs, what paying the set-aside money now
 * would change, and what a bank might offer instead.
 *
 * ★ **Every figure here is computed on the server** ([DebtPayoffDto]). The phone holds no amortisation and should
 * not: a payoff date is the kind of number that looks entirely plausible when it is wrong, and a second
 * implementation of compound interest in Kotlin would drift from the tested one without anybody noticing.
 *
 * ⚠️ The "extra per month" slider **snaps to the server's precomputed points** rather than interpolating between
 * them. Interpolating would invent months-saved figures nothing computed — which is the same mistake as doing the
 * maths locally, just with extra steps to hide it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PayoffSheet(
    bucketName: String,
    payoff: DebtPayoffDto?,
    loading: Boolean,
    onDismiss: () -> Unit,
    onProBlocked: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val money = rememberWalletsMoney(payoff?.currency.orEmpty())

    SheetScaffold(
        title = bucketName,
        saving = false,
        canSave = false,
        onDismiss = onDismiss,
        onSave = {},
        sheetState = sheetState,
        saveLabel = "",
    ) {
        if (loading || payoff == null) {
            Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }
            return@SheetScaffold
        }

        if (!payoff.available) {
            // A payment-driven loan has no schedule, and an installment that cannot out-run the interest never
            // clears. Both are real states, and both must read as "we can't say" rather than as a blank screen.
            Text(
                "This loan has no repayment schedule to project — either payments are logged as they happen, or " +
                    "the installment doesn't cover the interest. Set a monthly installment on the bucket to see a " +
                    "payoff date.",
                fontSize = 13.sp, color = tandem.muted,
            )
            return@SheetScaffold
        }

        val ends = payoff.payoffOn?.let {
            runCatching { LocalDate.parse(it).format(DateTimeFormatter.ofPattern("LLLL yyyy", Locale.getDefault())) }
                .getOrNull()
        }
        FigureLine("Owed now", money(payoff.balance), strong = true)
        FigureLine("Paying", "${money(payoff.installment)} / month")
        if (ends != null) FigureLine("Clear in", "$ends · ${payoff.months} months")
        FigureLine("Interest still to pay", money(payoff.totalInterest))

        // ── The one-off ─────────────────────────────────────────────────────────────────────────
        if (payoff.setAside > 0.0) {
            Spacer(Modifier.height(16.dp))
            SectionHeading("Pay the ${money(payoff.setAside)} you've set aside")
            Text(
                "A payment lowers the balance right away, so the interest you're charged drops from then on. " +
                    "This is one payment out of money you already have — not a new monthly commitment.",
                fontSize = 12.sp, color = tandem.muted,
            )
            Spacer(Modifier.height(8.dp))
            if (payoff.lumpClearsTheLoan) {
                FigureLine("That clears it", "The loan is gone", strong = true)
            } else {
                FigureLine("Left owing", money(payoff.lumpBalanceAfter))
                FigureLine("Finishes sooner by", "${payoff.lumpMonthsSaved} months")
                FigureLine("Interest saved", money(payoff.lumpInterestSaved), good = true)
            }
        }

        // ── What the bank might offer ───────────────────────────────────────────────────────────
        Spacer(Modifier.height(16.dp))
        SectionHeading("What your bank might offer")
        if (payoff.offers.isEmpty() && payoff.curve.isEmpty()) {
            // Pro-locked, or nothing set aside to model a lump against. The crown only claims the former.
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable(onClick = onProBlocked)
                    .padding(vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(TandemIcons.Crown, contentDescription = "Part of Pro", tint = tandem.warn, modifier = Modifier.size(14.dp))
                Spacer(Modifier.width(8.dp))
                Text(
                    "Compare what your bank might offer — with Pro",
                    fontSize = 13.sp,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.weight(1f),
                )
                Icon(TandemIcons.Chevron, contentDescription = null, tint = tandem.muted, modifier = Modifier.size(14.dp))
            }
        } else {
            if (payoff.offers.isEmpty()) {
                Text(
                    "Set money aside against this loan and we'll model what paying it early would let you " +
                        "renegotiate.",
                    fontSize = 12.sp, color = tandem.muted,
                )
            }
            payoff.offers.forEach { o ->
                Spacer(Modifier.height(8.dp))
                Text(
                    if (o.kind == "shorter") "Shorter term — same monthly payment" else "Lower payment — same end date",
                    fontSize = 13.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface,
                )
                FigureLine("Per month", money(o.perMonth))
                FigureLine("Runs for", "${o.months} months")
                FigureLine("Interest", money(o.newInterest))
                FigureLine("Saves you", money(o.savedInterest), good = true)
            }

            // ── The slider ──────────────────────────────────────────────────────────────────────
            if (payoff.curve.isNotEmpty()) {
                Spacer(Modifier.height(16.dp))
                SectionHeading("If you paid a bit more each month")
                var step by remember(payoff.curve) { mutableStateOf(0) }
                val point = payoff.curve[step.coerceIn(0, payoff.curve.lastIndex)]
                Slider(
                    value = step.toFloat(),
                    onValueChange = { step = it.toInt() },
                    // One stop per server-computed point, so the handle can only land on a figure that was
                    // actually calculated. `steps` counts the gaps BETWEEN stops, hence the -2.
                    valueRange = 0f..payoff.curve.lastIndex.toFloat(),
                    steps = (payoff.curve.size - 2).coerceAtLeast(0),
                )
                FigureLine("Extra each month", money(point.extra), strong = true)
                FigureLine("Finishes sooner by", "${point.monthsSaved} months")
                FigureLine("Interest saved", money(point.interestSaved), good = true)
            }
        }
        Spacer(Modifier.height(8.dp))
    }
}

@Composable
private fun SectionHeading(text: String) {
    Text(
        text,
        fontSize = 14.sp,
        fontWeight = FontWeight.Bold,
        color = MaterialTheme.colorScheme.onSurface,
        modifier = Modifier.padding(bottom = 4.dp),
    )
}

/** One label/value row. [good] marks a figure that is money kept rather than money owed. */
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
