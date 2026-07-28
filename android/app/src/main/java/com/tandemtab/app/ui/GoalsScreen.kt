package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
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
import com.tandemtab.app.GoalsUi
import com.tandemtab.app.data.SavingBucketDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.util.Locale

/**
 * The Goals tab: every savings bucket (goal / debt / investment / sinking fund) as a progress-bar row,
 * mirroring the web Dashboard's Goals tab. All figures are resolved server-side (SavingsViewDto) — the
 * client just renders. Filter chips narrow by kind.
 */
private enum class GoalFilter(val label: String, val kind: String?) {
    All("All", null),
    Debts("Debts", "debt"),
    Savings("Savings", "goal"),
    Investments("Investments", "investment"),
    Expenses("Expenses", "sinking"),
}

@Composable
fun GoalsScreen(goals: GoalsUi, onRetry: () -> Unit) {
    val tandem = LocalTandemColors.current
    val fmt = rememberGoalsMoney(goals.currency)
    var filter by remember { mutableStateOf(GoalFilter.All) }

    when {
        goals.loading && goals.buckets.isEmpty() ->
            Box(Modifier.fillMaxWidth().height(220.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }

        goals.error != null ->
            Column(Modifier.fillMaxWidth().padding(top = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(goals.error, color = MaterialTheme.colorScheme.error)
                Spacer(Modifier.height(8.dp))
                Text("Tap to retry", color = tandem.positive, modifier = Modifier.clickable(onClick = onRetry).padding(8.dp))
            }

        else -> {
            val active = goals.buckets.filter { !it.archived }
            SavedHeader(fmt(goals.saved))
            Spacer(Modifier.height(14.dp))

            Row(
                Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                GoalFilter.entries.forEach { f ->
                    val count = if (f.kind == null) active.size else active.count { it.kind == f.kind }
                    FilterChip(f.label, count, selected = filter == f, onClick = { filter = f })
                }
            }
            Spacer(Modifier.height(14.dp))

            val shown = if (filter.kind == null) active else active.filter { it.kind == filter.kind }
            if (shown.isEmpty()) {
                Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                    Text("Nothing here yet.", color = tandem.muted)
                }
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    shown.forEach { GoalRow(it, fmt) }
                }
            }
        }
    }
}

@Composable
private fun SavedHeader(saved: String) {
    val tandem = LocalTandemColors.current
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(18.dp),
    ) {
        Text("SAVED", fontSize = 10.sp, letterSpacing = 1.3.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Spacer(Modifier.height(4.dp))
        Text(saved, fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = tandem.saved)
    }
}

@Composable
private fun GoalRow(b: SavingBucketDto, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    // Kind-specific headline + progress. Bars read positive (progress toward a goal / down a debt), so green.
    val headline: String
    val subline: String?
    val progress: Float?
    when (b.kind) {
        "debt" -> {
            headline = "${fmt(b.debtBalance ?: 0.0)} · owed"
            progress = b.debtProgress?.toFloat()
            subline = b.debtMonthsAhead?.takeIf { it > 0 }?.let { "$it mo ahead of schedule" }
        }
        "investment" -> {
            headline = "${fmt(b.saved)} · invested"
            progress = null
            subline = b.investmentProjected?.let { "≈ ${fmt(it)} projected" }
        }
        "sinking" -> {
            headline = "${fmt(b.saved)} · saved"
            progress = null
            subline = buildString {
                b.monthlySetAside?.takeIf { it > 0 }?.let { append("${fmt(it)}/mo to stay covered") }
                b.targetShortfall?.takeIf { it > 0 }?.let {
                    if (isNotEmpty()) append(" · ")
                    append("${fmt(it)} still to find")
                }
            }.ifBlank { null }
        }
        else -> { // "goal" — with or without a target
            val target = b.goalTarget
            headline = if (target != null) "${fmt(b.saved)} / ${fmt(target)}" else "${fmt(b.saved)} · saved"
            progress = b.goalProgress?.toFloat()
            subline = null
        }
    }

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!b.icon.isNullOrBlank()) {
                Text(b.icon, fontSize = 18.sp)
                Spacer(Modifier.width(8.dp))
            }
            Text(b.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
            Text(headline, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
        }
        if (progress != null) {
            ProgressBar(progress.coerceIn(0f, 1f))
        }
        if (subline != null) {
            Text(subline, fontSize = 12.sp, color = tandem.muted)
        }
    }
}

@Composable
private fun ProgressBar(fraction: Float) {
    val tandem = LocalTandemColors.current
    Box(
        Modifier.fillMaxWidth().height(8.dp)
            .background(tandem.segmentTrack, RoundedCornerShape(4.dp)),
    ) {
        Box(
            Modifier.fillMaxWidth(fraction).height(8.dp)
                .background(tandem.saved, RoundedCornerShape(4.dp)),
        )
    }
}

@Composable
private fun FilterChip(label: String, count: Int, selected: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    val bg = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val fg = if (selected) MaterialTheme.colorScheme.onPrimary else tandem.muted
    Row(
        Modifier
            .background(bg, RoundedCornerShape(999.dp))
            .border(1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, RoundedCornerShape(999.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        Text(label, color = fg, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
        Text(count.toString(), color = fg, fontSize = 11.sp, fontWeight = FontWeight.Bold)
    }
}

private fun rememberGoalsMoney(currencyCode: String): (Double) -> String {
    val nf = java.text.NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = java.util.Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}
