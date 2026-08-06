package com.tandemtab.app

import android.app.Application
import android.content.Context
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.tandemtab.app.data.AccountOverviewDto
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.AddDepositRequest
import com.tandemtab.app.data.AddExpenseRequest
import com.tandemtab.app.data.AddSavingDepositRequest
import com.tandemtab.app.data.LogInstallmentRequest
import com.tandemtab.app.data.BankSyncStatusDto
import com.tandemtab.app.data.PendingBankTransactionDto
import com.tandemtab.app.data.RecordConsentRequest
import com.tandemtab.app.data.StartBankLinkRequest
import com.tandemtab.app.data.ChangePasswordRequest
import com.tandemtab.app.data.BudgetMutationDto
import com.tandemtab.app.data.BudgetRowDto
import com.tandemtab.app.data.CreateCategoryRequest
import com.tandemtab.app.data.EditCategoryRequest
import com.tandemtab.app.data.SetBudgetRequest
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.FundOptionDto
import com.tandemtab.app.data.FundRowDto
import com.tandemtab.app.data.FundTransferRowDto
import com.tandemtab.app.data.ConfirmRecurringRequest
import com.tandemtab.app.data.InsightsDto
import com.tandemtab.app.data.NotificationDto
import com.tandemtab.app.data.PeriodRowDto
import com.tandemtab.app.data.PeriodsViewDto
import com.tandemtab.app.data.RecurringRowDto
import com.tandemtab.app.data.RecurringViewDto
import com.tandemtab.app.data.SavingsViewDto
import com.tandemtab.app.data.SpendFromSavingsRequest
import com.tandemtab.app.data.TransferFundsRequest
import com.tandemtab.app.data.WalletsViewDto
import com.tandemtab.app.data.DepositRowDto
import com.tandemtab.app.data.RecentExpenseDto
import com.tandemtab.app.data.RunwayDto
import com.tandemtab.app.data.SavingBucketDto
import com.tandemtab.app.data.TargetDto
import com.tandemtab.app.data.TandemTabApi
import com.tandemtab.app.data.TokenStore
import com.tandemtab.app.data.CreateContributionCategoryRequest
import com.tandemtab.app.data.MutationResultDto
import com.tandemtab.app.data.ReschedulePeriodRequest
import com.tandemtab.app.data.SaveSavingBucketRequest
import com.tandemtab.app.data.StartNextPeriodRequest
import java.time.LocalDate
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Top-level screens for the first vertical slice. */
sealed interface Screen {
    /** Brief startup state while we check for a persisted session. */
    data object Splash : Screen
    data object Login : Screen
    /** A 2FA-gated account: enter the TOTP/recovery code to finish signing in. */
    data object TwoFactor : Screen
    data object Home : Screen
}

data class UiState(
    val screen: Screen = Screen.Splash,
    val busy: Boolean = false,
    val error: String? = null,
    val resetLinkSent: Boolean = false,
    val googleEnabled: Boolean = false,
    val username: String = "",
    val email: String = "",
    // Who "you" are on a shared account (from /me): decides the *you* tag, who can't be removed, and who is left
    // out of the hand-over picker. Blank until /me lands, which is why every use falls back to showing nothing
    // rather than to showing the wrong person.
    val myUserId: String = "",
    // The resolved plan ("free"/"pro"/"unlimited"). Decoration only — the crown next to Invite. The server's 402
    // is the actual gate, so a stale plan here can never wrongly allow or wrongly block anything.
    val plan: String = "",
    // External sign-in provider ("google"/"facebook") for the current user, or null for a local password account.
    val provider: String? = null,
    // Profile: data-URL avatar (provider-sourced for external logins), email-verified + 2FA-enabled flags.
    val avatar: String? = null,
    val emailVerified: Boolean = false,
    val twoFactorEnabled: Boolean = false,
    // Outstanding 2FA challenge ticket (from login/exchange), consumed on the TwoFactor screen.
    val twoFactorTicket: String? = null,
    // Home data
    val accounts: List<AccountSummaryDto> = emptyList(),
    val selectedAccountId: String? = null,
    val overview: AccountOverviewDto? = null,
    val periodLabel: String? = null,
    // Period navigation: the account's periods, its open/current index, and which one is being viewed (null = current).
    val periods: List<PeriodRowDto> = emptyList(),
    val currentPeriodIndex: Int = -1,
    val selectedPeriod: Int? = null,
    // Write-flight state for the period-lifecycle sheets (roll forward / reschedule / undo).
    val periodOps: PeriodUi = PeriodUi(),
    // Home forecast (server-computed): the runway card + the "on track for" targets.
    val runway: RunwayDto? = null,
    val targets: List<TargetDto> = emptyList(),
    // Current-period alerts (server-computed). Home renders the urgent ones; the rest are informational.
    val alerts: List<NotificationDto> = emptyList(),
    val spending: SpendingUi = SpendingUi(),
    val goals: GoalsUi = GoalsUi(),
    val wallets: WalletsUi = WalletsUi(),
    val health: HealthUi = HealthUi(),
    val recurring: RecurringUi = RecurringUi(),
    val settings: SettingsUi = SettingsUi(),
    val sharing: SharingUi = SharingUi(),
    val twoFactor: TwoFactorUi = TwoFactorUi(),
    val bank: BankUi = BankUi(),
    // The expense the FAB's "Edit last" is currently editing (null = the add sheet is in add mode / closed).
    val editingExpense: ExpenseDto? = null,
    // The deposit the income tab's "Edit last" is currently editing.
    val editingDeposit: DepositRowDto? = null,
) {
    val selectedAccount: AccountSummaryDto?
        get() = accounts.firstOrNull { it.id == selectedAccountId }

    /** Everyone on the open account except you — the people who can be removed, handed the account, or left it to. */
    val otherMembers: List<com.tandemtab.app.data.MemberDto>
        get() = selectedAccount?.members.orEmpty().filter { it.userId != myUserId }

    /** Whether inviting is out of this plan's reach, i.e. whether to wear the crown. Only "free" is gated; an
     *  unknown/absent plan is treated as ungated, so a failed /me never invents a paywall that isn't there. */
    val shareIsProLocked: Boolean
        get() = plan == "free"

    /** The period being looked at right now (the open one unless the user has paged back). */
    val viewedPeriod: PeriodRowDto?
        get() = periods.firstOrNull { it.index == (selectedPeriod ?: currentPeriodIndex) }

    /** You can only roll into the next period once the current one has actually ended — the same guard the server
     *  enforces (it 400s otherwise). Checked here too so the menu greys out rather than offering a doomed action. */
    val canStartNextPeriod: Boolean
        get() = periods.lastOrNull()?.let { runCatching { LocalDate.parse(it.to) < LocalDate.now() }.getOrDefault(false) } == true

    /** Undo needs something to fall back to: the server refuses to delete an account's only period. */
    val canRemoveLatestPeriod: Boolean
        get() = periods.size > 1
}

/** Write-flight state shared by the three period-lifecycle sheets. Only one can be open at a time, so one
 *  busy/error pair covers all of them. */
data class PeriodUi(
    val busy: Boolean = false,
    val error: String? = null,
)

/** Lazy-loaded state for the Spending tab. Also the source of the add-expense pickers (categories/funds come
 *  free with the /spending payload); `recent` is fetched separately from /expense-entry for the "most-used" chips. */
data class SpendingUi(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val error: String? = null,
    val currency: String = "",
    val spent: Double = 0.0,
    val expenses: List<ExpenseDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
    val funds: List<FundOptionDto> = emptyList(),
    val recent: List<RecentExpenseDto> = emptyList(),
    // Per-category budget coverage for the Categories view (fetched alongside /spending).
    val budgets: List<BudgetRowDto> = emptyList(),
    val totalBudgeted: Double = 0.0,
    val totalSpent: Double = 0.0,
    // Contribution (income source) categories for the add sheet's Income tab, from /income.
    val incomeCategories: List<CategoryOptionDto> = emptyList(),
    // Add sheet flight state (a batch expense save or an income deposit is in progress / its last error).
    val saving: Boolean = false,
    val saveError: String? = null,
)

/** Lazy-loaded state for the Goals tab (savings buckets: goals/debts/investments/sinking funds). */
data class GoalsUi(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val error: String? = null,
    val currency: String = "",
    val saved: Double = 0.0,
    val savedRate: Double? = null,   // saved-this-period as a share of income (0..1), null if unknown
    val buckets: List<SavingBucketDto> = emptyList(),
    val saving: Boolean = false,
    val saveError: String? = null,
)

/** Lazy-loaded state for the Recurring (bills/income) card + sheet. `busyId` is the item mid confirm/skip. */
data class RecurringUi(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val error: String? = null,
    val currency: String = "",
    val billsDue: Double = 0.0,
    val items: List<RecurringRowDto> = emptyList(),
    val busyId: String? = null,
    val actionError: String? = null,
)

/** Lazy-loaded state for the Home Health card + Insights modal. Currency comes from the selected account. */
data class HealthUi(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val error: String? = null,
    val currency: String = "",
    val data: InsightsDto? = null,
)

/** Lazy-loaded state for the Bank (External accounts) sheet. `enabled` gates the whole feature (allowlist +
 *  verified email, resolved server-side) — when false the Wallets entry point stays hidden. `linkUrl` is a
 *  one-shot the UI consumes to open the bank's consent page in a browser. `handlingId` is the pending row mid
 *  confirm/dismiss. */
data class BankUi(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val enabled: Boolean = false,
    val connected: Boolean = false,
    val institutionName: String? = null,
    val institutionLogo: String? = null,
    val balance: Double? = null,
    val balanceCurrency: String? = null,
    val lastSyncedAt: String? = null,
    val pending: List<PendingBankTransactionDto> = emptyList(),
    val busy: Boolean = false,
    val error: String? = null,
    val linkUrl: String? = null,
    val handlingId: String? = null,
)

/** State for the Two-factor enrollment flow in the profile. `setup` holds the QR/secret while enrolling;
 *  `recoveryCodes` holds the one-time codes shown right after a successful confirm. */
data class TwoFactorUi(
    val busy: Boolean = false,
    val error: String? = null,
    val setup: com.tandemtab.app.data.TwoFactorSetupDto? = null,   // non-null while enrolling (show the QR + code field)
    val recoveryCodes: List<String>? = null,                        // non-null right after confirm (show once)
    val disabling: Boolean = false,                                 // the "enter a code to turn off" panel is open
)

/** State for the Profile & Account settings sheet (change-password / rename write-flight + result). */
data class SettingsUi(
    val busy: Boolean = false,
    val error: String? = null,
    val passwordChanged: Boolean = false,
)

/**
 * Sharing: the invitations waiting on this user, plus the write-flight state for every membership action
 * (invite / accept / decline / remove / hand over). One busy flag covers them because they're all raised from
 * the same two surfaces and only one can be in flight at a time.
 *
 * `avatars` is the account's profile pictures by user id — fetched separately from the account summary, and
 * allowed to be empty (a member list of initials is fine; a failed picture must never empty the list).
 */
data class SharingUi(
    val invitations: List<com.tandemtab.app.data.InvitationDto> = emptyList(),
    val avatars: Map<String, String> = emptyMap(),
    val busy: Boolean = false,
    val error: String? = null,
    // Set after a successful invite so the sheet can say who it went to, cleared when the field is edited again.
    val invited: String? = null,
)

/** Lazy-loaded state for the Wallets tab (funds + this period's transfers). Also carries the contribution-category
 *  picker (fetched from /income) for the Add-income flow, plus write-flight state. */
data class WalletsUi(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val error: String? = null,
    val currency: String = "",
    val current: Double = 0.0,
    val funds: List<FundRowDto> = emptyList(),
    val transfers: List<FundTransferRowDto> = emptyList(),
    val incomeCategories: List<CategoryOptionDto> = emptyList(),
    val saving: Boolean = false,
    val saveError: String? = null,
)

class AppViewModel(app: Application) : AndroidViewModel(app) {

    private val api: TandemTabApi = TandemTabApi(store = TokenStore(app))

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    // Theme is a manual choice (not the system setting), persisted, defaulting to dark — mirroring the web,
    // whose finappGetTheme() returns localStorage 'finapp-theme' or 'dark'. Read synchronously so the first
    // frame paints the chosen theme with no light→dark flash.
    private val uiPrefs = app.getSharedPreferences("tandem_ui", Context.MODE_PRIVATE)
    private val _darkTheme = MutableStateFlow(uiPrefs.getBoolean("dark_theme", true))
    val darkTheme: StateFlow<Boolean> = _darkTheme.asStateFlow()

    fun toggleTheme() {
        val next = !_darkTheme.value
        uiPrefs.edit().putBoolean("dark_theme", next).apply()
        _darkTheme.value = next
    }

    init {
        // Discover which external providers to show (best-effort — button stays hidden if unreachable).
        viewModelScope.launch {
            val providers = runCatching { api.getProviders() }.getOrNull() ?: return@launch
            _state.update { it.copy(googleEnabled = providers.google) }
        }
        // Resume a persisted session if there is one, else fall through to the login screen.
        viewModelScope.launch { resumeSession() }
    }

    /** On launch: seed the saved session and open Home; if it's expired/revoked, drop cleanly to Login. */
    private suspend fun resumeSession() {
        val saved = api.restore()
        if (saved == null) {
            _state.update { it.copy(screen = Screen.Login) }
            return
        }
        _state.update { it.copy(busy = true, username = saved.username, error = null) }
        loadHome()
        if (_state.value.screen != Screen.Home) {
            // Couldn't resume — forget the dead session and show a clean login (no scary error).
            api.signOut()
            _state.update { UiState(screen = Screen.Login, googleEnabled = it.googleEnabled) }
        }
    }

    /** URL to open in a browser to start Google sign-in (result deep-links back into the app). */
    fun googleAuthUrl(): String = api.externalAuthUrl("google")

    /** Handle the com.tandemtab.app://auth/callback deep link after external sign-in. */
    fun onExternalAuthResult(authCode: String?, error: Boolean) {
        if (error || authCode.isNullOrBlank()) {
            _state.update { it.copy(busy = false, error = "Google sign-in was cancelled or failed.") }
            return
        }
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                handleAuthOutcome(api.exchangeCode(authCode), "Sign-in didn't complete.")
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Sign-in didn't complete.") }
            }
        }
    }

    /** Route a login/exchange result: 2FA-gate → the code screen, tokens → Home, otherwise a fallback error. */
    private suspend fun handleAuthOutcome(result: com.tandemtab.app.data.LoginResponse, fallbackError: String) {
        when {
            result.twoFactorRequired && result.twoFactorTicket != null ->
                _state.update { it.copy(busy = false, screen = Screen.TwoFactor, twoFactorTicket = result.twoFactorTicket, error = null) }
            result.auth != null -> {
                _state.update { it.copy(username = result.auth.username) }
                loadHome()
            }
            else -> _state.update { it.copy(busy = false, error = fallbackError) }
        }
    }

    /** Finish a 2FA-gated sign-in with the code the user typed. */
    fun submitTwoFactor(code: String) {
        val ticket = _state.value.twoFactorTicket
        if (ticket == null) {
            _state.update { it.copy(screen = Screen.Login, error = "That sign-in expired. Please try again.") }
            return
        }
        if (code.isBlank()) {
            _state.update { it.copy(error = "Enter the code from your authenticator app.") }
            return
        }
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val auth = api.twoFactor(ticket, code)
                _state.update { it.copy(username = auth.username, twoFactorTicket = null) }
                loadHome()
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Couldn't verify the code.") }
            }
        }
    }

    /** Abandon the 2FA challenge and go back to the sign-in screen. */
    fun cancelTwoFactor() = _state.update { it.copy(screen = Screen.Login, twoFactorTicket = null, busy = false, error = null) }

    fun login(usernameOrEmail: String, password: String) {
        if (usernameOrEmail.isBlank() || password.isBlank()) {
            _state.update { it.copy(error = "Enter your username/email and password.") }
            return
        }
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                handleAuthOutcome(api.login(usernameOrEmail, password), "Unexpected sign-in response.")
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Sign-in failed.") }
            }
        }
    }

    fun register(username: String, email: String, password: String) {
        when {
            username.isBlank() || email.isBlank() ->
                _state.update { it.copy(error = "Fill in a username, email and password to continue.") }
            password.length < 8 ->
                _state.update { it.copy(error = "Password must be at least 8 characters.") }
            else -> {
                _state.update { it.copy(busy = true, error = null) }
                viewModelScope.launch {
                    try {
                        val auth = api.register(username, email, password)
                        _state.update { it.copy(username = auth.username) }
                        loadHome()
                    } catch (e: Exception) {
                        _state.update { it.copy(busy = false, error = e.message ?: "Sign-up failed.") }
                    }
                }
            }
        }
    }

    fun sendResetLink(identifier: String) {
        if (identifier.isBlank()) {
            _state.update { it.copy(error = "Enter your username or email.") }
            return
        }
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                api.forgotPassword(identifier)
                _state.update { it.copy(busy = false, resetLinkSent = true, error = null) }
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Couldn't send the reset link.") }
            }
        }
    }

    fun clearResetLinkSent() = _state.update { it.copy(resetLinkSent = false) }

    private suspend fun loadHome() {
        try {
            val accounts = api.listAccounts()
            val selected = accounts.firstOrNull()
            val overview = selected?.let { api.overview(it.id, _state.value.selectedPeriod) }
            _state.update {
                it.copy(
                    screen = Screen.Home,
                    busy = false,
                    error = null,
                    accounts = accounts,
                    selectedAccountId = selected?.id,
                    overview = overview,
                )
            }
            selected?.let { loadPeriodLabel(it.id); loadForecast(it.id); loadMemberAvatars(it.id) }
            // Identity for the profile sheet (best-effort — the sheet still works from the stored username).
            runCatching { api.me() }.getOrNull()?.let { me ->
                _state.update { it.copy(
                    myUserId = me.id.ifBlank { it.myUserId }, plan = me.plan,
                    username = me.username.ifBlank { it.username }, email = me.email, provider = me.provider,
                    avatar = me.avatar, emailVerified = me.emailVerified, twoFactorEnabled = me.twoFactorEnabled,
                ) }
            }
            loadInvitations()
        } catch (e: Exception) {
            _state.update { it.copy(busy = false, error = e.message ?: "Couldn't load your accounts.") }
        }
    }

    /** Best-effort: fetch the account's periods; store them + the current index and label the viewed period. */
    private suspend fun loadPeriodLabel(accountId: String) {
        val p = runCatching { api.periods(accountId) }.getOrNull() ?: return
        _state.update { st ->
            val shown = st.selectedPeriod ?: p.currentIndex
            val row = p.periods.getOrNull(shown) ?: p.periods.getOrNull(p.currentIndex) ?: p.periods.lastOrNull()
            st.copy(periods = p.periods, currentPeriodIndex = p.currentIndex, periodLabel = row?.let { formatPeriod(it) })
        }
    }

    /** Switch the viewed period (null / the current index → back to the open period). Re-fetches every view. */
    fun selectPeriod(index: Int?) {
        val cur = _state.value.currentPeriodIndex
        val normalized = if (index == null || index == cur) null else index
        if (normalized == _state.value.selectedPeriod) return
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { st ->
            val row = st.periods.getOrNull(normalized ?: st.currentPeriodIndex)
            st.copy(
                selectedPeriod = normalized,
                periodLabel = row?.let { formatPeriod(it) } ?: st.periodLabel,
                overview = null, busy = true,
                spending = SpendingUi(), goals = GoalsUi(), wallets = WalletsUi(), recurring = RecurringUi(),
            )
        }
        viewModelScope.launch {
            runCatching { api.overview(accountId, normalized) }.getOrNull()?.let { ov -> _state.update { it.copy(overview = ov, busy = false) } }
            loadForecast(accountId)
            loadSpending(force = true); loadGoals(force = true); loadWallets(force = true); loadRecurring(force = true); loadHealth(force = true)
        }
    }

    /** Best-effort: fetch the Home forecast (runway + "on track for" targets), both server-computed. */
    private suspend fun loadForecast(accountId: String) {
        runCatching { api.runway(accountId) }.onSuccess { r -> _state.update { it.copy(runway = r) } }
        runCatching { api.targets(accountId) }.getOrNull()?.let { t -> _state.update { it.copy(targets = t.targets) } }
        // Alerts are only ever computed for the CURRENT period server-side, so a user browsing a past period would
        // otherwise see this month's warnings attached to a month that already closed. Clear them instead.
        val alerts = if (_state.value.selectedPeriod == null)
            runCatching { api.notifications(accountId) }.getOrNull()?.items.orEmpty() else emptyList()
        _state.update { it.copy(alerts = alerts) }
    }

    /** "July 2026" for a whole-month period, else a "d MMM – d MMM" range. Null if unparseable. */
    private fun formatPeriod(cur: PeriodRowDto): String? {
        return runCatching {
            val from = java.time.LocalDate.parse(cur.from)
            val to = java.time.LocalDate.parse(cur.to)
            if (from.month == to.month && from.year == to.year) {
                from.format(java.time.format.DateTimeFormatter.ofPattern("LLLL yyyy", java.util.Locale.getDefault()))
            } else {
                val f = java.time.format.DateTimeFormatter.ofPattern("d MMM", java.util.Locale.getDefault())
                "${from.format(f)} – ${to.format(f)}"
            }
        }.getOrNull()
    }

    // --- Period lifecycle: roll into the next month, reschedule, undo the last rollover -------------------
    // The gap R2 called out as making Android a *different* product: without these, a phone-only user can never
    // leave the month they signed up in. The web's flow is mirrored, including its reconciliation step — the
    // domain never reads a bank, so what each fund really holds is the caller's to supply.

    /** Prep the "Start next month" sheet: clear a stale error and make sure the funds (and their balances) are
     *  loaded, since they're what the opening-balance form is built from. */
    fun prepareStartNextPeriod() {
        _state.update { it.copy(periodOps = PeriodUi()) }
        loadWallets(false)
    }

    /** Clear a stale error before opening the reschedule / undo sheets (neither needs any data prefetched). */
    fun preparePeriodEdit() = _state.update { it.copy(periodOps = PeriodUi()) }

    /**
     * Close the open period and open the next one. [openings] is what each non-synced fund really holds now,
     * keyed by fund id — those become the new period's opening balances.
     *
     * [adjustments] is the reconciliation choice: when non-empty, each (fundId, gap) is written into the
     * **closing** period first, so its books balance before it's sealed. Passing an empty list is the "ignore"
     * branch — the difference simply carries forward untracked, which is a legitimate choice, not a failure.
     */
    fun startNextPeriod(
        copyBudgets: Boolean,
        adjustBudgets: Boolean,
        openings: Map<String, Double>,
        adjustments: List<Pair<String, Double>> = emptyList(),
        onDone: () -> Unit,
    ) {
        val accountId = _state.value.selectedAccountId ?: return
        val closing = _state.value.periods.lastOrNull() ?: return
        _state.update { it.copy(periodOps = PeriodUi(busy = true)) }
        viewModelScope.launch {
            try {
                if (adjustments.isNotEmpty()) recordReconciliationAdjustments(accountId, adjustments, closing.to)
                // The synced fund's opening isn't hand-entered. Prefer the balance recorded at the closing period's
                // month-end so a late rollover doesn't leak next-month activity in; fall back to the live balance.
                val syncedClose = if (_state.value.wallets.funds.any { it.synced }) {
                    api.bankBalanceAt(accountId, closing.to) ?: _state.value.bank.balance
                } else null
                api.startNextPeriod(accountId, StartNextPeriodRequest(
                    copyBudgets = copyBudgets,
                    adjustBudgets = adjustBudgets && copyBudgets,
                    fundOpenings = openings,
                    syncedFundClosingBalance = syncedClose,
                    today = LocalDate.now().toString(),
                ))
                reloadAfterPeriodChange(accountId)
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(periodOps = PeriodUi(error = e.message ?: "Couldn't start the next period.")) }
            }
        }
    }

    /** Move the viewed period's dates. Later periods shift to stay contiguous (the server does the shifting). */
    fun reschedulePeriod(index: Int, fromIso: String, toIso: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(periodOps = PeriodUi(busy = true)) }
        viewModelScope.launch {
            try {
                api.reschedulePeriod(accountId, index, ReschedulePeriodRequest(fromIso, toIso))
                reloadAfterPeriodChange(accountId)
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(periodOps = PeriodUi(error = e.message ?: "Couldn't change those dates.")) }
            }
        }
    }

    /** Undo the last rollover: delete the newest period and everything in it, re-opening the previous one. */
    fun removeLatestPeriod(onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(periodOps = PeriodUi(busy = true)) }
        viewModelScope.launch {
            try {
                api.removeLatestPeriod(accountId)
                reloadAfterPeriodChange(accountId)
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(periodOps = PeriodUi(error = e.message ?: "Couldn't remove that period.")) }
            }
        }
    }

    /**
     * File the per-fund drift as real entries in the closing period, so its books reconcile. A fund holding LESS
     * than the ledger says means spending that was never logged (an expense); MORE means money-in that wasn't
     * (a deposit). Both land in a category named "Adjustment", created on first use, so the user can recategorise
     * them later rather than hunting for an opaque correction.
     */
    private suspend fun recordReconciliationAdjustments(accountId: String, gaps: List<Pair<String, Double>>, dateIso: String) {
        var expenseCat: String? = null
        var contribCat: String? = null
        for ((fundId, gap) in gaps) {
            if (gap < 0) {
                expenseCat = expenseCat
                    ?: api.spending(accountId).categories.firstOrNull { it.name.equals("Adjustment", true) }?.id
                    ?: api.createCategory(accountId, CreateCategoryRequest("Adjustment", null, "⚖️")).entityId
                    ?: continue
                api.addExpense(accountId, AddExpenseRequest(expenseCat, -gap, fundId, dateIso, "Reconciliation"))
            } else if (gap > 0) {
                contribCat = contribCat
                    ?: api.income(accountId).categories.firstOrNull { it.name.equals("Adjustment", true) }?.id
                    ?: api.createContributionCategory(accountId, CreateContributionCategoryRequest("Adjustment", "⚖️")).entityId
                    ?: continue
                api.addDeposit(accountId, AddDepositRequest(contribCat, fundId, gap, dateIso))
            }
        }
    }

    /** A period write changes every figure on every tab, so drop back to the (new) open period and refetch. */
    private suspend fun reloadAfterPeriodChange(accountId: String) {
        _state.update {
            it.copy(
                periodOps = PeriodUi(), selectedPeriod = null, overview = null, busy = true,
                spending = SpendingUi(), goals = GoalsUi(), wallets = WalletsUi(), recurring = RecurringUi(), health = HealthUi(),
            )
        }
        runCatching { api.overview(accountId) }.getOrNull()?.let { ov -> _state.update { it.copy(overview = ov) } }
        _state.update { it.copy(busy = false) }
        loadPeriodLabel(accountId)
        loadForecast(accountId)
        loadSpending(force = true); loadGoals(force = true); loadWallets(force = true)
        loadRecurring(force = true); loadHealth(force = true)
    }

    fun selectAccount(accountId: String) {
        if (accountId == _state.value.selectedAccountId) return
        _state.update {
            it.copy(
                busy = true, selectedAccountId = accountId, overview = null, periodLabel = null,
                runway = null, targets = emptyList(),
                spending = SpendingUi(), goals = GoalsUi(), wallets = WalletsUi(), health = HealthUi(), recurring = RecurringUi(),
                bank = BankUi(),
                // Faces belong to the account that was open, not to this one — but the invitations are the user's
                // own and outlive any account switch, so they stay.
                sharing = it.sharing.copy(avatars = emptyMap(), error = null, invited = null),
            )
        }
        viewModelScope.launch {
            try {
                val overview = api.overview(accountId)
                _state.update { it.copy(busy = false, overview = overview) }
                loadPeriodLabel(accountId)
                loadForecast(accountId)
                loadMemberAvatars(accountId)
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Couldn't load that account.") }
            }
        }
    }

    /** Lazily load the Spending tab the first time it's shown (or when forced, e.g. pull-to-refresh). */
    fun loadSpending(force: Boolean = false) {
        val accountId = _state.value.selectedAccountId ?: return
        val cur = _state.value.spending
        if (!force && (cur.loaded || cur.loading)) return
        _state.update { it.copy(spending = it.spending.copy(loading = true, error = null)) }
        val period = _state.value.selectedPeriod
        viewModelScope.launch {
            try {
                val v = api.spending(accountId, period)
                _state.update {
                    it.copy(spending = it.spending.copy(
                        loading = false, loaded = true, error = null,
                        currency = v.currency, spent = v.overview.spent, expenses = v.expenses,
                        categories = v.categories, funds = v.funds,
                    ))
                }
                // Budget coverage rides alongside for the Categories view (best-effort — don't fail the tab on it).
                runCatching { api.budgets(accountId, period) }.getOrNull()?.let { b ->
                    _state.update { it.copy(spending = it.spending.copy(budgets = b.budgets, totalBudgeted = b.totalBudgeted, totalSpent = b.totalSpent)) }
                }
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(loading = false, error = e.message ?: "Couldn't load spending.")) }
            }
        }
    }

    /** Prepare the add sheet (Expense + Income): ensure the /spending pickers are loaded, pull the recent-expense
     *  history (/expense-entry) for the most-used chips + per-category default fund, and pull the contribution
     *  (income source) categories (/income) for the Income tab. */
    fun prepareAdd() {
        loadSpending(false)
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saveError = null)) }
        viewModelScope.launch {
            runCatching { api.expenseEntry(accountId) }.getOrNull()
                ?.let { entry -> _state.update { it.copy(spending = it.spending.copy(recent = entry.recent)) } }
            if (_state.value.spending.incomeCategories.isEmpty()) {
                runCatching { api.income(accountId) }.getOrNull()
                    ?.let { inc -> _state.update { it.copy(spending = it.spending.copy(incomeCategories = inc.categories)) } }
            }
        }
    }

    /** Record income from the add sheet's Income tab. Shares the add sheet's saving flag; on success it updates
     *  Home's overview and invalidates the Wallets cache so fund balances refresh on next view. */
    fun addIncomeFromAdd(fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.addDeposit(accountId, AddDepositRequest(categoryId, fundId, amount, date))
                _state.update {
                    it.copy(
                        overview = mut.overview,
                        spending = it.spending.copy(saving = false),
                        wallets = it.wallets.copy(loaded = false),   // balances changed → re-fetch on next Wallets view
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't record income.")) }
            }
        }
    }

    /** Save a batch of staged expenses (the S68 multi-add ✓). POSTs each in turn; splices every returned row into the
     *  Spending list and reflects the recomputed overview on Home. On any failure it stops and surfaces the message
     *  (rows already saved stay saved); [onDone] fires only on a clean full save so the sheet can close. */
    fun addExpenses(drafts: List<AddExpenseRequest>, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        if (drafts.isEmpty()) return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            val added = mutableListOf<ExpenseDto>()
            var lastOverview: AccountOverviewDto? = null
            try {
                for (d in drafts) {
                    val mut = api.addExpense(accountId, d)
                    mut.expense?.let { added.add(it) }
                    lastOverview = mut.overview
                }
                _state.update {
                    it.copy(
                        overview = lastOverview ?: it.overview,
                        spending = it.spending.copy(
                            saving = false, saveError = null,
                            expenses = added.reversed() + it.spending.expenses,
                            spent = lastOverview?.spent ?: it.spending.spent,
                        ),
                    )
                }
                onDone()
            } catch (e: Exception) {
                // Some rows may have saved before the failure — reflect those and let the user retry the rest.
                _state.update {
                    it.copy(
                        overview = lastOverview ?: it.overview,
                        spending = it.spending.copy(
                            saving = false,
                            saveError = e.message ?: "Couldn't save the expense.",
                            expenses = if (added.isEmpty()) it.spending.expenses else added.reversed() + it.spending.expenses,
                            spent = lastOverview?.spent ?: it.spending.spent,
                        ),
                    )
                }
            }
        }
    }

    /** Lazily load the Goals tab (savings buckets) the first time it's shown, or when forced. */
    fun loadGoals(force: Boolean = false) {
        val accountId = _state.value.selectedAccountId ?: return
        val cur = _state.value.goals
        if (!force && (cur.loaded || cur.loading)) return
        _state.update { it.copy(goals = it.goals.copy(loading = true, error = null)) }
        viewModelScope.launch {
            try {
                _state.update { it.copy(goals = goalsFrom(api.savings(accountId, _state.value.selectedPeriod))) }
            } catch (e: Exception) {
                _state.update { it.copy(goals = it.goals.copy(loading = false, error = e.message ?: "Couldn't load your goals.")) }
            }
        }
    }

    private fun goalsFrom(v: SavingsViewDto) =
        GoalsUi(loaded = true, currency = v.currency, saved = v.overview.saved, buckets = v.buckets)

    /** Clear a stale error before opening the "Add to savings" sheet. */
    fun prepareAllocateSaving() = _state.update { it.copy(goals = it.goals.copy(saveError = null)) }

    /** The "Spend from savings" sheet needs the spend-category + fund pickers, which ride the /spending payload. */
    fun prepareSpendFromSavings() {
        _state.update { it.copy(goals = it.goals.copy(saveError = null)) }
        loadSpending(false)
    }

    /** Earmark money into a bucket. The write returns a refreshed Savings view, so Goals + the Saved header
     *  reconcile with no re-fetch; [onDone] fires only on success. */
    fun allocateSaving(bucketId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(goals = it.goals.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.allocateSaving(accountId, AddSavingDepositRequest(bucketId, amount, date, note?.ifBlank { null }))
                _state.update { it.copy(overview = mut.view.overview, goals = goalsFrom(mut.view)) }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(goals = it.goals.copy(saving = false, saveError = e.message ?: "Couldn't add to savings.")) }
            }
        }
    }

    /** Draw a bucket down as a real expense. This both moves savings and posts to Spending, so re-fetch Savings
     *  for Goals/overview and invalidate the Spending cache so it reloads on next view. [onDone] fires on success. */
    fun spendFromSavings(bucketId: String, categoryId: String, fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(goals = it.goals.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                api.spendFromSavings(accountId, SpendFromSavingsRequest(bucketId, categoryId, amount, date, fundId, note?.ifBlank { null }))
                val v = api.savings(accountId)
                _state.update {
                    it.copy(
                        overview = v.overview,
                        goals = goalsFrom(v),
                        // The new expense isn't in our cached Spending list — drop it so the tab re-fetches.
                        spending = it.spending.copy(loaded = false),
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(goals = it.goals.copy(saving = false, saveError = e.message ?: "Couldn't spend from savings.")) }
            }
        }
    }

    /** Prep the "Log installment" sheet: clear a stale error and load the loan-category + fund pickers (/spending). */
    fun prepareLogInstallment() {
        _state.update { it.copy(goals = it.goals.copy(saveError = null)) }
        loadSpending(false)
    }

    /** Log a loan payment: posts the interest/principal rows and, on a payment-driven bucket, moves the debt
     *  balance. Like spendFromSavings this touches both Savings and Spending, so refetch Savings for Goals/overview
     *  and invalidate the Spending cache so the new rows appear on next view. [onDone] fires only on success. */
    fun logInstallment(
        bucketId: String, total: Double, fundId: String, date: String, categoryId: String, note: String?, onDone: () -> Unit,
    ) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(goals = it.goals.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                api.logInstallment(
                    accountId,
                    LogInstallmentRequest(
                        bucketId = bucketId, total = total, fundId = fundId, date = date,
                        principalCategoryId = categoryId, interestCategoryId = categoryId,
                        note = note?.ifBlank { null },
                    ),
                )
                val v = api.savings(accountId, _state.value.selectedPeriod)
                _state.update {
                    it.copy(
                        overview = v.overview,
                        goals = goalsFrom(v),
                        spending = it.spending.copy(loaded = false),   // new expense rows aren't cached — force a re-fetch
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(goals = it.goals.copy(saving = false, saveError = e.message ?: "Couldn't log the installment.")) }
            }
        }
    }

    // --- Savings bucket CRUD (R2's second L row) ---------------------------------------------------------
    // Without these a phone-only user can look at goals but never make one. The pickers the sheet needs (funds)
    // ride the /spending payload, so prepare loads that.

    /** Prep the new/edit-bucket sheet: clear a stale error and make sure the fund picker's options are loaded. */
    fun prepareSavingBucket() {
        _state.update { it.copy(goals = it.goals.copy(saveError = null)) }
        loadSpending(false)
    }

    /** Create a bucket, or reconfigure [bucketId] when it's non-null. ⚠️ The server applies the request as a full
     *  overwrite, so the sheet must send the bucket's whole configuration, not just what the user touched. */
    fun saveSavingBucket(bucketId: String?, req: SaveSavingBucketRequest, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(goals = it.goals.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                if (bucketId == null) api.createSavingBucket(accountId, req)
                else api.editSavingBucket(accountId, bucketId, req)
                refreshGoals(accountId)
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(goals = it.goals.copy(saving = false, saveError = e.message ?: "Couldn't save that goal.")) }
            }
        }
    }

    /** Archive (hide) or restore a bucket — reversible, and the money stays put. */
    fun archiveSavingBucket(bucketId: String, archived: Boolean, onDone: () -> Unit) =
        bucketMutation(onDone, "Couldn't archive that goal.") { acct -> api.archiveSavingBucket(acct, bucketId, archived) }

    /** Delete a bucket. The domain blocks this when there's savings activity to preserve; that 400 carries a
     *  message explaining why, and it's surfaced as-is because "archive it instead" is the useful answer. */
    fun deleteSavingBucket(bucketId: String, onDone: () -> Unit) =
        bucketMutation(onDone, "Couldn't delete that goal.") { acct -> api.deleteSavingBucket(acct, bucketId) }

    private fun bucketMutation(onDone: () -> Unit, fallback: String, call: suspend (String) -> MutationResultDto) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(goals = it.goals.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                call(accountId)
                refreshGoals(accountId)
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(goals = it.goals.copy(saving = false, saveError = e.message ?: fallback)) }
            }
        }
    }

    /** Re-read Savings after a bucket write. Buckets carry earmarks, so the overview's Saved/Free move too, and a
     *  delete can release money the Spending tab's budget bars are drawn against. */
    private suspend fun refreshGoals(accountId: String) {
        val v = api.savings(accountId, _state.value.selectedPeriod)
        _state.update { it.copy(overview = v.overview, goals = goalsFrom(v), spending = it.spending.copy(loaded = false)) }
    }

    /** Lazily load the Wallets tab (funds + transfers) the first time it's shown, or when forced. */
    fun loadWallets(force: Boolean = false) {
        val accountId = _state.value.selectedAccountId ?: return
        val cur = _state.value.wallets
        if (!force && (cur.loaded || cur.loading)) return
        _state.update { it.copy(wallets = it.wallets.copy(loading = true, error = null)) }
        viewModelScope.launch {
            try {
                val v = api.wallets(accountId, _state.value.selectedPeriod)
                _state.update { it.copy(wallets = walletsFrom(v, it.wallets.incomeCategories)) }
            } catch (e: Exception) {
                _state.update { it.copy(wallets = it.wallets.copy(loading = false, error = e.message ?: "Couldn't load your wallets.")) }
            }
        }
    }

    private fun walletsFrom(v: WalletsViewDto, incomeCategories: List<CategoryOptionDto>) = WalletsUi(
        loaded = true, currency = v.currency, current = v.overview.current,
        funds = v.funds, transfers = v.transfers, incomeCategories = incomeCategories,
    )

    /** Clear any stale write error before opening a Wallets action sheet. */
    fun prepareTransfer() = _state.update { it.copy(wallets = it.wallets.copy(saveError = null)) }

    /** Prep the Add-income sheet: clear stale errors and pull the contribution-category picker from /income. */
    fun prepareAddIncome() {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(wallets = it.wallets.copy(saveError = null)) }
        if (_state.value.wallets.incomeCategories.isNotEmpty()) return
        viewModelScope.launch {
            val inc = runCatching { api.income(accountId) }.getOrNull() ?: return@launch
            _state.update { it.copy(wallets = it.wallets.copy(incomeCategories = inc.categories)) }
        }
    }

    /** Move money between two funds (S68 fund-row Transfer). The write returns a refreshed Wallets view, so
     *  balances + this period's transfers reconcile with no re-fetch; [onDone] fires only on success. */
    fun transferFunds(fromFundId: String, toFundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(wallets = it.wallets.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.transferFunds(accountId, TransferFundsRequest(fromFundId, toFundId, amount, date, note?.ifBlank { null }))
                _state.update {
                    it.copy(
                        overview = mut.view.overview,
                        wallets = walletsFrom(mut.view, it.wallets.incomeCategories),
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(wallets = it.wallets.copy(saving = false, saveError = e.message ?: "Couldn't transfer.")) }
            }
        }
    }

    /** Record income into a fund (S68 fund-row Add income). Deposits move balances but the write returns only the
     *  overview, so re-fetch Wallets afterwards to reflect the new fund balance. [onDone] fires only on success. */
    fun addIncome(fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(wallets = it.wallets.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.addDeposit(accountId, AddDepositRequest(categoryId, fundId, amount, date))
                val v = api.wallets(accountId)
                _state.update {
                    it.copy(
                        overview = mut.overview,
                        wallets = walletsFrom(v, it.wallets.incomeCategories),
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(wallets = it.wallets.copy(saving = false, saveError = e.message ?: "Couldn't record income.")) }
            }
        }
    }

    /** Lazily load the Home Health card + Insights modal the first time Home is shown, or when forced. */
    fun loadHealth(force: Boolean = false) {
        val accountId = _state.value.selectedAccountId ?: return
        val cur = _state.value.health
        if (!force && (cur.loaded || cur.loading)) return
        _state.update { it.copy(health = it.health.copy(loading = true, error = null)) }
        viewModelScope.launch {
            try {
                val d = api.insights(accountId)
                val currency = _state.value.selectedAccount?.currency ?: _state.value.overview?.currency ?: ""
                _state.update { it.copy(health = HealthUi(loaded = true, currency = currency, data = d)) }
            } catch (e: Exception) {
                _state.update { it.copy(health = it.health.copy(loading = false, error = e.message ?: "Couldn't load your health score.")) }
            }
        }
    }

    /** Lazily load the Recurring (bills/income) card + sheet when Home is shown, or when forced. */
    fun loadRecurring(force: Boolean = false) {
        val accountId = _state.value.selectedAccountId ?: return
        val cur = _state.value.recurring
        if (!force && (cur.loaded || cur.loading)) return
        _state.update { it.copy(recurring = it.recurring.copy(loading = true, error = null)) }
        viewModelScope.launch {
            try {
                val v = api.recurring(accountId, _state.value.selectedPeriod)
                _state.update { it.copy(recurring = recurringFrom(v)) }
            } catch (e: Exception) {
                _state.update { it.copy(recurring = it.recurring.copy(loading = false, error = e.message ?: "Couldn't load your bills.")) }
            }
        }
    }

    private fun recurringFrom(v: RecurringViewDto) =
        RecurringUi(loaded = true, currency = v.currency, billsDue = v.billsDue, items = v.items)

    /** Confirm a due bill/income at [amount] (posts a real expense/income). Refreshes the recurring list from the
     *  write's view, re-reads the overview (bills-due/spent/contributed move), and invalidates the Spending cache. */
    fun confirmRecurring(recurringId: String, amount: Double) = runRecurringAction(recurringId) { accountId ->
        val mut = api.confirmRecurring(accountId, recurringId, ConfirmRecurringRequest(amount))
        mut.view
    }

    /** Skip a due item for this period (posts nothing, marks handled). Refreshes the list + overview. */
    fun skipRecurring(recurringId: String) = runRecurringAction(recurringId) { accountId ->
        api.skipRecurring(accountId, recurringId).view
    }

    private fun runRecurringAction(recurringId: String, action: suspend (String) -> RecurringViewDto) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(recurring = it.recurring.copy(busyId = recurringId, actionError = null)) }
        viewModelScope.launch {
            try {
                val view = action(accountId)
                val overview = runCatching { api.overview(accountId) }.getOrNull()
                _state.update {
                    it.copy(
                        recurring = recurringFrom(view),
                        overview = overview ?: it.overview,
                        spending = it.spending.copy(loaded = false),   // a posted bill lands in Spending → re-fetch
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(recurring = it.recurring.copy(busyId = null, actionError = e.message ?: "That didn't go through.")) }
            }
        }
    }

    // --- FAB "Edit last": reopen the most recent manual expense in the add sheet, save via PUT ------------

    /** Open the add sheet on the most recent manual (user-entered) expense. Loads Spending first if needed, then
     *  sets [UiState.editingExpense] which the UI watches to show the sheet in edit mode. No-op (leaves editing null)
     *  when there's nothing manual to edit — the UI surfaces that. */
    fun prepareEditLast() {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saveError = null)) }
        viewModelScope.launch {
            if (!_state.value.spending.loaded) {
                runCatching { api.spending(accountId) }.getOrNull()?.let { v ->
                    _state.update {
                        it.copy(spending = it.spending.copy(
                            loading = false, loaded = true, error = null, currency = v.currency,
                            spent = v.overview.spent, expenses = v.expenses, categories = v.categories, funds = v.funds,
                        ))
                    }
                }
                // The add sheet also wants the most-used chips / default funds.
                runCatching { api.expenseEntry(accountId) }.getOrNull()
                    ?.let { entry -> _state.update { it.copy(spending = it.spending.copy(recent = entry.recent)) } }
            }
            val last = _state.value.spending.expenses.firstOrNull { !it.autoFiled && !it.fromSavings }
                ?: _state.value.spending.expenses.firstOrNull()
            _state.update { it.copy(editingExpense = last) }
        }
    }

    fun clearEditing() = _state.update { it.copy(editingExpense = null) }

    /** Load the caller's most recent deposit into the income editor (the income tab's "Edit last"). */
    fun prepareEditLastIncome() {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saveError = null)) }
        viewModelScope.launch {
            val income = runCatching { api.income(accountId) }.getOrNull() ?: return@launch
            val last = income.deposits.maxByOrNull { it.date }
            _state.update { it.copy(editingDeposit = last) }
        }
    }

    fun clearEditingIncome() = _state.update { it.copy(editingDeposit = null) }

    /** Save an edit to an existing deposit; reflects the recomputed overview and invalidates the Wallets cache. */
    fun editDeposit(depositId: String, fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.editDeposit(accountId, depositId, AddDepositRequest(categoryId, fundId, amount, date))
                _state.update {
                    it.copy(
                        overview = mut.overview, editingDeposit = null,
                        spending = it.spending.copy(saving = false),
                        wallets = it.wallets.copy(loaded = false),
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't save the change.")) }
            }
        }
    }

    /** Delete an expense (swipe action). Splices it out of the Spending list and reflects the recomputed overview. */
    fun deleteExpense(expenseId: String, onDone: () -> Unit = {}) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.deleteExpense(accountId, expenseId)
                _state.update { st ->
                    st.copy(
                        overview = mut.overview ?: st.overview,
                        spending = st.spending.copy(
                            saving = false, saveError = null,
                            expenses = st.spending.expenses.filterNot { it.id == expenseId },
                            spent = mut.overview?.spent ?: st.spending.spent,
                        ),
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't delete the expense.")) }
            }
        }
    }

    /** Begin editing a specific expense picked from a list row — raises the shared add sheet in edit mode.
     *  Skips auto-filed / from-savings rows (they aren't hand-editable expenses). */
    fun beginEdit(expense: ExpenseDto) {
        if (expense.autoFiled || expense.fromSavings) return
        _state.update { it.copy(editingExpense = expense) }
    }

    /** Save an edit to an existing expense (the FAB's "Edit last"). Splices the updated row back into the Spending
     *  list and reflects the recomputed overview; [onDone] fires only on success so the sheet can close. */
    fun editExpense(expenseId: String, req: AddExpenseRequest, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.editExpense(accountId, expenseId, req)
                _state.update { st ->
                    val updated = mut.expense
                    val list = if (updated != null) st.spending.expenses.map { if (it.id == expenseId) updated else it }
                               else st.spending.expenses
                    st.copy(
                        overview = mut.overview,
                        editingExpense = null,
                        spending = st.spending.copy(saving = false, saveError = null, expenses = list, spent = mut.overview.spent),
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't save the change.")) }
            }
        }
    }

    // --- Budgets (inline set / remove on the Spending Categories view) ----------------------------------

    /** Upsert a category's budget cap; reconciles the Spending view from the returned budgets snapshot. */
    fun setBudget(categoryId: String, amount: Double, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.setBudget(accountId, categoryId, SetBudgetRequest(amount))
                _state.update { applyBudgetView(it, mut) }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't save the budget.")) }
            }
        }
    }

    /** Remove a category's budget cap; reconciles from the returned budgets snapshot. */
    fun removeBudget(categoryId: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.removeBudget(accountId, categoryId)
                _state.update { applyBudgetView(it, mut) }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't remove the budget.")) }
            }
        }
    }

    private fun applyBudgetView(st: UiState, mut: BudgetMutationDto): UiState = st.copy(
        spending = st.spending.copy(
            saving = false, saveError = null,
            budgets = mut.view.budgets,
            totalBudgeted = mut.view.totalBudgeted,
            totalSpent = mut.view.totalSpent,
        ),
    )

    // --- Category management (add / edit / archive from the Spending "Manage categories" sheet) ---------

    /** Create a category, then re-fetch /spending; passes the new category's id to [onDone] so a picker can select it. */
    fun addCategory(name: String, parentId: String?, icon: String?, onDone: (String?) -> Unit = {}) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                val mut = api.createCategory(accountId, CreateCategoryRequest(name.trim(), parentId, icon?.ifBlank { null }))
                val v = api.spending(accountId)
                _state.update {
                    it.copy(spending = it.spending.copy(
                        saving = false, saveError = null,
                        categories = v.categories, expenses = v.expenses, funds = v.funds,
                    ))
                }
                onDone(mut.entityId)
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't save the category.")) }
            }
        }
    }

    fun editCategory(categoryId: String, name: String, icon: String?, onDone: () -> Unit) =
        categoryMutation(onDone) { acct -> api.editCategory(acct, categoryId, EditCategoryRequest(name.trim(), icon?.ifBlank { null })) }

    fun archiveCategory(categoryId: String, onDone: () -> Unit) =
        categoryMutation(onDone) { acct -> api.archiveCategory(acct, categoryId, true) }

    /** Fire a category mutation, then re-fetch /spending so the refreshed category list (and any spend re-bucketing)
     *  shows immediately — the category endpoints return only a version, not a snapshot. */
    private fun categoryMutation(onDone: () -> Unit, action: suspend (String) -> Any) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(spending = it.spending.copy(saving = true, saveError = null)) }
        viewModelScope.launch {
            try {
                action(accountId)
                val v = api.spending(accountId)
                _state.update {
                    it.copy(spending = it.spending.copy(
                        saving = false, saveError = null,
                        categories = v.categories, expenses = v.expenses, funds = v.funds,
                    ))
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(spending = it.spending.copy(saving = false, saveError = e.message ?: "Couldn't save the category.")) }
            }
        }
    }

    // --- Profile & Account settings ---------------------------------------------------------------------

    /** Clear stale settings state before opening the profile sheet, and refresh identity best-effort. */
    fun openSettings() {
        _state.update { it.copy(settings = SettingsUi(), twoFactor = TwoFactorUi()) }
        viewModelScope.launch {
            runCatching { api.me() }.getOrNull()?.let { me ->
                _state.update { it.copy(
                    username = me.username.ifBlank { it.username }, email = me.email, provider = me.provider,
                    avatar = me.avatar, emailVerified = me.emailVerified, twoFactorEnabled = me.twoFactorEnabled,
                ) }
            }
        }
    }

    // --- Two-factor authentication ----------------------------------------------------------------------

    /** Begin enrollment: fetch the secret + QR and open the confirm panel. */
    fun beginTwoFactor() {
        _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = true, error = null, recoveryCodes = null)) }
        viewModelScope.launch {
            try {
                val setup = api.setupTwoFactor()
                _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = false, setup = setup)) }
            } catch (e: Exception) {
                _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = false, error = e.message ?: "Couldn't start two-factor setup.")) }
            }
        }
    }

    /** Confirm enrollment with a live code; on success show the one-time recovery codes and mark 2FA on. */
    fun confirmTwoFactor(code: String) {
        _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                val codes = api.confirmTwoFactor(code)
                _state.update { it.copy(twoFactorEnabled = true, twoFactor = it.twoFactor.copy(busy = false, setup = null, recoveryCodes = codes.recoveryCodes)) }
            } catch (e: Exception) {
                _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = false, error = e.message ?: "That code didn't match. Try again.")) }
            }
        }
    }

    /** Turn 2FA off with a current code. */
    fun disableTwoFactor(code: String) {
        _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.disableTwoFactor(code)
                _state.update { it.copy(twoFactorEnabled = false, twoFactor = TwoFactorUi()) }
            } catch (e: Exception) {
                _state.update { it.copy(twoFactor = it.twoFactor.copy(busy = false, error = e.message ?: "That code didn't match. Try again.")) }
            }
        }
    }

    /** Open/close the "enter a code to turn off" panel; clear enrollment/recovery state. */
    fun setTwoFactorDisabling(on: Boolean) = _state.update { it.copy(twoFactor = it.twoFactor.copy(disabling = on, error = null)) }
    fun cancelTwoFactorSetup() = _state.update { it.copy(twoFactor = TwoFactorUi()) }
    fun dismissRecoveryCodes() = _state.update { it.copy(twoFactor = it.twoFactor.copy(recoveryCodes = null)) }

    // --- Email verification + avatar --------------------------------------------------------------------

    /** Resend the email-verification link (local accounts). */
    fun resendVerification() {
        _state.update { it.copy(settings = it.settings.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.resendVerification()
                _state.update { it.copy(settings = it.settings.copy(busy = false)) }
            } catch (e: Exception) {
                _state.update { it.copy(settings = it.settings.copy(busy = false, error = e.message ?: "Couldn't send the email.")) }
            }
        }
    }

    /** Upload a new avatar (data URL from the image picker), reflecting it locally on success. */
    fun uploadAvatar(dataUrl: String) {
        _state.update { it.copy(settings = it.settings.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.setAvatar(dataUrl)
                _state.update { it.copy(avatar = dataUrl, settings = it.settings.copy(busy = false)) }
            } catch (e: Exception) {
                _state.update { it.copy(settings = it.settings.copy(busy = false, error = e.message ?: "Couldn't update your photo.")) }
            }
        }
    }

    fun changePassword(current: String, new: String) {
        if (current.isBlank() || new.length < 8) {
            _state.update { it.copy(settings = it.settings.copy(error = "Enter your current password and a new one (8+ characters).")) }
            return
        }
        _state.update { it.copy(settings = it.settings.copy(busy = true, error = null, passwordChanged = false)) }
        viewModelScope.launch {
            try {
                api.changePassword(ChangePasswordRequest(current, new))
                _state.update { it.copy(settings = it.settings.copy(busy = false, passwordChanged = true)) }
            } catch (e: Exception) {
                _state.update { it.copy(settings = it.settings.copy(busy = false, error = e.message ?: "Couldn't change your password.")) }
            }
        }
    }

    /** Rename the selected account. Updates the local accounts list on success so the header reflects it at once. */
    fun renameAccount(name: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        val trimmed = name.trim()
        if (trimmed.isBlank()) {
            _state.update { it.copy(settings = it.settings.copy(error = "Enter a name for the account.")) }
            return
        }
        _state.update { it.copy(settings = it.settings.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.renameAccount(accountId, trimmed)
                _state.update { st ->
                    st.copy(
                        settings = st.settings.copy(busy = false),
                        accounts = st.accounts.map { if (it.id == accountId) it.copy(name = trimmed) else it },
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(settings = it.settings.copy(busy = false, error = e.message ?: "Couldn't rename the account.")) }
            }
        }
    }

    /** Leave a shared account or delete it (owner). Both drop the account, so reload Home afterwards — which
     *  re-selects another account, or lands on the empty state if it was the last one. [onDone] fires on success.
     *
     *  [newOwnerUserId] is required when the OWNER leaves an account someone else is still on: the server refuses
     *  to orphan an account, so the picker in the confirm block is not a courtesy, it's the request being valid.
     *  A sole member passes null and the server archives the account instead. */
    fun leaveAccount(newOwnerUserId: String? = null, onDone: () -> Unit) =
        accountRemoval(onDone) { api.leaveAccount(it, newOwnerUserId) }

    fun deleteAccount(onDone: () -> Unit) = accountRemoval(onDone) { api.deleteAccount(it) }

    private fun accountRemoval(onDone: () -> Unit, action: suspend (String) -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(settings = it.settings.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                action(accountId)
                _state.update {
                    it.copy(
                        settings = it.settings.copy(busy = false),
                        selectedAccountId = null, overview = null, periodLabel = null, runway = null, targets = emptyList(),
                        spending = SpendingUi(), goals = GoalsUi(), wallets = WalletsUi(), health = HealthUi(), recurring = RecurringUi(),
                    )
                }
                onDone()
                loadHome()
            } catch (e: Exception) {
                _state.update { it.copy(settings = it.settings.copy(busy = false, error = e.message ?: "That didn't work.")) }
            }
        }
    }

    // --- Sharing: invite, join, and who's on the account -------------------------------------------------
    // The last R2 gap that changed what the product IS on a phone: sharing is the feature Pro is sold on, so
    // while it was missing a phone-only user couldn't reach the thing the paywall charges for. Two halves that
    // look alike and aren't: the INVITER's half is account-scoped and Pro-gated, the INVITEE's half is neither —
    // an invitation arrives before there is any membership to hang it off, and may land on a user with no
    // account at all.

    /** Best-effort: the invitations waiting on this user. Failure leaves the card hidden rather than erroring —
     *  nobody is blocked by not being told about an invite, and Home has better things to say. */
    private suspend fun loadInvitations() {
        val pending = runCatching { api.pendingInvitations() }.getOrNull() ?: return
        _state.update { it.copy(sharing = it.sharing.copy(invitations = pending)) }
    }

    /** Best-effort: the account's member profile pictures, by user id. Empty is a fine answer (initials). */
    private suspend fun loadMemberAvatars(accountId: String) {
        val avatars = api.memberAvatars(accountId)
        // Guard against a slow response for an account the user has since switched away from.
        if (_state.value.selectedAccountId != accountId) return
        _state.update { it.copy(sharing = it.sharing.copy(avatars = avatars)) }
    }

    /** Clear the "invitation sent" note + any stale error as soon as the username field is edited again. */
    fun clearInviteResult() = _state.update { it.copy(sharing = it.sharing.copy(invited = null, error = null)) }

    /**
     * Invite someone to the open account by username. Deliberately NOT gated client-side on [UiState.plan]: the
     * crown is decoration, the server's 402 is the gate, and letting the two disagree is how a paying user ends
     * up locked out by a stale plan string. Every failure here already carries a message worth reading verbatim
     * (no such user / already a contributor / already invited / "That's a Pro feature").
     */
    fun invite(username: String) {
        val accountId = _state.value.selectedAccountId ?: return
        val target = username.trim()
        if (target.isBlank()) {
            _state.update { it.copy(sharing = it.sharing.copy(error = "Enter the username of the person to invite.")) }
            return
        }
        _state.update { it.copy(sharing = it.sharing.copy(busy = true, error = null, invited = null)) }
        viewModelScope.launch {
            try {
                api.invite(accountId, target)
                _state.update { it.copy(sharing = it.sharing.copy(busy = false, invited = target)) }
            } catch (e: Exception) {
                _state.update { it.copy(sharing = it.sharing.copy(busy = false, error = e.message ?: "Couldn't send that invitation.")) }
            }
        }
    }

    /** Accept an invitation and open the account it was for — landing on someone else's budget without being
     *  taken to it would leave the user to work out what just happened from an unchanged screen. */
    fun acceptInvitation(invitationId: String) {
        _state.update { it.copy(sharing = it.sharing.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                val accountId = api.acceptInvitation(invitationId)
                val accounts = api.listAccounts()
                _state.update { st ->
                    st.copy(
                        accounts = accounts,
                        sharing = st.sharing.copy(busy = false, invitations = st.sharing.invitations.filterNot { it.id == invitationId }),
                    )
                }
                // Reuse the normal switch so every tab, the period label and the forecast reload for the new
                // account exactly as they would from the account picker.
                if (accountId.isNotBlank() && accounts.any { it.id == accountId }) selectAccount(accountId) else loadHome()
            } catch (e: Exception) {
                _state.update { it.copy(sharing = it.sharing.copy(busy = false, error = e.message ?: "Couldn't accept that invitation.")) }
            }
        }
    }

    /** Decline an invitation. The sender isn't notified; it simply stops being pending. */
    fun declineInvitation(invitationId: String) {
        _state.update { it.copy(sharing = it.sharing.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.declineInvitation(invitationId)
                _state.update { st ->
                    st.copy(sharing = st.sharing.copy(busy = false, invitations = st.sharing.invitations.filterNot { it.id == invitationId }))
                }
            } catch (e: Exception) {
                _state.update { it.copy(sharing = it.sharing.copy(busy = false, error = e.message ?: "Couldn't decline that invitation.")) }
            }
        }
    }

    /** Owner removes a member. Their recorded contributions and expenses stay — only their access goes. */
    fun removeMember(memberUserId: String, onDone: () -> Unit) = membershipWrite(onDone) { accountId ->
        api.removeMember(accountId, memberUserId)
    }

    /** Owner hands the account to another member and stays on as an ordinary contributor. */
    fun transferOwnership(newOwnerUserId: String, onDone: () -> Unit) = membershipWrite(onDone) { accountId ->
        api.transferOwnership(accountId, newOwnerUserId)
    }

    /** Shared plumbing for the two owner-only membership writes: both change the account summary (its member
     *  list or its owner), so both re-read /accounts rather than patching a guess into local state. */
    private fun membershipWrite(onDone: () -> Unit, action: suspend (String) -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(sharing = it.sharing.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                action(accountId)
                val accounts = runCatching { api.listAccounts() }.getOrNull()
                _state.update { st ->
                    st.copy(sharing = st.sharing.copy(busy = false), accounts = accounts ?: st.accounts)
                }
                loadMemberAvatars(accountId)
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(sharing = it.sharing.copy(busy = false, error = e.message ?: "That didn't work.")) }
            }
        }
    }

    // --- Bank sync (External accounts) ------------------------------------------------------------------

    private fun bankFrom(s: BankSyncStatusDto, pending: List<PendingBankTransactionDto>, loaded: Boolean = true) = BankUi(
        loaded = loaded, enabled = s.enabled, connected = s.connected,
        institutionName = s.institutionName, institutionLogo = s.institutionLogo,
        balance = s.balance, balanceCurrency = s.balanceCurrency, lastSyncedAt = s.lastSyncedAt,
        pending = pending,
    )

    /** Lazily load the Bank sheet: the connection status (gates the whole feature) and, when connected, the
     *  pending imports. Also warms the /spending pickers the confirm flow reuses. Best-effort — a failed status
     *  read leaves the feature hidden (enabled=false). */
    fun loadBank(force: Boolean = false) {
        val accountId = _state.value.selectedAccountId ?: return
        val cur = _state.value.bank
        if (!force && (cur.loaded || cur.loading)) return
        _state.update { it.copy(bank = it.bank.copy(loading = true, error = null)) }
        loadSpending(false)
        viewModelScope.launch {
            val status = runCatching { api.bankStatus(accountId) }.getOrDefault(BankSyncStatusDto(enabled = false))
            val pending = if (status.enabled && status.connected)
                runCatching { api.bankPending(accountId) }.getOrDefault(emptyList()) else emptyList()
            // The income picker is needed to file credits (money-in) as income.
            if (_state.value.spending.incomeCategories.isEmpty()) {
                runCatching { api.income(accountId) }.getOrNull()
                    ?.let { inc -> _state.update { it.copy(spending = it.spending.copy(incomeCategories = inc.categories)) } }
            }
            _state.update { it.copy(bank = bankFrom(status, pending)) }
        }
    }

    /** Begin linking: record bank-link consent, auto-pick the account's Revolut institution, ask the server for
     *  the consent URL (native → the callback deep-links back), and stash it for the UI to open in a browser. */
    fun connectBank() {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(bank = it.bank.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.recordConsent(RecordConsentRequest("bank_link", accountId, true))
                val bank = api.bankInstitutions(accountId).firstOrNull()
                    ?: throw com.tandemtab.app.data.ApiException(404, "No Revolut connection is available for your country yet.")
                val resp = api.startBankLink(accountId, StartBankLinkRequest(bank.name, bank.country, bank.logo, native = true))
                _state.update { it.copy(bank = it.bank.copy(busy = false, linkUrl = resp.linkUrl)) }
            } catch (e: Exception) {
                _state.update { it.copy(bank = it.bank.copy(busy = false, error = e.message ?: "Couldn't start the bank link.")) }
            }
        }
    }

    /** The UI consumes the one-shot link URL (opens it in a browser), then clears it. */
    fun clearBankLinkUrl() = _state.update { it.copy(bank = it.bank.copy(linkUrl = null)) }

    /** The com.tandemtab.app://bank/callback deep link fired after the user consented at their bank. On success
     *  refresh the connection, run a first sync, and load the pending imports; on error surface it. */
    fun onBankDeepLink(linked: Boolean) {
        val accountId = _state.value.selectedAccountId ?: return
        if (!linked) {
            _state.update { it.copy(bank = it.bank.copy(busy = false, error = "The bank link was cancelled or failed.")) }
            return
        }
        _state.update { it.copy(bank = it.bank.copy(busy = true, error = null)) }
        viewModelScope.launch {
            runCatching { api.syncBank(accountId) }
            val status = runCatching { api.bankStatus(accountId) }.getOrDefault(BankSyncStatusDto(enabled = false))
            val pending = runCatching { api.bankPending(accountId) }.getOrDefault(emptyList())
            _state.update { it.copy(bank = bankFrom(status, pending).copy(busy = false)) }
        }
    }

    /** Pull new transactions from the bank and refresh the pending list. */
    fun syncBank() {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(bank = it.bank.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.syncBank(accountId)
                val status = runCatching { api.bankStatus(accountId) }.getOrNull()
                val pending = api.bankPending(accountId)
                _state.update {
                    it.copy(bank = it.bank.copy(
                        busy = false, pending = pending,
                        lastSyncedAt = status?.lastSyncedAt ?: it.bank.lastSyncedAt,
                        balance = status?.balance ?: it.bank.balance,
                    ))
                }
            } catch (e: Exception) {
                _state.update { it.copy(bank = it.bank.copy(busy = false, error = e.message ?: "Couldn't sync your bank.")) }
            }
        }
    }

    /** Turn a pending debit into a real expense (category + fund), then ack it so a later sync won't resurface it. */
    fun confirmPendingExpense(externalId: String, categoryId: String, fundId: String, amount: Double, date: String, note: String?, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(bank = it.bank.copy(handlingId = externalId, error = null)) }
        viewModelScope.launch {
            try {
                val mut = api.addExpense(accountId, AddExpenseRequest(categoryId, kotlin.math.abs(amount), fundId, date, note?.ifBlank { null }))
                api.ackBank(accountId, externalId, confirmed = true)
                _state.update {
                    it.copy(
                        overview = mut.overview,
                        bank = it.bank.copy(handlingId = null, pending = it.bank.pending.filterNot { p -> p.externalId == externalId }),
                        spending = it.spending.copy(loaded = false),   // a new expense landed → re-fetch Spending
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(bank = it.bank.copy(handlingId = null, error = e.message ?: "Couldn't file that transaction.")) }
            }
        }
    }

    /** Turn a pending credit into income (source category + fund), then ack it. */
    fun confirmPendingIncome(externalId: String, categoryId: String, fundId: String, amount: Double, date: String, onDone: () -> Unit) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(bank = it.bank.copy(handlingId = externalId, error = null)) }
        viewModelScope.launch {
            try {
                val mut = api.addDeposit(accountId, AddDepositRequest(categoryId, fundId, kotlin.math.abs(amount), date))
                api.ackBank(accountId, externalId, confirmed = true)
                _state.update {
                    it.copy(
                        overview = mut.overview,
                        bank = it.bank.copy(handlingId = null, pending = it.bank.pending.filterNot { p -> p.externalId == externalId }),
                        wallets = it.wallets.copy(loaded = false),   // fund balances moved → re-fetch Wallets
                    )
                }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(bank = it.bank.copy(handlingId = null, error = e.message ?: "Couldn't file that transaction.")) }
            }
        }
    }

    /** Dismiss a pending transaction (posts nothing, marks it handled so a later sync won't resurface it). */
    fun dismissPending(externalId: String) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(bank = it.bank.copy(handlingId = externalId, error = null)) }
        viewModelScope.launch {
            try {
                api.ackBank(accountId, externalId, confirmed = false)
                _state.update { it.copy(bank = it.bank.copy(handlingId = null, pending = it.bank.pending.filterNot { p -> p.externalId == externalId })) }
            } catch (e: Exception) {
                _state.update { it.copy(bank = it.bank.copy(handlingId = null, error = e.message ?: "Couldn't dismiss that transaction.")) }
            }
        }
    }

    /** Drop the bank connection (keeps already-handled rows). Resets the sheet to the not-connected state. */
    fun disconnectBank(onDone: () -> Unit = {}) {
        val accountId = _state.value.selectedAccountId ?: return
        _state.update { it.copy(bank = it.bank.copy(busy = true, error = null)) }
        viewModelScope.launch {
            try {
                api.disconnectBank(accountId)
                _state.update { it.copy(bank = it.bank.copy(busy = false, connected = false, institutionName = null, institutionLogo = null, balance = null, lastSyncedAt = null, pending = emptyList())) }
                onDone()
            } catch (e: Exception) {
                _state.update { it.copy(bank = it.bank.copy(busy = false, error = e.message ?: "Couldn't disconnect.")) }
            }
        }
    }

    fun clearBankError() = _state.update { it.copy(bank = it.bank.copy(error = null)) }

    fun refresh() {
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch { loadHome() }
    }

    fun signOut() {
        val google = _state.value.googleEnabled
        _state.value = UiState(screen = Screen.Login, googleEnabled = google)
        viewModelScope.launch { api.signOut() }
    }

    fun clearError() = _state.update { it.copy(error = null) }
}
