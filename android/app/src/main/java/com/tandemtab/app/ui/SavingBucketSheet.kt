package com.tandemtab.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.GoalsUi
import com.tandemtab.app.data.FundOptionDto
import com.tandemtab.app.data.PlannedCostDto
import com.tandemtab.app.data.SaveSavingBucketRequest
import com.tandemtab.app.data.SavingBucketDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate

// The new/edit savings-bucket sheet — the write half of the Goals tab, and R2's second L row. Mirrors the web's
// AddBucket/EditBucket modal (Dashboard.razor), including the rule that a bucket's KIND is fixed after creation:
// the kinds carry different fields, actions and projections, so switching would strand data. To change kind you
// delete and recreate, which is what the web tells you too.

/** The four bucket kinds, in the web modal's order. `wire` matches SavingBucketDto.kind. */
private enum class BucketKind(val label: String, val icon: String, val wire: String) {
    Goal("Savings goal", "🪙", "goal"),
    Debt("Debt payoff", "💳", "debt"),
    Investment("Investment", "📈", "investment"),
    Expenses("Expenses fund", "🗓", "sinking"),
}

/** A cost row being edited. Held as a mutable holder so the list can be edited in place. */
private data class CostRow(var label: String, var amount: String, var cadence: String, var due: String?)

private val CADENCES = listOf("monthly" to "Monthly", "quarterly" to "Quarterly", "yearly" to "Yearly", "one-off" to "One-off")

/**
 * Create a bucket (`existing` null) or reconfigure one.
 *
 * ⚠️ **The save is a full overwrite, not a patch.** The server applies every field of the request, so this sheet
 * seeds *all* of them from [existing] — including the ones it never shows for the current kind. Anything not sent
 * back is cleared, which is why `SavingBucketDto` had to grow `fundId` / `thresholdPercent` / `notifyOnMilestone` /
 * `initialAmount` for R2: without them an edit silently wiped the held-in fund and reset the alert threshold.
 *
 * [canSetInitial] mirrors the web's `CanSetInitialSavings` (the account still has a single period). The starting
 * balance is setup-only, and the server drops it once a second period exists — so the field is hidden rather than
 * shown and quietly ignored.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SavingBucketSheet(
    existing: SavingBucketDto?,
    initialKind: String?,
    goals: GoalsUi,
    funds: List<FundOptionDto>,
    canSetInitial: Boolean,
    onDismiss: () -> Unit,
    onSave: (bucketId: String?, req: SaveSavingBucketRequest, onDone: () -> Unit) -> Unit,
    onArchive: (() -> Unit)?,
    onDelete: (() -> Unit)?,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = sheetMoney(goals.currency)
    // The synced fund is bank-driven, so it isn't offered as a bucket's home (mirrors web's SelectableFunds).
    val pickableFunds = remember(funds) { funds.filter { !it.synced } }
    val fc = existing?.forecast

    var kind by remember {
        mutableStateOf(
            BucketKind.entries.firstOrNull { it.wire == (existing?.kind ?: initialKind) } ?: BucketKind.Goal,
        )
    }
    var name by remember { mutableStateOf(existing?.name.orEmpty()) }
    var icon by remember { mutableStateOf(existing?.icon.orEmpty()) }
    var fundId by remember { mutableStateOf(existing?.fundId) }
    var initial by remember { mutableStateOf(existing?.initialAmount?.takeIf { it > 0 }?.let { trimAmount(it) }.orEmpty()) }

    // Goal
    var goalAmount by remember { mutableStateOf(existing?.goalTarget?.let { trimAmount(it) }.orEmpty()) }
    var threshold by remember { mutableStateOf(trimAmount(existing?.thresholdPercent ?: 80.0)) }
    var notify by remember { mutableStateOf(existing?.notifyOnMilestone ?: false) }

    // Debt. Two ways to state the balance: what's owed now, or the original principal plus how much has been
    // cleared — the latter is the only one that gives an honest "% paid off" baseline, so it's worth the toggle.
    var enterOriginal by remember { mutableStateOf(false) }
    var debtBalance by remember { mutableStateOf(fc?.debtStoredBalance?.let { trimAmount(it) }.orEmpty()) }
    var debtOriginal by remember { mutableStateOf(fc?.debtOriginalBalance?.let { trimAmount(it) }.orEmpty()) }
    var debtPaid by remember { mutableStateOf("") }
    var debtRate by remember { mutableStateOf(fc?.debtRatePercent?.let { trimAmount(it) }.orEmpty()) }
    var debtInstallment by remember { mutableStateOf(fc?.debtInstallment?.let { trimAmount(it) }.orEmpty()) }
    var debtDay by remember { mutableStateOf(existing?.debtInstallmentDay?.toString().orEmpty()) }
    var debtStart by remember { mutableStateOf(fc?.debtStartDate) }
    var payDriven by remember { mutableStateOf(existing?.debtPaymentDriven ?: false) }

    // Investment
    var invRate by remember { mutableStateOf(fc?.investmentRatePercent?.let { trimAmount(it) }.orEmpty()) }
    var invYears by remember { mutableStateOf(fc?.investmentTermYears?.let { trimAmount(it) }.orEmpty()) }
    var invCompounds by remember { mutableStateOf(fc?.investmentCompoundsPerYear ?: 12) }

    // Expenses fund
    val costs = remember {
        mutableStateListOf<CostRow>().apply {
            existing?.costs?.forEach { add(CostRow(it.label, trimAmount(it.amount), it.cadence, it.dueDate)) }
        }
    }
    var hint by remember { mutableStateOf<String?>(null) }

    fun num(s: String): Double = s.replace(',', '.').trim().toDoubleOrNull() ?: 0.0
    val validCosts = costs.filter { it.label.isNotBlank() && num(it.amount) > 0 }
    // An expenses fund with no costs computes a €0 set-aside and nudges about nothing — the cost list IS the kind.
    val canSave = name.isNotBlank() && !goals.saving && !(kind == BucketKind.Expenses && validCosts.isEmpty())

    fun build(): SaveSavingBucketRequest {
        val owedNow = if (enterOriginal) (num(debtOriginal) - num(debtPaid)).coerceAtLeast(0.0) else num(debtBalance)
        return SaveSavingBucketRequest(
            name = name.trim(),
            icon = icon.ifBlank { null },
            // Only the active kind's figures travel; the server clears the others by kind anyway, and sending a
            // stale goal alongside a debt would just be noise.
            goalAmount = if (kind == BucketKind.Goal) num(goalAmount).takeIf { it > 0 } else null,
            thresholdPercent = num(threshold).takeIf { it in 0.0..100.0 } ?: 80.0,
            notifyOnMilestone = kind == BucketKind.Goal && notify,
            initialAmount = if (canSetInitial) num(initial) else 0.0,
            isDebt = kind == BucketKind.Debt,
            debtBalance = if (kind == BucketKind.Debt) owedNow else 0.0,
            debtRate = if (kind == BucketKind.Debt) num(debtRate) else 0.0,
            debtInstallment = if (kind == BucketKind.Debt) num(debtInstallment) else 0.0,
            debtOriginalBalance = if (kind == BucketKind.Debt && enterOriginal) num(debtOriginal).takeIf { it > 0 } else null,
            debtInstallmentDay = if (kind == BucketKind.Debt) debtDay.trim().toIntOrNull()?.takeIf { it in 1..31 } else null,
            debtStartDate = if (kind == BucketKind.Debt) debtStart else null,
            // Not edited here (the web sets it from the bucket's own nudges), so carry it through untouched
            // rather than letting the overwrite drop it.
            plannedContribution = fc?.plannedContribution,
            isInvestment = kind == BucketKind.Investment,
            invRate = if (kind == BucketKind.Investment) num(invRate) else 0.0,
            invTermYears = if (kind == BucketKind.Investment) num(invYears) else 0.0,
            invCompounds = if (kind == BucketKind.Investment) invCompounds else 12,
            fundId = fundId,
            costs = if (kind == BucketKind.Expenses) validCosts.map {
                PlannedCostDto(it.label.trim(), num(it.amount), it.cadence, it.due.takeIf { _ -> it.cadence == "one-off" })
            } else null,
            isExpensesFund = kind == BucketKind.Expenses,
            debtPaymentDriven = kind == BucketKind.Debt && payDriven,
        )
    }

    SheetScaffold(
        title = if (existing == null) "New savings bucket" else "Edit ${existing.name}",
        saving = goals.saving,
        canSave = canSave,
        onDismiss = onDismiss,
        onSave = {
            if (name.isBlank()) hint = "Give it a name."
            else if (kind == BucketKind.Expenses && validCosts.isEmpty()) hint = "Add at least one cost this fund covers."
            else onSave(existing?.id, build()) { onDismiss() }
        },
        sheetState = sheetState,
        saveLabel = if (existing == null) "Add" else "Save",
    ) {
        OutlinedTextField(
            value = name,
            onValueChange = { name = it; hint = null },
            label = { Text("Name") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(10.dp))
        OutlinedTextField(
            value = icon,
            onValueChange = { icon = it },
            label = { Text("Icon (optional)") },
            placeholder = { Text("e.g. 🏝") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        // Kind is chosen at creation only — see the class comment.
        if (existing == null) {
            FieldLabel("Type")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                BucketKind.entries.forEach { k ->
                    PickChip(label = k.label, icon = k.icon, selected = kind == k) { kind = k; hint = null }
                }
            }
            Spacer(Modifier.height(14.dp))
        }

        when (kind) {
            BucketKind.Goal -> {
                MoneyField("Goal amount (optional)", goalAmount, goals.currency) { goalAmount = it }
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = threshold,
                    onValueChange = { threshold = it },
                    label = { Text("Alert at %") },
                    suffix = { Text("%") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                CheckRow("Notify on milestone", notify) { notify = it }
            }

            BucketKind.Debt -> {
                FieldLabel("Balance")
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    PickChip("What's owed now", null, !enterOriginal) { enterOriginal = false }
                    PickChip("Original + already paid", null, enterOriginal) { enterOriginal = true }
                }
                Spacer(Modifier.height(10.dp))
                if (!enterOriginal) {
                    MoneyField("Balance owed", debtBalance, goals.currency) { debtBalance = it }
                } else {
                    MoneyField("Original principal", debtOriginal, goals.currency) { debtOriginal = it }
                    Spacer(Modifier.height(10.dp))
                    MoneyField("Already paid off (principal)", debtPaid, goals.currency) { debtPaid = it }
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "Owed now: ${fmt((num(debtOriginal) - num(debtPaid)).coerceAtLeast(0.0))}",
                        color = tandem.muted, fontSize = 12.sp,
                    )
                }
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = debtRate,
                    onValueChange = { debtRate = it },
                    label = { Text("Interest rate (% APR)") },
                    suffix = { Text("%") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(10.dp))
                MoneyField("Monthly installment", debtInstallment, goals.currency) { debtInstallment = it }
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = debtDay,
                    onValueChange = { debtDay = it },
                    label = { Text("Installment due day (optional)") },
                    placeholder = { Text("1–31") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(10.dp))
                FieldLabel("Loan start date (optional)")
                OptionalDateField(debtStart, placeholder = "Not recorded") { debtStart = it }
                Text(
                    "The start date bases \"interest paid so far\" on the real timeline — otherwise it's inferred " +
                        "from the balance.",
                    color = tandem.muted, fontSize = 12.sp,
                )
                CheckRow("I log each installment here", payDriven) { payDriven = it }
                Text(
                    "Then the balance drops only when you log a payment. Otherwise it's carried forward on the " +
                        "loan's own schedule.",
                    color = tandem.muted, fontSize = 12.sp,
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    "Used only to project your payoff — it never changes budgets, savings or balances.",
                    color = tandem.muted, fontSize = 12.sp,
                )
            }

            BucketKind.Investment -> {
                OutlinedTextField(
                    value = invRate,
                    onValueChange = { invRate = it },
                    label = { Text("Expected return (% / year)") },
                    suffix = { Text("%") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = invYears,
                    onValueChange = { invYears = it },
                    label = { Text("Horizon (years)") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(12.dp))
                FieldLabel("Compounding")
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(12 to "Monthly", 4 to "Quarterly", 1 to "Yearly", 365 to "Daily").forEach { (n, lbl) ->
                        PickChip(lbl, null, invCompounds == n) { invCompounds = n }
                    }
                }
                Spacer(Modifier.height(8.dp))
                Text(
                    "Used only to project growth — it never changes budgets, savings or balances.",
                    color = tandem.muted, fontSize = 12.sp,
                )
            }

            BucketKind.Expenses -> {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("Costs this covers", modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp)
                    TextButton(onClick = { costs.add(CostRow("", "", "monthly", null)) }) { Text("+ Add a cost") }
                }
                costs.forEachIndexed { i, row ->
                    Spacer(Modifier.height(8.dp))
                    Column(Modifier.fillMaxWidth()) {
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            OutlinedTextField(
                                value = row.label,
                                onValueChange = { costs[i] = row.copy(label = it); hint = null },
                                label = { Text("What") },
                                placeholder = { Text("e.g. Insurance") },
                                singleLine = true,
                                modifier = Modifier.weight(1f),
                            )
                            Spacer(Modifier.width(8.dp))
                            TextButton(onClick = { costs.removeAt(i) }) {
                                Text("Remove", color = MaterialTheme.colorScheme.error, fontSize = 12.sp)
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                        MoneyField("Amount", row.amount, goals.currency) { costs[i] = row.copy(amount = it) }
                        Spacer(Modifier.height(8.dp))
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            CADENCES.forEach { (wire, lbl) ->
                                PickChip(lbl, null, row.cadence == wire) {
                                    costs[i] = row.copy(cadence = wire, due = if (wire == "one-off") row.due else null)
                                }
                            }
                        }
                        if (row.cadence == "one-off") {
                            Spacer(Modifier.height(8.dp))
                            FieldLabel("Due date")
                            OptionalDateField(row.due, placeholder = "Pick a date") { costs[i] = row.copy(due = it) }
                        }
                    }
                }
                Spacer(Modifier.height(10.dp))
                Text(
                    if (validCosts.isEmpty())
                        "Add the costs this fund covers — insurance, tax, a lease residual — and it works out the " +
                            "monthly set-aside."
                    else
                        "An amount to aim for, not a reservation — nothing moves on its own. Repeating costs are " +
                            "averaged over the year; a dated one-off is spread over the months until it's due.",
                    color = tandem.muted, fontSize = 12.sp,
                )
            }
        }

        Spacer(Modifier.height(16.dp))
        FieldLabel("Held in fund (optional)")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            PickChip("— none —", null, fundId == null) { fundId = null }
            pickableFunds.forEach { f -> PickChip(f.name, null, fundId == f.id) { fundId = f.id } }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            "It moves no money — it just labels the bucket and picks that fund by default when you pay from it.",
            color = tandem.muted, fontSize = 12.sp,
        )

        if (canSetInitial) {
            Spacer(Modifier.height(14.dp))
            MoneyField("Already saved (starting balance)", initial, goals.currency) { initial = it }
            Spacer(Modifier.height(6.dp))
            Text(
                "Money you already had here before using TandemTab. It counts toward the balance and goal, but " +
                    "not toward your savings rate.",
                color = tandem.muted, fontSize = 12.sp,
            )
        }

        // Archive / delete live at the foot of the edit form, well below Save — they're the exits, not the point
        // of the sheet, and putting them in the pinned action bar would crowd the button people actually want.
        if (onArchive != null || onDelete != null) {
            Spacer(Modifier.height(22.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                onArchive?.let {
                    TextButton(onClick = it, enabled = !goals.saving) { Text("Archive", color = tandem.muted) }
                }
                onDelete?.let {
                    TextButton(onClick = it, enabled = !goals.saving) {
                        Text("Delete", color = MaterialTheme.colorScheme.error)
                    }
                }
            }
        }

        Hints(hint, goals.saveError)
    }
}

/** Confirm deleting a bucket. The domain may refuse (savings activity to preserve) — the dialog says so up front
 *  and offers archiving, which is the answer in that case, rather than letting the user find out via a 400. */
@Composable
fun DeleteBucketDialog(
    bucket: SavingBucketDto,
    goals: GoalsUi,
    onDismiss: () -> Unit,
    onArchive: () -> Unit,
    onDelete: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = { if (!goals.saving) onDismiss() },
        title = { Text("Delete ${bucket.name}?") },
        text = {
            Column {
                Text(
                    "This removes the bucket for good. If it has savings history, it can't be deleted — archive " +
                        "it instead and it stops showing up while keeping its record.",
                )
                goals.saveError?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = onDelete,
                enabled = !goals.saving,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
            ) {
                if (goals.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                else Text("Delete")
            }
        },
        dismissButton = {
            Row {
                TextButton(onClick = onDismiss, enabled = !goals.saving) { Text("Cancel") }
                TextButton(onClick = onArchive, enabled = !goals.saving) { Text("Archive") }
            }
        },
    )
}

@Composable
private fun MoneyField(label: String, value: String, currency: String, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        label = { Text(label) },
        prefix = { Text(currencySymbol(currency)) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
        modifier = Modifier.fillMaxWidth(),
    )
}

/** A [DateField] that can also be empty — these dates are genuinely optional, and defaulting one to today would
 *  quietly assert a loan started this morning. */
@Composable
private fun OptionalDateField(dateIso: String?, placeholder: String, onPick: (String?) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            if (dateIso == null) {
                TextButton(onClick = { onPick(LocalDate.now().toString()) }) { Text(placeholder) }
            } else {
                DateField(dateIso) { onPick(it) }
            }
        }
        if (dateIso != null) {
            TextButton(onClick = { onPick(null) }) { Text("Clear", fontSize = 12.sp) }
        }
    }
}
