package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.GoalsUi
import com.tandemtab.app.data.SavingDepositRowDto
import com.tandemtab.app.data.SavingMovementRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/** One row of the activity list, whichever ledger it came from. */
private sealed interface Activity {
    val date: String

    data class Deposit(val row: SavingDepositRowDto) : Activity {
        override val date get() = row.date
    }

    data class Movement(val row: SavingMovementRowDto) : Activity {
        override val date get() = row.date
    }
}

/**
 * "This period's activity" on Goals — every saving that arrived and every movement of money already saved, each
 * with the action that reverses it.
 *
 * Until this existed the phone showed savings as a set of balances with no history: a deposit could be made and
 * never edited or undone, and the three movement endpoints had no row to hang an undo on. Both lists were missing
 * from the Kotlin view DTO entirely, so nothing was being hidden — there was nothing to hide.
 */
@Composable
fun SavingsActivity(
    goals: GoalsUi,
    fmt: (Double) -> String,
    onEditDeposit: (allocationId: String, amount: Double, onDone: () -> Unit) -> Unit,
    onRemoveDeposit: (allocationId: String, onDone: () -> Unit) -> Unit,
    onUndoMovement: (allocationId: String, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    var editing by remember { mutableStateOf<SavingDepositRowDto?>(null) }
    var confirmRemove by remember { mutableStateOf<Activity?>(null) }

    val rows = remember(goals.deposits, goals.movements) {
        (goals.deposits.map { Activity.Deposit(it) } + goals.movements.map { Activity.Movement(it) })
            .sortedByDescending { it.date }
    }
    if (rows.isEmpty()) return

    Spacer(Modifier.height(20.dp))
    Text(
        "THIS PERIOD'S ACTIVITY", fontSize = 10.sp, letterSpacing = 1.3.sp,
        fontWeight = FontWeight.Bold, color = tandem.muted,
    )
    Spacer(Modifier.height(8.dp))
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp))
            .background(MaterialTheme.colorScheme.surface)
            .clip(RoundedCornerShape(14.dp)),
    ) {
        rows.forEachIndexed { i, a ->
            when (a) {
                is Activity.Deposit -> ActivityRow(
                    title = a.row.bucketName,
                    // Money arriving reads as a gain, so it takes the positive colour and a plus — the same
                    // convention the ledger uses, rather than every savings number being spend-red.
                    detail = listOfNotNull("Added", a.row.note?.takeIf { it.isNotBlank() }).joinToString(" · "),
                    amount = "+" + fmt(a.row.amount),
                    amountColor = tandem.positive,
                    date = a.row.date,
                    onEdit = { editing = a.row },
                    onUndo = { confirmRemove = a },
                )

                is Activity.Movement -> ActivityRow(
                    title = a.row.bucketName,
                    detail = movementLabel(a.row),
                    // A movement is not spending — it is money doing the job it was saved for — so it stays
                    // neutral rather than red. Only the direction is stated, by the label.
                    amount = fmt(a.row.amount),
                    amountColor = MaterialTheme.colorScheme.onSurface,
                    date = a.row.date,
                    onEdit = null,
                    // The SERVER decides this. A "spent" row is a real movement whose undo the endpoint refuses,
                    // and the incoming half of a transfer is the outgoing half's reversal wearing a second button.
                    onUndo = if (a.row.undoable) ({ confirmRemove = a }) else null,
                )
            }
            if (i < rows.lastIndex) {
                Spacer(Modifier.fillMaxWidth().height(1.dp).background(tandem.hairline))
            }
        }
    }
    goals.saveError?.let {
        Spacer(Modifier.height(8.dp))
        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
    }

    editing?.let { row ->
        EditDepositDialog(
            row = row, goals = goals, fmt = fmt,
            onSave = { amount -> onEditDeposit(row.id, amount) { editing = null } },
            onDismiss = { editing = null },
        )
    }

    confirmRemove?.let { a ->
        val isDeposit = a is Activity.Deposit
        AlertDialog(
            onDismissRequest = { if (!goals.saving) confirmRemove = null },
            title = { Text(if (isDeposit) "Remove this saving?" else "Undo this?") },
            text = {
                Column {
                    Text(
                        when (a) {
                            is Activity.Deposit ->
                                "${fmt(a.row.amount)} goes back to what's free to spend, and ${a.row.bucketName} " +
                                    "drops by the same amount."
                            is Activity.Movement -> undoExplanation(a.row, fmt)
                        },
                    )
                    goals.saveError?.let {
                        Spacer(Modifier.height(10.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        when (a) {
                            is Activity.Deposit -> onRemoveDeposit(a.row.id) { confirmRemove = null }
                            is Activity.Movement -> onUndoMovement(a.row.id) { confirmRemove = null }
                        }
                    },
                    enabled = !goals.saving,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                ) {
                    if (goals.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                    else Text(if (isDeposit) "Remove" else "Undo")
                }
            },
            dismissButton = { TextButton(onClick = { confirmRemove = null }, enabled = !goals.saving) { Text("Cancel") } },
        )
    }
}

/** What a movement did, in the words the row needs — the counterpart is the whole point of the sentence. */
private fun movementLabel(m: SavingMovementRowDto): String {
    val what = when (m.kind) {
        "to-budget" -> m.counterpart?.let { "Moved into $it's budget" } ?: "Moved into the budget"
        "transfer-out" -> m.counterpart?.let { "Moved to $it" } ?: "Moved to another bucket"
        "transfer-in" -> m.counterpart?.let { "Moved from $it" } ?: "Moved from another bucket"
        "spent" -> m.counterpart?.let { "Spent on $it" } ?: "Spent"
        else -> "Deployed"
    }
    return listOfNotNull(what, m.note?.takeIf { it.isNotBlank() }).joinToString(" · ")
}

private fun undoExplanation(m: SavingMovementRowDto, fmt: (Double) -> String): String = when (m.kind) {
    "to-budget" -> "${fmt(m.amount)} goes back into ${m.bucketName} and comes off " +
        "${m.counterpart ?: "the category"}'s budget for this period."
    "transfer-out" -> "Both halves of the move are reversed: ${fmt(m.amount)} returns to ${m.bucketName} " +
        "from ${m.counterpart ?: "the other bucket"}."
    else -> "${fmt(m.amount)} returns to ${m.bucketName}, and the money that left the account comes back with it."
}

@Composable
private fun ActivityRow(
    title: String,
    detail: String,
    amount: String,
    amountColor: androidx.compose.ui.graphics.Color,
    date: String,
    onEdit: (() -> Unit)?,
    onUndo: (() -> Unit)?,
) {
    val tandem = LocalTandemColors.current
    Row(Modifier.fillMaxWidth().padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
            Spacer(Modifier.height(2.dp))
            Text("$detail · ${formatDay(date)}", fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Text(amount, fontWeight = FontWeight.Bold, color = amountColor)
        onEdit?.let {
            Icon(
                TandemIcons.Pencil, contentDescription = "Edit", tint = tandem.muted,
                modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = it).padding(6.dp).size(17.dp),
            )
        }
        onUndo?.let {
            Icon(
                TandemIcons.Rotate, contentDescription = "Undo", tint = tandem.muted,
                modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = it).padding(6.dp).size(17.dp),
            )
        }
    }
}

@Composable
private fun EditDepositDialog(
    row: SavingDepositRowDto,
    goals: GoalsUi,
    fmt: (Double) -> String,
    onSave: (Double) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var text by remember(row) { mutableStateOf(row.amount.toString()) }
    val parsed = text.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    AlertDialog(
        onDismissRequest = { if (!goals.saving) onDismiss() },
        title = { Text("Change this saving") },
        text = {
            Column {
                Text("Into ${row.bucketName}. Its date stays as it is.", color = tandem.muted, fontSize = 13.sp)
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = text, onValueChange = { text = it },
                    label = { Text("Amount") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                )
                goals.saveError?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(onClick = { parsed?.let(onSave) }, enabled = !goals.saving && parsed != null) {
                if (goals.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                else Text("Save")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !goals.saving) { Text("Cancel") } },
    )
}
