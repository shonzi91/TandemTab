package com.tandemtab.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.WalletsUi
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.FundRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate

/**
 * Move money out of [from] into another fund (S68 fund-row Transfer). The server reconciles balances + this
 * period's transfers from the write's refreshed view.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun TransferSheet(
    from: FundRowDto,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onSubmit: (toFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = sheetMoney(wallets.currency)
    val targets = remember(wallets.funds, from.id) { wallets.funds.filter { it.id != from.id } }

    var toFundId by remember { mutableStateOf(targets.firstOrNull()?.id) }
    var amountText by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var date by remember { mutableStateOf(LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }
    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    SheetScaffold(
        title = "Transfer from ${from.name}",
        saving = wallets.saving,
        canSave = amount != null && toFundId != null && !wallets.saving,
        onDismiss = onDismiss,
        onSave = {
            val to = toFundId; val amt = amount
            if (amt == null) { hint = "Enter an amount." } else if (to == null) { hint = "Pick a destination fund." }
            else onSubmit(to, amt, date, note.ifBlank { null }) { onDismiss() }
        },
        sheetState = sheetState,
    ) {
        Text("Available to move: ${fmt(from.availableToTransferOut)}", color = tandem.muted, fontSize = 13.sp)
        Spacer(Modifier.height(12.dp))

        OutlinedTextField(
            value = amountText,
            onValueChange = { amountText = it; hint = null },
            label = { Text("Amount") },
            prefix = { Text(currencySymbol(wallets.currency)) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("To fund")
        if (targets.isEmpty()) {
            Text("Add another fund to transfer into.", color = tandem.muted, fontSize = 13.sp)
        } else {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                targets.forEach { f ->
                    PickChip(
                        label = if (f.synced) "🏦 ${f.name}" else f.name,
                        icon = null,
                        selected = toFundId == f.id,
                        onClick = { toFundId = f.id },
                    )
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
        Hints(hint, wallets.saveError)
    }
}

/**
 * Record income into [to] (S68 fund-row Add income): amount, an optional contribution source, date.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun AddIncomeSheet(
    to: FundRowDto,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onSubmit: (categoryId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var categoryId by remember { mutableStateOf(GENERAL_INCOME) }
    var amountText by remember { mutableStateOf("") }
    var date by remember { mutableStateOf(LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }
    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    SheetScaffold(
        title = "Add income to ${to.name}",
        saving = wallets.saving,
        canSave = amount != null && !wallets.saving,
        onDismiss = onDismiss,
        onSave = {
            val amt = amount
            if (amt == null) hint = "Enter an amount."
            else onSubmit(categoryId, amt, date) { onDismiss() }
        },
        sheetState = sheetState,
    ) {
        OutlinedTextField(
            value = amountText,
            onValueChange = { amountText = it; hint = null },
            label = { Text("Amount") },
            prefix = { Text(currencySymbol(wallets.currency)) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("Source")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            PickChip(label = "General income", icon = null, selected = categoryId == GENERAL_INCOME) { categoryId = GENERAL_INCOME }
            wallets.incomeCategories.forEach { c: CategoryOptionDto ->
                PickChip(label = c.name, icon = c.icon, selected = categoryId == c.id) { categoryId = c.id }
            }
        }
        Spacer(Modifier.height(14.dp))

        FieldLabel("Date")
        DateField(date) { date = it }
        Hints(hint, wallets.saveError)
    }
}
