package com.tandemtab.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.AlertDialog
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.ui.theme.LocalTandemColors

/**
 * Settle part of an expense onto another account — the phone's half of the web's settle modal.
 *
 * The case it exists for: you paid for something that belongs to another of your accounts (the household's, say)
 * out of this one. Settling records a matching expense over there and reduces this one by the same amount, in a
 * single two-account save on the server.
 *
 * ⚠️ **The destination fund and category are the server's choice, not a picker here.** The web offers both, but it
 * is a thick client that already holds the other account's whole structure; the phone would have to fetch it just
 * to fill two dropdowns whose sensible default is what the server picks anyway (first spendable wallet, first
 * category). If that turns out to matter, the fix is a read model for another account's pickers — not a snapshot.
 *
 * The Unsettle button is the reason the read model grew `settledToAccountId` in S108: the undo route is addressed
 * by the destination account, so before that the phone could see a settled expense and could not undo it.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SettleExpenseSheet(
    expense: ExpenseDto,
    spending: SpendingUi,
    otherAccounts: List<AccountSummaryDto>,
    onDismiss: () -> Unit,
    onSettle: (destinationAccountId: String, amount: Double, note: String?, onDone: () -> Unit) -> Unit,
    onUnsettle: (destinationAccountId: String, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = sheetMoney(spending.currency)

    // Already settled? Then this is an edit of that settlement, and the destination is fixed to the account it is
    // already on — re-settling somewhere else would leave the first account holding an expense nobody can see here.
    val settledTo = expense.settledToAccountId
    val editing = expense.isSettlementSource && settledTo != null

    var destinationId by remember(expense.id) {
        mutableStateOf(settledTo ?: otherAccounts.firstOrNull()?.id)
    }
    // The amount already moved when editing; otherwise the whole expense, which is the common case — you paid for
    // something that was entirely theirs.
    var amountText by remember(expense.id) {
        mutableStateOf(trimAmount(if (editing) expense.settledAmount else expense.amount))
    }
    var note by remember(expense.id) { mutableStateOf("") }
    var hint by remember { mutableStateOf<String?>(null) }
    var confirmingUnsettle by remember { mutableStateOf(false) }

    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }
    // What is on the row plus what has already moved: the original, and the cap the server enforces.
    val settleable = expense.amount + expense.settledAmount

    SheetScaffold(
        title = if (editing) "Change this settlement" else "Settle onto another account",
        saving = spending.saving,
        canSave = amount != null && destinationId != null && !spending.saving,
        onDismiss = onDismiss,
        onSave = {
            val dest = destinationId
            when {
                amount == null -> hint = "Enter an amount."
                dest == null -> hint = "Pick an account to settle onto."
                amount > settleable -> hint = "You can settle at most ${fmt(settleable)}."
                else -> onSettle(dest, amount, note.ifBlank { null }) { onDismiss() }
            }
        },
        sheetState = sheetState,
        saveLabel = if (editing) "Save" else "Settle",
    ) {
        Text(
            "${prettyDateLabel(expense.date)} · ${expense.categoryName} · ${fmt(settleable)}",
            color = tandem.muted,
            fontSize = 13.sp,
        )
        Spacer(Modifier.height(14.dp))

        if (otherAccounts.isEmpty()) {
            Text(
                "You have no other account in ${spending.currency} to settle onto. Settling needs two accounts " +
                    "in the same currency.",
                color = tandem.muted,
                fontSize = 13.sp,
            )
            return@SheetScaffold
        }

        OutlinedTextField(
            value = amountText,
            onValueChange = { amountText = it; hint = null },
            label = { Text("Amount to move") },
            prefix = { Text(currencySymbol(spending.currency)) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("Onto which account")
        if (editing) {
            // Fixed, and said plainly rather than shown as a picker with one option.
            Text(
                otherAccounts.firstOrNull { it.id == destinationId }?.name ?: "another account",
                color = MaterialTheme.colorScheme.onSurface,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                "Already settled onto this account. Unsettle first to move it somewhere else.",
                color = tandem.muted,
                fontSize = 12.sp,
            )
        } else {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                otherAccounts.forEach { a ->
                    PickChip(label = a.name, icon = null, selected = destinationId == a.id) { destinationId = a.id }
                }
            }
        }
        Spacer(Modifier.height(14.dp))

        OutlinedTextField(
            value = note,
            onValueChange = { note = it },
            label = { Text("Note (optional)") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(8.dp))
        Text(
            "This records the amount as an expense on that account — in its first wallet and category — and " +
                "reduces this expense by the same amount.",
            color = tandem.muted,
            fontSize = 12.sp,
        )

        if (editing && settledTo != null) {
            Spacer(Modifier.height(6.dp))
            TextButton(onClick = { confirmingUnsettle = true }, enabled = !spending.saving, modifier = Modifier.fillMaxWidth()) {
                Text("Unsettle", color = tandem.spent)
            }
        }

        Hints(hint, spending.saveError)
    }

    // Unsettling removes a real expense from the OTHER account, which is exactly the kind of write that should
    // never happen on one tap.
    if (confirmingUnsettle && settledTo != null) {
        AlertDialog(
            onDismissRequest = { if (!spending.saving) confirmingUnsettle = false },
            title = { Text("Undo this settlement?") },
            text = {
                Text(
                    "The ${fmt(expense.settledAmount)} expense on " +
                        "${otherAccounts.firstOrNull { it.id == settledTo }?.name ?: "the other account"} is removed, " +
                        "and this expense goes back to ${fmt(settleable)}.",
                )
            },
            confirmButton = {
                TextButton(onClick = { onUnsettle(settledTo) { confirmingUnsettle = false; onDismiss() } }) {
                    Text("Unsettle", color = tandem.spent)
                }
            },
            dismissButton = { TextButton(onClick = { confirmingUnsettle = false }) { Text("Cancel") } },
        )
    }
}
