package com.tandemtab.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
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

/** Earmark money into [bucket] ("Add to savings"): raises "saved", lowers "free"; nothing leaves the balance. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AllocateSavingSheet(
    bucket: SavingBucketDto,
    goals: GoalsUi,
    onDismiss: () -> Unit,
    onSubmit: (amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var amountText by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var date by remember { mutableStateOf(java.time.LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }
    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    SheetScaffold(
        title = "Add to ${bucket.name}",
        saving = goals.saving,
        canSave = amount != null && !goals.saving,
        onDismiss = onDismiss,
        onSave = { if (amount == null) hint = "Enter an amount." else onSubmit(amount, date, note.ifBlank { null }) { onDismiss() } },
        sheetState = sheetState,
    ) {
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

/** Draw [bucket] down as a real expense ("Spend from savings"): pick the spend category + fund; both the earmark
 *  and the balance fall. Pickers come from the /spending payload. */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SpendFromSavingsSheet(
    bucket: SavingBucketDto,
    goals: GoalsUi,
    spending: SpendingUi,
    onDismiss: () -> Unit,
    onSubmit: (categoryId: String, fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = moneyFormatter(goals.currency)
    val cats = spending.categories
    val funds = spending.funds

    var categoryId by remember(spending.loaded) { mutableStateOf(cats.firstOrNull()?.id) }
    var fundId by remember(spending.loaded) { mutableStateOf(funds.firstOrNull { !it.synced }?.id ?: funds.firstOrNull()?.id) }
    var amountText by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var date by remember { mutableStateOf(java.time.LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }
    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    SheetScaffold(
        title = "Spend from ${bucket.name}",
        saving = goals.saving,
        canSave = amount != null && categoryId != null && fundId != null && !goals.saving,
        onDismiss = onDismiss,
        onSave = {
            val c = categoryId; val f = fundId; val amt = amount
            if (amt == null) hint = "Enter an amount."
            else if (c == null || f == null) hint = "Pick a category and fund."
            else onSubmit(c, f, amt, date, note.ifBlank { null }) { onDismiss() }
        },
        sheetState = sheetState,
    ) {
        Text("Available in this bucket: ${fmt(bucket.saved)}", color = tandem.muted, fontSize = 13.sp)
        Spacer(Modifier.height(12.dp))

        if (cats.isEmpty() || funds.isEmpty()) {
            Text("Loading categories…", color = tandem.muted)
            Hints(hint, goals.saveError)
            return@SheetScaffold
        }

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

        FieldLabel("Spend as")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            cats.forEach { c -> PickChip(label = c.name, icon = c.icon, selected = categoryId == c.id) { categoryId = c.id } }
        }
        Spacer(Modifier.height(14.dp))

        FieldLabel("Fund")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            funds.forEach { f -> PickChip(label = if (f.synced) "🏦 ${f.name}" else f.name, icon = null, selected = fundId == f.id) { fundId = f.id } }
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
