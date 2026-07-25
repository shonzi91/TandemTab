package com.tandemtab.app

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.tandemtab.app.data.AccountOverviewDto
import com.tandemtab.app.data.AccountSummaryDto
import com.tandemtab.app.data.TandemTabApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Top-level screens for the first vertical slice. */
sealed interface Screen {
    data object Login : Screen
    data object Home : Screen
}

data class UiState(
    val screen: Screen = Screen.Login,
    val busy: Boolean = false,
    val error: String? = null,
    val resetLinkSent: Boolean = false,
    val googleEnabled: Boolean = false,
    val username: String = "",
    // Home data
    val accounts: List<AccountSummaryDto> = emptyList(),
    val selectedAccountId: String? = null,
    val overview: AccountOverviewDto? = null,
) {
    val selectedAccount: AccountSummaryDto?
        get() = accounts.firstOrNull { it.id == selectedAccountId }
}

class AppViewModel(
    private val api: TandemTabApi = TandemTabApi(),
) : ViewModel() {

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    init {
        // Discover which external providers to show (best-effort — button stays hidden if unreachable).
        viewModelScope.launch {
            val providers = runCatching { api.getProviders() }.getOrNull() ?: return@launch
            _state.update { it.copy(googleEnabled = providers.google) }
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
                val auth = api.exchangeCode(authCode)
                _state.update { it.copy(username = auth.username) }
                loadHome()
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Sign-in didn't complete.") }
            }
        }
    }

    fun login(usernameOrEmail: String, password: String) {
        if (usernameOrEmail.isBlank() || password.isBlank()) {
            _state.update { it.copy(error = "Enter your username/email and password.") }
            return
        }
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val result = api.login(usernameOrEmail, password)
                when {
                    result.twoFactorRequired -> _state.update {
                        it.copy(busy = false, error = "This account has two-factor sign-in, which the app doesn't support yet.")
                    }
                    result.auth != null -> {
                        _state.update { it.copy(username = result.auth.username) }
                        loadHome()
                    }
                    else -> _state.update { it.copy(busy = false, error = "Unexpected sign-in response.") }
                }
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
        _state.update { it.copy(busy = true, selectedAccountId = accountId, overview = null) }
        viewModelScope.launch {
            try {
                val overview = api.overview(accountId)
                _state.update { it.copy(busy = false, overview = overview) }
            } catch (e: Exception) {
                _state.update { it.copy(busy = false, error = e.message ?: "Couldn't load that account.") }
            }
        }
    }

    fun refresh() {
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch { loadHome() }
    }

    fun signOut() {
        api.signOut()
        _state.value = UiState()
    }

    fun clearError() = _state.update { it.copy(error = null) }
}
