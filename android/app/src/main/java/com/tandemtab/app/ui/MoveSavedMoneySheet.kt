package com.tandemtab.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.GoalsUi
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.SavingBucketDto
import com.tandemtab.app.ui.theme.LocalTandemColors

/** The three things you can do with money that is already saved. */
private enum class MoveMode(val label: String) {
    Deploy("Deploy it"),
    ToBudget("Into a budget"),
    ToBucket("To another goal"),
}

/**
 * "Move saved money" — the three movement commands behind one sheet, mirroring what the domain actually offers:
 * deploy a bucket to its purpose (money leaves the account, but not as spending), mature it into this period's
 * budget for a category, or move it to another bucket.
 *
 * One sheet with a mode switch rather than three entry points on the row: they answer the same question ("this
 * money is saved — now what?") and three more pills on every goal is how a card stops being readable.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun MoveSavedMoneySheet(
    bucket: SavingBucketDto,
    goals: GoalsUi,
    spending: SpendingUi,
    onDismiss: () -> Unit,
    onDisburse: (fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onToBudget: (categoryId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onToBucket: (toBucketId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = sheetMoney(goals.currency)

    val cats = spending.categories
    val funds = spending.funds
    val others = remember(goals.buckets, bucket.id) { goals.buckets.filter { it.id != bucket.id && !it.archived } }

    // A debt bucket exists to be deployed, so it opens on that; everything else opens on the budget move, which is
    // the commoner everyday answer.
    var mode by remember(bucket.id) { mutableStateOf(if (bucket.kind == "debt") MoveMode.Deploy else MoveMode.ToBudget) }
    var fundId by remember(spending.loaded) { mutableStateOf(funds.firstOrNull { !it.synced }?.id ?: funds.firstOrNull()?.id) }
    var categoryId by remember(spending.loaded) { mutableStateOf(cats.firstOrNull()?.id) }
    var toBucketId by remember(others) { mutableStateOf(others.firstOrNull()?.id) }
    var amountText by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var date by remember { mutableStateOf(java.time.LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }

    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }
    val modes = MoveMode.entries.filter { it != MoveMode.ToBucket || others.isNotEmpty() }
    val targetChosen = when (mode) {
        MoveMode.Deploy -> fundId != null
        MoveMode.ToBudget -> categoryId != null
        MoveMode.ToBucket -> toBucketId != null
    }

    SheetScaffold(
        title = "Move ${bucket.name}",
        saving = goals.saving,
        canSave = amount != null && targetChosen && !goals.saving,
        onDismiss = onDismiss,
        onSave = {
            val amt = amount
            if (amt == null) { hint = "Enter an amount."; return@SheetScaffold }
            // The server enforces the real limit against the bucket; this only saves a round trip on the obvious case.
            if (amt > bucket.saved) { hint = "${bucket.name} only holds ${fmt(bucket.saved)}."; return@SheetScaffold }
            val n = note.ifBlank { null }
            when (mode) {
                MoveMode.Deploy -> fundId?.let { onDisburse(it, amt, date, n) { onDismiss() } } ?: run { hint = "Pick a wallet." }
                MoveMode.ToBudget -> categoryId?.let { onToBudget(it, amt, date, n) { onDismiss() } } ?: run { hint = "Pick a category." }
                MoveMode.ToBucket -> toBucketId?.let { onToBucket(it, amt, date, n) { onDismiss() } } ?: run { hint = "Pick a goal." }
            }
        },
        sheetState = sheetState,
    ) {
        Text("This bucket holds ${fmt(bucket.saved)}.", color = tandem.muted, fontSize = 13.sp)
        Spacer(Modifier.height(12.dp))

        FieldLabel("What happens to it")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            modes.forEach { m ->
                PickChip(label = m.label, icon = null, selected = mode == m) { mode = m; hint = null }
            }
        }
        Spacer(Modifier.height(6.dp))
        // Each mode does something genuinely different to the money, and the difference is not guessable from a
        // three-word chip — so it is stated rather than left to be discovered from the figures afterwards.
        Text(
            when (mode) {
                MoveMode.Deploy ->
                    "The money leaves the account for what it was saved for. It is not recorded as spending, so it " +
                        "never lands in your expenses or eats a budget."
                MoveMode.ToBudget ->
                    "The earmark is released into this period's budget for a category, so it becomes money you can " +
                        "spend now. Nothing leaves the account."
                MoveMode.ToBucket ->
                    "Moves it between goals. Your total saved does not change."
            },
            color = tandem.muted, fontSize = 12.sp,
        )
        Spacer(Modifier.height(14.dp))

        OutlinedTextField(
            value = amountText,
            onValueChange = { amountText = it; hint = null },
            label = { Text("Amount") },
            prefix = { Text(currencySymbol(goals.currency)) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        when (mode) {
            MoveMode.Deploy -> {
                FieldLabel("Out of which wallet")
                if (funds.isEmpty()) Text("Loading wallets…", color = tandem.muted)
                else FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    funds.forEach { f ->
                        PickChip(label = if (f.synced) "🏦 ${f.name}" else f.name, icon = null, selected = fundId == f.id) { fundId = f.id }
                    }
                }
            }

            MoveMode.ToBudget -> {
                FieldLabel("Into which budget")
                if (cats.isEmpty()) Text("Loading categories…", color = tandem.muted)
                else FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    cats.forEach { c -> PickChip(label = c.name, icon = c.icon, selected = categoryId == c.id) { categoryId = c.id } }
                }
            }

            MoveMode.ToBucket -> {
                FieldLabel("Into which goal")
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    others.forEach { b -> PickChip(label = b.name, icon = b.icon, selected = toBucketId == b.id) { toBucketId = b.id } }
                }
            }
        }

        Spacer(Modifier.height(14.dp))
        FieldLabel("Date")
        DateField(date) { date = it }
        Spacer(Modifier.height(14.dp))
        OutlinedTextField(
            value = note,
            onValueChange = { note = it },
            label = { Text("Note (optional)") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Hints(hint, goals.saveError)
    }
}
