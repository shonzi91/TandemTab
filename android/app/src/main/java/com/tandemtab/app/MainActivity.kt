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
import com.tandemtab.app.ui.LoginScreen
import com.tandemtab.app.ui.theme.TandemTabTheme

class MainActivity : ComponentActivity() {
    // Activity-scoped so the deep-link handler and the Composable share one instance.
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        handleAuthDeepLink(intent)
        setContent {
            TandemTabTheme {
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

    /** com.tandemtab.app://auth/callback?authCode=… (or ?error=…) after external sign-in. */
    private fun handleAuthDeepLink(intent: Intent?) {
        val data: Uri = intent?.data ?: return
        if (data.scheme != "com.tandemtab.app") return
        vm.onExternalAuthResult(
            authCode = data.getQueryParameter("authCode"),
            error = data.getQueryParameter("error") != null,
        )
    }

    private fun startGoogleSignIn() {
        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(vm.googleAuthUrl())))
    }
}

@Composable
private fun App(vm: AppViewModel, onGoogle: () -> Unit) {
    val state by vm.state.collectAsState()
    when (state.screen) {
        Screen.Splash -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
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
        Screen.Home -> HomeScreen(
            state = state,
            onSelectAccount = vm::selectAccount,
            onSignOut = vm::signOut,
            onLoadSpending = vm::loadSpending,
        )
    }
}
