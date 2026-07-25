package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * The Spending tab: this period's expenses, grouped by day, newest first — the thin `SpendingViewDto`
 * rendered directly (category/fund names resolved server-side).
 */
@Composable
fun SpendingScreen(spending: SpendingUi, onRetry: () -> Unit) {
    val tandem = LocalTandemColors.current
    val fmt = rememberMoney(spending.currency)

    when {
        spending.loading && spending.expenses.isEmpty() ->
            Box(Modifier.fillMaxWidth().height(220.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }

        spending.error != null ->
            Column(Modifier.fillMaxWidth().padding(top = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(spending.error, color = MaterialTheme.colorScheme.error)
                Spacer(Modifier.height(8.dp))
                Text("Tap to retry", color = tandem.positive, modifier = Modifier.clickable(onClick = onRetry).padding(8.dp))
            }

        else -> {
            SpentHeader(fmt(spending.spent))
            Spacer(Modifier.height(16.dp))
            if (spending.expenses.isEmpty()) {
                Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                    Text("No expenses this period yet.", color = tandem.muted)
                }
            } else {
                // Group by date, newest day first.
                val byDay = spending.expenses
                    .sortedByDescending { it.date }
                    .groupBy { it.date }
                byDay.forEach { (day, rows) ->
                    DayHeader(day)
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
                            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp)),
                    ) {
                        rows.forEachIndexed { i, e ->
                            ExpenseRow(e, fmt)
                            if (i < rows.lastIndex) {
                                Box(Modifier.fillMaxWidth().height(1.dp).padding(horizontal = 14.dp).background(tandem.hairline))
                            }
                        }
                    }
                    Spacer(Modifier.height(14.dp))
                }
            }
        }
    }
}

@Composable
private fun SpentHeader(spent: String) {
    val tandem = LocalTandemColors.current
    Column(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(18.dp),
    ) {
        Text("SPENT THIS PERIOD", fontSize = 10.sp, letterSpacing = 1.3.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Spacer(Modifier.height(4.dp))
        Text(spent, fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = tandem.spent)
    }
}

@Composable
private fun DayHeader(iso: String) {
    val tandem = LocalTandemColors.current
    Text(
        formatDay(iso),
        fontSize = 11.sp,
        fontWeight = FontWeight.Bold,
        letterSpacing = 0.8.sp,
        color = tandem.muted,
        modifier = Modifier.padding(start = 4.dp, bottom = 6.dp),
    )
}

@Composable
private fun ExpenseRow(e: ExpenseDto, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    Row(
        modifier = Modifier.fillMaxWidth().padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (!e.categoryIcon.isNullOrBlank()) {
            Text(e.categoryIcon, fontSize = 18.sp)
            Spacer(Modifier.width(10.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(e.categoryName, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
            val sub = e.note?.takeIf { it.isNotBlank() } ?: e.fundName
            Text(sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Column(horizontalAlignment = Alignment.End) {
            Text(fmt(e.amount), fontWeight = FontWeight.Bold, color = tandem.spent)
            if (e.autoFiled || e.fromSavings) {
                Text(if (e.fromSavings) "from savings" else "auto", fontSize = 10.sp, color = tandem.muted)
            }
        }
    }
}

private fun formatDay(iso: String): String = runCatching {
    val d = LocalDate.parse(iso)
    val today = LocalDate.now()
    when (d) {
        today -> "TODAY"
        today.minusDays(1) -> "YESTERDAY"
        else -> d.format(DateTimeFormatter.ofPattern("EEE, d MMM", Locale.getDefault())).uppercase()
    }
}.getOrDefault(iso)

private fun rememberMoney(currencyCode: String): (Double) -> String {
    val nf = java.text.NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = java.util.Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}
