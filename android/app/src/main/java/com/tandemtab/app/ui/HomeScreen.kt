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
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Velocity
import androidx.compose.foundation.Image
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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import com.tandemtab.app.TripsUi
import com.tandemtab.app.UiState
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.BreakdownViewDto
import com.tandemtab.app.data.FundCurrencyEdit
import com.tandemtab.app.data.ImportRowDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.ActiveTripDto
import com.tandemtab.app.data.MilestonesDto
import com.tandemtab.app.data.PlanFeatures
import com.tandemtab.app.data.RunwayDto
import com.tandemtab.app.data.TargetDto
import com.tandemtab.app.data.WeeklyRecapViewDto
import kotlin.math.abs
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
// ⚠️ "Dashboard" is a LABEL change (O9) — the enum entry stays `Home`, which is also the string the landing-tab
// preference stores on both platforms. Renaming the entry would rewrite every saved preference for no visible gain.
private enum class NavDest(val label: String, val icon: ImageVector) {
    Home("Dashboard", TandemIcons.House),
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
    landingTab: String,
    onSetLandingTab: (String) -> Unit,
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
    onRestoreAccount: (String) -> Unit,
    onRenameAccount: (String, () -> Unit) -> Unit,
    onSetSavingsTarget: (Double, () -> Unit) -> Unit,
    onLeaveAccount: (String?, () -> Unit) -> Unit,
    onDeleteAccount: (() -> Unit) -> Unit,
    onExportAccount: ((java.io.File) -> Unit) -> Unit,
    onInvite: (String) -> Unit,
    onClearInviteResult: () -> Unit,
    onRemoveMember: (String, () -> Unit) -> Unit,
    onTransferOwnership: (String, () -> Unit) -> Unit,
    onAcceptInvitation: (String) -> Unit,
    onDeclineInvitation: (String) -> Unit,
    onLoadOnboarding: () -> Unit,
    onDismissOnboarding: () -> Unit,
    onLoadMilestones: () -> Unit,
    onLoadActiveTrips: () -> Unit,
    onSetTripMode: (Boolean) -> Unit,
    onLoadAchievements: (Boolean) -> Unit,
    onBeginSettle: (ExpenseDto) -> Unit,
    onClearSettling: () -> Unit,
    onSettleExpense: (expenseId: String, destinationAccountId: String, amount: Double, note: String?, onDone: () -> Unit) -> Unit,
    onUnsettleExpense: (expenseId: String, destinationAccountId: String, onDone: () -> Unit) -> Unit,
    onLoadSpending: (Boolean) -> Unit,
    onLoadGoals: (Boolean) -> Unit,
    onLoadWallets: (Boolean) -> Unit,
    onLoadBank: (Boolean) -> Unit,
    onConnectBank: () -> Unit,
    onSyncBank: () -> Unit,
    onDisconnectBank: () -> Unit,
    onConfirmBankExpense: (String, String, String, Double, String, String?, () -> Unit) -> Unit,
    onConfirmBankIncome: (String, String, String, Double, String, () -> Unit) -> Unit,
    onConfirmBankRefund: (String, String, Double, () -> Unit) -> Unit,
    onUndoRefund: (String) -> Unit,
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
    onDeleteDeposit: (depositId: String, onDone: () -> Unit) -> Unit,
    // The same two writes as above, driven from the Wallets tab's income list. Separate because they raise that
    // tab's saving/error flags rather than the add sheet's — see the note on the pair in AppViewModel.
    onEditWalletDeposit: (String, String, String, Double, String, () -> Unit) -> Unit,
    onDeleteWalletDeposit: (depositId: String, onDone: () -> Unit) -> Unit,
    onClearEditingIncome: () -> Unit,
    onBeginEditExpense: (com.tandemtab.app.data.ExpenseDto) -> Unit,
    onDeleteExpense: (com.tandemtab.app.data.ExpenseDto) -> Unit,
    onSetBudget: (String, Double, () -> Unit) -> Unit,
    onRemoveBudget: (String, () -> Unit) -> Unit,
    onAddCategory: (String, String?, String?, (String?) -> Unit) -> Unit,
    // The expense sheet's find-or-add tag box. Distinct from onAddTag below, which is the tags-MANAGEMENT sheet's
    // and hands back no id — see AddSheet.onAddExpenseTag.
    onAddExpenseTag: (String, Boolean, (String?) -> Unit) -> Unit = { _, _, done -> done(null) },
    onEditCategory: (String, String, String?, () -> Unit) -> Unit,
    onArchiveCategory: (String, () -> Unit) -> Unit,
    onDeleteCategory: (id: String, moveTo: String?, onDone: () -> Unit) -> Unit,
    onLoadTrips: (Boolean) -> Unit,
    onSaveTrip: (String?, String, String, String, String?, String?, String?, Double?, String?, () -> Unit) -> Unit,
    onDeleteTrip: (String, () -> Unit) -> Unit,
    onStartTrip: (String, Boolean) -> Unit,
    onFinishTrip: (String, Boolean) -> Unit,
    onAttachExpenseToTrip: (String, String?, () -> Unit) -> Unit,
    onOpenTrip: (String?) -> Unit,
    onPrepareTrip: () -> Unit,
    // Raise the upgrade prompt for a feature key. One callback for the whole screen — a gate can refuse from any
    // of its sheets, and the prompt itself lives above every screen (see MainActivity.App).
    onProBlocked: (String) -> Unit,
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
    onEditIncomeSource: (id: String, name: String, icon: String?, onDone: () -> Unit) -> Unit,
    onDeleteIncomeSource: (id: String, onDone: () -> Unit) -> Unit,
    onClearEditing: () -> Unit,
    onAddExpenses: (List<com.tandemtab.app.data.AddExpenseRequest>, () -> Unit) -> Unit,
    onEditExpense: (String, com.tandemtab.app.data.AddExpenseRequest, () -> Unit) -> Unit,
    onAddIncomeQuick: (String, String, Double, String, () -> Unit) -> Unit,
    onPrepareTransfer: () -> Unit,
    onPrepareAddIncome: () -> Unit,
    onTransfer: (String, String, Double, String, String?, () -> Unit) -> Unit,
    onAddIncome: (String, String, Double, String, () -> Unit) -> Unit,
    onPrepareFund: () -> Unit,
    onSaveFund: (String?, String, String?, String?, Double?, FundCurrencyEdit?, () -> Unit) -> Unit,
    onImportTransactions: (List<ImportRowDto>, Boolean, (Int, Int) -> Unit) -> Unit,
    onLoadBankMappings: () -> Unit,
    onPinMerchant: (description: String, categoryId: String, currentlyPinned: Boolean) -> Unit,
    onOpenPayoff: (bucketId: String, bucketName: String) -> Unit,
    // S119 — the bank review's "money back on" search, which spans every period rather than the one on screen.
    onSearchRefundable: (String) -> Unit,
    // R2.5 — the whole-stack plan on the Goals tab. Separate from onOpenPayoff above: that one is about a single
    // loan, this one about being debt-free.
    onOpenDebtPlan: () -> Unit,
    onCloseDebtPlan: () -> Unit,
    onSetDebtPlan: (Double?, String?) -> Unit,
    onClosePayoff: () -> Unit,
    onOpenBreakdown: (String?) -> Unit,
    onCloseBreakdown: () -> Unit,
    onOpenWeekRecap: () -> Unit,
    onCloseWeekRecap: () -> Unit,
    onDismissWeekRecap: () -> Unit,
    // R2.5 — Trends shares the Breakdown drawer, so one callback drives both the switcher and the range chips
    // inside it. A null range means "leave the range as it is".
    onShowTrends: (Boolean, String?) -> Unit,
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
    // Opens on the user's chosen tab (O9). `remember` with the preference as its key so changing the setting in the
    // profile sheet moves you there at once — the web's twin only applies next launch, and the reason is the same
    // one either way: the tab state belongs to the screen, and here the screen can see the preference change.
    var dest by remember(landingTab) { mutableStateOf(NavDest.entries.firstOrNull { it.name == landingTab } ?: NavDest.Home) }

    val snackbar = remember { SnackbarHostState() }
    var showAddExpense by remember { mutableStateOf(false) }
    var showHealth by remember { mutableStateOf(false) }
    var showRunway by remember { mutableStateOf(false) }
    var showAchievements by remember { mutableStateOf(false) }
    var showRecurring by remember { mutableStateOf(false) }
    var showProfile by remember { mutableStateOf(false) }
    var showAccount by remember { mutableStateOf(false) }
    var showBank by remember { mutableStateOf(false) }
    var showImport by remember { mutableStateOf(false) }
    var showNotifications by remember { mutableStateOf(false) }
    var showNextPeriod by remember { mutableStateOf(false) }
    var showEditPeriod by remember { mutableStateOf(false) }
    var showRemovePeriod by remember { mutableStateOf(false) }
    var showCreateAccount by remember { mutableStateOf(false) }

    // "Edit last" flows through the ViewModel: prepareEditLast loads + picks the last expense into state.editingExpense,
    // which we watch here to raise the add sheet in edit mode.
    val editing = state.editingExpense

    // Where an expense could be settled: another account of this user's, in the SAME currency (the server rejects
    // anything else). Falls back to the overview's currency while the account summary is still loading.
    val settleCurrency = state.selectedAccount?.currency ?: state.overview?.currency
    val settleTargets = state.accounts.filter {
        it.id != state.selectedAccountId && (settleCurrency == null || it.currency.equals(settleCurrency, ignoreCase = true))
    }

    Scaffold(
        modifier = Modifier.fillMaxSize().tandemCanvas(darkTheme, tandem.canvas),
        containerColor = Color.Transparent,
        topBar = {
          Column(Modifier.fillMaxWidth().statusBarsPadding()) {
            // Privacy mode's indicator, above the header exactly as the web puts it above .hdr-top. It renders
            // only while masking is on, and it is the only way to turn masking off — see PrivacyBar.
            PrivacyBar()
            // ★ R4.5 — above the header, because it changes how everything below it should be read and so
            // cannot be something you have to scroll to. Mirrors the web's amber strip.
            OfflineStrip(asOf = state.offlineAsOf, pending = state.pendingExpenses)
            // Compact one-row header (no logo): account switcher · period · account-actions · profile.
            Row(
                Modifier.fillMaxWidth().padding(start = 14.dp, end = 6.dp, top = 6.dp, bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                // Keep the brand mark (icon only, no wordmark) to anchor the compact header.
                TandemLogo(size = 24.dp)
                Spacer(Modifier.width(10.dp))
                Box(Modifier.weight(1f)) {
                    AccountSwitcher(
                        state,
                        onSelectAccount,
                        onCreateAccount = { showCreateAccount = true },
                        onOpened = onLoadActiveTrips,
                    )
                }
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
        // Flip-to-hide. Mounted here, under the signed-in screen, so the gesture exists exactly where there are
        // figures to hide: nothing is registered on Login or Splash, and signing out tears it down with the screen.
        FlipToHideWatcher()
        FlipExplainerDialog()

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
                onDeleteDeposit = onDeleteDeposit,
                onAddCategory = onAddCategory,
                onAddExpenseTag = onAddExpenseTag,
                // Only offered when there is somewhere to settle onto. Same currency, because the server refuses
                // a cross-currency settlement outright — offering it would be a button that always fails.
                onSettle = if (settleTargets.isEmpty()) null else onBeginSettle,
                onUndoRefund = { e -> onUndoRefund(e.id) },
            )
        }
        state.settlingExpense?.let { se ->
            SettleExpenseSheet(
                expense = se,
                spending = state.spending,
                otherAccounts = settleTargets,
                onDismiss = onClearSettling,
                onSettle = { dest, amount, note, onDone -> onSettleExpense(se.id, dest, amount, note, onDone) },
                onUnsettle = { dest, onDone -> onUnsettleExpense(se.id, dest, onDone) },
            )
        }
        LaunchedEffect(dest, state.selectedAccountId) {
            when (dest) {
                NavDest.Spending -> onLoadSpending(false)
                NavDest.Goals -> onLoadGoals(false)
                NavDest.Wallets -> { onLoadWallets(false); onLoadBank(false) }
                NavDest.Home -> { onLoadHealth(false); onLoadRecurring(false); onLoadGoals(false); onLoadOnboarding(); onLoadMilestones() }
            }
        }

        // Swipe left → the next period, right → the previous one, matching the ‹ › arrows' own direction. A whole
        // gesture rather than a pager because the app holds ONE period's data at a time: a pager would have to
        // render pages it has no content for, and a swipe that briefly shows the wrong month's figures is worse
        // than no animation. Nothing happens at either end of the list, and a drag under the threshold is a scroll.
        var dragX by remember { mutableStateOf(0f) }
        // Hoisted so the pull-down gesture can ask whether the page is actually at the top before claiming a drag.
        val homeScroll = rememberScrollState()

        // The pull-down that opens the full notification list. It watches what the scrolling Column could not
        // use: at the top of the list a downward drag scrolls nothing, so the whole delta arrives here as
        // leftover, and that leftover is the pull. Anything the list DID consume is ordinary scrolling and is
        // none of our business.
        val pullThresholdPx = with(LocalDensity.current) { 110.dp.toPx() }
        var pulled by remember { mutableStateOf(0f) }
        val pullDown = remember(pullThresholdPx) {
            object : NestedScrollConnection {
                override fun onPostScroll(consumed: Offset, available: Offset, source: NestedScrollSource): Offset {
                    // Only a real finger, and only downward leftover. An upward drag means the reader is going
                    // back into the list, so the pull is abandoned rather than merely paused.
                    if (source != NestedScrollSource.Drag || available.y == 0f) return Offset.Zero
                    if (available.y < 0f) { pulled = 0f; return Offset.Zero }
                    pulled += available.y
                    // ⚠️ CONSUME it, and this is the half that makes the gesture work at all. Returning Zero
                    // leaves the leftover to the stretch overscroll, which swallows it to draw the stretch — a
                    // measured 1000px drag then reached this connection as **89px** of leftover, against a 289px
                    // threshold it could never cross. Not "the gesture is flaky": it was arithmetically
                    // unreachable, and it looked like the nested-scroll wiring was wrong. Claiming the delta is
                    // what pull-to-refresh does for the same reason. Cost: no stretch at the top of Home, which
                    // is the affordance this gesture replaces anyway.
                    return Offset(0f, available.y)
                }

                // Fires when the finger lifts, whatever the velocity — so this is the gesture's end, not just a
                // fling. Reset unconditionally: a pull that fell short must not add to the next one.
                override suspend fun onPreFling(available: Velocity): Velocity {
                    if (pulled >= pullThresholdPx) showNotifications = true
                    pulled = 0f
                    return Velocity.Zero
                }
            }
        }
        Box(
            Modifier
                .fillMaxSize()
                .padding(padding)
                // ⚠️ The horizontal swipe used to step PERIODS. Owner's call to reassign it. Right opens
                // SPENDING, which is where trips live — trips is a section of that tab, not a tab of its own, so
                // "swipe right for trips" is honestly "swipe right to the screen holding them".
                // Month stepping did not simply vanish with it — the period chip grew prev/next arrows (see
                // PeriodSwitcher), because it was given the gesture originally for costing chip + menu + tap.
                // Left opens the Breakdown, which now has a server read behind it (GET /breakdown, added S115 —
                // before that there was nothing for this gesture to open, which is why it was left unassigned).
                .pointerInput(Unit) {
                    detectHorizontalDragGestures(
                        onDragStart = { dragX = 0f },
                        onDragEnd = {
                            val threshold = 96.dp.toPx()
                            if (dragX >= threshold) dest = NavDest.Spending
                            else if (dragX <= -threshold) onOpenBreakdown(null)
                            dragX = 0f
                        },
                        onDragCancel = { dragX = 0f },
                    ) { _, delta -> dragX += delta }
                }
                // Pull down for the full notification list.
                // ⚠️⚠️ This MUST be nested scroll, not a second `pointerInput`. It was one, and the gesture never
                // fired once on a device: this Box wraps a `verticalScroll` Column, and the inner scrollable is
                // hit first, so it claims every vertical drag — including the ones at offset 0 that scroll
                // nothing. The outer detector was therefore never handed a drag to measure. The horizontal
                // detector above survives only because nothing inside it consumes horizontal drags, which is
                // exactly why the bug looked impossible from the code: its identical-looking sibling works.
                // Nested scroll is how pull-to-refresh actually does it — the child scrolls first and reports
                // what it could NOT use, and overscroll at the top is precisely that leftover.
                .nestedScroll(pullDown),
        ) {
            run {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .verticalScroll(homeScroll)
                        .padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 24.dp),
                ) {
                    when (dest) {
                        NavDest.Home -> HomePage(
                            state,
                            darkTheme = darkTheme,
                            onOpenNotifications = { showNotifications = true },
                            onOpenHealth = { showHealth = true },
                            onOpenRunway = { showRunway = true },
                            // Same destination as the left-swipe above, which is the point: the gesture stays,
                            // and stops being the only way in.
                            onOpenBreakdown = { onOpenBreakdown(null) },
                            onOpenWeekRecap = onOpenWeekRecap,
                            onDismissWeekRecap = onDismissWeekRecap,
                            // Both halves of the web's `ShowTripsTab(id)`: switch tabs, and open that journey's
                            // card. Spending starts on its Trips segment because a trip is open — the flag it
                            // reads is state, not a second navigation argument threaded through the screen.
                            onOpenLiveTrip = { id -> onOpenTrip(id); dest = NavDest.Spending },
                            // The catalogue is only fetched when it's asked for — the tally on the line is the
                            // cheap half, and most visits to Home never open this.
                            onOpenAchievements = { onLoadAchievements(false); showAchievements = true },
                            onAcceptInvitation = onAcceptInvitation,
                            onDeclineInvitation = onDeclineInvitation,
                            onDismissOnboarding = onDismissOnboarding,
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
                            onDeleteCategory = onDeleteCategory,
                            onLoadTrips = { onLoadTrips(false) },
                            onSaveTrip = onSaveTrip,
                            onDeleteTrip = onDeleteTrip,
                            onStartTrip = onStartTrip,
                            onFinishTrip = onFinishTrip,
                            onAttachExpenseToTrip = onAttachExpenseToTrip,
                            onOpenTrip = onOpenTrip,
                            onPrepareTrip = onPrepareTrip,
                            onUseTripSavings = onUseTripSavings,
                            tripsProLocked = !state.allowsPro(PlanFeatures.TRIPS),
                            onTripsProBlocked = { onProBlocked(PlanFeatures.TRIPS) },
                            onLoadTags = { onLoadTags(false) },
                            onPrepareTags = onPrepareTags,
                            onAddTag = onAddTag,
                            onEditTag = onEditTag,
                            onSetTagArchived = onSetTagArchived,
                            onDeleteTag = onDeleteTag,
                            onEditIncomeSource = onEditIncomeSource,
                            onDeleteIncomeSource = onDeleteIncomeSource,
                        )
                        NavDest.Goals -> GoalsScreen(
                            goals = state.goals,
                            spending = state.spending,
                            onRetry = { onLoadGoals(true) },
                            onOpenPayoff = onOpenPayoff,
                            debtPlan = state.debtPlan,
                            debtPlanOpen = state.debtPlanOpen,
                            debtPlanLoading = state.debtPlanLoading,
                            debtPlanExtra = state.debtPlanExtra,
                            onOpenDebtPlan = onOpenDebtPlan,
                            onCloseDebtPlan = onCloseDebtPlan,
                            onSetDebtPlan = onSetDebtPlan,
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
                            onEditDeposit = onEditWalletDeposit,
                            onDeleteDeposit = onDeleteWalletDeposit,
                            onPrepareFund = onPrepareFund,
                            onSaveFund = onSaveFund,
                            // Same feature as trips, and deliberately the same gate: a foreign-cash wallet is what
                            // a trip abroad spends from, so the two are one entitlement.
                            canHoldForeignCash = state.allowsPro(PlanFeatures.TRIPS),
                            onProBlocked = { onProBlocked(PlanFeatures.TRIPS) },
                            canImport = state.allowsPro(PlanFeatures.IMPORT),
                            // Rules are fetched when the sheet opens, not on every Wallets render — they only
                            // matter to this one flow, and an import is rare.
                            onOpenImport = { onLoadBankMappings(); showImport = true },
                            onImportProBlocked = { onProBlocked(PlanFeatures.IMPORT) },
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
        if (showAchievements) {
            AchievementsSheet(
                achievements = state.achievements,
                onDismiss = { showAchievements = false },
                onRetry = { onLoadAchievements(true) },
            )
        }
        state.runway?.let { rw ->
            if (showRunway) {
                RunwaySheet(runway = rw, fmt = moneyFormatter(rw.currency), onDismiss = { showRunway = false })
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
                landingTab = landingTab,
                onSetLandingTab = onSetLandingTab,
                onChangePassword = onChangePassword,
                onResendVerification = onResendVerification,
                onUploadAvatar = onUploadAvatar,
                onBeginTwoFactor = onBeginTwoFactor,
                onConfirmTwoFactor = onConfirmTwoFactor,
                onDisableTwoFactor = onDisableTwoFactor,
                onSetTwoFactorDisabling = onSetTwoFactorDisabling,
                onCancelTwoFactorSetup = onCancelTwoFactorSetup,
                onDismissRecoveryCodes = onDismissRecoveryCodes,
                // Closes the sheet on the way: the restored account is re-selectable from the account switcher,
                // and leaving the profile open over a list that just lost its only row reads like nothing happened.
                onRestoreAccount = { showProfile = false; onRestoreAccount(it) },
                onSetTripMode = onSetTripMode,
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
                // Deliberately leaves the sheet OPEN: the share chooser comes up over it, and dismissing the
                // sheet first would drop the user back on Home behind a chooser they hadn't answered yet.
                onExport = onExportAccount,
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
                onConfirmRefund = onConfirmBankRefund,
                onDismissPending = onDismissBankPending,
                onDismiss = { showBank = false },
                refundResults = state.refundResults,
                refundSearch = state.refundSearch,
                refundSearching = state.refundSearching,
                onSearchRefundable = onSearchRefundable,
            )
        }
        if (state.breakdownOpen) {
            BreakdownSheet(
                breakdown = state.breakdown,
                loading = state.breakdownLoading,
                trends = state.trends,
                trendsOverTime = state.trendsOverTime,
                trendsLoading = state.trendsLoading,
                trendsRange = state.trendsRange,
                onShowTrends = onShowTrends,
                onDismiss = onCloseBreakdown,
                onGroupBy = { onOpenBreakdown(it) },
            )
        }
        if (state.weekRecapOpen) {
            // The card's own gate already proved there is something to show; guarding again here keeps the sheet
            // from opening on a null if the account is switched while it is up.
            state.weekRecap?.let { WeekRecapSheet(recap = it, onDismiss = onCloseWeekRecap) }
        }
        if (showNotifications) {
            NotificationsSheet(
                alerts = state.alerts,
                onDismiss = { showNotifications = false },
                onOpenTab = { tab ->
                    showNotifications = false
                    NavDest.entries.firstOrNull { it.name.equals(tab, ignoreCase = true) }?.let { dest = it }
                },
                recurring = state.recurring,
                // One sheet closes as the other opens: Compose only reliably drives one modal at a time, which
                // is the same reason the recurring editor lives *inside* its own sheet rather than above it.
                onManageBills = { showNotifications = false; showRecurring = true },
            )
        }
        state.payoffBucketId?.let {
            PayoffSheet(
                bucketName = state.payoffBucketName,
                payoff = state.payoff,
                loading = state.payoffLoading,
                onDismiss = onClosePayoff,
                onProBlocked = { onProBlocked(PlanFeatures.DEBT) },
            )
        }
        if (showImport) {
            ImportSheet(
                currency = state.spending.currency,
                funds = state.spending.funds,
                categories = state.spending.categories,
                incomeCategories = state.spending.incomeCategories,
                periodFrom = state.viewedPeriod?.from,
                periodTo = state.viewedPeriod?.to,
                saving = state.spending.saving,
                saveError = state.spending.saveError,
                rules = state.bankMappings,
                onDismiss = { showImport = false },
                onImport = onImportTransactions,
                onPinMerchant = onPinMerchant,
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
    onOpenNotifications: () -> Unit,
    onOpenHealth: () -> Unit,
    onOpenRunway: () -> Unit,
    onOpenBreakdown: () -> Unit,
    onOpenLiveTrip: (String) -> Unit,
    onOpenAchievements: () -> Unit,
    onOpenWeekRecap: () -> Unit,
    onDismissWeekRecap: () -> Unit,
    onAcceptInvitation: (String) -> Unit,
    onDeclineInvitation: (String) -> Unit,
    onDismissOnboarding: () -> Unit,
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
            val fmt = moneyFormatter(overview.currency)
            // The period being viewed, which decides both the hero's shape (open vs closed) and F3's day count.
            val viewedIndex = state.selectedPeriod ?: state.currentPeriodIndex
            val viewed = state.periods.firstOrNull { it.index == viewedIndex }
            BalanceHero(overview = overview, period = viewed, fmt = fmt, dark = darkTheme)
            Spacer(Modifier.height(14.dp))
            // Under the money, not above it: someone who has already set up should see their balance first, and
            // the card removes itself once every step is done anyway.
            if (state.onboarding?.dismissed == false) {
                GettingStartedCard(state.onboarding, onDismissOnboarding)
                Spacer(Modifier.height(14.dp))
            }
            // While a journey is running it sits above everything else on the tab, as on the web: every figure
            // below is being distorted by the trip, so the card that explains that comes first. (The web puts it
            // under its three action buttons; the phone's equivalent is the FAB, which is not in this column.)
            TripHeroCard(state.trips, onOpen = onOpenLiveTrip)
            // The week recap takes the slot directly under the hero — and only when no journey is running, which
            // is the web's own gate. While you are away, "last week at home" is the least interesting thing on
            // the screen, and the two cards are the same shape competing for the same glance.
            if (state.trips.live == null) {
                WeekRecapCard(
                    recap = state.weekRecap,
                    dismissedFrom = state.weekRecapDismissed,
                    onOpen = onOpenWeekRecap,
                    onDismiss = onDismissWeekRecap,
                )
            }
            // Order per the design: health score on top, then "on track for", and finally the runway.
            // ⚠️ **Bills are no longer a card here** (owner's call, S117). They live in the notification list —
            // which is where the web has always kept them, with its own `no Home link` comment saying so. The
            // two clients had opposite answers and neither recorded that the other existed; this is the one
            // being kept. The list is reached by the pull-down and by tapping the strip below.
            HealthCard(health = state.health, onOpen = onOpenHealth)
            // The urgent strip hangs directly off the score, as on web, so "how am I doing" reads as one block.
            // Bills that are actually DUE already arrive here as urgent alerts from the server, so the summary
            // the card used to carry has not gone anywhere — only its permanent slot has.
            AlertStrip(state.alerts, onOpenAll = onOpenNotifications)
            Spacer(Modifier.height(14.dp))
            TargetsCard(state.targets, fmt)
            // Directly above the runway, as the web pairs them: "where it went" and "at this rate" are one glance
            // there (`home-glance`), and the phone stacks what the web puts side by side.
            BreakdownCard(state.homeBreakdown, onOpen = onOpenBreakdown)
            RunwayCard(state.runway, fmt, onOpen = onOpenRunway)
            MilestonesLine(state.milestones, onOpen = onOpenAchievements)
        }
    }
}

/**
 * "🏆 Milestones in progress · 3 ›" — the web's one-line pointer at the Achievements screen, ported.
 *
 * A line and not a panel, for the web's own reason: the full progress lives behind it, so Home has no business
 * stacking a second motivational block under "You're on track for".
 *
 * ⚠️ One deliberate departure. The web only draws this line when something is in progress, because the web also
 * carries a trophy in its header — a phone header with four controls in it has no room for a fifth, so this line
 * is the only door. It therefore stays put once anything has been earned and says "N of M earned" instead. A
 * screen reachable only while you happen to be mid-milestone is a screen most people would never find twice.
 */
@Composable
private fun MilestonesLine(milestones: MilestonesDto?, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    // Null = /milestones hasn't answered. A brand-new account with nothing earned and nothing started has no
    // progress to point at, so it gets no line either.
    val m = milestones ?: return
    if (m.total == 0 || (m.inProgress == 0 && m.earned == 0)) return

    Spacer(Modifier.height(14.dp))
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .clickable(onClick = onOpen)
            .padding(vertical = 12.dp, horizontal = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(TandemIcons.Trophy, contentDescription = null, tint = tandem.muted, modifier = Modifier.size(17.dp))
        Spacer(Modifier.width(9.dp))
        Text(
            if (m.inProgress > 0) "Milestones in progress" else "Milestones",
            fontSize = 13.sp,
            fontWeight = FontWeight.SemiBold,
            color = tandem.muted,
        )
        Spacer(Modifier.width(9.dp))
        Text(
            if (m.inProgress > 0) "${m.inProgress}" else "${m.earned} of ${m.total} earned",
            fontSize = 13.sp,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.primary,
        )
        Spacer(Modifier.weight(1f))
        Icon(TandemIcons.Chevron, contentDescription = "Open achievements", tint = tandem.muted, modifier = Modifier.size(15.dp))
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
private fun AlertStrip(alerts: List<com.tandemtab.app.data.NotificationDto>, onOpenAll: () -> Unit) {
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
                // ⚠️ Tapping the strip opens the full list, and that is not decoration. Since bills moved into
                // the notification list (owner's call, S117), the pull-down became the only route to them — and
                // that pull-down is the gesture that had never fired once until `cda2852`. A second, *visible*
                // door to the same sheet is what stops one silent gesture regression from hiding the bills.
                Modifier.fillMaxWidth()
                    .clip(RoundedCornerShape(14.dp))
                    .background(tandem.alertBg, RoundedCornerShape(14.dp))
                    .border(1.dp, tandem.alertBorder, RoundedCornerShape(14.dp))
                    .clickable(onClick = onOpenAll)
                    .padding(horizontal = 14.dp, vertical = 11.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("⚠️", fontSize = 15.sp)
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    // ⚠️ Server-formatted prose with the amount baked in ("overspent by €4,760.00") — the money
                    // formatters never see it, so privacy mode has to mask it here. See maskServerText.
                    Text(maskServerText(item.text), fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurface)
                    item.desc?.let { Text(maskServerText(it), fontSize = 11.sp, color = tandem.muted) }
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
                // The web switches this figure to warn-text when the account is over-allocated (Dashboard.razor:
                // `State.IsOverAllocated ? "warn-text" : "bal-free-v"`); Android painted it the positive accent
                // unconditionally, so "-€1,221.54" — the largest number on the screen a user lands on — arrived in
                // the colour reserved for good news. ⚠️ The thin overview carries no `isOverAllocated`, so this
                // catches the subset it can see: a figure that has actually gone negative. The sub-line below has
                // branched on its own sign since it was written, which is what makes the headline stand out.
                val freeShort = overview.free < 0.0
                HeroPart("Safe to spend", fmt(overview.free), main = true, valueColor = if (freeShort) tandem.warn else tandem.positive) {
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
                    // ⚠️ Through maskPct. This line sat directly under a masked figure, on the screen the bar
                    // says "Figures hidden" on, and printed the savings rate in full — found on the emulator,
                    // not by reading. A rate beside a hidden amount is an invitation to work out what it is a
                    // rate OF, and the web has always masked its half of this pair with FmtPct.
                    // ⚠️ Only the NUMBER goes through the mask, exactly as the web does it — masking the whole
                    // phrase takes "of money in" with it and leaves a bare row of dots that says nothing about
                    // what was hidden. The label is not the secret; the figure is.
                    HeroSub("${maskPct(Math.round(it * 100).toString())}% of money in", tandem.muted)
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

/**
 * The live-trip hero — Home's card for the journey being lived right now, ported from the web's `trip-hero`.
 *
 * ★ **No dismiss and no pulse.** It is self-limiting: it appears the morning the trip starts and is gone the
 * moment the trip is over, so there is nothing to dismiss — and a glow above the fold for eight straight days
 * would be the nag this whole feature exists not to be.
 *
 * Two bars, and they measure different things on purpose. The **days** bar cannot be behind or ahead of
 * anything, so it reads as pure context for the **spend** bar, which can. The spend fill is capped at 100% so an
 * overspend cannot run off the end of the card; the figure beside it states the overspend and is not capped.
 *
 * ⚠️ `spent` is this account's own total, not `spentIncludingOtherAccounts` — matching the trip card on the
 * Trips tab. The combined figure may only be shown next to `paidFromOtherAccounts`, and a hero has no room to
 * label it; an unlabelled shared figure is one nobody can reconcile.
 */
@Composable
private fun TripHeroCard(trips: TripsUi, onOpen: (String) -> Unit) {
    val tandem = LocalTandemColors.current
    val trip = trips.live ?: return
    val fmt = moneyFormatter(trips.currency)
    val over = trip.overBudget
    val day = trip.day ?: 1
    // Day 1 of 1 must read as a full bar, not 100% of nothing — hence day/length, both ends inclusive.
    val dayPct = (day.toFloat() / trip.lengthInDays.coerceAtLeast(1)).coerceIn(0f, 1f)
    val budget: Double? = if ((trip.budget ?: 0.0) > 0.0) trip.budget else null
    // Capped at 100% so an overspend can't run the fill off the end of the card; the figure beside it states the
    // overspend and is not capped.
    val spendPct: Float = if (budget == null) 0f else (trip.spent / budget).toFloat().coerceIn(0f, 1f)
    val accent = if (over) tandem.spent else tandem.positive

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, accent.copy(alpha = 0.45f), RoundedCornerShape(16.dp))
            .clickable { onOpen(trip.id) }
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            CatIcon(trip.icon ?: "plane", trip.name, size = 20.dp)
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(trip.name, fontWeight = FontWeight.Bold, fontSize = 15.sp, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                val where = trip.destination.orEmpty().trim()
                val line = if (where.isEmpty()) "Day $day of ${trip.lengthInDays}"
                else "Day $day of ${trip.lengthInDays} · $where"
                Text(line, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
            }
            OpenChip()
        }

        MiniBar(fraction = dayPct, fill = tandem.muted)

        Row(verticalAlignment = Alignment.Bottom) {
            Column(Modifier.weight(1f)) {
                Text("So far", fontSize = 11.sp, color = tandem.muted)
                Text(fmt(trip.spent), fontWeight = FontWeight.Bold, fontSize = 17.sp, color = MaterialTheme.colorScheme.onSurface)
            }
            if (budget != null) {
                Column(horizontalAlignment = Alignment.End) {
                    Text("of ${fmt(budget)} planned", fontSize = 11.sp, color = tandem.muted)
                    val delta = kotlin.math.abs(trip.spent - budget)
                    Text(
                        if (over) "${fmt(delta)} over" else "${fmt(delta)} left",
                        fontWeight = FontWeight.Bold, fontSize = 15.sp, color = accent,
                    )
                }
            }
        }
        if (budget != null) MiniBar(fraction = spendPct, fill = accent)

        // Stated here rather than left to be found on the Trips tab: this headline is the one figure in the app
        // that deliberately does NOT mean "what I spent this week" — a flight bought in March is inside it.
        if (trip.prePaid != 0.0) {
            Text(
                "${fmt(trip.prePaid)} booked ahead · ${fmt(trip.onTrip)} while away",
                fontSize = 11.sp, color = tandem.muted,
            )
        }
    }
    Spacer(Modifier.height(14.dp))
}

/** A flat progress rail. Two of these sit on the trip hero measuring different things, so it takes its fill
 *  colour from the caller rather than deciding one. */
@Composable
private fun MiniBar(fraction: Float, fill: Color) {
    Box(
        Modifier.fillMaxWidth().height(5.dp)
            .clip(RoundedCornerShape(999.dp))
            .background(MaterialTheme.colorScheme.outline),
    ) {
        Box(Modifier.fillMaxWidth(fraction).height(5.dp).clip(RoundedCornerShape(999.dp)).background(fill))
    }
}

/**
 * "Where your money went" — Home's door to the Breakdown, ported from the web's `home-brk-card`.
 *
 * ⚠️ **This exists because the Breakdown had no visible door at all.** Its only route in was a left-swipe on
 * Home: undiscoverable, undocumented anywhere in the app, and — as the notification pull-down proved on the same
 * screen — a gesture nobody can see is a gesture that can stop working without anyone noticing. The swipe stays;
 * it is no longer load-bearing.
 *
 * Hidden until there is spend, exactly as on the web: a ring of nothing promotes nothing. The figures come from
 * the shared [moneyFormatter], so a masked account masks the total and the legend with everything else.
 */
@Composable
private fun BreakdownCard(breakdown: BreakdownViewDto?, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    val b = breakdown ?: return
    if (b.spent <= 0.0 || b.slices.isEmpty()) return
    val money = moneyFormatter(b.currency)
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .clickable(onClick = onOpen)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            // No figure inside a ring this small — at 62dp the total would be a few points of type fighting the
            // wedges. It goes beside the title instead, where it has room to be read.
            BreakdownRing(b.slices, size = 62.dp, stroke = 11.dp)
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    "Where your money went",
                    fontWeight = FontWeight.Bold, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface,
                )
                Text("${money(b.spent)} used", fontSize = 12.sp, color = tandem.muted)
            }
            OpenChip()
        }
        // The web shows its top four; so does this. The rest are one tap away, and the card's job is to say
        // "there is a shape to this" rather than to be the chart.
        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            b.slices.take(4).forEach { s ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(8.dp).clip(RoundedCornerShape(2.dp)).background(parseColor(s.color)))
                    Spacer(Modifier.width(8.dp))
                    Text(s.label, fontSize = 12.sp, color = tandem.muted, maxLines = 1, modifier = Modifier.weight(1f))
                    Text(money(s.amount), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
                }
            }
        }
    }
    Spacer(Modifier.height(14.dp))
}

/**
 * "Your week in money" — Home's week recap, ported from the web's `week-recap` section (R2.5, the last of the
 * three server-read rows).
 *
 * ★ It is a **look back at a finished week**, not a running total: the covered week is the last completed
 * Monday–Sunday, so the card says the same thing all week and dismissing it retires it until next Monday. A
 * recap of the week you are still in changes every time you open it, and its comparison puts three days against
 * seven — which reads as a spending collapse every Tuesday.
 *
 * ⚠️ Three gates, all of them deliberate, and the card draws nothing unless all three pass: the read has landed,
 * the server says there is something to report ([WeeklyRecapViewDto.isEmpty]), and this week has not already been
 * waved away. The dismissal is read from prefs in the same pass as the recap, so the card cannot appear and then
 * vanish a moment later.
 */
@Composable
private fun WeekRecapCard(
    recap: WeeklyRecapViewDto?,
    dismissedFrom: String?,
    onOpen: () -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val r = recap ?: return
    if (r.isEmpty || r.from == dismissedFrom) return
    val money = moneyFormatter(r.currency)
    val down = r.change < 0.0

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .clickable(onClick = onOpen)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    "Your week in money",
                    fontWeight = FontWeight.Bold, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface,
                )
                Text("${weekDayLabel(r.from)} – ${weekDayLabel(r.to)}", fontSize = 12.sp, color = tandem.muted)
            }
            // ⚠️ Dismissal is its own hit target next to the chevron rather than a swipe. It is not destructive —
            // the card comes back next Monday — so it does not get a confirm; but it is also not something to
            // find by accident, which is what a swipe on a card in a scrolling column would be.
            Text(
                "✕",
                fontSize = 15.sp, color = tandem.muted,
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .clickable(onClick = onDismiss)
                    .padding(horizontal = 8.dp, vertical = 4.dp),
            )
            OpenChip()
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(18.dp)) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text("Spent", fontSize = 11.sp, color = tandem.muted)
                Text(
                    money(r.spent), fontSize = 16.sp, fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                // Only against a week that actually had spending — the server decides that, not this card.
                if (r.hasComparison) {
                    Text(
                        "${money(abs(r.change))} ${if (down) "less" else "more"}",
                        fontSize = 10.sp, color = if (down) tandem.positive else tandem.spent,
                    )
                }
            }
            r.topCategoryName?.let { top ->
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text("Most of it", fontSize = 11.sp, color = tandem.muted)
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        CatIcon(r.topCategoryIcon, top, size = 15.dp)
                        Spacer(Modifier.width(5.dp))
                        Text(
                            top, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, maxLines = 1,
                            color = MaterialTheme.colorScheme.onSurface,
                        )
                    }
                    Text(money(r.topCategorySpent), fontSize = 10.sp, color = tandem.muted)
                }
            }
            if (r.saved > 0.0) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text("Set aside", fontSize = 11.sp, color = tandem.muted)
                    Text(
                        money(r.saved), fontSize = 16.sp, fontWeight = FontWeight.Bold,
                        color = tandem.positive,
                    )
                }
            }
        }
    }
    Spacer(Modifier.height(14.dp))
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
private fun AccountSwitcher(
    state: UiState,
    onSelectAccount: (String) -> Unit,
    onCreateAccount: () -> Unit,
    onOpened: () -> Unit,
) {
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
        Modifier.clip(RoundedCornerShape(10.dp)).clickable { open = true; onOpened() }.padding(vertical = 2.dp, horizontal = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(account.name, fontSize = 20.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onBackground, maxLines = 1)
        Spacer(Modifier.width(4.dp))
        Icon(TandemIcons.Chevron, contentDescription = "Switch account", tint = LocalTandemColors.current.muted, modifier = Modifier.size(18.dp).rotate(90f))
    }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        state.accounts.forEachIndexed { i, a ->
            val selected = a.id == state.selectedAccountId
            // R2.5: the web's Trip-mode badge, ported. It answers "which of these is on a journey" without
            // switching into each one to find out, which is the whole reason it lives on the row rather than
            // inside the account. Case-insensitive id match — the server hands back GUIDs and the two sides do
            // not agree on casing, which is exactly the kind of comparison that silently never matches.
            val trip = state.activeTrips.firstOrNull { it.accountId.equals(a.id, ignoreCase = true) }
            DropdownMenuItem(
                text = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            a.name,
                            fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                        )
                        if (trip != null) { Spacer(Modifier.width(7.dp)); TripModeTag(trip) }
                    }
                },
                onClick = { onSelectAccount(a.id); open = false },
                leadingIcon = { AccountAvatar(a, i, state.sharing.avatars) },
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

/**
 * R4.5 — "these figures are not live, and here is how old they are".
 *
 * ⚠️ The TIME is the whole point. "Offline" on its own leaves somebody deciding what is safe to spend from a
 * figure of unknown age, which is worse than showing nothing. ★ It also carries the outbox count, because a
 * queued expense is the one thing a person will not simply trust: they typed it and it has not gone anywhere.
 * ⚠️ Amber, not red — nothing is wrong, and nothing has been lost.
 */
@Composable
private fun OfflineStrip(asOf: Long?, pending: Int) {
    if (asOf == null && pending == 0) return
    val when_ = asOf?.let {
        java.text.SimpleDateFormat("d MMM, HH:mm", java.util.Locale.getDefault()).format(java.util.Date(it))
    }
    Row(
        Modifier
            .fillMaxWidth()
            .background(Color(0xFF33290F))
            .padding(horizontal = 14.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            if (asOf != null) "Offline — showing what this device last knew" else "Waiting for a signal",
            fontSize = 12.sp, fontWeight = FontWeight.Medium, color = Color(0xFFE6B45A),
            maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
        )
        Spacer(Modifier.width(8.dp))
        Text(
            // The queue is the more urgent of the two, so it wins the trailing slot when both are true.
            when {
                pending == 1 -> "1 to send"
                pending > 1 -> "$pending to send"
                else -> "as of $when_"
            },
            fontSize = 12.sp, fontWeight = FontWeight.Bold, color = Color(0xFFE6B45A), maxLines = 1,
        )
    }
}

/** The switcher's Trip-mode badge: the journey's icon and name on a tinted pill.
 *
 *  ⚠️ The name is truncated rather than wrapped. A dropdown row is one line and a long journey name would either
 *  push the account's own name out of view or make the row two lines tall — and the account name is what the
 *  menu is for. The badge is a hint that a trip is running, not the place to read its title. */
@Composable
private fun TripModeTag(trip: ActiveTripDto) {
    // Brand primary in both themes, like the web's .trip-tag — deliberately not the muted grey the rest of the
    // row wears, because the badge is the one thing on it that is news.
    Row(
        Modifier
            .background(MaterialTheme.colorScheme.primary.copy(alpha = .12f), RoundedCornerShape(7.dp))
            .padding(horizontal = 6.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CatIcon(trip.icon ?: "plane", trip.tripName, size = 12.dp)
        Spacer(Modifier.width(4.dp))
        Text(
            trip.tripName,
            fontSize = 11.sp,
            fontWeight = FontWeight.SemiBold,
            color = MaterialTheme.colorScheme.primary,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.widthIn(max = 96.dp),
        )
    }
}

/** A small avatar for an account row: the members' pictures stacked (up to 3, falling back to initials), or a
 *  colour-coded account initial when an account has no members to show.
 *
 *  ⚠️ `avatars` only ever holds the **selected** account's members — it comes from
 *  `GET /accounts/{id}/avatars` for the account currently open. So rows for the *other* accounts in this
 *  dropdown fall back to initials, and that is not a bug to fix here: the web behaves identically
 *  (`Dashboard.razor:110` looks up the same per-account map), and fetching every account's avatars to decorate
 *  a dropdown would be several requests for a row most people never open. A person who appears in more than
 *  one of your accounts is shown their picture on the row that is loaded, their initial on the rest. */
@Composable
private fun AccountAvatar(account: AccountSummaryDto, index: Int, avatars: Map<String, String>) {
    val members = account.members
    if (members.isEmpty()) {
        AvatarCircle(initialOf(account.name), avatarPalette[index % avatarPalette.size])
        return
    }
    val shown = members.take(3)
    Row(horizontalArrangement = Arrangement.spacedBy((-8).dp)) {
        for (i in shown.indices) {
            AvatarCircle(initialOf(shown[i].displayName), avatarPalette[i % avatarPalette.size], avatars[shown[i].userId])
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
    // ⚠️ Arrows, because the horizontal swipe no longer steps months. Stepping used to cost chip + menu + tap,
    // which is why it was given the gesture in the first place; handing the gesture to the breakdown without
    // putting stepping back within one tap would trade one complaint for another.
    val firstIdx = state.periods.firstOrNull()?.index
    val lastIdx = state.periods.lastOrNull()?.index
    Row(verticalAlignment = Alignment.CenterVertically) {
        val canBack = firstIdx != null && viewing > firstIdx
        Icon(
            TandemIcons.Chevron,
            contentDescription = "Previous month",
            tint = if (canBack) tandem.muted else tandem.muted.copy(alpha = 0.3f),
            modifier = Modifier
                .size(26.dp)
                .clip(RoundedCornerShape(999.dp))
                .clickable(enabled = canBack) { onSelectPeriod(viewing - 1) }
                .padding(6.dp)
                .rotate(180f),
        )
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
        val canForward = lastIdx != null && viewing < lastIdx
        Icon(
            TandemIcons.Chevron,
            contentDescription = "Next month",
            tint = if (canForward) tandem.muted else tandem.muted.copy(alpha = 0.3f),
            modifier = Modifier
                .size(26.dp)
                .clip(RoundedCornerShape(999.dp))
                .clickable(enabled = canForward) { onSelectPeriod(viewing + 1) }
                .padding(6.dp),
        )
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

/** One stacked avatar: the person's picture if we hold one, else their initial on a palette colour.
 *
 *  ⚠️ The 2.dp ring is what makes an overlapping row readable as separate people, so the picture must not
 *  paint over it. A `border` draws under the Box's content, so the image is inset by the same 2.dp instead and
 *  the ring colour is painted as the background behind it — same ring either way, picture or initial. */
@Composable
private fun AvatarCircle(text: String, color: Color, dataUrl: String? = null) {
    val bitmap = remember(dataUrl) { decodeDataUrlImage(dataUrl) }
    val ring = MaterialTheme.colorScheme.surface
    Box(
        Modifier
            .size(30.dp)
            .clip(CircleShape)
            .background(if (bitmap != null) ring else color)
            .border(2.dp, ring, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        if (bitmap != null) {
            Image(
                bitmap,
                contentDescription = null,
                modifier = Modifier.size(30.dp).padding(2.dp).clip(CircleShape),
                contentScale = ContentScale.Crop,
            )
        } else {
            Text(text, color = Color.White, fontSize = if (text.length > 1) 11.sp else 13.sp, fontWeight = FontWeight.Bold)
        }
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
