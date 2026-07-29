package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.RunwayDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.round

/**
 * The runway "Details" screen — the web's "show the math" + what-if, ported. Shows the monthly arithmetic behind the
 * Home runway card (starting balance, money in/out, net) and a spending what-if slider that re-runs the projection
 * client-side (the RunwayDto carries everything the forecast needs, so no round-trip).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RunwaySheet(runway: RunwayDto, fmt: (Double) -> String, onDismiss: () -> Unit) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var whatIfPct by remember { mutableFloatStateOf(0f) }

    val inBase = runway.monthlyIncome
    val outBase = runway.monthlySpending
    val outAdj = (outBase * (1 + whatIfPct / 100.0)).coerceAtLeast(0.0)
    val net = inBase - outAdj
    val source = when {
        runway.basedOnRecurring -> "your recurring income"
        runway.completedPeriodCount <= 1 -> "an average of your last month"
        else -> "an average of your last ${runway.completedPeriodCount} months"
    }
    val (shortfallIdx, ending) = project(runway.openingBalance, inBase, outAdj, runway.months)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Box(Modifier.fillMaxWidth().fillMaxHeight()) {
            Column(
                Modifier.fillMaxWidth().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()).padding(bottom = 92.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text("Your runway", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp))

                // The month-by-month arithmetic.
                Column(
                    Modifier.fillMaxWidth()
                        .background(tandem.hero, RoundedCornerShape(14.dp))
                        .padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    MathRow("Starting balance", fmt(runway.openingBalance), null)
                    MathRow("Money in / month", "+ ${fmt(inBase)}", source, tandem.positive)
                    MathRow("Money out / month", "− ${fmt(outAdj)}", if (whatIfPct != 0f) "${if (whatIfPct > 0) "+" else ""}${whatIfPct.toInt()}%" else null, tandem.spent)
                    Box(Modifier.fillMaxWidth().height(1.dp).background(tandem.hairline))
                    MathRow("Net / month", (if (net < 0) "− " else "+ ") + fmt(kotlin.math.abs(net)), null, if (net < 0) tandem.spent else tandem.positive, bold = true)
                }
                Text(
                    "Each month adds the money in and takes the money out; the runway is how long your balance stays above zero. No AI, no guesswork — just this arithmetic on your own numbers.",
                    color = tandem.muted, fontSize = 12.sp,
                )
                if (runway.monthlyCommitted > 0) {
                    Text("You also set aside ${fmt(runway.monthlyCommitted)}/month into savings — reserved, not spent, so it stays in the balance above.", color = tandem.muted, fontSize = 12.sp)
                }

                // What-if: model spending differently and see the outcome move.
                Spacer(Modifier.height(4.dp))
                Text("What if I spent differently?", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp)
                Slider(value = whatIfPct, onValueChange = { whatIfPct = round(it / 5f) * 5f }, valueRange = -50f..50f)
                val outcome = when {
                    shortfallIdx != null -> "Runs short in ${monthAt(runway.fromMonth, shortfallIdx)}"
                    else -> "Balance holds ~${runway.months} months, ending near ${fmt(ending)}"
                }
                Text(
                    outcome,
                    color = if (shortfallIdx != null) tandem.spent else tandem.positive,
                    fontWeight = FontWeight.Bold,
                    fontSize = 14.sp,
                )
            }
            Row(
                Modifier.align(Alignment.BottomCenter).fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surface).navigationBarsPadding()
                    .padding(horizontal = 18.dp, vertical = 12.dp),
            ) {
                Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Done") }
            }
        }
    }
}

@Composable
private fun MathRow(label: String, value: String, sub: String?, valueColor: androidx.compose.ui.graphics.Color? = null, bold: Boolean = false) {
    val tandem = LocalTandemColors.current
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(label, color = MaterialTheme.colorScheme.onSurface, fontWeight = if (bold) FontWeight.Bold else FontWeight.Medium, fontSize = 14.sp)
            sub?.let { Text(it, color = tandem.muted, fontSize = 11.sp) }
        }
        Text(value, color = valueColor ?: MaterialTheme.colorScheme.onSurface, fontWeight = FontWeight.Bold, fontSize = 15.sp)
    }
}

/** Walk the balance forward like the server's CashFlowForecast.Project: returns (first shortfall month index, ending balance). */
private fun project(opening: Double, income: Double, spending: Double, months: Int): Pair<Int?, Double> {
    var bal = opening
    var shortfall: Int? = null
    val horizon = months.coerceIn(1, 24)
    for (i in 0 until horizon) {
        bal = round((bal + income - spending) * 100.0) / 100.0
        if (shortfall == null && bal < 0.0) shortfall = i
    }
    return shortfall to bal
}

private fun monthAt(fromIso: String, monthsAhead: Int): String = runCatching {
    LocalDate.parse(fromIso).plusMonths(monthsAhead.toLong()).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault()))
}.getOrDefault("")
