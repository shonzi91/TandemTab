package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.SwapHoriz
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
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
import com.tandemtab.app.WalletsUi
import com.tandemtab.app.data.FundRowDto
import com.tandemtab.app.data.FundTransferRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * The Wallets tab: where the money lives — each fund with its balance, plus this period's transfers.
 * Thin `WalletsViewDto` rendered directly (balances computed server-side, incl. synced funds). Non-synced
 * funds surface Transfer + Add-income actions (S68), which open the write sheets.
 */
@Composable
fun WalletsScreen(
    wallets: WalletsUi,
    onRetry: () -> Unit,
    onPrepareTransfer: () -> Unit,
    onPrepareAddIncome: () -> Unit,
    onTransfer: (fromFundId: String, toFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onAddIncome: (fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
    bankEnabled: Boolean = false,
    bankConnected: Boolean = false,
    bankReviewCount: Int = 0,
    onOpenBank: () -> Unit = {},
) {
    val tandem = LocalTandemColors.current
    val fmt = rememberWalletsMoney(wallets.currency)
    var transferFrom by remember { mutableStateOf<FundRowDto?>(null) }
    var incomeTo by remember { mutableStateOf<FundRowDto?>(null) }

    when {
        wallets.loading && wallets.funds.isEmpty() ->
            Box(Modifier.fillMaxWidth().height(220.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }

        wallets.error != null ->
            Column(Modifier.fillMaxWidth().padding(top = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(wallets.error, color = MaterialTheme.colorScheme.error)
                Spacer(Modifier.height(8.dp))
                Text("Tap to retry", color = tandem.positive, modifier = Modifier.clickable(onClick = onRetry).padding(8.dp))
            }

        else -> {
            TotalHeader(fmt(wallets.current))
            Spacer(Modifier.height(14.dp))

            if (wallets.funds.isEmpty()) {
                Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                    Text("No funds yet.", color = tandem.muted)
                }
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    wallets.funds.forEach { f ->
                        FundRow(
                            f, fmt,
                            onTransfer = { onPrepareTransfer(); transferFrom = f },
                            onAddIncome = { onPrepareAddIncome(); incomeTo = f },
                        )
                    }
                }
            }

            // External accounts (bank connection) — only when the feature is available for this account.
            if (bankEnabled) {
                Spacer(Modifier.height(12.dp))
                BankEntryRow(connected = bankConnected, reviewCount = bankReviewCount, onClick = onOpenBank)
            }

            if (wallets.transfers.isNotEmpty()) {
                Spacer(Modifier.height(20.dp))
                Text(
                    "TRANSFERS THIS PERIOD",
                    fontSize = 11.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.8.sp,
                    color = tandem.muted, modifier = Modifier.padding(start = 4.dp, bottom = 8.dp),
                )
                Column(
                    Modifier.fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
                        .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp)),
                ) {
                    wallets.transfers.forEachIndexed { i, t ->
                        TransferRow(t, fmt)
                        if (i < wallets.transfers.lastIndex) {
                            Box(Modifier.fillMaxWidth().height(1.dp).padding(horizontal = 14.dp).background(tandem.hairline))
                        }
                    }
                }
            }
        }
    }

    transferFrom?.let { from ->
        TransferSheet(
            from = from,
            wallets = wallets,
            onDismiss = { transferFrom = null },
            onSubmit = { to, amt, date, note, onDone -> onTransfer(from.id, to, amt, date, note, onDone) },
        )
    }
    incomeTo?.let { to ->
        AddIncomeSheet(
            to = to,
            wallets = wallets,
            onDismiss = { incomeTo = null },
            onSubmit = { cat, amt, date, onDone -> onAddIncome(to.id, cat, amt, date, onDone) },
        )
    }
}

@Composable
private fun TotalHeader(total: String) {
    val tandem = LocalTandemColors.current
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(18.dp),
    ) {
        Text("TOTAL BALANCE", fontSize = 10.sp, letterSpacing = 1.3.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Spacer(Modifier.height(4.dp))
        Text(total, fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onBackground)
    }
}

@Composable
private fun FundRow(f: FundRowDto, fmt: (Double) -> String, onTransfer: () -> Unit, onAddIncome: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .padding(start = 14.dp, top = 6.dp, bottom = 6.dp, end = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(f.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                if (f.synced) {
                    Spacer(Modifier.width(6.dp))
                    Text("🏦", fontSize = 13.sp) // 🏦 bank-synced marker
                }
            }
            val sub = f.note?.takeIf { it.isNotBlank() } ?: if (f.synced) "Bank-synced" else null
            if (sub != null) Text(sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Text(fmt(f.balance), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
        // The two common actions sit next to the balance — only for a non-synced fund (a synced fund is
        // driven by its real bank balance, so manual moves don't apply).
        if (!f.synced) {
            IconButton(onClick = onTransfer, modifier = Modifier.size(38.dp)) {
                Icon(TandemIcons.Swap, "Transfer", tint = tandem.muted)
            }
            IconButton(onClick = onAddIncome, modifier = Modifier.size(38.dp)) {
                Icon(TandemIcons.Plus, "Add income", tint = tandem.muted)
            }
        }
    }
}

/** The "External accounts" row that opens the Bank sheet. Shows a review badge when imports are pending. */
@Composable
private fun BankEntryRow(connected: Boolean, reviewCount: Int, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable(onClick = onClick)
            .padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(TandemIcons.Bank, contentDescription = null, tint = tandem.catAccent, modifier = Modifier.size(20.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text("External accounts", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
            Text(
                if (connected) "Sync transactions from your bank" else "Link your bank to import transactions",
                fontSize = 12.sp, color = tandem.muted,
            )
        }
        if (reviewCount > 0) {
            Box(
                Modifier.size(24.dp).background(tandem.positive, RoundedCornerShape(999.dp)),
                contentAlignment = Alignment.Center,
            ) { Text("$reviewCount", color = androidx.compose.ui.graphics.Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold) }
            Spacer(Modifier.width(8.dp))
        }
        Icon(TandemIcons.Chevron, contentDescription = null, tint = tandem.muted, modifier = Modifier.size(16.dp))
    }
}

@Composable
private fun TransferRow(t: FundTransferRowDto, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth().padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text("${t.fromFundName} → ${t.toFundName}", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
            val sub = t.note?.takeIf { it.isNotBlank() } ?: formatTransferDate(t.date)
            Text(sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Text(fmt(t.amount), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
    }
}

private fun formatTransferDate(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))
}.getOrDefault(iso)

private fun rememberWalletsMoney(currencyCode: String): (Double) -> String {
    val nf = java.text.NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = java.util.Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}
