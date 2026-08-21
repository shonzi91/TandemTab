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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.WalletsUi
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.FundCurrencyEdit
import com.tandemtab.app.data.FundRowDto
import com.tandemtab.app.data.FundTransferRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
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

// --- Fund management (S95) --------------------------------------------------------------------------------
// Create / rename / re-icon a fund, set what it opened the period with, archive or restore it, and remove one.
// Mirrors the web's Wallets section: an "Add a fund" affordance, an Edit pencil on each row, a collapsed archived
// list, and the destructive halves behind confirms. Archive and Remove use dialogs rather than in-sheet confirm
// blocks on purpose — a dialog floats above everything, so neither can be born under a floating action bar (the
// SheetShell hazard that has now bitten four times).

/**
 * New fund / Edit fund. [existing] null means create.
 *
 * ⚠️ The edit endpoint is a full overwrite of (name, note, icon), so the form is seeded from the row and every
 * field is sent back — including the icon, which is why `FundRowDto.icon` carries the *raw stored* value rather
 * than the display fallback. Seeding from the fallback would freeze a guessed icon into storage on first edit.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FundEditorSheet(
    existing: FundRowDto?,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onSave: (
        fundId: String?, name: String, icon: String?, note: String?, openingBalance: Double?,
        // null = the currency was not touched, so don't write it at all. A present pair is authoritative, and
        // (null, null) inside it means "put this back to an ordinary wallet".
        currency: FundCurrencyEdit?,
        onDone: () -> Unit,
    ) -> Unit,
    onArchive: (() -> Unit)?,
    // True when this account may hold foreign cash. False shows the locked row instead of the fields — the gate is
    // named where the feature is, rather than letting someone fill the form in and be refused on Save.
    canHoldForeignCash: Boolean = true,
    onProLocked: () -> Unit = {},
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val isNew = existing == null

    var name by remember(existing?.id) { mutableStateOf(existing?.name.orEmpty()) }
    // The palette works in icon *names*; null = "Auto" (guessed from the name), which is what an unset icon means.
    var iconName by remember(existing?.id) {
        mutableStateOf(existing?.icon?.takeIf { it.isNotBlank() }?.let { CategoryIcons.effective(it, existing.name) })
    }
    var note by remember(existing?.id) { mutableStateOf(existing?.note.orEmpty()) }
    var openingText by remember(existing?.id) {
        mutableStateOf(existing?.openingBalance?.takeIf { it != 0.0 }?.let { trimAmount(it) } ?: "")
    }
    // A three-letter code, upper-cased as it is typed. Blank = an ordinary wallet.
    var ccyText by remember(existing?.id) { mutableStateOf(existing?.currency.orEmpty()) }
    var rateText by remember(existing?.id) { mutableStateOf(existing?.rate?.let { trimAmount(it) } ?: "") }
    var hint by remember { mutableStateOf<String?>(null) }

    val opening = openingText.replace(',', '.').toDoubleOrNull()
    val ccy = ccyText.trim().uppercase().takeIf { it.isNotBlank() }
    val rate = rateText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0.0 }
    // A synced fund's balance is the real bank account's, so its opening balance isn't the user's to set.
    val showOpening = existing?.synced != true

    SheetScaffold(
        title = if (isNew) "New fund" else "Edit ${existing.name}",
        saving = wallets.saving,
        canSave = name.isNotBlank() && !wallets.saving,
        onDismiss = onDismiss,
        onSave = {
            if (name.isBlank()) {
                hint = "Give the fund a name."
            } else if (openingText.isNotBlank() && opening == null) {
                hint = "That opening balance isn't a number."
            } else if (ccy != null && ccy.length != 3) {
                hint = "A currency is a three-letter code, like SEK or GBP."
            } else if (ccy != null && rate == null) {
                // Refused rather than stored half-set: a currency with no rate cannot convert, and the entry form
                // would go on taking the amount at face value while the wallet claims to hold something else.
                hint = "Say what one $ccy is worth in ${wallets.currency}, or clear the currency."
            } else if (ccy == null && rateText.isNotBlank()) {
                hint = "A rate needs a currency to be a rate of."
            } else {
                // Only write an opening balance when there's something to say: on create when one was typed, on
                // edit when it actually changed. Sending an unchanged value would overwrite the period for nothing.
                val current = existing?.openingBalance ?: 0.0
                val wanted = opening ?: 0.0
                val send = when {
                    !showOpening -> null
                    isNew -> wanted.takeIf { it != 0.0 }
                    kotlin.math.abs(wanted - current) >= 0.005 -> wanted
                    else -> null
                }
                // Written only when it actually changed. The endpoint is Pro-gated on SETTING a currency, so a
                // Free user renaming an ordinary wallet must not trip that gate over a field they never touched.
                val ccyChanged = ccy != existing?.currency || rate != existing?.rate
                val currencyEdit = if (ccyChanged) FundCurrencyEdit(ccy, rate) else null
                onSave(existing?.id, name, iconName, note.ifBlank { null }, send, currencyEdit) { onDismiss() }
            }
        },
        sheetState = sheetState,
        saveLabel = if (isNew) "Add" else "Save",
    ) {
        OutlinedTextField(
            value = name,
            onValueChange = { name = it; hint = null },
            label = { Text("Name") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("Icon")
        IconPalette(selected = iconName, name = name) { iconName = it }
        Spacer(Modifier.height(14.dp))

        OutlinedTextField(
            value = note,
            onValueChange = { note = it },
            label = { Text("Note (optional)") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )

        // ── Foreign cash ────────────────────────────────────────────────────────────────────────
        // A holiday wallet: a pile of kronor bought at one rate. Everything spent from it is typed in that
        // currency and stored in the account's, so this is the setting that decides what the Amount field on the
        // add-expense sheet even means.
        Spacer(Modifier.height(16.dp))
        FieldLabel("Currency")
        if (!canHoldForeignCash) {
            // The crown shows only where the plan actually can't reach — a Pro account never sees one. Named here
            // rather than on Save, so nobody fills the pair in and is refused afterwards.
            Row(
                Modifier.fillMaxWidth().clickable { onProLocked() }.padding(vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(TandemIcons.Crown, contentDescription = "Part of Pro", tint = tandem.warn, modifier = Modifier.size(14.dp))
                Spacer(Modifier.width(8.dp))
                Text("Hold a wallet in another currency — with Pro", color = tandem.muted, fontSize = 13.sp)
            }
        } else {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = ccyText,
                    onValueChange = { ccyText = it.uppercase().filter { c -> c.isLetter() }.take(3); hint = null },
                    label = { Text("Code") },
                    placeholder = { Text(wallets.currency) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters),
                    modifier = Modifier.weight(1f),
                )
                OutlinedTextField(
                    value = rateText,
                    onValueChange = { rateText = it; hint = null },
                    // The label carries the direction. "Rate" alone is the field people invert — 1 SEK = 0.087 EUR
                    // and 1 EUR = 11.5 SEK are both "the rate", and one of them silently multiplies by 130x.
                    label = { Text(if (ccy != null) "1 $ccy is" else "Rate") },
                    prefix = { Text(currencySymbol(wallets.currency)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.weight(1f),
                )
            }
            Text(
                if (ccy == null)
                    "Leave this empty for an ordinary wallet. Set it for a pile of foreign cash — a holiday " +
                        "wallet — and everything you spend from it converts at the rate you bought it at."
                else
                    "Amounts spent from this wallet are typed in $ccy and stored in ${wallets.currency}. Card " +
                        "payments don't come through here — they leave your bank already converted.",
                fontSize = 12.sp, color = tandem.muted,
                modifier = Modifier.padding(top = 6.dp),
            )
        }

        if (showOpening) {
            Spacer(Modifier.height(14.dp))
            OutlinedTextField(
                value = openingText,
                onValueChange = { openingText = it; hint = null },
                label = { Text(if (isNew) "Opening balance this period (optional)" else "Opening balance this period") },
                prefix = { Text(currencySymbol(wallets.currency)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                modifier = Modifier.fillMaxWidth(),
            )
            Text(
                "What this fund held when the period started.",
                fontSize = 12.sp, color = tandem.muted,
                modifier = Modifier.padding(top = 6.dp),
            )
        } else if (existing?.synced == true) {
            Spacer(Modifier.height(12.dp))
            Text(
                "This fund mirrors your linked bank account, so its balance comes from the bank rather than from " +
                    "entries here.",
                fontSize = 12.sp, color = tandem.muted,
            )
        }

        onArchive?.let {
            Spacer(Modifier.height(10.dp))
            TextButton(onClick = it, enabled = !wallets.saving, modifier = Modifier.fillMaxWidth()) {
                Text("Archive fund", color = tandem.spent)
            }
        }
        Hints(hint, wallets.saveError)
    }
}

/**
 * Archiving a fund that still holds money has to land that money somewhere: the fund keeps its history and can be
 * restored, so leaving a balance inside a hidden fund would quietly remove it from the total. The picker *is* the
 * request being valid — Archive stays disabled until a destination is chosen, rather than hidden, so the button
 * explains what it wants by sitting under the list that answers it.
 */
@Composable
fun ArchiveFundDialog(
    fund: FundRowDto,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onConfirm: (moveBalanceTo: String?) -> Unit,
) {
    val fmt = sheetMoney(wallets.currency)
    // A synced fund can't receive the money: its balance is the bank's, so a transfer in wouldn't move anything.
    val targets = remember(wallets.funds, fund.id) { wallets.funds.filter { it.id != fund.id && !it.synced } }
    var moveTo by remember(fund.id) { mutableStateOf(targets.firstOrNull()?.id) }
    val needsMove = fund.balance > 0.0

    AlertDialog(
        onDismissRequest = { if (!wallets.saving) onDismiss() },
        title = { Text("Archive ${fund.name}?") },
        text = {
            Column {
                if (needsMove) {
                    Text(
                        "This fund still holds ${fmt(fund.balance)}. Move it to another fund first — the history " +
                            "stays intact and you can restore the fund anytime.",
                    )
                    Spacer(Modifier.height(12.dp))
                    if (targets.isEmpty()) {
                        Text(
                            "There's nowhere to move it — add another fund first.",
                            color = LocalTandemColors.current.warn, fontSize = 13.sp,
                        )
                    } else {
                        FieldLabel("Move balance to")
                        FundChips(targets, selected = moveTo) { moveTo = it }
                    }
                } else {
                    Text("Archiving hides this fund but keeps all of its history. You can restore it anytime.")
                }
                wallets.saveError?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = { onConfirm(if (needsMove) moveTo else null) },
                enabled = !wallets.saving && (!needsMove || moveTo != null),
            ) {
                if (wallets.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                else Text("Archive")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !wallets.saving) { Text("Cancel") } },
    )
}

/**
 * Removing a fund for good. The domain refuses when something still points at the fund (sub-funds, the only fund,
 * an expense or transfer referencing it) and says which — so the 400's message is shown verbatim rather than
 * guessed at client-side, and archiving is offered as the way out in the same breath.
 *
 * The "move opening balance" picker is offered whenever there's somewhere to move it, not only when *this*
 * period's opening is non-zero: the thin view carries the open period's opening balance, while the server drops
 * the fund's openings in **every** period, so a fund that was funded in an earlier month would otherwise lose
 * that money with no warning.
 */
@Composable
fun DeleteFundDialog(
    fund: FundRowDto,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onRestore: () -> Unit,
    onDelete: (moveOpeningBalancesTo: String?) -> Unit,
) {
    val fmt = sheetMoney(wallets.currency)
    val targets = remember(wallets.funds, fund.id) { wallets.funds.filter { it.id != fund.id } }
    var moveTo by remember(fund.id) { mutableStateOf<String?>(null) }   // default: don't move, as on the web

    AlertDialog(
        onDismissRequest = { if (!wallets.saving) onDismiss() },
        title = { Text("Remove ${fund.name}?") },
        text = {
            Column {
                Text(
                    "This removes the fund for good. If anything still points at it — an expense, a transfer, or " +
                        "it being your only fund — it can't be removed; restore it instead and it keeps its record.",
                )
                if (targets.isNotEmpty()) {
                    Spacer(Modifier.height(12.dp))
                    if (fund.openingBalance != 0.0) {
                        Text(
                            "It opened this period with ${fmt(fund.openingBalance)}. Move that to another fund, or " +
                                "remove it as-is and the balance goes with it.",
                            fontSize = 13.sp, color = LocalTandemColors.current.muted,
                        )
                        Spacer(Modifier.height(10.dp))
                    }
                    FieldLabel("Move opening balance to")
                    FundChips(targets, selected = moveTo, noneLabel = "Don't move") { moveTo = it }
                }
                wallets.saveError?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = { onDelete(moveTo) },
                enabled = !wallets.saving,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
            ) {
                if (wallets.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                else Text("Remove")
            }
        },
        dismissButton = {
            Row {
                TextButton(onClick = onDismiss, enabled = !wallets.saving) { Text("Cancel") }
                TextButton(onClick = onRestore, enabled = !wallets.saving) { Text("Restore") }
            }
        },
    )
}

/**
 * Edit a transfer already recorded this period, or undo it. The server keeps the original date (there's no date
 * field on the edit request), so this changes the *what*, never the *when*.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun EditTransferSheet(
    transfer: FundTransferRowDto,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onSave: (fromFundId: String, toFundId: String, amount: Double, note: String?, onDone: () -> Unit) -> Unit,
    onDelete: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    // Either side may have been archived since the transfer was made. Those funds are gone from `funds`, so add
    // them back as pickable options — otherwise re-saving would silently retarget the transfer to another fund.
    val options = remember(wallets.funds, wallets.archivedFunds, transfer.id) {
        val active = wallets.funds
        val extra = wallets.archivedFunds.filter { it.id == transfer.fromFundId || it.id == transfer.toFundId }
        active + extra.filterNot { a -> active.any { it.id == a.id } }
    }

    var fromId by remember(transfer.id) { mutableStateOf(transfer.fromFundId) }
    var toId by remember(transfer.id) { mutableStateOf(transfer.toFundId) }
    var amountText by remember(transfer.id) { mutableStateOf(trimAmount(transfer.amount)) }
    var note by remember(transfer.id) { mutableStateOf(transfer.note.orEmpty()) }
    var hint by remember { mutableStateOf<String?>(null) }
    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    SheetScaffold(
        title = "Edit transfer",
        saving = wallets.saving,
        canSave = amount != null && fromId != toId && !wallets.saving,
        onDismiss = onDismiss,
        onSave = {
            val amt = amount
            if (amt == null) hint = "Enter an amount."
            else if (fromId == toId) hint = "Pick two different funds."
            else onSave(fromId, toId, amt, note.ifBlank { null }) { onDismiss() }
        },
        sheetState = sheetState,
    ) {
        Text(prettyDateLabel(transfer.date), color = tandem.muted, fontSize = 13.sp)
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

        FieldLabel("From fund")
        FundChips(options, selected = fromId) { fromId = it ?: fromId }
        Spacer(Modifier.height(14.dp))

        FieldLabel("To fund")
        FundChips(options.filter { it.id != fromId }, selected = toId) { toId = it ?: toId }
        Spacer(Modifier.height(14.dp))

        OutlinedTextField(
            value = note,
            onValueChange = { note = it },
            label = { Text("Note (optional)") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )

        Spacer(Modifier.height(10.dp))
        TextButton(onClick = onDelete, enabled = !wallets.saving, modifier = Modifier.fillMaxWidth()) {
            Text("Remove this transfer", color = tandem.spent)
        }
        Hints(hint, wallets.saveError)
    }
}

/** Confirm undoing a transfer — every delete in this app asks first. */
@Composable
fun DeleteTransferDialog(
    transfer: FundTransferRowDto,
    wallets: WalletsUi,
    onDismiss: () -> Unit,
    onDelete: () -> Unit,
) {
    val fmt = sheetMoney(wallets.currency)
    AlertDialog(
        onDismissRequest = { if (!wallets.saving) onDismiss() },
        title = { Text("Remove this transfer?") },
        text = {
            Column {
                Text("${fmt(transfer.amount)} from ${transfer.fromFundName} to ${transfer.toFundName} goes back where it came from.")
                wallets.saveError?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = onDelete,
                enabled = !wallets.saving,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
            ) {
                if (wallets.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                else Text("Remove")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !wallets.saving) { Text("Cancel") } },
    )
}

/** A wrapping row of fund chips, optionally led by a "none" chip (used for "Don't move"). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun FundChips(
    funds: List<FundRowDto>,
    selected: String?,
    noneLabel: String? = null,
    onPick: (String?) -> Unit,
) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        noneLabel?.let { PickChip(label = it, icon = null, selected = selected == null) { onPick(null) } }
        funds.forEach { f ->
            PickChip(
                label = if (f.synced) "🏦 ${f.name}" else f.name,
                icon = null,
                selected = selected == f.id,
                onClick = { onPick(f.id) },
            )
        }
    }
}
