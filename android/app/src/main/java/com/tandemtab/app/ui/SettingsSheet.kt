package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.rounded.DeleteForever
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.UiState
import com.tandemtab.app.ui.theme.LocalTandemColors

// ── Profile (personal) ──────────────────────────────────────────────────────────────────────────────

/** The personal Profile sheet (top-bar gear): identity, change-password (hidden for external sign-in), sign out.
 *  Account-level actions live in [AccountSheet]. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProfileSheet(
    state: UiState,
    darkTheme: Boolean,
    onToggleTheme: () -> Unit,
    onChangePassword: (String, String) -> Unit,
    onSignOut: () -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val settings = state.settings
    var curPwd by remember { mutableStateOf("") }
    var newPwd by remember { mutableStateOf("") }

    SheetShell(sheetState, onDismiss, title = "Profile") {
        // Identity.
        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary), contentAlignment = Alignment.Center) {
                Text(state.username.trim().firstOrNull()?.uppercase() ?: "?", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
            }
            Spacer(Modifier.width(12.dp))
            Column {
                Text(state.username.ifBlank { "You" }, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 16.sp)
                if (state.email.isNotBlank() && state.email != state.username) Text(state.email, color = tandem.muted, fontSize = 13.sp)
            }
        }

        SectionDivider()

        // Change password — only for local-password accounts. External sign-in (Google/Facebook) has no password here.
        val provider = state.provider
        if (provider != null) {
            SectionTitle("Password")
            Text(
                "You sign in with ${providerName(provider)} — there's no password to manage here.",
                color = tandem.muted, fontSize = 13.sp,
            )
        } else {
            SectionTitle("Change password")
            OutlinedTextField(
                value = curPwd, onValueChange = { curPwd = it },
                label = { Text("Current password") }, singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            OutlinedTextField(
                value = newPwd, onValueChange = { newPwd = it },
                label = { Text("New password (8+ characters)") }, singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                modifier = Modifier.fillMaxWidth(),
            )
            if (settings.passwordChanged) Text("Password changed.", color = tandem.positive, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
            settings.error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp)) }
            Spacer(Modifier.height(8.dp))
            Button(
                onClick = { onChangePassword(curPwd, newPwd) },
                enabled = !settings.busy && curPwd.isNotBlank() && newPwd.length >= 8,
                modifier = Modifier.fillMaxWidth(),
            ) {
                if (settings.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                else Text("Change password")
            }
        }

        SectionDivider()

        // Appearance — a manual light/dark switch, mirroring the web's sun/moon toggle in the profile menu.
        SectionTitle("Appearance")
        Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(if (darkTheme) "Dark theme" else "Light theme", color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
            Box(
                Modifier.size(44.dp).clip(CircleShape)
                    .border(1.dp, MaterialTheme.colorScheme.outline, CircleShape)
                    .background(tandem.hero)
                    .clickable(onClick = onToggleTheme),
                contentAlignment = Alignment.Center,
            ) {
                Text(if (darkTheme) "☀️" else "🌙", fontSize = 18.sp)
            }
        }

        SectionDivider()
        OutlinedButton(onClick = onSignOut, modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
            Text("Sign out", color = MaterialTheme.colorScheme.error)
        }
    }
}

// ── Account ─────────────────────────────────────────────────────────────────────────────────────────

/** The Account actions sheet (the ⋯ by the account name), mirroring the web account menu: rename (owner), members,
 *  Recurring, and the destructive Leave / Delete. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AccountSheet(
    state: UiState,
    onRenameAccount: (String, () -> Unit) -> Unit,
    onOpenRecurring: () -> Unit,
    onLeave: () -> Unit,
    onDelete: () -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val account = state.selectedAccount
    val settings = state.settings
    val isOwner = account?.isOwner == true
    var accountName by remember(account?.id) { mutableStateOf(account?.name ?: "") }
    var renameSaved by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf<String?>(null) }   // "leave" | "delete" | null

    SheetShell(sheetState, onDismiss, title = "Account") {
        if (isOwner) {
            SectionTitle("Account name")
            OutlinedTextField(
                value = accountName, onValueChange = { accountName = it; renameSaved = false },
                singleLine = true, label = { Text("Name") }, modifier = Modifier.fillMaxWidth(),
            )
            if (renameSaved) Text("Saved.", color = tandem.positive, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
            Spacer(Modifier.height(8.dp))
            Button(
                onClick = { onRenameAccount(accountName) { renameSaved = true } },
                enabled = !settings.busy && accountName.isNotBlank() && accountName != account?.name,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("Rename account") }
            SectionDivider()
        } else if (account != null) {
            Text(account.name, fontSize = 20.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 8.dp))
            SectionDivider()
        }

        // Members.
        val members = account?.members ?: emptyList()
        if (members.isNotEmpty()) {
            SectionTitle("Members")
            members.forEach { m ->
                Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(30.dp).clip(CircleShape).background(tandem.savingsTileBg), contentAlignment = Alignment.Center) {
                        Text(m.displayName.trim().firstOrNull()?.uppercase() ?: "?", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                    }
                    Spacer(Modifier.width(10.dp))
                    Text(m.displayName.ifBlank { "—" }, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
                    if (m.userId == account?.ownerUserId) Text("owner", color = tandem.muted, fontSize = 11.sp)
                }
            }
            SectionDivider()
        }

        // Actions.
        ActionRow("Recurring bills & income", Icons.Rounded.Repeat, tint = MaterialTheme.colorScheme.onSurface) { onOpenRecurring() }

        settings.error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp)) }

        if (!isOwner) {
            ActionRow("Leave account", Icons.AutoMirrored.Rounded.Logout, tint = MaterialTheme.colorScheme.error) { confirm = "leave" }
        } else {
            ActionRow("Delete account", Icons.Rounded.DeleteForever, tint = MaterialTheme.colorScheme.error) { confirm = "delete" }
        }

        if (confirm != null) {
            val leaving = confirm == "leave"
            Spacer(Modifier.height(8.dp))
            Column(
                Modifier.fillMaxWidth().background(tandem.alertBg, RoundedCornerShape(12.dp)).border(1.dp, tandem.alertBorder, RoundedCornerShape(12.dp)).padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    if (leaving) "Leave “${account?.name}”? You'll lose access unless you're invited back."
                    else "Delete “${account?.name}”? It's removed after a 30-day grace period.",
                    color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedButton(onClick = { confirm = null }, modifier = Modifier.weight(1f), enabled = !settings.busy) { Text("Cancel") }
                    Button(
                        onClick = { if (leaving) onLeave() else onDelete() },
                        enabled = !settings.busy,
                        modifier = Modifier.weight(1f),
                        colors = androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                    ) {
                        if (settings.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Color.White)
                        else Text(if (leaving) "Leave" else "Delete")
                    }
                }
            }
        }
    }
}

// ── Shared chrome ───────────────────────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SheetShell(
    sheetState: androidx.compose.material3.SheetState,
    onDismiss: () -> Unit,
    title: String,
    body: @Composable () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Box(Modifier.fillMaxWidth().fillMaxHeight()) {
            Column(
                Modifier.fillMaxWidth().imePadding().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()).padding(bottom = 92.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(title, fontSize = 20.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 4.dp))
                body()
            }
            Row(
                Modifier.align(Alignment.BottomCenter).fillMaxWidth().background(MaterialTheme.colorScheme.surface).navigationBarsPadding().padding(horizontal = 18.dp, vertical = 12.dp),
            ) {
                Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Done") }
            }
        }
    }
}

@Composable
private fun ActionRow(label: String, icon: ImageVector, tint: Color, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(20.dp))
        Spacer(Modifier.width(12.dp))
        Text(label, color = tint, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(text.uppercase(), fontSize = 11.sp, letterSpacing = 1.1.sp, fontWeight = FontWeight.Bold, color = LocalTandemColors.current.muted, modifier = Modifier.padding(bottom = 6.dp))
}

@Composable
private fun SectionDivider() {
    Spacer(Modifier.height(12.dp))
    Box(Modifier.fillMaxWidth().height(1.dp).background(LocalTandemColors.current.hairline))
    Spacer(Modifier.height(12.dp))
}

private fun providerName(p: String): String = when (p.lowercase()) {
    "google" -> "Google"
    "facebook" -> "Facebook"
    else -> p.replaceFirstChar { it.uppercase() }
}
