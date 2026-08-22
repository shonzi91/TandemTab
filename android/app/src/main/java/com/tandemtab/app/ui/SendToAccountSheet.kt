package com.tandemtab.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.size
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.WalletsUi
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.AccountTransferRowDto
import com.tandemtab.app.data.FundRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors

/**
 * Send money to another account you belong to, or change one already sent.
 *
 * Unlike a wallet-to-wallet transfer this money genuinely **leaves** — an outflow here and a matching deposit there,
 * written as one two-account save. The cap offered is the source wallet's balance *minus what is already set aside*,
 * so an earmark can't be sent away by accident; the server enforces it either way, this only says so first.
 *
 * ⚠️ Editing rewrites **both halves**, addressed by the pair id the row carries. A transfer written before that link
 * existed has no findable counterpart, so it never reaches this sheet — its row is not tappable.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SendToAccountSheet(
    from: FundRowDto?,
    wallets: WalletsUi,
    otherAccounts: List<AccountSummaryDto>,
    existing: AccountTransferRowDto?,
    onDismiss: () -> Unit,
    onSubmit: (destinationAccountId: String, fromFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onDelete: ((onDone: () -> Unit) -> Unit)?,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = moneyFormatter(wallets.currency)
    val editing = existing != null

    var destinationId by remember(existing) {
        mutableStateOf(existing?.toAccountId ?: otherAccounts.firstOrNull()?.id)
    }
    var sourceFundId by remember(existing, from?.id) { mutableStateOf(existing?.fromFundId ?: from?.id) }
    var amountText by remember(existing) { mutableStateOf(existing?.amount?.toString() ?: "") }
    var note by remember(existing) { mutableStateOf(existing?.note.orEmpty()) }
    var date by remember(existing) { mutableStateOf(existing?.date ?: java.time.LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }
    var confirmingDelete by remember { mutableStateOf(false) }

    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    SheetScaffold(
        title = if (editing) "Change this transfer" else "Send to another account",
        saving = wallets.saving,
        canSave = amount != null && destinationId != null && sourceFundId != null && !wallets.saving,
        onDismiss = onDismiss,
        onSave = {
            val dest = destinationId
            val src = sourceFundId
            val amt = amount
            when {
                amt == null -> hint = "Enter an amount."
                dest == null -> hint = "Pick an account to send it to."
                src == null -> hint = "Pick a wallet to send it from."
                else -> onSubmit(dest, src, amt, date, note.ifBlank { null }) { onDismiss() }
            }
        },
        sheetState = sheetState,
    ) {
        if (otherAccounts.isEmpty()) {
            Text(
                "You only belong to this account, so there is nowhere to send money to yet.",
                color = tandem.muted, fontSize = 13.sp,
            )
            return@SheetScaffold
        }

        // Which wallet it leaves from. Fixed when editing: moving an existing transfer between wallets is a
        // different operation from correcting its amount, and the edit endpoint would have to rewrite both halves
        // against a fund the other account never saw.
        val sourceFunds = wallets.funds.filter { !it.synced }
        if (editing) {
            from?.let { f ->
                Text("Out of ${f.name}", color = tandem.muted, fontSize = 13.sp)
                Spacer(Modifier.height(12.dp))
            }
        } else {
            FieldLabel("Out of which wallet")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                sourceFunds.forEach { f ->
                    PickChip(label = f.name, icon = f.icon, selected = sourceFundId == f.id) { sourceFundId = f.id }
                }
            }
            // The cap, stated. It is the balance minus what is already earmarked, which is why it can be lower than
            // the wallet's own figure and why showing the balance here instead would be misleading.
            sourceFunds.firstOrNull { it.id == sourceFundId }?.let { f ->
                Spacer(Modifier.height(6.dp))
                Text("${fmt(f.availableToTransferOut)} free to send — the rest is already set aside.", color = tandem.muted, fontSize = 12.sp)
            }
            Spacer(Modifier.height(14.dp))
        }

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

        FieldLabel("To which account")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            otherAccounts.forEach { a ->
                PickChip(label = a.name, icon = null, selected = destinationId == a.id) { destinationId = a.id }
            }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            "It arrives as income in that account's first wallet. Your total drops; theirs rises.",
            color = tandem.muted, fontSize = 12.sp,
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

        onDelete?.let {
            Spacer(Modifier.height(6.dp))
            TextButton(onClick = { confirmingDelete = true }, enabled = !wallets.saving, modifier = Modifier.fillMaxWidth()) {
                Text("Remove this transfer", color = tandem.spent)
            }
        }

        Hints(hint, wallets.saveError)
    }

    if (confirmingDelete && onDelete != null) {
        AlertDialog(
            onDismissRequest = { if (!wallets.saving) confirmingDelete = false },
            title = { Text("Remove this transfer?") },
            text = {
                Column {
                    Text(
                        "Both halves go: the money comes back to ${from?.name ?: "this account"}, and the deposit it " +
                            "made in ${existing?.toAccountName ?: "the other account"} is removed.",
                    )
                    wallets.saveError?.let {
                        Spacer(Modifier.height(10.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = { onDelete { confirmingDelete = false; onDismiss() } },
                    enabled = !wallets.saving,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                ) {
                    if (wallets.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                    else Text("Remove")
                }
            },
            dismissButton = { TextButton(onClick = { confirmingDelete = false }, enabled = !wallets.saving) { Text("Cancel") } },
        )
    }
}
