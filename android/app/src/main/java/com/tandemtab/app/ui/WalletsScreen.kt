package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
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
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.WalletsUi
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.AccountTransferRowDto
import com.tandemtab.app.data.FundCurrencyEdit
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
 * funds surface Transfer + Add-income actions (S68), which open the write sheets. S95 adds the management
 * half — create a fund, edit it, archive/restore it, remove one, and edit or undo a transfer.
 */
@Composable
fun WalletsScreen(
    wallets: WalletsUi,
    onRetry: () -> Unit,
    onPrepareTransfer: () -> Unit,
    onPrepareAddIncome: () -> Unit,
    onTransfer: (fromFundId: String, toFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onAddIncome: (fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
    onPrepareFund: () -> Unit = {},
    onSaveFund: (fundId: String?, name: String, icon: String?, note: String?, openingBalance: Double?, currency: FundCurrencyEdit?, onDone: () -> Unit) -> Unit = { _, _, _, _, _, _, _ -> },
    // Holding a wallet in another currency is the Pro half of trips. False draws the crowned row instead of the
    // fields; clearing an existing one is never gated, which is why the editor still sends a cleared pair.
    canHoldForeignCash: Boolean = true,
    onProBlocked: () -> Unit = {},
    // Statement import is its own Pro feature, separate from the wallet-currency one above — so it raises its own
    // prompt. A gate that names the wrong feature explains the wrong refusal.
    canImport: Boolean = true,
    onOpenImport: () -> Unit = {},
    onImportProBlocked: () -> Unit = {},
    onArchiveFund: (fundId: String, archived: Boolean, moveBalanceTo: String?, amount: Double, onDone: () -> Unit) -> Unit = { _, _, _, _, _ -> },
    onDeleteFund: (fundId: String, moveOpeningBalancesTo: String?, onDone: () -> Unit) -> Unit = { _, _, _ -> },
    onEditTransfer: (transferId: String, fromFundId: String, toFundId: String, amount: Double, note: String?, onDone: () -> Unit) -> Unit = { _, _, _, _, _, _ -> },
    onDeleteTransfer: (transferId: String, onDone: () -> Unit) -> Unit = { _, _ -> },
    // The other accounts this user belongs to — the destinations for money leaving. Empty means there is nowhere
    // to send it, and the action is hidden rather than shown leading to an empty picker.
    otherAccounts: List<AccountSummaryDto> = emptyList(),
    onTransferToAccount: (destinationAccountId: String, fromFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit = { _, _, _, _, _, _ -> },
    onEditAccountTransfer: (pairId: String, destinationAccountId: String, amount: Double, fromFundId: String?, note: String?, date: String?, onDone: () -> Unit) -> Unit = { _, _, _, _, _, _, _ -> },
    onDeleteAccountTransfer: (pairId: String, destinationAccountId: String, onDone: () -> Unit) -> Unit = { _, _, _ -> },
    bankEnabled: Boolean = false,
    bankConnected: Boolean = false,
    bankReviewCount: Int = 0,
    // The live bank balance of the synced fund (from the bank connection) — shown on the synced row instead of the
    // app-internal balance (which is 0, since a synced fund isn't debited: the real bank balance is the source).
    syncedBalance: Double? = null,
    syncedBalanceCurrency: String? = null,
    onOpenBank: () -> Unit = {},
) {
    val tandem = LocalTandemColors.current
    val fmt = moneyFormatter(wallets.currency)
    var transferFrom by remember { mutableStateOf<FundRowDto?>(null) }
    var incomeTo by remember { mutableStateOf<FundRowDto?>(null) }
    // ⚠️ The editor holds an **id**, not a captured row (the S94 lesson). Editing refreshes the list, and a
    // captured row would keep showing the balance the fund had before the money moved. "Creating" is its own
    // flag rather than a null id, so a fund vanishing under the editor can't be mistaken for a new one.
    var creatingFund by remember { mutableStateOf(false) }
    var editingFundId by remember { mutableStateOf<String?>(null) }
    var archivingFundId by remember { mutableStateOf<String?>(null) }
    var deletingFundId by remember { mutableStateOf<String?>(null) }
    var editingTransferId by remember { mutableStateOf<String?>(null) }
    var deletingTransferId by remember { mutableStateOf<String?>(null) }
    // Same id-not-a-row rule as the fund editor: the list is re-read after every write.
    var sendingFrom by remember { mutableStateOf<FundRowDto?>(null) }
    var editingAccountTransferId by remember { mutableStateOf<String?>(null) }
    var showArchived by remember { mutableStateOf(false) }

    fun findFund(id: String?): FundRowDto? = id?.let { f ->
        wallets.funds.firstOrNull { it.id == f } ?: wallets.archivedFunds.firstOrNull { it.id == f }
    }
    val editingFund = findFund(editingFundId)
    val editingTransfer = editingTransferId?.let { id -> wallets.transfers.firstOrNull { it.id == id } }
    val editingAccountTransfer = editingAccountTransferId?.let { id -> wallets.accountTransfers.firstOrNull { it.id == id } }

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

            // "Where your money is" (the web's .fund-donut). It lives on THIS tab only: the web's Home donut is a
            // different chart — the expense breakdown — and the phone reaches that by swiping, so a ring whose job
            // was to promote it has no job left there. Here it describes the very list underneath it.
            FundDonut(
                funds = wallets.funds,
                syncedBalance = syncedBalance,
                total = fmt(wallets.current),
            )

            AddFundButton { onPrepareFund(); creatingFund = true }
            Spacer(Modifier.height(12.dp))

            if (wallets.funds.isEmpty()) {
                Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                    Text("No funds yet.", color = tandem.muted)
                }
            } else {
                val bankFmt = remember(syncedBalanceCurrency, wallets.currency) {
                    moneyFormatter(syncedBalanceCurrency ?: wallets.currency)
                }
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    wallets.funds.forEach { f ->
                        // A synced fund shows its live bank balance (once loaded), not the app-internal 0.
                        val liveBalance = if (f.synced) syncedBalance else null
                        FundRow(
                            f, fmt,
                            liveBalance = liveBalance,
                            liveFmt = bankFmt,
                            onTransfer = { onPrepareTransfer(); transferFrom = f },
                            onAddIncome = { onPrepareAddIncome(); incomeTo = f },
                            onEdit = { onPrepareFund(); editingFundId = f.id },
                        )
                    }
                }
            }

            // Archived funds stay reachable — archiving is what the server suggests when a removal is refused, so
            // hiding them with no way back would make that advice a dead end. Collapsed, as on the Goals tab.
            if (wallets.archivedFunds.isNotEmpty()) {
                Spacer(Modifier.height(14.dp))
                Text(
                    if (showArchived) "Hide archived (${wallets.archivedFunds.size})"
                    else "Show archived (${wallets.archivedFunds.size})",
                    color = tandem.muted,
                    fontSize = 13.sp,
                    modifier = Modifier.clickable { showArchived = !showArchived }.padding(vertical = 6.dp),
                )
                if (showArchived) {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        wallets.archivedFunds.forEach { f ->
                            ArchivedFundRow(
                                f, fmt,
                                onRestore = { onArchiveFund(f.id, false, null, 0.0) {} },
                                onRemove = { onPrepareFund(); deletingFundId = f.id },
                            )
                        }
                    }
                }
            }

            // External accounts (bank connection) — only when the feature is available for this account.
            if (bankEnabled) {
                Spacer(Modifier.height(12.dp))
                BankEntryRow(connected = bankConnected, reviewCount = bankReviewCount, onClick = onOpenBank)
            }

            // Import sits NEXT TO the bank row but OUTSIDE its gate, and the difference is the whole point.
            // ⚠️ It used to be inside `if (bankEnabled)`, which read naturally — "both answer money that happened
            // elsewhere" — and quietly made import unreachable for almost everyone. `bankEnabled` means external
            // sync is available: a configured provider, an allowlisted email, a verified address. Import needs
            // none of that. It parses a file already on the phone and posts the rows the reader approved, which
            // is exactly why it is the one that "works at every bank rather than only the connected ones" — and
            // that sentence was sitting two lines above the gate that made it false.
            // Its own gate is Pro, which `proLocked` already applies.
            Spacer(Modifier.height(if (bankEnabled) 10.dp else 12.dp))
            ImportEntryRow(proLocked = !canImport, onClick = { if (canImport) onOpenImport() else onImportProBlocked() })

            // One entry rather than a fourth icon on every wallet row — the sheet asks which wallet it comes out
            // of, which is a question, not a pre-selection worth three more taps' worth of chrome.
            if (otherAccounts.isNotEmpty()) {
                Spacer(Modifier.height(12.dp))
                OutlinedButton(
                    onClick = { onPrepareTransfer(); sendingFrom = wallets.funds.firstOrNull { !it.synced } },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Icon(TandemIcons.Swap, null, modifier = Modifier.size(17.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Send money to another account")
                }
            }

            // Money that LEFT for another account, kept apart from the wallet-to-wallet moves above: one changes
            // where the money sits, the other changes how much there is, and one list of both explains neither.
            if (wallets.accountTransfers.isNotEmpty()) {
                Spacer(Modifier.height(20.dp))
                Text(
                    "SENT TO OTHER ACCOUNTS",
                    fontSize = 11.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.8.sp,
                    color = tandem.muted, modifier = Modifier.padding(start = 4.dp, bottom = 8.dp),
                )
                Column(
                    Modifier.fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
                        .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp)),
                ) {
                    wallets.accountTransfers.forEachIndexed { i, t ->
                        AccountTransferRow(t, fmt) { onPrepareFund(); editingAccountTransferId = t.id }
                        if (i < wallets.accountTransfers.lastIndex) {
                            Box(Modifier.fillMaxWidth().height(1.dp).padding(horizontal = 14.dp).background(tandem.hairline))
                        }
                    }
                }
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
                        TransferRow(t, fmt) { onPrepareFund(); editingTransferId = t.id }
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
    sendingFrom?.let { from ->
        SendToAccountSheet(
            from = from,
            wallets = wallets,
            otherAccounts = otherAccounts,
            existing = null,
            onDismiss = { sendingFrom = null },
            onSubmit = { dest, src, amt, date, note, onDone -> onTransferToAccount(dest, src, amt, date, note, onDone) },
            onDelete = null,
        )
    }
    editingAccountTransfer?.let { t ->
        SendToAccountSheet(
            from = findFund(t.fromFundId),
            wallets = wallets,
            otherAccounts = otherAccounts,
            existing = t,
            onDismiss = { editingAccountTransferId = null },
            onSubmit = { dest, src, amt, date, note, onDone ->
                // Addressed by the PAIR id, never the row's own id — the edit rewrites the deposit on the far side too.
                t.pairId?.let { onEditAccountTransfer(it, dest, amt, src, note, date, onDone) }
            },
            onDelete = t.pairId?.let { pair ->
                { onDone: () -> Unit -> onDeleteAccountTransfer(pair, t.toAccountId.orEmpty(), onDone) }
            },
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

    if (creatingFund || editingFund != null) {
        FundEditorSheet(
            existing = editingFund,
            wallets = wallets,
            onDismiss = { creatingFund = false; editingFundId = null },
            onSave = onSaveFund,
            // Archive is offered only for a saved, non-synced fund: a synced fund's balance is the bank's, so
            // there'd be nothing to move out and nothing meaningful to hide.
            onArchive = editingFund?.takeIf { !it.synced && !it.archived }?.let { f ->
                { editingFundId = null; archivingFundId = f.id }
            },
            // A wallet that ALREADY holds foreign cash keeps its fields on a downgraded plan — otherwise the only
            // way back to an ordinary wallet would be hidden behind the paywall it is trying to leave.
            canHoldForeignCash = canHoldForeignCash || editingFund?.currency != null,
            onProLocked = onProBlocked,
        )
    }
    findFund(archivingFundId)?.let { f ->
        ArchiveFundDialog(
            fund = f,
            wallets = wallets,
            onDismiss = { archivingFundId = null },
            onConfirm = { moveTo -> onArchiveFund(f.id, true, moveTo, f.balance) { archivingFundId = null } },
        )
    }
    findFund(deletingFundId)?.let { f ->
        DeleteFundDialog(
            fund = f,
            wallets = wallets,
            onDismiss = { deletingFundId = null },
            onRestore = { onArchiveFund(f.id, false, null, 0.0) { deletingFundId = null } },
            onDelete = { moveTo -> onDeleteFund(f.id, moveTo) { deletingFundId = null } },
        )
    }
    editingTransfer?.let { t ->
        EditTransferSheet(
            transfer = t,
            wallets = wallets,
            onDismiss = { editingTransferId = null },
            onSave = { from, to, amt, note, onDone -> onEditTransfer(t.id, from, to, amt, note, onDone) },
            onDelete = { editingTransferId = null; deletingTransferId = t.id },
        )
    }
    deletingTransferId?.let { id ->
        wallets.transfers.firstOrNull { it.id == id }?.let { t ->
            DeleteTransferDialog(
                transfer = t,
                wallets = wallets,
                onDismiss = { deletingTransferId = null },
                onDelete = { onDeleteTransfer(t.id) { deletingTransferId = null } },
            )
        }
    }
}

/** The Wallets header's add affordance — a dashed-feeling full-width row, matching the Goals tab's, so it reads
 *  as "there's room for more" rather than competing with the bottom bar's FAB (which adds an expense). */
@Composable
private fun AddFundButton(onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.10f))
            .clickable(onClick = onClick)
            .padding(vertical = 12.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(TandemIcons.Plus, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(17.dp))
        Spacer(Modifier.width(7.dp))
        Text("Add a fund", color = MaterialTheme.colorScheme.primary, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** An archived fund: name + what it holds, with the two ways out. Deliberately flat — it's out of play. */
@Composable
private fun ArchivedFundRow(f: FundRowDto, fmt: (Double) -> String, onRestore: () -> Unit, onRemove: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(12.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
            .padding(start = 12.dp, top = 4.dp, bottom = 4.dp, end = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CatIcon(f.icon, f.name)
        Spacer(Modifier.width(8.dp))
        Text(f.name, color = tandem.muted, modifier = Modifier.weight(1f), maxLines = 1, fontSize = 14.sp)
        Text(fmt(f.balance), color = tandem.muted, fontSize = 13.sp)
        TextButton(onClick = onRestore) { Text("Restore", fontSize = 13.sp) }
        IconButton(onClick = onRemove, modifier = Modifier.size(38.dp)) {
            Icon(TandemIcons.Trash, "Remove ${f.name}", tint = tandem.spent, modifier = Modifier.size(18.dp))
        }
    }
}

// The web's FundPalette, verbatim — greens that stay distinguishable side by side. A synced fund is amber and a
// negative one red, both outside the rotation, because those two say something the hue is carrying rather than
// just separating one wallet from the next.
private val fundPalette = listOf(
    Color(0xFF1D9E75), Color(0xFF5DCAA5), Color(0xFF0F6E56),
    Color(0xFF37B6A0), Color(0xFF8FD9C4), Color(0xFF2A8F6B),
)
private val fundSyncedColor = Color(0xFFEAB308)

/**
 * "Where your money is": one arc per wallet, sized by its share of the total, with the total in the middle.
 *
 * ⚠️ **Only positive balances get an arc**, and that is not a rounding convenience — a donut is a part-to-whole
 * chart, and a negative balance is not a part of a total it reduces. An overdrawn wallet is named in the list
 * below with its own colour; drawing it as a slice would make the ring add up to something no figure on screen
 * agrees with. Hidden entirely when nothing is positive, rather than drawing an empty track that reads as a bug.
 */
@Composable
private fun FundDonut(funds: List<FundRowDto>, syncedBalance: Double?, total: String) {
    val tandem = LocalTandemColors.current
    // A synced wallet's app-internal balance is 0 — the real figure comes from the bank — so use the live one when
    // it has loaded, exactly as the rows below do. Without this the bank wallet silently vanishes from the ring.
    val slices = remember(funds, syncedBalance) {
        var i = 0
        funds.mapNotNull { f ->
            val amount = if (f.synced) (syncedBalance ?: 0.0) else f.balance
            if (amount <= 0.0) null
            else Triple(f.id, amount, if (f.synced) fundSyncedColor else fundPalette[i++ % fundPalette.size])
        }
    }
    val sum = slices.sumOf { it.second }
    if (slices.isEmpty() || sum <= 0.0) return

    Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) {
        val trackColor = MaterialTheme.colorScheme.outline
        Canvas(Modifier.size(180.dp)) {
            val stroke = Stroke(width = 26.dp.toPx(), cap = StrokeCap.Butt)
            val inset = 26.dp.toPx() / 2f
            val arcSize = Size(size.width - inset * 2, size.height - inset * 2)
            val topLeft = Offset(inset, inset)
            drawArc(trackColor, 0f, 360f, false, topLeft, arcSize, style = stroke)
            if (slices.size == 1) {
                // A full ring, not a 360° arc: an arc that starts and ends at the same angle leaves a visible seam
                // where its two butt caps meet. The web does the same, for the same reason.
                drawCircle(slices[0].third, radius = arcSize.width / 2f, style = stroke)
            } else {
                var start = -90f
                slices.forEach { (_, amount, color) ->
                    val sweep = (amount / sum * 360.0).toFloat()
                    drawArc(color, start, sweep, false, topLeft, arcSize, style = stroke)
                    start += sweep
                }
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text("TOTAL BALANCE", fontSize = 9.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
            Spacer(Modifier.height(2.dp))
            Text(total, fontSize = 20.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onBackground)
            Spacer(Modifier.height(2.dp))
            Text(
                "across ${slices.size} " + if (slices.size == 1) "fund" else "funds",
                fontSize = 11.sp,
                color = tandem.muted,
            )
        }
    }
    Spacer(Modifier.height(14.dp))
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
private fun FundRow(
    f: FundRowDto,
    fmt: (Double) -> String,
    onTransfer: () -> Unit,
    onAddIncome: () -> Unit,
    onEdit: () -> Unit,
    liveBalance: Double? = null,
    liveFmt: (Double) -> String = fmt,
) {
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
            val sub = f.note?.takeIf { it.isNotBlank() } ?: if (f.synced) (if (liveBalance != null) "Live bank balance" else "Bank-synced") else null
            if (sub != null) Text(sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        // Synced funds show the live bank balance once it's loaded; everything else shows the app balance.
        Text(if (liveBalance != null) liveFmt(liveBalance) else fmt(f.balance), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
        // The two common actions sit next to the balance — only for a non-synced fund (a synced fund is
        // driven by its real bank balance, so manual moves don't apply). Edit follows for every fund: a synced
        // fund can still be renamed or re-iconed, it just can't be moved money by hand.
        if (!f.synced) {
            IconButton(onClick = onTransfer, modifier = Modifier.size(38.dp)) {
                Icon(TandemIcons.Swap, "Transfer", tint = tandem.muted)
            }
            IconButton(onClick = onAddIncome, modifier = Modifier.size(38.dp)) {
                Icon(TandemIcons.Plus, "Add income", tint = tandem.muted)
            }
        }
        IconButton(onClick = onEdit, modifier = Modifier.size(38.dp)) {
            Icon(TandemIcons.Pencil, "Edit ${f.name}", tint = tandem.muted, modifier = Modifier.size(17.dp))
        }
    }
}

/** The "External accounts" row that opens the Bank sheet. Shows a review badge when imports are pending. */
@Composable
private fun ImportEntryRow(proLocked: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable(onClick = onClick)
            .padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(TandemIcons.Receipt, contentDescription = null, tint = tandem.catAccent, modifier = Modifier.size(20.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text("Import a statement", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
            Text("CSV, OFX or QIF — read on this phone", fontSize = 12.sp, color = tandem.muted)
        }
        // The crown appears only where the plan can't reach, so a Pro account never sees one.
        if (proLocked) {
            Icon(TandemIcons.Crown, contentDescription = "Part of Pro", tint = tandem.warn, modifier = Modifier.size(14.dp))
            Spacer(Modifier.width(8.dp))
        }
        Icon(TandemIcons.Chevron, contentDescription = null, tint = tandem.muted, modifier = Modifier.size(16.dp))
    }
}

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
private fun AccountTransferRow(t: AccountTransferRowDto, fmt: (Double) -> String, onEdit: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        // Only an editable row is tappable: a transfer written before the pair link existed has no counterpart the
        // edit can address, so opening an editor on it would present a form that cannot save.
        Modifier.fillMaxWidth().then(if (t.editable) Modifier.clickable(onClick = onEdit) else Modifier).padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                "${t.fromFundName} → ${t.toAccountName ?: "another account"}",
                fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1,
            )
            val sub = t.note?.takeIf { it.isNotBlank() } ?: formatTransferDate(t.date)
            Text(sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        // Money out of this account, so it reads like spending does — the wallet-to-wallet rows above stay neutral
        // because nothing leaves on those.
        Text("−${fmt(t.amount)}", fontWeight = FontWeight.Bold, color = tandem.spent)
        if (t.editable) {
            Spacer(Modifier.width(6.dp))
            Icon(TandemIcons.Pencil, contentDescription = "Edit transfer", tint = tandem.muted, modifier = Modifier.size(15.dp))
        }
    }
}

@Composable
private fun TransferRow(t: FundTransferRowDto, fmt: (Double) -> String, onEdit: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onEdit).padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text("${t.fromFundName} → ${t.toFundName}", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
            val sub = t.note?.takeIf { it.isNotBlank() } ?: formatTransferDate(t.date)
            Text(sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Text(fmt(t.amount), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
        Spacer(Modifier.width(6.dp))
        Icon(TandemIcons.Pencil, contentDescription = "Edit transfer", tint = tandem.muted, modifier = Modifier.size(15.dp))
    }
}

private fun formatTransferDate(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))
}.getOrDefault(iso)
