package com.tandemtab.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.lifecycle.viewmodel.compose.viewModel
import com.tandemtab.app.ui.HomeScreen
import com.tandemtab.app.ui.LoginScreen
import com.tandemtab.app.ui.theme.TandemTabTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            TandemTabTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    App()
                }
            }
        }
    }
}

@Composable
private fun App(vm: AppViewModel = viewModel()) {
    val state by vm.state.collectAsState()
    when (state.screen) {
        Screen.Login -> LoginScreen(
            busy = state.busy,
            error = state.error,
            resetLinkSent = state.resetLinkSent,
            onSignIn = vm::login,
            onRegister = vm::register,
            onSendResetLink = vm::sendResetLink,
            onClearResetSent = vm::clearResetLinkSent,
        )
        Screen.Home -> HomeScreen(
            state = state,
            onSelectAccount = vm::selectAccount,
            onSignOut = vm::signOut,
        )
    }
}
