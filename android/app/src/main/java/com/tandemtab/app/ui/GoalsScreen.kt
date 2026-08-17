package com.tandemtab.app.ui

import androidx.compose.animation.AnimatedVisibility
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
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.text.font.FontWeight
import com.tandemtab.app.ui.theme.TandemIcons
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.GoalsUi
import com.tandemtab.app.SpendingUi
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

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun GoalsScreen(
    goals: GoalsUi,
    spending: SpendingUi,
    onRetry: () -> Unit,
    onPrepareAllocate: () -> Unit,
    onPrepareSpend: () -> Unit,
    onAllocate: (bucketId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onSpend: (bucketId: String, categoryId: String, fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onPrepareInstallment: () -> Unit,
    onLogInstallment: (bucketId: String, total: Double, fundId: String, date: String, categoryId: String, note: String?, onDone: () -> Unit) -> Unit,
    canSetInitial: Boolean,
    onPrepareBucket: () -> Unit,
    onSaveBucket: (bucketId: String?, req: com.tandemtab.app.data.SaveSavingBucketRequest, onDone: () -> Unit) -> Unit,
    onArchiveBucket: (bucketId: String, archived: Boolean, onDone: () -> Unit) -> Unit,
    onDeleteBucket: (bucketId: String, onDone: () -> Unit) -> Unit,
    onEditDeposit: (allocationId: String, amount: Double, onDone: () -> Unit) -> Unit,
    onRemoveDeposit: (allocationId: String, onDone: () -> Unit) -> Unit,
    onUndoMovement: (allocationId: String, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val fmt = rememberGoalsMoney(goals.currency)
    var filter by remember { mutableStateOf(GoalFilter.All) }
    var allocateBucket by remember { mutableStateOf<SavingBucketDto?>(null) }
    var spendBucket by remember { mutableStateOf<SavingBucketDto?>(null) }
    var installmentBucket by remember { mutableStateOf<SavingBucketDto?>(null) }
    // null = closed; the Unit-holder distinguishes "new" (editBucket set, value null) from "editing that bucket".
    var editing by remember { mutableStateOf<Pair<Boolean, SavingBucketDto?>?>(null) }
    var deleting by remember { mutableStateOf<SavingBucketDto?>(null) }
    var showArchived by remember { mutableStateOf(false) }
    // Which kind a *new* bucket opens on — taken from the active filter, so "Debts → add" starts on a debt.
    var pendingKind by remember { mutableStateOf<String?>(null) }

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
            val archived = goals.buckets.filter { it.archived }
            SavedHeader(fmt(goals.saved))
            Spacer(Modifier.height(10.dp))
            // The web puts one "Add goal" in the Goals header and pre-selects the kind from the active filter;
            // same here, so filtering to Debts and tapping add starts you on a debt.
            AddBucketButton {
                onPrepareBucket()
                editing = true to null
                pendingKind = filter.kind
            }
            Spacer(Modifier.height(14.dp))

            // Wrap onto multiple lines rather than scrolling off-screen.
            FlowRow(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
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
                    shown.forEach { b ->
                        GoalRow(
                            b, fmt,
                            onAllocate = { onPrepareAllocate(); allocateBucket = b },
                            // Spend-from-savings applies to ordinary goal / sinking buckets that hold money;
                            // debt buckets are disbursed and investments withdrawn (a later slice).
                            canSpend = b.saved > 0 && b.kind != "debt" && b.kind != "investment",
                            onSpend = { onPrepareSpend(); spendBucket = b },
                            onEdit = { onPrepareBucket(); pendingKind = null; editing = false to b },
                            // Only debts take a logged installment; the switch that turns it payment-driven has had
                            // nowhere to land since S91. Offered on every debt (not just payment-driven ones) — the
                            // web does too, since logging the split is useful even when the balance walks a schedule.
                            onLogInstallment = if (b.kind == "debt") { { onPrepareInstallment(); installmentBucket = b } } else null,
                        )
                    }
                }
            }

            SavingsActivity(goals, fmt, onEditDeposit, onRemoveDeposit, onUndoMovement)

            // Archived buckets are kept out of the way but reachable — archiving is the answer when a delete is
            // refused, so hiding them with no way back would make that advice a dead end.
            if (archived.isNotEmpty()) {
                Spacer(Modifier.height(16.dp))
                Text(
                    if (showArchived) "Hide archived (${archived.size})" else "Show archived (${archived.size})",
                    color = tandem.muted,
                    fontSize = 13.sp,
                    modifier = Modifier.clickable { showArchived = !showArchived }.padding(vertical = 6.dp),
                )
                if (showArchived) {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        archived.forEach { b ->
                            ArchivedRow(b, fmt, onRestore = { onArchiveBucket(b.id, false) {} })
                        }
                    }
                }
            }
        }
    }

    editing?.let { (isNew, bucket) ->
        SavingBucketSheet(
            existing = bucket,
            initialKind = if (isNew) pendingKind else null,
            goals = goals,
            funds = spending.funds,
            canSetInitial = canSetInitial,
            onDismiss = { editing = null },
            onSave = onSaveBucket,
            onArchive = bucket?.let { b -> { onArchiveBucket(b.id, true) { editing = null } } },
            onDelete = bucket?.let { b -> { editing = null; deleting = b } },
        )
    }
    deleting?.let { b ->
        DeleteBucketDialog(
            bucket = b, goals = goals,
            onDismiss = { deleting = null },
            onArchive = { onArchiveBucket(b.id, true) { deleting = null } },
            onDelete = { onDeleteBucket(b.id) { deleting = null } },
        )
    }

    allocateBucket?.let { b ->
        AllocateSavingSheet(
            bucket = b, goals = goals,
            onDismiss = { allocateBucket = null },
            onSubmit = { amt, date, note, onDone -> onAllocate(b.id, amt, date, note, onDone) },
        )
    }
    spendBucket?.let { b ->
        SpendFromSavingsSheet(
            bucket = b, goals = goals, spending = spending,
            onDismiss = { spendBucket = null },
            onSubmit = { cat, fund, amt, date, note, onDone -> onSpend(b.id, cat, fund, amt, date, note, onDone) },
        )
    }
    installmentBucket?.let { b ->
        LogInstallmentSheet(
            bucket = b, goals = goals, spending = spending,
            onDismiss = { installmentBucket = null },
            onLog = onLogInstallment,
        )
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
private fun GoalRow(
    b: SavingBucketDto,
    fmt: (Double) -> String,
    onAllocate: () -> Unit,
    canSpend: Boolean,
    onSpend: () -> Unit,
    onEdit: () -> Unit,
    onLogInstallment: (() -> Unit)? = null,
) {
    val tandem = LocalTandemColors.current
    var expanded by remember(b.id) { mutableStateOf(false) }
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
    val details = bucketDetails(b, fmt)

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        // Header row is tap-to-expand (when there's extra detail to show).
        Row(
            Modifier.fillMaxWidth().let { if (details.isNotEmpty()) it.clickable { expanded = !expanded } else it },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            CatIcon(b.icon, b.name)
            Spacer(Modifier.width(8.dp))
            Text(b.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
            Text(headline, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
            if (details.isNotEmpty()) {
                Spacer(Modifier.width(6.dp))
                Icon(TandemIcons.Chevron, null, tint = tandem.muted, modifier = Modifier.size(16.dp).rotate(if (expanded) -90f else 90f))
            }
        }
        if (progress != null) ProgressBar(progress.coerceIn(0f, 1f))
        if (subline != null) Text(subline, fontSize = 12.sp, color = tandem.muted)

        // Expanded per-kind breakdown (debt payoff, goal shortfall, investment projection, sinking cover).
        AnimatedVisibility(expanded) {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 2.dp)) {
                Box(Modifier.fillMaxWidth().height(1.dp).background(tandem.hairline))
                details.forEach { (label, value) ->
                    Row(Modifier.fillMaxWidth().padding(top = 4.dp)) {
                        Text(label, fontSize = 12.sp, color = tandem.muted, modifier = Modifier.weight(1f))
                        Text(value, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
                    }
                }
            }
        }

        // Money-movement actions as fancier icon pills, right-aligned. Edit sits on the left, apart from them:
        // it changes what the bucket *is*, not how much is in it.
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            ActionPill(TandemIcons.Pencil, "Edit", tandem.muted, onEdit)
            Spacer(Modifier.weight(1f))
            // Debt-only: log an actual payment (splits into interest/principal). Distinct from "Add", which sets
            // money aside toward the debt rather than paying the lender.
            onLogInstallment?.let {
                ActionPill(TandemIcons.Note, "Log payment", MaterialTheme.colorScheme.primary, it)
                Spacer(Modifier.width(8.dp))
            }
            if (canSpend) {
                ActionPill(TandemIcons.Minus, "Spend", tandem.spent, onSpend)
                Spacer(Modifier.width(8.dp))
            }
            ActionPill(TandemIcons.Plus, "Add", MaterialTheme.colorScheme.primary, onAllocate)
        }
    }
}

/** The kind-specific figures shown when a bucket row is expanded (only the ones the server actually provided). */
private fun bucketDetails(b: SavingBucketDto, fmt: (Double) -> String): List<Pair<String, String>> = buildList {
    when (b.kind) {
        "debt" -> {
            add("Owed today" to fmt(b.debtBalance ?: 0.0))
            b.debtProgress?.let { add("Paid off" to "${(it * 100).toInt()}%") }
            b.debtMonthsAhead?.takeIf { it > 0 }?.let { add("Ahead of schedule" to "$it mo") }
            b.monthlySetAside?.takeIf { it > 0 }?.let { add("Paying" to "${fmt(it)}/mo") }
        }
        "investment" -> {
            add("Invested" to fmt(b.saved))
            b.investmentProjected?.let { add("Projected" to "≈ ${fmt(it)}") }
            b.monthlySetAside?.takeIf { it > 0 }?.let { add("Contributing" to "${fmt(it)}/mo") }
        }
        "sinking" -> {
            add("Saved" to fmt(b.saved))
            b.goalTarget?.let { add("Target" to fmt(it)) }
            b.monthlySetAside?.takeIf { it > 0 }?.let { add("Set aside" to "${fmt(it)}/mo") }
            b.targetShortfall?.takeIf { it > 0 }?.let { add("Still to find" to fmt(it)) }
        }
        else -> {
            add("Saved" to fmt(b.saved))
            b.goalTarget?.let { add("Target" to fmt(it)) }
            b.goalProgress?.let { add("Progress" to "${(it * 100).toInt()}%") }
            b.targetShortfall?.takeIf { it > 0 }?.let { add("Still to save" to fmt(it)) }
            b.monthlySetAside?.takeIf { it > 0 }?.let { add("Set aside" to "${fmt(it)}/mo") }
        }
    }
}

/** The Goals header's single add affordance — a dashed full-width row, so it reads as "there's room for more"
 *  rather than competing with the bottom bar's FAB (which adds an expense, not a goal). */
@Composable
private fun AddBucketButton(onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.10f))
            .clickable(onClick = onClick)
            .padding(vertical = 12.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(TandemIcons.Plus, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(17.dp))
        Spacer(Modifier.width(7.dp))
        Text("New goal, debt or fund", color = MaterialTheme.colorScheme.primary, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** An archived bucket: name + what it holds, and one way back. Deliberately flat — it's out of play. */
@Composable
private fun ArchivedRow(b: SavingBucketDto, fmt: (Double) -> String, onRestore: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(12.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CatIcon(b.icon, b.name)
        Spacer(Modifier.width(8.dp))
        Text(b.name, color = tandem.muted, modifier = Modifier.weight(1f), maxLines = 1, fontSize = 14.sp)
        Text(fmt(b.saved), color = tandem.muted, fontSize = 13.sp)
        Spacer(Modifier.width(8.dp))
        TextButton(onClick = onRestore) { Text("Restore", fontSize = 13.sp) }
    }
}

/** A small rounded action button: a tinted icon + label (Add / Spend), fancier than a plain text button. */
@Composable
private fun ActionPill(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, color: androidx.compose.ui.graphics.Color, onClick: () -> Unit) {
    Row(
        Modifier.clip(RoundedCornerShape(999.dp)).background(color.copy(alpha = 0.12f))
            .clickable(onClick = onClick).padding(horizontal = 12.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        Icon(icon, null, tint = color, modifier = Modifier.size(16.dp))
        Text(label, color = color, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
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
