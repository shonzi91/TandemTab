package com.tandemtab.app

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import com.tandemtab.app.ui.HomeScreen
import com.tandemtab.app.ui.LogoLoader
import com.tandemtab.app.ui.LoginScreen
import com.tandemtab.app.ui.TwoFactorScreen
import com.tandemtab.app.ui.theme.TandemTabTheme

class MainActivity : ComponentActivity() {
    // Activity-scoped so the deep-link handler and the Composable share one instance.
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        handleAuthDeepLink(intent)
        setContent {
            val dark by vm.darkTheme.collectAsState()
            TandemTabTheme(darkTheme = dark) {
                Surface(modifier = Modifier.fillMaxSize()) {
                    App(vm = vm, onGoogle = ::startGoogleSignIn)
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleAuthDeepLink(intent)
    }

    /** com.tandemtab.app://{auth|bank}/callback deep links: external sign-in result, or the bank-consent outcome. */
    private fun handleAuthDeepLink(intent: Intent?) {
        val data: Uri = intent?.data ?: return
        if (data.scheme != "com.tandemtab.app") return
        when (data.host) {
            "bank" -> vm.onBankDeepLink(linked = data.getQueryParameter("bank") == "linked")
            else -> vm.onExternalAuthResult(
                authCode = data.getQueryParameter("authCode"),
                error = data.getQueryParameter("error") != null,
            )
        }
    }

    private fun startGoogleSignIn() {
        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(vm.googleAuthUrl())))
    }
}

@Composable
private fun App(vm: AppViewModel, onGoogle: () -> Unit) {
    val state by vm.state.collectAsState()
    val dark by vm.darkTheme.collectAsState()
    when (state.screen) {
        Screen.Splash -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            LogoLoader()
        }
        Screen.Login -> LoginScreen(
            busy = state.busy,
            error = state.error,
            resetLinkSent = state.resetLinkSent,
            googleEnabled = state.googleEnabled,
            onSignIn = vm::login,
            onRegister = vm::register,
            onSendResetLink = vm::sendResetLink,
            onClearResetSent = vm::clearResetLinkSent,
            onGoogle = onGoogle,
        )
        Screen.TwoFactor -> TwoFactorScreen(
            busy = state.busy,
            error = state.error,
            onSubmit = vm::submitTwoFactor,
            onCancel = vm::cancelTwoFactor,
        )
        Screen.Home -> HomeScreen(
            state = state,
            darkTheme = dark,
            onToggleTheme = vm::toggleTheme,
            onSelectAccount = vm::selectAccount,
            onSelectPeriod = vm::selectPeriod,
            onPrepareStartNextPeriod = vm::prepareStartNextPeriod,
            onPreparePeriodEdit = vm::preparePeriodEdit,
            onStartNextPeriod = vm::startNextPeriod,
            onReschedulePeriod = vm::reschedulePeriod,
            onRemoveLatestPeriod = vm::removeLatestPeriod,
            onSignOut = vm::signOut,
            onOpenSettings = vm::openSettings,
            onChangePassword = vm::changePassword,
            onResendVerification = vm::resendVerification,
            onUploadAvatar = vm::uploadAvatar,
            onBeginTwoFactor = vm::beginTwoFactor,
            onConfirmTwoFactor = vm::confirmTwoFactor,
            onDisableTwoFactor = vm::disableTwoFactor,
            onSetTwoFactorDisabling = vm::setTwoFactorDisabling,
            onCancelTwoFactorSetup = vm::cancelTwoFactorSetup,
            onDismissRecoveryCodes = vm::dismissRecoveryCodes,
            onRenameAccount = vm::renameAccount,
            onLeaveAccount = vm::leaveAccount,
            onDeleteAccount = vm::deleteAccount,
            onInvite = vm::invite,
            onClearInviteResult = vm::clearInviteResult,
            onRemoveMember = vm::removeMember,
            onTransferOwnership = vm::transferOwnership,
            onAcceptInvitation = vm::acceptInvitation,
            onDeclineInvitation = vm::declineInvitation,
            onLoadSpending = vm::loadSpending,
            onLoadGoals = vm::loadGoals,
            onLoadWallets = vm::loadWallets,
            onLoadBank = vm::loadBank,
            onConnectBank = vm::connectBank,
            onSyncBank = vm::syncBank,
            onDisconnectBank = { vm.disconnectBank() },
            onConfirmBankExpense = vm::confirmPendingExpense,
            onConfirmBankIncome = vm::confirmPendingIncome,
            onDismissBankPending = vm::dismissPending,
            onBankLinkUrlHandled = vm::clearBankLinkUrl,
            onLoadHealth = vm::loadHealth,
            onLoadRecurring = vm::loadRecurring,
            onConfirmRecurring = vm::confirmRecurring,
            onSkipRecurring = vm::skipRecurring,
            onPrepareAdd = vm::prepareAdd,
            onPrepareEditLast = vm::prepareEditLast,
            onPrepareEditLastIncome = vm::prepareEditLastIncome,
            onEditDeposit = vm::editDeposit,
            onClearEditingIncome = vm::clearEditingIncome,
            onBeginEditExpense = vm::beginEdit,
            onDeleteExpense = { vm.deleteExpense(it.id) },
            onSetBudget = vm::setBudget,
            onRemoveBudget = vm::removeBudget,
            onAddCategory = vm::addCategory,
            onEditCategory = vm::editCategory,
            onArchiveCategory = vm::archiveCategory,
            onClearEditing = vm::clearEditing,
            onAddExpenses = vm::addExpenses,
            onEditExpense = vm::editExpense,
            onAddIncomeQuick = vm::addIncomeFromAdd,
            onPrepareTransfer = vm::prepareTransfer,
            onPrepareAddIncome = vm::prepareAddIncome,
            onTransfer = vm::transferFunds,
            onAddIncome = vm::addIncome,
            onCreateAccount = vm::createAccount,
            onPrepareAllocate = vm::prepareAllocateSaving,
            onPrepareSpend = vm::prepareSpendFromSavings,
            onAllocate = vm::allocateSaving,
            onSpendFromSavings = vm::spendFromSavings,
            onPrepareInstallment = vm::prepareLogInstallment,
            onLogInstallment = vm::logInstallment,
            onPrepareBucket = vm::prepareSavingBucket,
            onSaveBucket = vm::saveSavingBucket,
            onArchiveBucket = vm::archiveSavingBucket,
            onDeleteBucket = vm::deleteSavingBucket,
        )
    }
}
