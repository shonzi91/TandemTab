package com.tandemtab.app.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.ui.platform.LocalContext
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.AccountBalanceWallet
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.Flag
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.rounded.Payments
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material.icons.automirrored.rounded.ReceiptLong
import androidx.compose.material.icons.automirrored.rounded.TrendingUp
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import com.tandemtab.app.UiState
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.MemberDto
import com.tandemtab.app.data.RunwayDto
import com.tandemtab.app.data.TargetDto
import com.tandemtab.app.ui.theme.BrandGreen
import com.tandemtab.app.ui.theme.BrandGreenDark
import com.tandemtab.app.ui.theme.Mint
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import kotlinx.coroutines.launch
import java.text.NumberFormat
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Currency
import java.util.Locale

// Mirrors the thick prod Dashboard's 4 tabs (Dashboard.razor: Overview/Budgets/Savings/Account),
// in the same order and labels.
private enum class NavDest(val label: String, val icon: ImageVector) {
    Home("Home", TandemIcons.House),
    Spending("Spending", TandemIcons.Receipt),
    Goals("Goals", TandemIcons.Flag),
    Wallets("Wallets", TandemIcons.Wallet),
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    state: UiState,
    darkTheme: Boolean,
    onToggleTheme: () -> Unit,
    onSelectAccount: (String) -> Unit,
    onCreateAccount: (String, String, () -> Unit) -> Unit,
    onSelectPeriod: (Int?) -> Unit,
    onPrepareStartNextPeriod: () -> Unit,
    onPreparePeriodEdit: () -> Unit,
    onStartNextPeriod: (Boolean, Boolean, Map<String, Double>, List<Pair<String, Double>>, () -> Unit) -> Unit,
    onReschedulePeriod: (Int, String, String, () -> Unit) -> Unit,
    onRemoveLatestPeriod: (() -> Unit) -> Unit,
    onSignOut: () -> Unit,
    onOpenSettings: () -> Unit,
    onChangePassword: (String, String) -> Unit,
    onResendVerification: () -> Unit,
    onUploadAvatar: (String) -> Unit,
    onBeginTwoFactor: () -> Unit,
    onConfirmTwoFactor: (String) -> Unit,
    onDisableTwoFactor: (String) -> Unit,
    onSetTwoFactorDisabling: (Boolean) -> Unit,
    onCancelTwoFactorSetup: () -> Unit,
    onDismissRecoveryCodes: () -> Unit,
    onRenameAccount: (String, () -> Unit) -> Unit,
    onSetSavingsTarget: (Double, () -> Unit) -> Unit,
    onLeaveAccount: (String?, () -> Unit) -> Unit,
    onDeleteAccount: (() -> Unit) -> Unit,
    onInvite: (String) -> Unit,
    onClearInviteResult: () -> Unit,
    onRemoveMember: (String, () -> Unit) -> Unit,
    onTransferOwnership: (String, () -> Unit) -> Unit,
    onAcceptInvitation: (String) -> Unit,
    onDeclineInvitation: (String) -> Unit,
    onLoadSpending: (Boolean) -> Unit,
    onLoadGoals: (Boolean) -> Unit,
    onLoadWallets: (Boolean) -> Unit,
    onLoadBank: (Boolean) -> Unit,
    onConnectBank: () -> Unit,
    onSyncBank: () -> Unit,
    onDisconnectBank: () -> Unit,
    onConfirmBankExpense: (String, String, String, Double, String, String?, () -> Unit) -> Unit,
    onConfirmBankIncome: (String, String, String, Double, String, () -> Unit) -> Unit,
    onDismissBankPending: (String) -> Unit,
    onBankLinkUrlHandled: () -> Unit,
    onLoadHealth: (Boolean) -> Unit,
    onLoadRecurring: (Boolean) -> Unit,
    onConfirmRecurring: (String, Double) -> Unit,
    onSkipRecurring: (String) -> Unit,
    onUnskipRecurring: (String) -> Unit,
    onAddRecurring: (com.tandemtab.app.data.AddRecurringRequest, () -> Unit) -> Unit,
    onUpdateRecurring: (String, com.tandemtab.app.data.UpdateRecurringRequest, () -> Unit) -> Unit,
    onSetRecurringActive: (String, Boolean) -> Unit,
    onDeleteRecurring: (String, () -> Unit) -> Unit,
    onPrepareAdd: () -> Unit,
    onPrepareEditLast: () -> Unit,
    onPrepareEditLastIncome: () -> Unit,
    onEditDeposit: (String, String, String, Double, String, () -> Unit) -> Unit,
    onClearEditingIncome: () -> Unit,
    onBeginEditExpense: (com.tandemtab.app.data.ExpenseDto) -> Unit,
    onDeleteExpense: (com.tandemtab.app.data.ExpenseDto) -> Unit,
    onSetBudget: (String, Double, () -> Unit) -> Unit,
    onRemoveBudget: (String, () -> Unit) -> Unit,
    onAddCategory: (String, String?, String?, (String?) -> Unit) -> Unit,
    onEditCategory: (String, String, String?, () -> Unit) -> Unit,
    onArchiveCategory: (String, () -> Unit) -> Unit,
    onLoadTrips: (Boolean) -> Unit,
    onSaveTrip: (String?, String, String, String, String?, String?, String?, Double?, String?, () -> Unit) -> Unit,
    onDeleteTrip: (String, () -> Unit) -> Unit,
    onStartTrip: (String, Boolean) -> Unit,
    onFinishTrip: (String, Boolean) -> Unit,
    onAttachExpenseToTrip: (String, String?, () -> Unit) -> Unit,
    onOpenTrip: (String?) -> Unit,
    onPrepareTrip: () -> Unit,
    onUseTripSavings: (tripId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
    onDisburse: (bucketId: String, fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onToBudget: (bucketId: String, categoryId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onTransferSavings: (fromBucketId: String, toBucketId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onEditSavingDeposit: (allocationId: String, amount: Double, onDone: () -> Unit) -> Unit,
    onRemoveSavingDeposit: (allocationId: String, onDone: () -> Unit) -> Unit,
    onUndoSavingMovement: (allocationId: String, onDone: () -> Unit) -> Unit,
    onLoadTags: (Boolean) -> Unit,
    onPrepareTags: () -> Unit,
    onAddTag: (name: String, onDone: () -> Unit) -> Unit,
    onEditTag: (id: String, name: String, icon: String?, categoryId: String?, onDone: () -> Unit) -> Unit,
    onSetTagArchived: (id: String, archived: Boolean) -> Unit,
    onDeleteTag: (id: String, onDone: () -> Unit) -> Unit,
    onClearEditing: () -> Unit,
    onAddExpenses: (List<com.tandemtab.app.data.AddExpenseRequest>, () -> Unit) -> Unit,
    onEditExpense: (String, com.tandemtab.app.data.AddExpenseRequest, () -> Unit) -> Unit,
    onAddIncomeQuick: (String, String, Double, String, () -> Unit) -> Unit,
    onPrepareTransfer: () -> Unit,
    onPrepareAddIncome: () -> Unit,
    onTransfer: (String, String, Double, String, String?, () -> Unit) -> Unit,
    onAddIncome: (String, String, Double, String, () -> Unit) -> Unit,
    onPrepareFund: () -> Unit,
    onSaveFund: (String?, String, String?, String?, Double?, () -> Unit) -> Unit,
    onArchiveFund: (String, Boolean, String?, Double, () -> Unit) -> Unit,
    onDeleteFund: (String, String?, () -> Unit) -> Unit,
    onEditTransfer: (String, String, String, Double, String?, () -> Unit) -> Unit,
    onDeleteTransfer: (String, () -> Unit) -> Unit,
    onTransferToAccount: (destinationAccountId: String, fromFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) -> Unit,
    onEditAccountTransfer: (pairId: String, destinationAccountId: String, amount: Double, fromFundId: String?, note: String?, date: String?, onDone: () -> Unit) -> Unit,
    onDeleteAccountTransfer: (pairId: String, destinationAccountId: String, onDone: () -> Unit) -> Unit,
    onPrepareAllocate: () -> Unit,
    onPrepareSpend: () -> Unit,
    onAllocate: (String, Double, String, String?, () -> Unit) -> Unit,
    onSpendFromSavings: (String, String, String, Double, String, String?, () -> Unit) -> Unit,
    onPrepareInstallment: () -> Unit,
    onLogInstallment: (String, Double, String, String, String, String?, () -> Unit) -> Unit,
    onPrepareBucket: () -> Unit,
    onSaveBucket: (String?, com.tandemtab.app.data.SaveSavingBucketRequest, () -> Unit) -> Unit,
    onArchiveBucket: (String, Boolean, () -> Unit) -> Unit,
    onDeleteBucket: (String, () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val scope = rememberCoroutineScope()
    // Tabs are the bottom bar's job; the horizontal swipe now moves between PERIODS. The four tabs are always one
    // tap away on a bar that is permanently on screen, so spending the only full-width gesture on them bought
    // nothing — while stepping through months previously needed the period chip, a menu and a tap.
    var dest by remember { mutableStateOf(NavDest.Home) }

    val snackbar = remember { SnackbarHostState() }
    var showAddExpense by remember { mutableStateOf(false) }
    var showHealth by remember { mutableStateOf(false) }
    var showRunway by remember { mutableStateOf(false) }
    var showRecurring by remember { mutableStateOf(false) }
    var showProfile by remember { mutableStateOf(false) }
    var showAccount by remember { mutableStateOf(false) }
    var showBank by remember { mutableStateOf(false) }
    var showNextPeriod by remember { mutableStateOf(false) }
    var showEditPeriod by remember { mutableStateOf(false) }
    var showRemovePeriod by remember { mutableStateOf(false) }
    var showCreateAccount by remember { mutableStateOf(false) }

    // "Edit last" flows through the ViewModel: prepareEditLast loads + picks the last expense into state.editingExpense,
    // which we watch here to raise the add sheet in edit mode.
    val editing = state.editingExpense

    Scaffold(
        modifier = Modifier.fillMaxSize().tandemCanvas(darkTheme, tandem.canvas),
        containerColor = Color.Transparent,
        topBar = {
            // Compact one-row header (no logo): account switcher · period · account-actions · profile.
            Row(
                Modifier.fillMaxWidth().statusBarsPadding().padding(start = 14.dp, end = 6.dp, top = 6.dp, bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                // Keep the brand mark (icon only, no wordmark) to anchor the compact header.
                TandemLogo(size = 24.dp)
                Spacer(Modifier.width(10.dp))
                Box(Modifier.weight(1f)) { AccountSwitcher(state, onSelectAccount, onCreateAccount = { showCreateAccount = true }) }
                PeriodSwitcher(
                    state = state,
                    onSelectPeriod = onSelectPeriod,
                    onStartNextPeriod = { onPrepareStartNextPeriod(); showNextPeriod = true },
                    onEditPeriodDates = { onPreparePeriodEdit(); showEditPeriod = true },
                    onRemovePeriod = { onPreparePeriodEdit(); showRemovePeriod = true },
                )
                IconButton(onClick = { onOpenSettings(); showAccount = true }) {
                    Icon(TandemIcons.Sliders, contentDescription = "Account", tint = tandem.muted)
                }
                IconButton(onClick = { onOpenSettings(); showProfile = true }) {
                    Icon(TandemIcons.User, contentDescription = "Profile", tint = tandem.muted)
                }
            }
        },
        // Custom bottom bar with the add-FAB cradled in the centre (tabs split 2-and-2). The FAB opens a speed-dial.
        bottomBar = {
            TandemBottomBar(
                current = dest,
                onSelect = { d -> dest = d },
                onAdd = { onPrepareAdd(); showAddExpense = true },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        // The add / edit sheet. The FAB opens it in add mode; the in-sheet "Edit last" pulls in the last expense
        // (via the VM's editingExpense), which switches this same sheet into edit mode.
        val editingIncome = state.editingDeposit
        if (showCreateAccount) {
            CreateAccountSheet(
                busy = state.busy,
                error = state.error,
                onDismiss = { showCreateAccount = false },
                onCreate = onCreateAccount,
            )
        }
        if (showAddExpense || editing != null || editingIncome != null) {
            AddSheet(
                spending = state.spending,
                trips = state.trips,
                editing = editing,
                editingDeposit = editingIncome,
                onEditLast = onPrepareEditLast,
                onEditLastIncome = onPrepareEditLastIncome,
                onDismiss = { showAddExpense = false; if (editing != null) onClearEditing(); if (editingIncome != null) onClearEditingIncome() },
                onSaveExpenses = onAddExpenses,
                onEditExpense = onEditExpense,
                onAddIncome = onAddIncomeQuick,
                onEditDeposit = onEditDeposit,
                onAddCategory = onAddCategory,
            )
        }
        LaunchedEffect(dest, state.selectedAccountId) {
            when (dest) {
                NavDest.Spending -> onLoadSpending(false)
                NavDest.Goals -> onLoadGoals(false)
                NavDest.Wallets -> { onLoadWallets(false); onLoadBank(false) }
                NavDest.Home -> { onLoadHealth(false); onLoadRecurring(false); onLoadGoals(false) }
            }
        }

        // Swipe left → the next period, right → the previous one, matching the ‹ › arrows' own direction. A whole
        // gesture rather than a pager because the app holds ONE period's data at a time: a pager would have to
        // render pages it has no content for, and a swipe that briefly shows the wrong month's figures is worse
        // than no animation. Nothing happens at either end of the list, and a drag under the threshold is a scroll.
        val periodIdx = state.selectedPeriod ?: state.currentPeriodIndex
        val firstIdx = state.periods.firstOrNull()?.index
        val lastIdx = state.periods.lastOrNull()?.index
        var dragX by remember { mutableStateOf(0f) }
        Box(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .pointerInput(periodIdx, state.periods.size) {
                    detectHorizontalDragGestures(
                        onDragStart = { dragX = 0f },
                        onDragEnd = {
                            val threshold = 96.dp.toPx()
                            if (dragX <= -threshold && lastIdx != null && periodIdx < lastIdx) {
                                onSelectPeriod(periodIdx + 1)
                            } else if (dragX >= threshold && firstIdx != null && periodIdx > firstIdx) {
                                onSelectPeriod(periodIdx - 1)
                            }
                            dragX = 0f
                        },
                        onDragCancel = { dragX = 0f },
                    ) { _, delta -> dragX += delta }
                },
        ) {
            run {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 24.dp),
                ) {
                    when (dest) {
                        NavDest.Home -> HomePage(
                            state,
                            darkTheme = darkTheme,
                            onOpenRecurring = { showRecurring = true },
                            onOpenHealth = { showHealth = true },
                            onOpenRunway = { showRunway = true },
                            onAcceptInvitation = onAcceptInvitation,
                            onDeclineInvitation = onDeclineInvitation,
                        )
                        NavDest.Spending -> SpendingScreen(
                            spending = state.spending,
                            trips = state.trips,
                            tags = state.tags,
                            onRetry = { onLoadSpending(true) },
                            onEdit = onBeginEditExpense,
                            onDelete = onDeleteExpense,
                            onSetBudget = onSetBudget,
                            onRemoveBudget = onRemoveBudget,
                            onAddCategory = onAddCategory,
                            onEditCategory = onEditCategory,
                            onArchiveCategory = onArchiveCategory,
                            onLoadTrips = { onLoadTrips(false) },
                            onSaveTrip = onSaveTrip,
                            onDeleteTrip = onDeleteTrip,
                            onStartTrip = onStartTrip,
                            onFinishTrip = onFinishTrip,
                            onAttachExpenseToTrip = onAttachExpenseToTrip,
                            onOpenTrip = onOpenTrip,
                            onPrepareTrip = onPrepareTrip,
                            onUseTripSavings = onUseTripSavings,
                            onLoadTags = { onLoadTags(false) },
                            onPrepareTags = onPrepareTags,
                            onAddTag = onAddTag,
                            onEditTag = onEditTag,
                            onSetTagArchived = onSetTagArchived,
                            onDeleteTag = onDeleteTag,
                        )
                        NavDest.Goals -> GoalsScreen(
                            goals = state.goals,
                            spending = state.spending,
                            onRetry = { onLoadGoals(true) },
                            onPrepareAllocate = onPrepareAllocate,
                            onPrepareSpend = onPrepareSpend,
                            onAllocate = onAllocate,
                            onSpend = onSpendFromSavings,
                            onPrepareInstallment = onPrepareInstallment,
                            onLogInstallment = onLogInstallment,
                            // The starting-balance field is setup-only (the server drops it once a second period
                            // exists), so it's shown only while the account still has one — mirrors the web.
                            canSetInitial = state.periods.size <= 1,
                            onPrepareBucket = onPrepareBucket,
                            onSaveBucket = onSaveBucket,
                            onArchiveBucket = onArchiveBucket,
                            onDeleteBucket = onDeleteBucket,
                            onDisburse = onDisburse,
                            onToBudget = onToBudget,
                            onTransferSavings = onTransferSavings,
                            onEditDeposit = onEditSavingDeposit,
                            onRemoveDeposit = onRemoveSavingDeposit,
                            onUndoMovement = onUndoSavingMovement,
                        )
                        NavDest.Wallets -> WalletsScreen(
                            wallets = state.wallets,
                            onRetry = { onLoadWallets(true) },
                            onPrepareTransfer = onPrepareTransfer,
                            onPrepareAddIncome = onPrepareAddIncome,
                            onTransfer = onTransfer,
                            onAddIncome = onAddIncome,
                            onPrepareFund = onPrepareFund,
                            onSaveFund = onSaveFund,
                            onArchiveFund = onArchiveFund,
                            onDeleteFund = onDeleteFund,
                            onEditTransfer = onEditTransfer,
                            onDeleteTransfer = onDeleteTransfer,
                            otherAccounts = state.accounts.filter { it.id != state.selectedAccountId },
                            onTransferToAccount = onTransferToAccount,
                            onEditAccountTransfer = onEditAccountTransfer,
                            onDeleteAccountTransfer = onDeleteAccountTransfer,
                            bankEnabled = state.bank.enabled,
                            bankConnected = state.bank.connected,
                            bankReviewCount = state.bank.pending.size,
                            syncedBalance = state.bank.balance,
                            syncedBalanceCurrency = state.bank.balanceCurrency,
                            onOpenBank = { onLoadBank(false); showBank = true },
                        )
                    }
                }
            }
        }

        if (showHealth && state.health.data?.hasData == true) {
            HealthSheet(health = state.health, onDismiss = { showHealth = false })
        }
        state.runway?.let { rw ->
            if (showRunway) {
                RunwaySheet(runway = rw, fmt = rememberCurrency(rw.currency), onDismiss = { showRunway = false })
            }
        }
        if (showRecurring) {
            RecurringSheet(
                recurring = state.recurring,
                onConfirm = onConfirmRecurring,
                onSkip = onSkipRecurring,
                onUnskip = onUnskipRecurring,
                onAdd = onAddRecurring,
                onUpdate = onUpdateRecurring,
                onSetActive = onSetRecurringActive,
                onDelete = onDeleteRecurring,
                onDismiss = { showRecurring = false },
            )
        }
        if (showProfile) {
            ProfileSheet(
                state = state,
                darkTheme = darkTheme,
                onToggleTheme = onToggleTheme,
                onChangePassword = onChangePassword,
                onResendVerification = onResendVerification,
                onUploadAvatar = onUploadAvatar,
                onBeginTwoFactor = onBeginTwoFactor,
                onConfirmTwoFactor = onConfirmTwoFactor,
                onDisableTwoFactor = onDisableTwoFactor,
                onSetTwoFactorDisabling = onSetTwoFactorDisabling,
                onCancelTwoFactorSetup = onCancelTwoFactorSetup,
                onDismissRecoveryCodes = onDismissRecoveryCodes,
                onSignOut = { showProfile = false; onSignOut() },
                onDismiss = { showProfile = false },
            )
        }
        if (showAccount) {
            AccountSheet(
                state = state,
                onRenameAccount = onRenameAccount,
                onSetSavingsTarget = onSetSavingsTarget,
                onOpenRecurring = { showAccount = false; showRecurring = true },
                onInvite = onInvite,
                onClearInviteResult = onClearInviteResult,
                onRemoveMember = onRemoveMember,
                onTransferOwnership = onTransferOwnership,
                onLeave = { newOwner -> onLeaveAccount(newOwner) { showAccount = false } },
                onDelete = { onDeleteAccount { showAccount = false } },
                onDismiss = { showAccount = false },
            )
        }
        // Period lifecycle. Rolling forward always acts on the newest month (never the one being browsed), so it
        // reads its dates + funds from there; changing dates acts on whichever month is on screen.
        if (showNextPeriod) {
            state.periods.lastOrNull()?.let { latest ->
                StartNextPeriodSheet(
                    closing = latest,
                    wallets = state.wallets,
                    ops = state.periodOps,
                    onDismiss = { showNextPeriod = false },
                    onSubmit = onStartNextPeriod,
                )
            }
        }
        if (showEditPeriod) {
            state.viewedPeriod?.let { p ->
                EditPeriodDatesSheet(
                    period = p,
                    ops = state.periodOps,
                    onDismiss = { showEditPeriod = false },
                    onSubmit = onReschedulePeriod,
                )
            }
        }
        if (showRemovePeriod) {
            state.periods.lastOrNull()?.let { latest ->
                RemovePeriodDialog(
                    latest = latest,
                    ops = state.periodOps,
                    onDismiss = { showRemovePeriod = false },
                    onConfirm = onRemoveLatestPeriod,
                )
            }
        }
        if (showBank) {
            BankSheet(
                bank = state.bank,
                spending = state.spending,
                onConnect = onConnectBank,
                onSync = onSyncBank,
                onDisconnect = onDisconnectBank,
                onConfirmExpense = onConfirmBankExpense,
                onConfirmIncome = onConfirmBankIncome,
                onDismissPending = onDismissBankPending,
                onDismiss = { showBank = false },
            )
        }
        // The bank-link URL is a one-shot: open the bank's consent page in a browser, then clear it. The result
        // returns via the com.tandemtab.app://bank/callback deep link (handled by the ViewModel).
        val ctx = LocalContext.current
        LaunchedEffect(state.bank.linkUrl) {
            state.bank.linkUrl?.let { url ->
                runCatching { ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }
                onBankLinkUrlHandled()
            }
        }
    }
}

/** The Home tab body: the balance hero, then the health / bills / targets / runway cards. The account switcher +
 *  period + actions now live in the top header row. */
@Composable
private fun HomePage(
    state: UiState,
    darkTheme: Boolean,
    onOpenRecurring: () -> Unit,
    onOpenHealth: () -> Unit,
    onOpenRunway: () -> Unit,
    onAcceptInvitation: (String) -> Unit,
    onDeclineInvitation: (String) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val overview = state.overview
    // Invitations sit ABOVE the money, and outside the branches below, deliberately: an invitation can arrive
    // before the user has any account at all, and the "nothing to show" state is exactly when being told
    // someone wants to share theirs matters most.
    InvitationsCard(state.sharing, onAcceptInvitation, onDeclineInvitation)
    when {
        state.busy && overview == null -> Box(
            Modifier.fillMaxWidth().height(200.dp),
            contentAlignment = Alignment.Center,
        ) { LogoLoader() }

        overview == null -> Text("No overview to show.", color = tandem.muted)

        else -> {
            val fmt = rememberCurrency(overview.currency)
            // The period being viewed, which decides both the hero's shape (open vs closed) and F3's day count.
            val viewedIndex = state.selectedPeriod ?: state.currentPeriodIndex
            val viewed = state.periods.firstOrNull { it.index == viewedIndex }
            BalanceHero(overview = overview, period = viewed, fmt = fmt, dark = darkTheme)
            Spacer(Modifier.height(14.dp))
            // Order per the design: health score on top, then bills, then "on track for", and finally the runway.
            HealthCard(health = state.health, onOpen = onOpenHealth)
            // The urgent strip hangs directly off the score, as on web, so "how am I doing" reads as one block.
            AlertStrip(state.alerts)
            Spacer(Modifier.height(14.dp))
            RecurringCard(recurring = state.recurring, onOpen = onOpenRecurring)
            Spacer(Modifier.height(14.dp))
            TargetsCard(state.targets, fmt)
            RunwayCard(state.runway, fmt, onOpen = onOpenRunway)
        }
    }
}

/** The bottom bar: four tabs split 2-and-2 with the brand-gradient add-FAB cradled in the centre gap. The FAB
 *  straddles the top edge of the bar, so it reads as a docked primary action and never covers content. */
@Composable
private fun TandemBottomBar(current: NavDest, onSelect: (NavDest) -> Unit, onAdd: () -> Unit) {
    val barHeight = 70.dp
    val fabSize = 60.dp
    Box(
        Modifier
            .fillMaxWidth()
            .height(barHeight + 22.dp),   // extra top room for the FAB to rise above the bar
    ) {
        Surface(
            modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(barHeight),
            color = MaterialTheme.colorScheme.surface,
            shadowElevation = 10.dp,
        ) {
            Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                BarItem(NavDest.Home, current, Modifier.weight(1f), onSelect)
                BarItem(NavDest.Spending, current, Modifier.weight(1f), onSelect)
                Spacer(Modifier.weight(1f))   // the cradle gap the FAB sits in
                BarItem(NavDest.Goals, current, Modifier.weight(1f), onSelect)
                BarItem(NavDest.Wallets, current, Modifier.weight(1f), onSelect)
            }
        }
        Box(
            Modifier
                .align(Alignment.TopCenter)
                .size(fabSize)
                .shadow(10.dp, CircleShape, clip = false)
                .clip(CircleShape)
                .background(Brush.linearGradient(listOf(BrandGreen, BrandGreenDark)))
                .clickable(onClick = onAdd),
            contentAlignment = Alignment.Center,
        ) {
            Icon(TandemIcons.Plus, contentDescription = "Add", tint = Color.White, modifier = Modifier.size(30.dp))
        }
    }
}

@Composable
private fun BarItem(dest: NavDest, current: NavDest, modifier: Modifier, onSelect: (NavDest) -> Unit) {
    val tandem = LocalTandemColors.current
    val selected = dest == current
    val tint = if (selected) MaterialTheme.colorScheme.primary else tandem.muted
    Column(
        modifier
            .fillMaxHeight()
            .clickable(onClick = { onSelect(dest) }),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Icon(dest.icon, contentDescription = dest.label, tint = tint)
        Spacer(Modifier.height(3.dp))
        Text(dest.label, fontSize = 11.sp, color = tint, fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal)
    }
}

/**
 * "X invited you to Y" — the invitee's half of sharing, and the only way onto someone else's account from a
 * phone. Accepting switches straight to the account joined (see the ViewModel), so the answer to "what just
 * happened" is the screen itself.
 *
 * Both buttons are full-width and labelled rather than a ✓/✕ pair: declining is not undoable from here (the
 * invitation stops being pending and only the sender can re-issue it), and a one-character control is a poor
 * place to learn that.
 */
@Composable
private fun InvitationsCard(
    sharing: com.tandemtab.app.SharingUi,
    onAccept: (String) -> Unit,
    onDecline: (String) -> Unit,
) {
    val tandem = LocalTandemColors.current
    if (sharing.invitations.isEmpty()) return
    Column(verticalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(bottom = 14.dp)) {
        sharing.invitations.forEach { inv ->
            Column(
                Modifier.fillMaxWidth()
                    .background(tandem.savingsTileBg, RoundedCornerShape(14.dp))
                    .border(1.dp, tandem.hairline, RoundedCornerShape(14.dp))
                    .padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    MemberAvatar(inv.invitedByUsername, null, size = 34.dp)
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text("You're invited", fontSize = 11.sp, fontWeight = FontWeight.Bold,
                            letterSpacing = 1.1.sp, color = tandem.muted)
                        Text(
                            "${inv.invitedByUsername} invited you to ${inv.accountName}",
                            fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.onSurface,
                        )
                    }
                }
                sharing.error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 12.sp) }
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(
                        onClick = { onDecline(inv.id) },
                        enabled = !sharing.busy,
                        modifier = Modifier.weight(1f),
                    ) { Text("Decline") }
                    Button(
                        onClick = { onAccept(inv.id) },
                        enabled = !sharing.busy,
                        modifier = Modifier.weight(1f),
                    ) {
                        if (sharing.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                        else Text("Accept")
                    }
                }
            }
        }
    }
}

/**
 * The "needs attention" strip: the urgent alerts for the open period (a savings deficit, a category over its
 * budget), sitting directly under the health score.
 *
 * **Alerts of the same kind are collapsed to one row with a ↻ to cycle**, which is what the web ends up showing.
 * The server sends one item per over-budget category, and rendering them all would push the rest of Home off the
 * screen on the exact months the user most needs to see it — five over-budget categories is a bad month, not five
 * separate things to read. Non-urgent items (bills due, no income yet) are deliberately not here: they belong to
 * the bell, and repeating them on Home is how a warning strip becomes wallpaper.
 */
@Composable
private fun AlertStrip(alerts: List<com.tandemtab.app.data.NotificationDto>) {
    val tandem = LocalTandemColors.current
    val urgent = alerts.filter { it.urgent }
    if (urgent.isEmpty()) return
    val groups = urgent.groupBy { it.icon }.values.toList()
    Spacer(Modifier.height(10.dp))
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        groups.forEach { group ->
            // One index per group, remembered on the group's identity so cycling one doesn't reset another.
            var shown by remember(group.first().text, group.size) { mutableStateOf(0) }
            val item = group[shown % group.size]
            Row(
                Modifier.fillMaxWidth()
                    .background(tandem.alertBg, RoundedCornerShape(14.dp))
                    .border(1.dp, tandem.alertBorder, RoundedCornerShape(14.dp))
                    .padding(horizontal = 14.dp, vertical = 11.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("⚠️", fontSize = 15.sp)
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(item.text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurface)
                    item.desc?.let { Text(it, fontSize = 11.sp, color = tandem.muted) }
                }
                if (group.size > 1) {
                    Spacer(Modifier.width(8.dp))
                    Row(
                        Modifier.clip(RoundedCornerShape(999.dp)).clickable { shown++ }
                            .padding(horizontal = 8.dp, vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text("↻", fontSize = 14.sp, color = tandem.warn, fontWeight = FontWeight.Bold)
                        Spacer(Modifier.width(4.dp))
                        Text("${(shown % group.size) + 1}/${group.size}", fontSize = 11.sp,
                            fontWeight = FontWeight.Bold, color = tandem.warn)
                    }
                }
            }
        }
    }
}

/**
 * The Home money summary — the web hero, tile for tile. It is the only header the Home tab shows, so it doubles as
 * the "how am I doing" glance: what is safe to spend now, what was set aside this period (and what share of the
 * money that came in), what went out, and what came in.
 *
 * Laid out **2×2, which is what the web itself does below 720px** — four columns on a phone would shrink the
 * headline figure to the size of its own caption.
 *
 * A closed period keeps the same four tiles but in their final state: what it closed with, and what came in / went
 * out / was set aside while it ran. No "still due" or per-day sub-lines — those describe a period you can still act
 * on, and reading them on a period that ended weeks ago would be an invitation to spend money that is already gone.
 */
@Composable
private fun BalanceHero(
    overview: com.tandemtab.app.data.AccountOverviewDto,
    period: com.tandemtab.app.data.PeriodRowDto?,
    fmt: (Double) -> String,
    dark: Boolean,
) {
    val tandem = LocalTandemColors.current
    val shape = RoundedCornerShape(18.dp)
    // In dark, the web's mint "glass" hero: an opaque base (so the glow doesn't bleed through), a mint tint on top,
    // a mint border, and a soft mint glow underneath. Light keeps the flat hero card.
    val borderColor = if (dark) Color(0x3D3FE0C5) else tandem.hairline
    var heroMod = Modifier.fillMaxWidth()
    if (dark) heroMod = heroMod.shadow(16.dp, shape, ambientColor = Mint, spotColor = Mint)
    heroMod = heroMod.clip(shape).background(tandem.hero)
    if (dark) heroMod = heroMod.background(Brush.linearGradient(listOf(Color(0x293FE0C5), Color(0x0A3FE0C5))))
    heroMod = heroMod.border(1.dp, borderColor, shape).padding(vertical = 12.dp)

    val open = period?.isOpen ?: true
    val carried = overview.moneyIn - overview.contributed
    val moneyOut = overview.spent + overview.transfersOut

    Column(modifier = heroMod) {
        Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min)) {
            if (open) {
                HeroPart("Safe to spend", fmt(overview.free), main = true, valueColor = tandem.positive) {
                    // At most two context lines, in the order they answer "can I spend this?": what stays free once
                    // the bills already known about are paid, then that headroom spread over the days still to go.
                    if (overview.billsDue > 0.0) {
                        val short = overview.safeAfterBills < 0.0
                        HeroSub("${fmt(overview.safeAfterBills)} after bills", if (short) tandem.warn else tandem.muted)
                    }
                    perDay(overview, period)?.let { HeroSub("${fmt(it)} a day left", tandem.positive) }
                }
            } else {
                HeroPart("Closed with", fmt(overview.current), main = true, valueColor = tandem.positive) {
                    period?.let { HeroSub(closedOnLabel(it.to), tandem.muted) }
                }
            }
            HeroDivider()
            HeroPart("Saved", fmt(overview.savedThisPeriod), valueColor = tandem.saved) {
                // No rate at all when nothing came in — the server sends null rather than a zero, and printing
                // "0% of money in" to someone who has just started reads as a verdict on them.
                overview.savedRate?.takeIf { it > 0.0 }?.let {
                    HeroSub("${Math.round(it * 100)}% of money in", tandem.muted)
                }
            }
        }
        Spacer(Modifier.height(10.dp))
        Box(Modifier.fillMaxWidth().height(1.dp).background(tandem.hairline))
        Spacer(Modifier.height(10.dp))
        Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min)) {
            HeroPart("Spent", fmt(moneyOut), valueColor = MaterialTheme.colorScheme.onBackground) {
                // Money moved to another account is money out, but it is not spending. Naming it stops a single
                // transfer from reading as a blow-out month.
                if (overview.transfersOut > 0.0) HeroSub("+${fmt(overview.transfersOut)} transferred", tandem.muted)
            }
            HeroDivider()
            HeroPart("Money in", fmt(overview.moneyIn), valueColor = MaterialTheme.colorScheme.onBackground) {
                if (carried > 0.0) HeroSub("+${fmt(carried)} carried", tandem.muted)
            }
        }
    }
}

/**
 * F3 "left to spend today": the after-bills headroom spread over the days still left in the period. The numerator is
 * `safeAfterBills`, already net of the bills we know are coming, so it cannot promise money that rent is about to
 * take. Shown only on the open latest period and only when there is headroom — a negative or zero figure here is
 * discouraging noise, and the over-budget alerts are where that gets said properly.
 */
private fun perDay(overview: com.tandemtab.app.data.AccountOverviewDto, period: com.tandemtab.app.data.PeriodRowDto?): Double? {
    val p = period ?: return null
    if (!p.isOpen || !p.isLatest || overview.safeAfterBills <= 0.0) return null
    val end = runCatching { LocalDate.parse(p.to) }.getOrNull() ?: return null
    val daysLeft = (end.toEpochDay() - LocalDate.now().toEpochDay() + 1).toInt()
    if (daysLeft < 1) return null
    return Math.round(overview.safeAfterBills / daysLeft * 100.0) / 100.0
}

private fun closedOnLabel(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("dd MMM yyyy", Locale.getDefault()))
}.getOrDefault(iso)

@Composable
private fun androidx.compose.foundation.layout.RowScope.HeroPart(
    label: String,
    value: String,
    main: Boolean = false,
    valueColor: androidx.compose.ui.graphics.Color,
    subs: @Composable ColumnScope.() -> Unit = {},
) {
    val tandem = LocalTandemColors.current
    Column(
        modifier = Modifier
            .weight(1f)
            .padding(horizontal = 14.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Text(
            label.uppercase(),
            fontSize = 10.sp,
            letterSpacing = 1.3.sp,
            fontWeight = FontWeight.Bold,
            color = tandem.muted,
            maxLines = 1,
        )
        // Shrink long amounts by length so big balances never clip on a narrow phone.
        Text(
            value,
            fontSize = fitFont(value, base = if (main) 25 else 19, min = if (main) 15 else 13),
            fontWeight = if (main) FontWeight.ExtraBold else FontWeight.Bold,
            color = valueColor,
            maxLines = 1,
            softWrap = false,
        )
        subs()
    }
}

@Composable
private fun HeroSub(text: String, color: Color) {
    Text(text, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = color, maxLines = 1)
}

/** A length-based font size so long currency strings fit their column without wrapping or clipping. */
private fun fitFont(text: String, base: Int, min: Int): TextUnit {
    val n = text.length
    val size = when {
        n <= 8 -> base
        n <= 11 -> base - 3
        n <= 14 -> base - 6
        else -> min
    }
    return size.coerceAtLeast(min).sp
}

@Composable
private fun HeroDivider() {
    Box(
        Modifier
            .fillMaxHeight()
            .width(1.dp)
            .background(LocalTandemColors.current.hairline),
    )
}

/** The Home "At this rate…" runway card, driven by the server's cash-flow projection (GET /runway). Amber when the
 *  balance runs short in-window, else a neutral "keeps growing / lasts beyond N months". Hidden when there's no
 *  trustworthy basis (server returned 204 → null). */
@Composable
private fun RunwayCard(runway: RunwayDto?, fmt: (Double) -> String, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    val rw = runway ?: return
    val warn = rw.firstShortfallMonth != null
    val headline = when {
        warn -> "At this rate, money runs short in ${monthLabel(rw.firstShortfallMonth!!)}"
        rw.monthlyIncome >= rw.monthlySpending -> "At this rate, your balance keeps growing"
        else -> "At this rate, your balance lasts beyond ${rw.months} months"
    }
    val bg = if (warn) tandem.alertBg else MaterialTheme.colorScheme.surface
    val border = if (warn) tandem.alertBorder else MaterialTheme.colorScheme.outline
    Column(
        Modifier.fillMaxWidth()
            .background(bg, RoundedCornerShape(16.dp))
            .border(1.dp, border, RoundedCornerShape(16.dp))
            .clickable(onClick = onOpen)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(if (warn) "⚠️" else "🛡️", fontSize = 16.sp)
            Spacer(Modifier.width(8.dp))
            Text(headline, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp, modifier = Modifier.weight(1f))
            OpenChip()
        }
        Text(
            "${fmt(rw.monthlyIncome)} in · ${fmt(rw.monthlySpending)} out each month" +
                if (rw.basedOnRecurring) " · from your declared bills" else " · averaged from recent months",
            color = tandem.muted, fontSize = 12.sp,
        )
    }
    Spacer(Modifier.height(14.dp))
}

/** The Home "You're on track for" card, driven by the server's targets (GET /targets): the combined debt-free date and
 *  each savings goal's projected month (or "reached 🎉"). Hidden when the server returns nothing to project. */
@Composable
private fun TargetsCard(targets: List<TargetDto>, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    if (targets.isEmpty()) return
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(TandemIcons.Trending, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text("You're on track for", fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 15.sp)
        }
        targets.forEach { t ->
            // Debt-free is a synthetic target: the server sends an empty name, the client supplies the label + flag.
            val name = if (t.kind == "debt-free") "Debt-free" else t.name
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (t.kind == "debt-free") Icon(TandemIcons.Flag, null, tint = tandem.catAccent, modifier = Modifier.size(18.dp))
                else CatIcon(t.icon, t.name)
                Spacer(Modifier.width(8.dp))
                Text(name, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
                if (t.reached) {
                    Text("reached 🎉", color = tandem.positive, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                } else {
                    // One line: the green target month/year, then the muted "in Xy Ym".
                    Text(payoffDate(t.months), fontWeight = FontWeight.Bold, color = tandem.positive, fontSize = 13.sp, maxLines = 1)
                    Spacer(Modifier.width(6.dp))
                    Text(monthsText(t.months), color = tandem.muted, fontSize = 12.sp, maxLines = 1)
                }
            }
        }
        Text("Projections at your current pace — they don't move real money.", color = tandem.muted, fontSize = 11.sp)
    }
    Spacer(Modifier.height(14.dp))
}

/** "Mar 2040" from an ISO date the server sends for the first-shortfall month. */
private fun monthLabel(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault()))
}.getOrDefault(iso)

private fun payoffDate(months: Int): String =
    LocalDate.now().plusMonths(months.toLong()).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault()))

private fun monthsText(months: Int): String = when {
    months <= 0 -> "this month"
    months == 1 -> "in 1 month"
    months < 12 -> "in $months months"
    else -> { val y = months / 12; val m = months % 12; "in ${y}y${if (m > 0) " ${m}m" else ""}" }
}

/** The body account header, collapsed to a single row: the account switcher (a dropdown when there's more than one
 *  account, else a plain name), the current-period label, and the account-actions button. Member avatars/names moved
 *  into the Account sheet — they're reference info, not something you act on from Home each session. */
/** The account name — a tap-to-switch dropdown when the user has more than one account, else a plain heading. Each
 *  dropdown row leads with the account's avatar (its members stacked, or a coloured initial). */
@Composable
private fun AccountSwitcher(state: UiState, onSelectAccount: (String) -> Unit, onCreateAccount: () -> Unit) {
    val account = state.selectedAccount
    // No account at all — the phone-only dead-end. Registration makes a user, not an account, so a fresh sign-up
    // lands here with nothing; this is the only way out. Show a plain "create" affordance in place of the name.
    if (account == null) {
        Row(
            Modifier.clip(RoundedCornerShape(10.dp)).clickable { onCreateAccount() }.padding(vertical = 2.dp, horizontal = 2.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(TandemIcons.Plus, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(6.dp))
            Text("Create account", fontSize = 18.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.primary, maxLines = 1)
        }
        return
    }
    // Always a dropdown now (even for a single account), so "New account" has a home — the only place a second
    // account can be made on the phone. Pro-gated: the server 402s a free user's second account.
    var open by remember { mutableStateOf(false) }
    Row(
        Modifier.clip(RoundedCornerShape(10.dp)).clickable { open = true }.padding(vertical = 2.dp, horizontal = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(account.name, fontSize = 20.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onBackground, maxLines = 1)
        Spacer(Modifier.width(4.dp))
        Icon(TandemIcons.Chevron, contentDescription = "Switch account", tint = LocalTandemColors.current.muted, modifier = Modifier.size(18.dp).rotate(90f))
    }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        state.accounts.forEachIndexed { i, a ->
            val selected = a.id == state.selectedAccountId
            DropdownMenuItem(
                text = { Text(a.name, fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal, color = MaterialTheme.colorScheme.onSurface) },
                onClick = { onSelectAccount(a.id); open = false },
                leadingIcon = { AccountAvatar(a, i) },
                trailingIcon = if (selected) {
                    { Icon(TandemIcons.Check, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp)) }
                } else null,
            )
        }
        HorizontalDivider()
        DropdownMenuItem(
            text = { Text("New account", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary) },
            onClick = { open = false; onCreateAccount() },
            leadingIcon = { Icon(TandemIcons.Plus, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp)) },
        )
    }
}

/** A small avatar for an account row: the members' initials stacked (up to 3), or a colour-coded account initial. */
@Composable
private fun AccountAvatar(account: AccountSummaryDto, index: Int) {
    val members = account.members
    if (members.isEmpty()) {
        AvatarCircle(initialOf(account.name), avatarPalette[index % avatarPalette.size])
        return
    }
    val shown = members.take(3)
    Row(horizontalArrangement = Arrangement.spacedBy((-8).dp)) {
        for (i in shown.indices) {
            AvatarCircle(initialOf(shown[i].displayName), avatarPalette[i % avatarPalette.size])
        }
    }
}

/** The viewed-period chip: the month/range label; tapping opens a menu of every period (newest first) to switch
 *  to, plus the lifecycle actions (start next month / change dates / remove) as on the web's period popover. */
@Composable
private fun PeriodSwitcher(
    state: UiState,
    onSelectPeriod: (Int?) -> Unit,
    onStartNextPeriod: () -> Unit,
    onEditPeriodDates: () -> Unit,
    onRemovePeriod: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val label = state.periodLabel ?: return
    var open by remember { mutableStateOf(false) }
    val viewing = state.selectedPeriod ?: state.currentPeriodIndex
    val onLatest = viewing == state.periods.lastOrNull()?.index
    Row(
        Modifier.clip(RoundedCornerShape(999.dp)).clickable(enabled = state.periods.isNotEmpty()) { open = true }
            .padding(horizontal = 8.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, fontSize = 13.sp, color = tandem.muted, fontWeight = FontWeight.Medium, maxLines = 1)
        if (state.periods.size > 1) {
            Spacer(Modifier.width(2.dp))
            Icon(TandemIcons.Chevron, null, tint = tandem.muted, modifier = Modifier.size(14.dp).rotate(90f))
        }
    }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        state.periods.sortedByDescending { it.index }.forEach { p ->
            val isCurrent = p.index == state.currentPeriodIndex
            DropdownMenuItem(
                text = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(periodRowLabel(p.from, p.to), color = MaterialTheme.colorScheme.onSurface, fontWeight = if (p.index == viewing) FontWeight.Bold else FontWeight.Normal)
                        if (isCurrent) { Spacer(Modifier.width(6.dp)); Text("current", fontSize = 11.sp, color = tandem.positive) }
                    }
                },
                onClick = { onSelectPeriod(if (isCurrent) null else p.index); open = false },
                trailingIcon = if (p.index == viewing) {
                    { Icon(TandemIcons.Check, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp)) }
                } else null,
            )
        }
        HorizontalDivider()
        // Rolling forward is only ever offered on the newest month, and only once it has actually ended — the
        // server enforces both, so a live-looking-but-doomed menu item would just be a 400 with extra steps.
        DropdownMenuItem(
            text = {
                Column {
                    Text("Start next month", color = MaterialTheme.colorScheme.onSurface)
                    if (!state.canStartNextPeriod) {
                        Text("Available once this month ends", fontSize = 11.sp, color = tandem.muted)
                    }
                }
            },
            enabled = state.canStartNextPeriod,
            leadingIcon = { Icon(Icons.Rounded.Add, null, modifier = Modifier.size(18.dp)) },
            onClick = { open = false; onStartNextPeriod() },
        )
        DropdownMenuItem(
            text = { Text("Change these dates", color = MaterialTheme.colorScheme.onSurface) },
            leadingIcon = { Icon(Icons.Rounded.Edit, null, modifier = Modifier.size(18.dp)) },
            onClick = { open = false; onEditPeriodDates() },
        )
        if (onLatest && state.canRemoveLatestPeriod) {
            DropdownMenuItem(
                text = { Text("Remove this month", color = MaterialTheme.colorScheme.error) },
                leadingIcon = { Icon(Icons.Rounded.Close, null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(18.dp)) },
                onClick = { open = false; onRemovePeriod() },
            )
        }
    }
}

private fun periodRowLabel(fromIso: String, toIso: String): String = runCatching {
    val from = LocalDate.parse(fromIso); val to = LocalDate.parse(toIso)
    if (from.month == to.month && from.year == to.year)
        from.format(DateTimeFormatter.ofPattern("LLLL yyyy", Locale.getDefault()))
    else "${from.format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))} – ${to.format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))}"
}.getOrDefault("$fromIso – $toIso")

private val avatarPalette = listOf(
    Color(0xFF13A06E), Color(0xFF3B82F6), Color(0xFFF59E0B), Color(0xFF8B5CF6), Color(0xFFEF4444),
)

@Composable
private fun MemberAvatars(members: List<MemberDto>) {
    if (members.isEmpty()) return
    val tandem = LocalTandemColors.current
    Row(verticalAlignment = Alignment.CenterVertically) {
        Row(horizontalArrangement = Arrangement.spacedBy((-10).dp)) {
            members.take(4).forEachIndexed { i, m ->
                AvatarCircle(initialOf(m.displayName), avatarPalette[i % avatarPalette.size])
            }
            if (members.size > 4) AvatarCircle("+${members.size - 4}", tandem.muted)
        }
        Spacer(Modifier.width(10.dp))
        val names = members.joinToString(", ") { it.displayName.ifBlank { "—" } }
        Text(
            if (names.length > 26) names.take(24) + "…" else names,
            fontSize = 12.sp, color = tandem.muted, maxLines = 1, fontWeight = FontWeight.Medium,
        )
    }
}

@Composable
private fun AvatarCircle(text: String, color: Color) {
    Box(
        Modifier
            .size(30.dp)
            .clip(CircleShape)
            .background(color)
            .border(2.dp, MaterialTheme.colorScheme.surface, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, color = Color.White, fontSize = if (text.length > 1) 11.sp else 13.sp, fontWeight = FontWeight.Bold)
    }
}

private fun initialOf(name: String): String = name.trim().firstOrNull()?.uppercase() ?: "?"

@Composable
private fun AccountChip(name: String, selected: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    val bg = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val fg = if (selected) MaterialTheme.colorScheme.onPrimary else tandem.muted
    Text(
        name,
        color = fg,
        fontSize = 13.sp,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier
            .background(bg, RoundedCornerShape(999.dp))
            .border(1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, RoundedCornerShape(999.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
    )
}

/** Currency formatter for the account's ISO code, matching the web app's money formatting. */
private fun rememberCurrency(currencyCode: String): (Double) -> String {
    val nf = NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}

/** The app canvas: a flat base in light; in dark, the web's two corner glows (mint top-left, coral top-right) over
 *  the deep base, mirroring the dark body's layered radial gradients. */
private fun Modifier.tandemCanvas(dark: Boolean, base: Color): Modifier = drawBehind {
    drawRect(base)
    if (dark) {
        drawRect(Brush.radialGradient(
            colors = listOf(Color(0x1C3FE0C5), Color(0x003FE0C5)),
            center = Offset(0f, 0f), radius = size.width * 0.95f,
        ))
        drawRect(Brush.radialGradient(
            colors = listOf(Color(0x21FF7A66), Color(0x00FF7A66)),
            center = Offset(size.width, -size.height * 0.04f), radius = size.width,
        ))
    }
}
