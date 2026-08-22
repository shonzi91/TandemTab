package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.PeriodUi
import com.tandemtab.app.WalletsUi
import com.tandemtab.app.data.PeriodRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.abs

// The period-lifecycle sheets, mirroring the web's period-chip actions (Dashboard.razor Modal.NextPeriod /
// EditPeriod / RemovePeriod). Rolling into a new month is the one action an Android-only user could never
// perform, so it's the first of R2's four "different product" gaps to close.

/** One fund's reconciliation drift: what was entered minus what the ledger computed. */
private data class Gap(val fundId: String, val name: String, val amount: Double)

/**
 * "Start next month": close the open period and open the next one, carrying real opening balances forward.
 *
 * Two stages in one sheet, as on web. Stage one is the opening-balance form (prefilled with what the ledger
 * thinks each fund holds, so the common case is just confirming). If what the user enters disagrees with the
 * ledger, stage two names the difference per fund and offers three genuinely different outcomes — deliberately
 * as three labelled buttons stacked full-width, never an icon row: this is the choice the web shipped as
 * ✕ ✕ ✓, where the tick (which reads as plain confirmation) was the one that writes ledger entries.
 *
 * The synced fund is excluded throughout — bank sync is authoritative for it, so there's nothing to hand-enter
 * and no drift to explain.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun StartNextPeriodSheet(
    closing: PeriodRowDto,
    wallets: WalletsUi,
    ops: PeriodUi,
    onDismiss: () -> Unit,
    onSubmit: (copyBudgets: Boolean, adjustBudgets: Boolean, openings: Map<String, Double>, adjustments: List<Pair<String, Double>>, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = moneyFormatter(wallets.currency)
    val funds = remember(wallets.funds) { wallets.funds.filter { !it.synced } }

    // Prefilled with the computed balance: the honest default is "nothing has gone unlogged", and typing over it
    // is what declares otherwise. Keyed by fund id so a re-fetch of the funds list can't scramble the entries.
    val openings = remember(funds) {
        mutableStateMapOf<String, String>().apply { funds.forEach { put(it.id, trimAmount(it.balance)) } }
    }
    var copyBudgets by remember { mutableStateOf(true) }
    var adjustBudgets by remember { mutableStateOf(true) }
    var gaps by remember { mutableStateOf<List<Gap>?>(null) }
    var hint by remember { mutableStateOf<String?>(null) }

    // The drift block appears at the foot of a form that is already taller than the screen, so pressing the
    // primary button would otherwise swap the action bar under the user's thumb with no visible explanation.
    val scroll = rememberScrollState()
    LaunchedEffect(gaps) { if (!gaps.isNullOrEmpty()) scroll.animateScrollTo(scroll.maxValue) }

    fun entered(fundId: String): Double? = openings[fundId]?.replace(',', '.')?.trim()?.toDoubleOrNull()
    val allParse = funds.all { entered(it.id) != null }
    fun enteredMap(): Map<String, Double> = funds.associate { it.id to (entered(it.id) ?: 0.0) }

    fun computeGaps(): List<Gap> = funds.mapNotNull { f ->
        val gap = Math.round(((entered(f.id) ?: 0.0) - f.balance) * 100.0) / 100.0
        if (abs(gap) >= 0.01) Gap(f.id, f.name, gap) else null
    }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = MaterialTheme.colorScheme.surface,
    ) {
        // The action bar is a SIBLING of the scrolling body, not floating over it. The reconcile step grows the
        // bar from two buttons to three stacked ones, and any hand-picked bottom padding that clears the short
        // bar hides the tail of the tall one — which here is the per-fund breakdown the choice depends on.
        Column(Modifier.fillMaxWidth().fillMaxHeight()) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .imePadding()
                    .padding(horizontal = 18.dp)
                    .verticalScroll(scroll)
                    .padding(bottom = 12.dp),
            ) {
                Text(
                    "Start next month",
                    modifier = Modifier.fillMaxWidth().padding(top = 4.dp, bottom = 4.dp),
                    fontSize = 19.sp, fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Spacer(Modifier.height(8.dp))
                Text(
                    "Closing ${periodDates(closing)}. Enter what each fund really holds now — that money carries " +
                        "over and is fully available to budget or save.",
                    color = tandem.muted, fontSize = 13.sp,
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    "Previous closing balance: ${fmt(wallets.current)}",
                    color = tandem.muted, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                )
                Spacer(Modifier.height(14.dp))

                if (funds.isEmpty()) {
                    Text("No funds to carry over.", color = tandem.muted, fontSize = 13.sp)
                }
                funds.forEach { f ->
                    OutlinedTextField(
                        value = openings[f.id].orEmpty(),
                        onValueChange = { openings[f.id] = it; gaps = null; hint = null },
                        label = { Text(f.name) },
                        prefix = { Text(currencySymbol(wallets.currency)) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(10.dp))
                }

                Spacer(Modifier.height(4.dp))
                CheckRow("Copy this month's budgets forward", copyBudgets) { copyBudgets = it; gaps = null }
                if (copyBudgets) {
                    CheckRow("Adjust budgets to what you actually spent", adjustBudgets) { adjustBudgets = it }
                    Text(
                        "Each budget moves halfway toward this month's spending, rounded up to the nearest 10.",
                        color = tandem.muted, fontSize = 12.sp, modifier = Modifier.padding(start = 4.dp),
                    )
                }

                gaps?.takeIf { it.isNotEmpty() }?.let { found ->
                    Spacer(Modifier.height(16.dp))
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .background(tandem.alertBg, RoundedCornerShape(14.dp))
                            .border(1.dp, tandem.alertBorder, RoundedCornerShape(14.dp))
                            .padding(14.dp),
                    ) {
                        Text(
                            "Some funds don't match what you logged — that usually means a transaction wasn't recorded:",
                            color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                        )
                        Spacer(Modifier.height(8.dp))
                        found.forEach { g ->
                            Row(Modifier.fillMaxWidth().padding(vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
                                Text(g.name, color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp, modifier = Modifier.weight(1f))
                                Text(
                                    "${fmt(abs(g.amount))} ${if (g.amount < 0) "less than expected" else "more than expected"}",
                                    color = tandem.warn, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                                )
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "Logging an adjustment adds a matching entry to this month so its books balance. " +
                                "Ignoring carries the difference forward untracked.",
                            color = tandem.muted, fontSize = 12.sp,
                        )
                    }
                }

                Hints(hint, ops.error)
            }

            // The action bar. Two buttons normally; three stacked, labelled ones once drift has been found —
            // they are three different outcomes, and squeezing them into one row is what made them unreadable.
            val found = gaps
            if (found.isNullOrEmpty()) {
                SheetActionBar(
                    saving = ops.busy,
                    canSave = allParse && !ops.busy,
                    onDismiss = onDismiss,
                    onSave = {
                        val drift = if (allParse) computeGaps() else null
                        when {
                            drift == null -> hint = "Enter a number for every fund."
                            // Explain the drift first; the choice of what to do about it is the user's.
                            drift.isNotEmpty() -> gaps = drift
                            else -> onSubmit(copyBudgets, adjustBudgets, enteredMap(), emptyList()) { onDismiss() }
                        }
                    },
                    saveLabel = "Start next month",
                )
            } else {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface)
                        .navigationBarsPadding()
                        .imePadding()
                        .padding(horizontal = 18.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Button(
                        onClick = {
                            onSubmit(copyBudgets, adjustBudgets, enteredMap(), found.map { it.fundId to it.amount }) { onDismiss() }
                        },
                        enabled = !ops.busy,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        if (ops.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                        else Text("Log as adjustment, then start")
                    }
                    OutlinedButton(
                        onClick = { onSubmit(copyBudgets, adjustBudgets, enteredMap(), emptyList()) { onDismiss() } },
                        enabled = !ops.busy,
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text("Ignore and start anyway") }
                    TextButton(
                        onClick = onDismiss,
                        enabled = !ops.busy,
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text("I'll add the missing entries myself") }
                }
            }
        }
    }
}

/** Move a period's dates. Later periods shift to stay contiguous, so this is safe on any period, not just the last. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EditPeriodDatesSheet(
    period: PeriodRowDto,
    ops: PeriodUi,
    onDismiss: () -> Unit,
    onSubmit: (index: Int, from: String, to: String, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var from by remember { mutableStateOf(period.from) }
    var to by remember { mutableStateOf(period.to) }
    var hint by remember { mutableStateOf<String?>(null) }
    val valid = runCatching { !LocalDate.parse(to).isBefore(LocalDate.parse(from)) }.getOrDefault(false)

    SheetScaffold(
        title = "Change these dates",
        saving = ops.busy,
        canSave = valid && !ops.busy,
        onDismiss = onDismiss,
        onSave = {
            if (!valid) hint = "The end date can't be before the start date."
            else onSubmit(period.index, from, to) { onDismiss() }
        },
        sheetState = sheetState,
        saveLabel = "Save dates",
    ) {
        Text(
            "Any later months shift to stay back-to-back, each keeping its own length.",
            color = tandem.muted, fontSize = 13.sp,
        )
        Spacer(Modifier.height(14.dp))
        FieldLabel("Starts")
        DateField(from) { from = it; hint = null }
        Spacer(Modifier.height(14.dp))
        FieldLabel("Ends")
        DateField(to) { to = it; hint = null }
        Hints(hint, ops.error)
    }
}

/** Confirm undoing the last rollover. Destructive and unrecoverable, so it always asks first. */
@Composable
fun RemovePeriodDialog(
    latest: PeriodRowDto,
    ops: PeriodUi,
    onDismiss: () -> Unit,
    onConfirm: (onDone: () -> Unit) -> Unit,
) {
    AlertDialog(
        onDismissRequest = { if (!ops.busy) onDismiss() },
        title = { Text("Remove this month?") },
        text = {
            Column {
                Text(
                    "This deletes ${periodDates(latest)} and everything logged in it, then re-opens the month " +
                        "before as active. This can't be undone.",
                )
                ops.error?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = { onConfirm { onDismiss() } },
                enabled = !ops.busy,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
            ) {
                if (ops.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                else Text("Remove month")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !ops.busy) { Text("Cancel") } },
    )
}

/** "1 – 31 Jul 2026" for a period row, for the sheet copy that has to name which month is being acted on. */
internal fun periodDates(p: PeriodRowDto): String = runCatching {
    val from = LocalDate.parse(p.from); val to = LocalDate.parse(p.to)
    val short = DateTimeFormatter.ofPattern("d MMM", Locale.getDefault())
    val long = DateTimeFormatter.ofPattern("d MMM yyyy", Locale.getDefault())
    "${from.format(short)} – ${to.format(long)}"
}.getOrDefault("${p.from} – ${p.to}")
