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
    // External sign-in provider ("google"/"facebook") for the current user, or null for a local password account.
    val provider: String? = null,
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
    // Home forecast (server-computed): the runway card + the "on track for" targets.
    val runway: RunwayDto? = null,
    val targets: List<TargetDto> = emptyList(),
    val spending: SpendingUi = SpendingUi(),
    val goals: GoalsUi = GoalsUi(),
    val wallets: WalletsUi = WalletsUi(),
    val health: HealthUi = HealthUi(),
    val recurring: RecurringUi = RecurringUi(),
    val settings: SettingsUi = SettingsUi(),
    // The expense the FAB's "Edit last" is currently editing (null = the add sheet is in add mode / closed).
    val editingExpense: ExpenseDto? = null,
    // The deposit the income tab's "Edit last" is currently editing.
    val editingDeposit: DepositRowDto? = null,
) {
    val selectedAccount: AccountSummaryDto?
        get() = accounts.firstOrNull { it.id == selectedAccountId }
}

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

/** State for the Profile & Account settings sheet (change-password / rename write-flight + result). */
data class SettingsUi(
    val busy: Boolean = false,
    val error: String? = null,
    val passwordChanged: Boolean = false,
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
            selected?.let { loadPeriodLabel(it.id); loadForecast(it.id) }
            // Identity for the profile sheet (best-effort — the sheet still works from the stored username).
            runCatching { api.me() }.getOrNull()?.let { me ->
                _state.update { it.copy(username = me.username.ifBlank { it.username }, email = me.email, provider = me.provider) }
            }
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

    fun selectAccount(accountId: String) {
        if (accountId == _state.value.selectedAccountId) return
        _state.update {
            it.copy(
                busy = true, selectedAccountId = accountId, overview = null, periodLabel = null,
                runway = null, targets = emptyList(),
                spending = SpendingUi(), goals = GoalsUi(), wallets = WalletsUi(), health = HealthUi(), recurring = RecurringUi(),
            )
        }
        viewModelScope.launch {
            try {
                val overview = api.overview(accountId)
                _state.update { it.copy(busy = false, overview = overview) }
                loadPeriodLabel(accountId)
                loadForecast(accountId)
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
        _state.update { it.copy(settings = SettingsUi()) }
        viewModelScope.launch {
            runCatching { api.me() }.getOrNull()?.let { me ->
                _state.update { it.copy(username = me.username.ifBlank { it.username }, email = me.email, provider = me.provider) }
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

    /** Leave a shared account (non-owner) or delete it (owner). Both drop the account, so reload Home afterwards —
     *  which re-selects another account, or lands on the empty state if it was the last one. [onDone] fires on success. */
    fun leaveAccount(onDone: () -> Unit) = accountRemoval(onDone) { api.leaveAccount(it) }
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
