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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.RecurringUi
import com.tandemtab.app.data.RecurringRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/** Sort: due first, then upcoming, then the rest; paused (inactive) sink to the bottom. */
private fun ordered(items: List<RecurringRowDto>): List<RecurringRowDto> =
    items.sortedWith(compareByDescending<RecurringRowDto> { it.active && it.due }
        .thenByDescending { it.active && it.upcoming }
        .thenBy { it.daysUntilDue })

/** The Home "Bills & income" card — a compact due/upcoming summary that opens the full sheet. Hidden when there
 *  are no recurring items at all. */
@Composable
fun RecurringCard(recurring: RecurringUi, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    if (!recurring.loaded || recurring.items.isEmpty()) return
    val active = recurring.items.filter { it.active }
    val due = active.count { it.due }
    val upcoming = active.count { it.upcoming && !it.due }

    Column(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .clickable(onClick = onOpen)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("BILLS & INCOME", fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = tandem.muted, modifier = Modifier.weight(1f))
            Text("Manage ›", color = MaterialTheme.colorScheme.primary, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
        }
        val summary = when {
            due > 0 -> "$due due now" + (if (upcoming > 0) " · $upcoming upcoming" else "")
            upcoming > 0 -> "$upcoming upcoming"
            else -> "All bills handled for now"
        }
        Text(summary, fontWeight = FontWeight.Bold, color = if (due > 0) tandem.spent else MaterialTheme.colorScheme.onSurface, fontSize = 16.sp)
    }
}

/** The Recurring sheet: every bill/income expectation with its due state; due items can be confirmed or skipped. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RecurringSheet(
    recurring: RecurringUi,
    onConfirm: (id: String, amount: Double) -> Unit,
    onSkip: (id: String) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val money = sheetMoney(recurring.currency)
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier
                .fillMaxWidth()
                .imePadding()
                .padding(horizontal = 18.dp)
                .verticalScroll(rememberScrollState())
                .padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("Bills & income", modifier = Modifier.weight(1f), fontSize = 18.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
                IconButton(onClick = onDismiss) { Icon(TandemIcons.Close, "Close", tint = tandem.muted) }
            }
            if (recurring.billsDue > 0) {
                Text("${money(recurring.billsDue)} in bills still expected this period.", color = tandem.muted, fontSize = 13.sp)
            }
            recurring.actionError?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp) }

            ordered(recurring.items).forEach { item ->
                RecurringRow(
                    item, money,
                    busy = recurring.busyId == item.id,
                    onConfirm = { onConfirm(item.id, item.expected) },
                    onSkip = { onSkip(item.id) },
                )
            }
        }
    }
}

@Composable
private fun RecurringRow(item: RecurringRowDto, money: (Double) -> String, busy: Boolean, onConfirm: () -> Unit, onSkip: () -> Unit) {
    val tandem = LocalTandemColors.current
    val income = item.kind == "income"
    val amountColor = if (income) tandem.positive else tandem.spent
    Column(
        Modifier
            .fillMaxWidth()
            .background(tandem.hero, RoundedCornerShape(12.dp))
            .border(1.dp, if (item.active && item.due) amountColor.copy(alpha = 0.5f) else tandem.hairline, RoundedCornerShape(12.dp))
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!item.icon.isNullOrBlank()) { Text(item.icon, fontSize = 16.sp); Spacer(Modifier.width(8.dp)) }
            Column(Modifier.weight(1f)) {
                Text(item.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                Text(stateLine(item), fontSize = 12.sp, color = if (item.active && item.due) amountColor else tandem.muted, maxLines = 1)
            }
            Spacer(Modifier.width(8.dp))
            if (item.hasKnownAmount) {
                Text((if (income) "+" else "") + money(item.expected), fontWeight = FontWeight.Bold, color = amountColor)
            }
        }
        // Actions only for a due, active item.
        if (item.active && item.due) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                if (busy) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.primary)
                } else {
                    TextButton(onClick = onSkip) { Text("Skip", color = tandem.muted, fontWeight = FontWeight.SemiBold) }
                    if (item.hasKnownAmount) {
                        TextButton(onClick = onConfirm) { Text("Confirm", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold) }
                    } else {
                        TextButton(onClick = onSkip) { Text("Mark handled", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold) }
                    }
                }
            }
        }
    }
}

private fun stateLine(item: RecurringRowDto): String {
    val what = if (item.kind == "income") "income" else "bill"
    return when {
        !item.active -> "Paused"
        item.due -> "Due now · $what"
        item.upcoming -> if (item.daysUntilDue <= 0) "Due today" else "In ${item.daysUntilDue} day${if (item.daysUntilDue == 1) "" else "s"}"
        item.dayOfMonth > 0 -> "Day ${item.dayOfMonth} · $what"
        else -> what.replaceFirstChar { it.uppercase() }
    }
}
