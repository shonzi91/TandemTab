package com.tandemtab.app.ui

import androidx.compose.animation.AnimatedVisibility
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.BudgetRowDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

private enum class SpendView { Categories, ByDate }

/**
 * The Spending tab: a Categories view (each category as a spent-vs-budget progress-bar row, expandable to its
 * expenses — mirroring the web) and a By-date ledger. Figures resolved server-side (/spending + /budgets).
 */
@Composable
fun SpendingScreen(spending: SpendingUi, onRetry: () -> Unit) {
    val tandem = LocalTandemColors.current
    val fmt = rememberMoney(spending.currency)
    var view by remember { mutableStateOf(SpendView.Categories) }

    when {
        spending.loading && spending.expenses.isEmpty() && spending.budgets.isEmpty() ->
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
            ViewToggle(view) { view = it }
            Spacer(Modifier.height(14.dp))
            when (view) {
                SpendView.Categories -> CategoriesView(spending, fmt)
                SpendView.ByDate -> ByDateView(spending, fmt)
            }
        }
    }
}

@Composable
private fun ViewToggle(view: SpendView, onSelect: (SpendView) -> Unit) {
    val tandem = LocalTandemColors.current
    Row(Modifier.fillMaxWidth().background(tandem.segmentTrack, RoundedCornerShape(12.dp)).padding(3.dp)) {
        listOf(SpendView.Categories to "Categories", SpendView.ByDate to "By date").forEach { (v, label) ->
            val selected = view == v
            Box(
                Modifier.weight(1f)
                    .background(if (selected) MaterialTheme.colorScheme.surface else androidx.compose.ui.graphics.Color.Transparent, RoundedCornerShape(9.dp))
                    .clickable { onSelect(v) }
                    .padding(vertical = 9.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(label, color = if (selected) MaterialTheme.colorScheme.primary else tandem.muted, fontWeight = if (selected) FontWeight.Bold else FontWeight.SemiBold, fontSize = 14.sp)
            }
        }
    }
}

// --- Categories view --------------------------------------------------------------------------------

@Composable
private fun CategoriesView(spending: SpendingUi, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    val budgetedIds = remember(spending.budgets) { spending.budgets.map { it.categoryId }.toSet() }
    val catParent = remember(spending.categories) { spending.categories.associate { it.id to it.parentId } }

    fun expensesFor(catId: String): List<ExpenseDto> =
        spending.expenses.filter { it.categoryId == catId || catParent[it.categoryId] == catId }

    // Top-level categories with spend but no budget row — the "other spending" list.
    val unbudgeted = remember(spending.expenses, spending.budgets, spending.categories) {
        spending.categories
            .filter { it.parentId == null && it.id !in budgetedIds }
            .mapNotNull { c ->
                val spent = expensesFor(c.id).sumOf { it.amount }
                if (spent > 0.0) Triple(c.id, c.name to c.icon, spent) else null
            }
            .sortedByDescending { it.third }
    }

    // Budget coverage summary.
    if (spending.budgets.isNotEmpty()) {
        SummaryHeader("BUDGET USED", fmt(spending.totalSpent), " of ${fmt(spending.totalBudgeted)}",
            fraction = if (spending.totalBudgeted > 0) (spending.totalSpent / spending.totalBudgeted).toFloat() else 0f)
        Spacer(Modifier.height(14.dp))
    } else {
        SpentHeader(fmt(spending.spent))
        Spacer(Modifier.height(14.dp))
    }

    if (spending.budgets.isEmpty() && unbudgeted.isEmpty()) {
        Box(Modifier.fillMaxWidth().height(140.dp), contentAlignment = Alignment.Center) {
            Text("No spending this period yet.", color = tandem.muted)
        }
        return
    }

    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        spending.budgets.sortedByDescending { it.spent }.forEach { b ->
            BudgetRow(b, fmt, expenses = { expensesFor(b.categoryId) })
        }
    }

    if (unbudgeted.isNotEmpty()) {
        Spacer(Modifier.height(18.dp))
        Text("OTHER SPENDING", fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = tandem.muted, modifier = Modifier.padding(start = 4.dp, bottom = 8.dp))
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            unbudgeted.forEach { (id, nameIcon, spent) ->
                UnbudgetedRow(nameIcon.first, nameIcon.second, spent, fmt, expenses = { expensesFor(id) })
            }
        }
    }
}

@Composable
private fun BudgetRow(b: BudgetRowDto, fmt: (Double) -> String, expenses: () -> List<ExpenseDto>) {
    val tandem = LocalTandemColors.current
    var open by remember { mutableStateOf(false) }
    val fraction = if (b.allocated > 0) (b.spent / b.allocated).toFloat().coerceIn(0f, 1f) else if (b.spent > 0) 1f else 0f
    val barColor = if (b.over) tandem.spent else tandem.positive

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable { open = !open }
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!b.icon.isNullOrBlank()) { Text(b.icon, fontSize = 18.sp); Spacer(Modifier.width(8.dp)) }
            Text(b.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
            Text("${fmt(b.spent)} / ${fmt(b.allocated)}", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
        }
        SpendBar(fraction, barColor)
        Text(
            if (b.over) "${fmt(-b.remaining)} over budget" else "${fmt(b.remaining)} left",
            fontSize = 12.sp, color = if (b.over) tandem.spent else tandem.muted,
        )
        ExpenseDrawer(open, expenses, fmt)
    }
}

@Composable
private fun UnbudgetedRow(name: String, icon: String?, spent: Double, fmt: (Double) -> String, expenses: () -> List<ExpenseDto>) {
    val tandem = LocalTandemColors.current
    var open by remember { mutableStateOf(false) }
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable { open = !open }
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!icon.isNullOrBlank()) { Text(icon, fontSize = 18.sp); Spacer(Modifier.width(8.dp)) }
            Text(name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
            Text(fmt(spent), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = tandem.spent)
        }
        Text("No budget set", fontSize = 12.sp, color = tandem.muted)
        ExpenseDrawer(open, expenses, fmt)
    }
}

@Composable
private fun ExpenseDrawer(open: Boolean, expenses: () -> List<ExpenseDto>, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    AnimatedVisibility(open) {
        Column(Modifier.padding(top = 4.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            val rows = expenses().sortedByDescending { it.date }
            if (rows.isEmpty()) {
                Text("No expenses yet.", fontSize = 12.sp, color = tandem.muted)
            } else rows.forEach { e ->
                Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(e.note?.takeIf { it.isNotBlank() } ?: e.categoryName, fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                        Text("${shortDate(e.date)} · ${e.fundName}", fontSize = 11.sp, color = tandem.muted, maxLines = 1)
                    }
                    Text(fmt(e.amount), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = tandem.spent)
                }
            }
        }
    }
}

@Composable
private fun SpendBar(fraction: Float, color: androidx.compose.ui.graphics.Color) {
    val tandem = LocalTandemColors.current
    Box(Modifier.fillMaxWidth().height(8.dp).background(tandem.segmentTrack, RoundedCornerShape(4.dp))) {
        Box(Modifier.fillMaxWidth(fraction).height(8.dp).background(color, RoundedCornerShape(4.dp)))
    }
}

@Composable
private fun SummaryHeader(label: String, value: String, suffix: String, fraction: Float) {
    val tandem = LocalTandemColors.current
    val over = fraction >= 1f
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(label, fontSize = 10.sp, letterSpacing = 1.3.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Row(verticalAlignment = Alignment.Bottom) {
            Text(value, fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = tandem.spent)
            Text(suffix, fontSize = 14.sp, color = tandem.muted, modifier = Modifier.padding(bottom = 3.dp))
        }
        SpendBar(fraction.coerceIn(0f, 1f), if (over) tandem.spent else tandem.positive)
    }
}

// --- By-date view -----------------------------------------------------------------------------------

@Composable
private fun ByDateView(spending: SpendingUi, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    SpentHeader(fmt(spending.spent))
    Spacer(Modifier.height(16.dp))
    if (spending.expenses.isEmpty()) {
        Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
            Text("No expenses this period yet.", color = tandem.muted)
        }
        return
    }
    val byDay = spending.expenses.sortedByDescending { it.date }.groupBy { it.date }
    byDay.forEach { (day, rows) ->
        DayHeader(day)
        Column(
            Modifier.fillMaxWidth()
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

@Composable
private fun SpentHeader(spent: String) {
    val tandem = LocalTandemColors.current
    Column(
        Modifier.fillMaxWidth()
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
    Text(formatDay(iso), fontSize = 11.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.8.sp, color = tandem.muted, modifier = Modifier.padding(start = 4.dp, bottom = 6.dp))
}

@Composable
private fun ExpenseRow(e: ExpenseDto, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    Row(modifier = Modifier.fillMaxWidth().padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
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

private fun shortDate(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))
}.getOrDefault(iso)

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
