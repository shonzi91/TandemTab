package com.tandemtab.app

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.tandemtab.app.data.AccountOverviewDto
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.AddDepositRequest
import com.tandemtab.app.data.AddExpenseRequest
import com.tandemtab.app.data.AddSavingDepositRequest
import com.tandemtab.app.data.BudgetRowDto
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.FundOptionDto
import com.tandemtab.app.data.FundRowDto
import com.tandemtab.app.data.FundTransferRowDto
import com.tandemtab.app.data.ConfirmRecurringRequest
import com.tandemtab.app.data.InsightsDto
import com.tandemtab.app.data.RecurringRowDto
import com.tandemtab.app.data.RecurringViewDto
import com.tandemtab.app.data.SavingsViewDto
import com.tandemtab.app.data.SpendFromSavingsRequest
import com.tandemtab.app.data.TransferFundsRequest
import com.tandemtab.app.data.WalletsViewDto
import com.tandemtab.app.data.RecentExpenseDto
import com.tandemtab.app.data.SavingBucketDto
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
    // Outstanding 2FA challenge ticket (from login/exchange), consumed on the TwoFactor screen.
    val twoFactorTicket: String? = null,
    // Home data
    val accounts: List<AccountSummaryDto> = emptyList(),
    val selectedAccountId: String? = null,
    val overview: AccountOverviewDto? = null,
    val spending: SpendingUi = SpendingUi(),
    val goals: GoalsUi = GoalsUi(),
    val wallets: WalletsUi = WalletsUi(),
    val health: HealthUi = HealthUi(),
    val recurring: RecurringUi = RecurringUi(),
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
            val overview = selected?.let { api.overview(it.id) }
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
        } catch (e: Exception) {
            _state.update { it.copy(busy = false, error = e.message ?: "Couldn't load your accounts.") }
        }
    }

    fun selectAccount(accountId: String) {
        if (accountId == _state.value.selectedAccountId) return
        _state.update {
            it.copy(
                busy = true, selectedAccountId = accountId, overview = null,
                spending = SpendingUi(), goals = GoalsUi(), wallets = WalletsUi(), health = HealthUi(), recurring = RecurringUi(),
            )
        }
        viewModelScope.launch {
            try {
                val overview = api.overview(accountId)
                _state.update { it.copy(busy = false, overview = overview) }
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
        viewModelScope.launch {
            try {
                val v = api.spending(accountId)
                _state.update {
                    it.copy(spending = it.spending.copy(
                        loading = false, loaded = true, error = null,
                        currency = v.currency, spent = v.overview.spent, expenses = v.expenses,
                        categories = v.categories, funds = v.funds,
                    ))
                }
                // Budget coverage rides alongside for the Categories view (best-effort — don't fail the tab on it).
                runCatching { api.budgets(accountId) }.getOrNull()?.let { b ->
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
                _state.update { it.copy(goals = goalsFrom(api.savings(accountId))) }
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
                val v = api.wallets(accountId)
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
                val v = api.recurring(accountId)
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
