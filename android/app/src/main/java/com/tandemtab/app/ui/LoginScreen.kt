package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.ui.theme.BrandGreen
import com.tandemtab.app.ui.theme.LocalTandemColors

private enum class AuthMode { Login, Register, Forgot }

@Composable
fun LoginScreen(
    busy: Boolean,
    error: String?,
    resetLinkSent: Boolean,
    onSignIn: (String, String) -> Unit,
    onRegister: (String, String, String) -> Unit,
    onSendResetLink: (String) -> Unit,
    onClearResetSent: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var mode by rememberSaveable { mutableStateOf(AuthMode.Login) }
    var identifier by rememberSaveable { mutableStateOf("") }
    var username by rememberSaveable { mutableStateOf("") }
    var email by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(tandem.canvas)
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(horizontal = 20.dp, vertical = 24.dp),
        contentAlignment = Alignment.TopCenter,
    ) {
        Column(
            modifier = Modifier
                .widthIn(max = 400.dp)
                .fillMaxWidth()
                .padding(top = 24.dp)
                .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(18.dp))
                .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(18.dp))
                .padding(horizontal = 24.dp, vertical = 28.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            BrandHeader()

            if (mode != AuthMode.Forgot) {
                SegmentedTabs(
                    mode = mode,
                    onSelect = { mode = it },
                )
            }

            if (error != null) {
                AlertBox(error)
            }

            when (mode) {
                AuthMode.Login -> {
                    AuthField(identifier, { identifier = it }, "Username or email", "you@example.com or username", busy)
                    AuthField(password, { password = it }, "Password", "Your password", busy, isPassword = true, ime = ImeAction.Done)
                    PrimaryButton(if (busy) "Signing in…" else "Sign in", enabled = !busy) {
                        onSignIn(identifier, password)
                    }
                    ForgotLink { mode = AuthMode.Forgot }
                }

                AuthMode.Register -> {
                    AuthField(username, { username = it }, "Username", "Pick a username", busy)
                    AuthField(email, { email = it }, "Email", "you@example.com", busy, keyboard = KeyboardType.Email)
                    AuthField(password, { password = it }, "Password", "At least 8 characters", busy, isPassword = true, ime = ImeAction.Done)
                    PrimaryButton(if (busy) "Creating…" else "Create account", enabled = !busy) {
                        onRegister(username, email, password)
                    }
                    Text(
                        "Password must be at least 8 characters.",
                        style = MaterialTheme.typography.bodySmall,
                        color = tandem.muted,
                    )
                }

                AuthMode.Forgot -> {
                    if (resetLinkSent) {
                        Text(
                            "📧 If an account matches, we've emailed a link to reset your password. It expires in an hour.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = tandem.muted,
                            textAlign = TextAlign.Center,
                        )
                        SecondaryButton("Back to sign in") {
                            onClearResetSent(); mode = AuthMode.Login
                        }
                    } else {
                        Text(
                            "Enter your username or email and we'll send you a link to reset your password.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = tandem.muted,
                            textAlign = TextAlign.Center,
                        )
                        AuthField(identifier, { identifier = it }, "Username or email", "you@example.com or username", busy, ime = ImeAction.Done)
                        PrimaryButton(if (busy) "Sending…" else "Send reset link", enabled = !busy) {
                            onSendResetLink(identifier)
                        }
                        SecondaryButton("Back to sign in") { mode = AuthMode.Login }
                    }
                }
            }

            LegalFooter()
        }
    }
}

@Composable
private fun BrandHeader() {
    val tandem = LocalTandemColors.current
    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Box(
                modifier = Modifier
                    .background(
                        Brush.linearGradient(listOf(tandem.savingsTileBg, tandem.savingsTileBg)),
                        RoundedCornerShape(16.dp),
                    )
                    .border(1.dp, tandem.savingsTileBorder, RoundedCornerShape(16.dp))
                    .padding(8.dp),
                contentAlignment = Alignment.Center,
            ) {
                TandemLogo(size = 44.dp)
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Tandem", fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = BrandGreen)
                Box(
                    modifier = Modifier
                        .padding(start = 4.dp)
                        .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(9.dp))
                        .border(1.dp, tandem.savingsTileBorder, RoundedCornerShape(9.dp))
                        .padding(horizontal = 8.dp, vertical = 1.dp),
                ) {
                    Text("Tab", fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = BrandGreen)
                }
            }
        }
        Spacer(Modifier.height(10.dp))
        Text(
            "Track together, save together.",
            fontSize = 15.sp,
            fontWeight = FontWeight.SemiBold,
            color = tandem.positive,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            "Simple family goals, zero stress. Sign in or create an account to begin.",
            style = MaterialTheme.typography.bodySmall,
            color = tandem.muted,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun SegmentedTabs(mode: AuthMode, onSelect: (AuthMode) -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(tandem.segmentTrack, RoundedCornerShape(12.dp))
            .padding(4.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        SegmentTab("Sign in", mode == AuthMode.Login, Modifier.weight(1f)) { onSelect(AuthMode.Login) }
        SegmentTab("Create account", mode == AuthMode.Register, Modifier.weight(1f)) { onSelect(AuthMode.Register) }
    }
}

@Composable
private fun SegmentTab(label: String, active: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Box(
        modifier = modifier
            .background(
                if (active) MaterialTheme.colorScheme.surface else androidx.compose.ui.graphics.Color.Transparent,
                RoundedCornerShape(9.dp),
            )
            .clickable(onClick = onClick)
            .padding(vertical = 9.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            label,
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
            color = if (active) BrandGreen else tandem.muted,
        )
    }
}

@Composable
private fun AuthField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    placeholder: String,
    busy: Boolean,
    isPassword: Boolean = false,
    keyboard: KeyboardType = KeyboardType.Text,
    ime: ImeAction = ImeAction.Next,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        placeholder = { Text(placeholder) },
        singleLine = true,
        enabled = !busy,
        shape = RoundedCornerShape(10.dp),
        colors = OutlinedTextFieldDefaults.colors(
            focusedBorderColor = BrandGreen,
            focusedLabelColor = BrandGreen,
            unfocusedBorderColor = MaterialTheme.colorScheme.outline,
        ),
        visualTransformation = if (isPassword) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        keyboardOptions = KeyboardOptions(
            keyboardType = if (isPassword) KeyboardType.Password else keyboard,
            imeAction = ime,
        ),
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun PrimaryButton(label: String, enabled: Boolean, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(10.dp),
        colors = ButtonDefaults.buttonColors(containerColor = BrandGreen, contentColor = androidx.compose.ui.graphics.Color.White),
        modifier = Modifier
            .fillMaxWidth()
            .height(50.dp),
    ) {
        if (!enabled) {
            CircularProgressIndicator(modifier = Modifier.height(20.dp), strokeWidth = 2.dp, color = androidx.compose.ui.graphics.Color.White)
        } else {
            Text(label, fontWeight = FontWeight.Bold)
        }
    }
}

@Composable
private fun SecondaryButton(label: String, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(10.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(10.dp))
            .clickable(onClick = onClick)
            .padding(vertical = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
    }
}

@Composable
private fun ForgotLink(onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
        Text(
            "Forgot your password?",
            fontSize = 13.sp,
            fontWeight = FontWeight.SemiBold,
            color = tandem.positive,
            modifier = Modifier
                .clickable(onClick = onClick)
                .padding(4.dp),
        )
    }
}

@Composable
private fun AlertBox(message: String) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(androidx.compose.ui.graphics.Color(0x1AEF4444), RoundedCornerShape(10.dp))
            .border(1.dp, androidx.compose.ui.graphics.Color(0x33EF4444), RoundedCornerShape(10.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        Text(message, color = androidx.compose.ui.graphics.Color(0xFFB4232A), style = MaterialTheme.typography.bodySmall)
    }
}

@Composable
private fun LegalFooter() {
    val tandem = LocalTandemColors.current
    val uri = LocalUriHandler.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 4.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            "Privacy",
            fontSize = 13.sp,
            color = tandem.muted,
            modifier = Modifier.clickable { uri.openUri("https://tandemtab.com/privacy.html") },
        )
        Text("  ·  ", fontSize = 13.sp, color = tandem.muted)
        Text(
            "Terms",
            fontSize = 13.sp,
            color = tandem.muted,
            modifier = Modifier.clickable { uri.openUri("https://tandemtab.com/terms.html") },
        )
    }
}
