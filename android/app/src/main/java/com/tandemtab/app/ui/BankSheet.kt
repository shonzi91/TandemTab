package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
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
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.BankUi
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.PendingBankTransactionDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.abs

/**
 * The Bank (External accounts) sheet — mirrors the web's External-accounts modal + bank-review flow.
 * Gated by `bank.enabled` (allowlist + verified email, resolved server-side): the Wallets entry point only
 * shows the button when the feature is available, so this sheet assumes it is.
 *
 * Not connected → a consent note + "Connect Revolut" (opens the bank's consent page in a browser via the VM's
 * one-shot linkUrl, handled in HomeScreen). Connected → the institution, live balance, last-sync time, a "Sync
 * now" button, and each pending transaction to file as an expense (debit) / income (credit) or dismiss.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BankSheet(
    bank: BankUi,
    spending: SpendingUi,
    onConnect: () -> Unit,
    onSync: () -> Unit,
    onDisconnect: () -> Unit,
    onConfirmExpense: (externalId: String, categoryId: String, fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onConfirmIncome: (externalId: String, categoryId: String, fundId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
    onDismissPending: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = sheetMoney(bank.balanceCurrency ?: spending.currency)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier.fillMaxWidth().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()).padding(bottom = 28.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(TandemIcons.Bank, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
                Spacer(Modifier.width(8.dp))
                Text("External accounts", fontSize = 19.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
            }
            Spacer(Modifier.height(12.dp))

            bank.error?.let {
                AlertBox(it)
                Spacer(Modifier.height(12.dp))
            }

            if (!bank.connected) {
                Text(
                    "Link your Revolut account to pull transactions in automatically. You authorise read-only access " +
                        "through our regulated provider — no payments are ever made, and you can disconnect at any time.",
                    fontSize = 13.sp, color = tandem.muted,
                )
                Spacer(Modifier.height(16.dp))
                Button(onClick = onConnect, enabled = !bank.busy, modifier = Modifier.fillMaxWidth()) {
                    if (bank.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    else {
                        Icon(TandemIcons.Bank, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(8.dp))
                        Text("Connect Revolut")
                    }
                }
            } else {
                ConnectionHeader(bank, fmt)
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button(onClick = onSync, enabled = !bank.busy, modifier = Modifier.weight(1f)) {
                        if (bank.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                        else {
                            Icon(TandemIcons.Swap, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(8.dp))
                            Text("Sync now")
                        }
                    }
                    OutlinedButton(onClick = onDisconnect, enabled = !bank.busy) { Text("Disconnect", color = MaterialTheme.colorScheme.error) }
                }

                Spacer(Modifier.height(18.dp))
                if (bank.pending.isEmpty()) {
                    Box(Modifier.fillMaxWidth().height(80.dp), contentAlignment = Alignment.Center) {
                        Text("Nothing to review — you're all caught up.", color = tandem.muted, fontSize = 13.sp)
                    }
                } else {
                    Text(
                        "${bank.pending.size} TO REVIEW",
                        fontSize = 11.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.8.sp,
                        color = tandem.muted, modifier = Modifier.padding(bottom = 8.dp),
                    )
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        bank.pending.forEach { tx ->
                            PendingRow(
                                tx = tx, spending = spending, fmt = fmt, busy = bank.handlingId == tx.externalId,
                                onConfirmExpense = onConfirmExpense, onConfirmIncome = onConfirmIncome, onDismiss = onDismissPending,
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ConnectionHeader(bank: BankUi, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.background, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .padding(14.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(TandemIcons.Bank, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(bank.institutionName ?: "Your bank", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
            Spacer(Modifier.weight(1f))
            bank.balance?.let { Text(fmt(it), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface) }
        }
        bank.lastSyncedAt?.let {
            Spacer(Modifier.height(4.dp))
            Text("Last synced ${prettySyncTime(it)}", fontSize = 12.sp, color = tandem.muted)
        }
    }
}

@Composable
private fun PendingRow(
    tx: PendingBankTransactionDto,
    spending: SpendingUi,
    fmt: (Double) -> String,
    busy: Boolean,
    onConfirmExpense: (String, String, String, Double, String, String?, () -> Unit) -> Unit,
    onConfirmIncome: (String, String, String, Double, String, () -> Unit) -> Unit,
    onDismiss: (String) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val isCredit = tx.amount > 0
    var expanded by remember { mutableStateOf(false) }

    // Filing pickers: debits pick a spend category; credits pick an income source. The fund defaults to the
    // bank-synced fund when there is one (imports file there without moving the synced balance), else the first.
    val categories = if (isCredit) spending.incomeCategories else spending.categories
    val funds = spending.funds
    val defaultFund = funds.firstOrNull { it.synced }?.id ?: funds.firstOrNull()?.id
    var categoryId by remember { mutableStateOf(categories.firstOrNull()?.id) }
    var fundId by remember { mutableStateOf(defaultFund) }

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.background, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable { expanded = !expanded }
            .padding(14.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(tx.description.ifBlank { if (isCredit) "Money in" else "Card payment" }, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                Text(prettyTxDate(tx.date), fontSize = 12.sp, color = tandem.muted)
            }
            Spacer(Modifier.width(8.dp))
            Text(fmt(abs(tx.amount)), fontWeight = FontWeight.Bold, color = if (isCredit) tandem.positive else MaterialTheme.colorScheme.onSurface)
        }

        if (expanded) {
            Spacer(Modifier.height(12.dp))
            FieldLabel(if (isCredit) "Income source" else "Category")
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                categories.forEach { c ->
                    PickChip(c.name, c.icon, selected = c.id == categoryId, catName = c.name) { categoryId = c.id }
                }
            }
            Spacer(Modifier.height(10.dp))
            FieldLabel("Fund")
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                funds.forEach { f ->
                    PickChip(f.name, if (f.synced) "🏦" else null, selected = f.id == fundId) { fundId = f.id }
                }
            }
            Spacer(Modifier.height(14.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                TextButton(onClick = { onDismiss(tx.externalId) }, enabled = !busy) {
                    Text("Dismiss", color = tandem.muted)
                }
                Spacer(Modifier.weight(1f))
                Button(
                    onClick = {
                        val cat = categoryId ?: return@Button
                        val fund = fundId ?: return@Button
                        if (isCredit) onConfirmIncome(tx.externalId, cat, fund, tx.amount, tx.date) { expanded = false }
                        else onConfirmExpense(tx.externalId, cat, fund, tx.amount, tx.date, tx.description) { expanded = false }
                    },
                    enabled = !busy && categoryId != null && fundId != null,
                ) {
                    if (busy) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    else Text(if (isCredit) "Add income" else "Add expense")
                }
            }
        }
    }
}

@Composable
private fun AlertBox(message: String) {
    val tandem = LocalTandemColors.current
    Box(
        Modifier.fillMaxWidth()
            .background(tandem.warn.copy(alpha = 0.12f), RoundedCornerShape(12.dp))
            .border(1.dp, tandem.warn.copy(alpha = 0.5f), RoundedCornerShape(12.dp))
            .padding(12.dp),
    ) { Text(message, color = tandem.warn, fontSize = 13.sp) }
}

private fun prettyTxDate(iso: String): String = runCatching {
    val d = LocalDate.parse(iso)
    when (d) {
        LocalDate.now() -> "Today"
        LocalDate.now().minusDays(1) -> "Yesterday"
        else -> d.format(DateTimeFormatter.ofPattern("d MMM yyyy", Locale.getDefault()))
    }
}.getOrDefault(iso)

private fun prettySyncTime(iso: String): String = runCatching {
    java.time.OffsetDateTime.parse(iso).toLocalDateTime()
        .format(DateTimeFormatter.ofPattern("d MMM, HH:mm", Locale.getDefault()))
}.getOrDefault(iso)
