package com.tandemtab.app.ui

import android.content.Intent
import androidx.core.content.FileProvider
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.withFrameNanos
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.UiState
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import kotlin.math.roundToInt

// ── Profile (personal) ──────────────────────────────────────────────────────────────────────────────

/** The landing-tab choices (O9). The stored values are the NavDest entry names, which are also what the web stores
 *  under `finapp-landing-tab` — one preference, spelled the same on both platforms, so a person who uses both sees
 *  one setting rather than two that happen to rhyme. `Home` keeps its name; only the label reads "Dashboard". */
private val LANDING_TABS = listOf(
    "Home" to "Dashboard",
    "Spending" to "Spending",
    "Goals" to "Goals",
    "Wallets" to "Wallets",
)

/** The personal Profile sheet (top-bar gear): identity, change-password (hidden for external sign-in), sign out.
 *  Account-level actions live in [AccountSheet]. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProfileSheet(
    state: UiState,
    darkTheme: Boolean,
    onToggleTheme: () -> Unit,
    landingTab: String,
    onSetLandingTab: (String) -> Unit,
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
    onSignOut: () -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val settings = state.settings
    val context = LocalContext.current
    var curPwd by remember { mutableStateOf("") }
    var newPwd by remember { mutableStateOf("") }
    val isExternal = state.provider != null

    // Avatar picker (local accounts only — external logins get their picture from the provider). Reads the picked
    // image, downscales it, and hands up a data-URL to upload.
    val avatarPicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri != null) encodeAvatarDataUrl(context, uri)?.let(onUploadAvatar)
    }

    SheetShell(sheetState, onDismiss, title = "Profile") {
        // Identity — the avatar picture if we have one, else the username initial.
        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            val avatarBitmap = remember(state.avatar) { decodeDataUrlImage(state.avatar) }
            Box(Modifier.size(48.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary), contentAlignment = Alignment.Center) {
                if (avatarBitmap != null) {
                    Image(avatarBitmap, contentDescription = "Profile photo", modifier = Modifier.size(48.dp).clip(CircleShape), contentScale = ContentScale.Crop)
                } else {
                    Text(state.username.trim().firstOrNull()?.uppercase() ?: "?", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
                }
            }
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(state.username.ifBlank { "You" }, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 16.sp)
                if (state.email.isNotBlank() && state.email != state.username) Text(state.email, color = tandem.muted, fontSize = 13.sp)
            }
            // Upload is offered only for local accounts; external logins carry the provider's picture.
            if (!isExternal) {
                Text("Change", color = tandem.positive, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.clickable { avatarPicker.launch("image/*") }.padding(6.dp))
            }
        }

        // Email verification — local accounts only (external logins are verified via the provider).
        if (!isExternal) {
            SectionDivider()
            SectionTitle("Email")
            if (state.emailVerified) {
                Text("✓ Your email address is verified.", color = tandem.positive, fontSize = 13.sp)
            } else {
                Text("Your email address isn't verified yet.", color = tandem.muted, fontSize = 13.sp)
                Spacer(Modifier.height(8.dp))
                OutlinedButton(onClick = onResendVerification, enabled = !settings.busy) { Text("Resend verification email") }
            }
        }

        // Change password — only for local-password accounts. External sign-in (Google/Facebook) has no password,
        // so the whole section is dropped rather than showing a "nothing to manage" note that only takes space.
        if (state.provider == null) {
            SectionDivider()
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

        // Two-factor authentication — available to every user, including external sign-ins.
        TwoFactorSection(state, onBeginTwoFactor, onConfirmTwoFactor, onDisableTwoFactor, onSetTwoFactorDisabling, onCancelTwoFactorSetup, onDismissRecoveryCodes)

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

        // Which tab the app opens on (O9) — the same preference the web keeps beside its theme, offered here in the
        // same place for the same reason: it is how the app is set up, not what it is showing you right now.
        Text("Open the app on", color = tandem.muted, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
        Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            LANDING_TABS.forEach { (value, label) ->
                val on = landingTab == value
                Text(
                    label,
                    color = if (on) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface,
                    fontSize = 13.sp,
                    fontWeight = if (on) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier
                        .clip(RoundedCornerShape(999.dp))
                        .background(if (on) MaterialTheme.colorScheme.primary else tandem.hero)
                        .clickable { onSetLandingTab(value) }
                        .padding(horizontal = 12.dp, vertical = 7.dp),
                )
            }
        }

        // Deleted accounts still inside their grace window. Shown only when there ARE any — an empty section
        // would advertise a state most people never reach, and its whole job is to be found in the one week
        // somebody needs it.
        // ★ This closes a real dead-end: the phone could already delete an account, and deleting is a SOFT
        // delete the server keeps for 30 days. Until now the undo lived only in a browser, so a mistake made
        // here became permanent by simply being ignored. Placed in the profile to mirror the web exactly.
        if (settings.archivedAccounts.isNotEmpty()) {
            SectionDivider()
            SectionTitle("Deleted accounts")
            Text(
                "Deleted accounts are removed for good after 30 days. Restore one to bring it back.",
                color = tandem.muted, fontSize = 13.sp,
            )
            Spacer(Modifier.height(8.dp))
            settings.archivedAccounts.forEach { archived ->
                Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(archived.name.ifBlank { "Account" }, color = MaterialTheme.colorScheme.onSurface, fontWeight = FontWeight.SemiBold)
                        // The number that matters is how long is left to act, not when it was deleted.
                        Text(daysLeftLabel(archived.purgeAt), color = tandem.muted, fontSize = 13.sp)
                    }
                    OutlinedButton(onClick = { onRestoreAccount(archived.id) }, enabled = !settings.busy) {
                        Text("Restore")
                    }
                }
            }
            settings.error?.let {
                Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp))
            }
        }

        SectionDivider()
        OutlinedButton(onClick = onSignOut, modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
            Text("Sign out", color = MaterialTheme.colorScheme.error)
        }
    }
}

/**
 * "N days left" from an ISO-8601 purge instant, rounded UP so a window with any time left never reads "0 days".
 * ⚠️ Falls back to a bare "Deleted" if the timestamp won't parse: a restorable account must still be listed and
 * restorable, and an unparseable date is no reason to hide the button that is the whole point of the row.
 */
private fun daysLeftLabel(purgeAt: String): String {
    val instant = runCatching { java.time.OffsetDateTime.parse(purgeAt).toInstant() }.getOrNull()
        ?: return "Deleted"
    val seconds = java.time.Duration.between(java.time.Instant.now(), instant).seconds
    val days = Math.max(0L, Math.ceil(seconds / 86_400.0).toLong())
    return if (days == 1L) "1 day left" else "$days days left"
}

// ── Account ─────────────────────────────────────────────────────────────────────────────────────────

/** The Account actions sheet (the ⋯ by the account name), mirroring the web account menu: rename (owner), the
 *  People block (invite + who's on the account + the owner's per-member actions), Recurring, and the destructive
 *  Leave / Delete. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AccountSheet(
    state: UiState,
    onRenameAccount: (String, () -> Unit) -> Unit,
    onSetSavingsTarget: (Double, () -> Unit) -> Unit,
    onOpenRecurring: () -> Unit,
    onInvite: (String) -> Unit,
    onClearInviteResult: () -> Unit,
    onRemoveMember: (String, () -> Unit) -> Unit,
    onTransferOwnership: (String, () -> Unit) -> Unit,
    onLeave: (String?) -> Unit,
    onDelete: () -> Unit,
    onExport: ((java.io.File) -> Unit) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val context = LocalContext.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val account = state.selectedAccount
    val settings = state.settings
    val isOwner = account?.isOwner == true
    var accountName by remember(account?.id) { mutableStateOf(account?.name ?: "") }
    var renameSaved by remember { mutableStateOf(false) }
    // Seeded from the loaded target rather than a default: /settings answers after the sheet opens, so keying on
    // the loaded value is what makes the field fill in when it lands (and re-settle on whatever was saved).
    var targetText by remember(account?.id, settings.savingsTarget) {
        mutableStateOf(settings.savingsTarget?.let { (it * 100).roundToInt().toString() } ?: "")
    }
    var targetSaved by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf<String?>(null) }   // "leave" | "delete" | null
    // Which member's action row is open. Deliberately SEPARATE from handOverTo below: sharing one id between
    // "whose actions are showing" and "who takes the account over" makes picking a new owner silently expand
    // that person's row higher up the sheet, which pushes the confirm block back under the Done bar.
    var expandedMemberId by remember(account?.id) { mutableStateOf<String?>(null) }
    // Who the owner is handing the account to on the way out (the server refuses to leave one with no owner).
    var handOverTo by remember(account?.id) { mutableStateOf<String?>(null) }
    // The member whose Remove is awaiting confirmation (never a delete without a confirm).
    var removing by remember(account?.id) { mutableStateOf<com.tandemtab.app.data.MemberDto?>(null) }

    SheetShell(sheetState, onDismiss, title = "Account", scrollToEnd = confirm) {
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

            // The savings target — what the Insights health score measures you against. It sits with the account
            // name because that's where the web keeps it (its modal is "Edit account", not "Rename", for exactly
            // this reason), and because a single number doesn't earn a screen of its own.
            SectionTitle("Savings target")
            val typed = targetText.toDoubleOrNull()
            val inRange = typed != null && typed in 0.0..100.0
            OutlinedTextField(
                value = targetText,
                onValueChange = { targetText = it; targetSaved = false },
                singleLine = true,
                enabled = settings.savingsTarget != null,
                label = { Text("Target (% of money in)") },
                suffix = { Text("%") },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth(),
            )
            // What you're actually saving this period, next to what you're aiming at — the number is a decision,
            // and it can't be made against a blank. Null means nothing has come in yet, which is not 0%.
            val actual = state.overview?.savedRate
            Text(
                when {
                    settings.savingsTarget == null -> "Loading your current target…"
                    actual != null -> "You're saving ${(actual * 100).roundToInt()}% of money in this period."
                    else -> "Nothing has come in this period yet, so there's no rate to compare against."
                },
                fontSize = 12.sp, color = tandem.muted, modifier = Modifier.padding(top = 6.dp),
            )
            if (targetText.isNotBlank() && !inRange) {
                Text(
                    "Keep this between 0 and 100%.",
                    color = tandem.warn, fontSize = 12.sp, modifier = Modifier.padding(top = 4.dp),
                )
            }
            if (targetSaved) Text("Saved.", color = tandem.positive, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
            Spacer(Modifier.height(8.dp))
            Button(
                onClick = { typed?.let { t -> onSetSavingsTarget(t) { targetSaved = true } } },
                enabled = !settings.busy && inRange &&
                    typed != settings.savingsTarget?.let { (it * 100).roundToInt().toDouble() },
                modifier = Modifier.fillMaxWidth(),
            ) { Text("Save target") }
            SectionDivider()
        } else if (account != null) {
            Text(account.name, fontSize = 20.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 8.dp))
            SectionDivider()
        }

        // People — who's on the account, plus the invite that puts them there. The owner's per-member actions
        // (hand over / remove) expand under the tapped row rather than hiding in a menu: two actions don't earn
        // a menu, and an inline row keeps the person they apply to on screen while you read them.
        val members = account?.members ?: emptyList()
        if (account != null) {
            SectionTitle("People")
            members.forEach { m ->
                val isMe = m.userId == state.myUserId
                val isAccountOwner = m.userId == account.ownerUserId
                val actionable = isOwner && !isMe
                val expanded = expandedMemberId == m.userId
                Column {
                    Row(
                        Modifier.fillMaxWidth()
                            .then(if (actionable) Modifier.clickable { expandedMemberId = if (expanded) null else m.userId } else Modifier)
                            .padding(vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        MemberAvatar(m.displayName, state.sharing.avatars[m.userId])
                        Spacer(Modifier.width(10.dp))
                        Text(m.displayName.ifBlank { "—" }, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
                        if (isMe) PersonTag("you")
                        if (isAccountOwner) PersonTag("owner")
                        if (actionable) {
                            Spacer(Modifier.width(6.dp))
                            Icon(TandemIcons.Dots, contentDescription = "Manage ${m.displayName}", tint = tandem.muted, modifier = Modifier.size(18.dp))
                        }
                    }
                    if (expanded) {
                        Row(Modifier.fillMaxWidth().padding(start = 40.dp, bottom = 8.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            OutlinedButton(
                                onClick = { onTransferOwnership(m.userId) { expandedMemberId = null } },
                                enabled = !state.sharing.busy, modifier = Modifier.weight(1f),
                            ) { Text("Make owner", fontSize = 13.sp) }
                            OutlinedButton(
                                onClick = { removing = m; expandedMemberId = null },
                                enabled = !state.sharing.busy, modifier = Modifier.weight(1f),
                            ) { Text("Remove", color = MaterialTheme.colorScheme.error, fontSize = 13.sp) }
                        }
                    }
                }
            }

            InviteRow(
                proLocked = state.shareIsProLocked,
                sharing = state.sharing,
                onInvite = onInvite,
                onEdited = onClearInviteResult,
            )
            SectionDivider()
        }

        // Actions.
        ActionRow("Recurring bills & income", TandemIcons.Repeat, tint = MaterialTheme.colorScheme.onSurface) { onOpenRecurring() }

        // ★ Export lives HERE because the web's privacy panel tells people so in as many words: "from the account
        // menu (⋯) → Export to Excel". Putting it anywhere else would make our own instructions wrong on half our
        // surfaces — and that panel is where the portability promise is printed, next to the GDPR address.
        ActionRow("Export to a spreadsheet", TandemIcons.Share, tint = MaterialTheme.colorScheme.onSurface) {
            onExport { file ->
                // A per-share content:// grant, not a file path: no storage permission on any api level, and the
                // receiving app's read access dies with the share.
                val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
                val send = Intent(Intent.ACTION_SEND).apply {
                    type = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    putExtra(Intent.EXTRA_STREAM, uri)
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
                context.startActivity(Intent.createChooser(send, "Export account"))
            }
        }

        settings.error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp)) }

        // An OWNER can leave too, as long as someone is left to take over — the server refuses to orphan an
        // account, so the hand-over picker below is part of the request, not a courtesy. A sole owner has no one
        // to hand it to; for them "leave" and "delete" are the same act, and Delete already says so plainly.
        val others = state.otherMembers
        val ownerCanLeave = isOwner && others.isNotEmpty()
        if (!isOwner || ownerCanLeave) {
            ActionRow("Leave account", TandemIcons.Logout, tint = MaterialTheme.colorScheme.error) {
                confirm = "leave"; handOverTo = null; expandedMemberId = null
            }
        }
        if (isOwner) {
            ActionRow("Delete account", TandemIcons.Trash, tint = MaterialTheme.colorScheme.error) { confirm = "delete" }
        }

        if (confirm != null) {
            val leaving = confirm == "leave"
            val needsHandOver = leaving && ownerCanLeave
            Spacer(Modifier.height(8.dp))
            Column(
                Modifier.fillMaxWidth().background(tandem.alertBg, RoundedCornerShape(12.dp)).border(1.dp, tandem.alertBorder, RoundedCornerShape(12.dp)).padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    when {
                        needsHandOver -> "Leave “${account?.name}”? You own it, so hand it to someone else before you go."
                        leaving -> "Leave “${account?.name}”? You'll lose access unless you're invited back."
                        else -> "Delete “${account?.name}”? It's removed after a 30-day grace period."
                    },
                    color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp,
                )
                if (needsHandOver) {
                    others.forEach { m ->
                        val picked = handOverTo == m.userId
                        Row(
                            Modifier.fillMaxWidth().clickable { handOverTo = m.userId }.padding(vertical = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = picked, onClick = { handOverTo = m.userId })
                            Text(m.displayName.ifBlank { "—" }, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp)
                        }
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedButton(onClick = { confirm = null }, modifier = Modifier.weight(1f), enabled = !settings.busy) { Text("Cancel") }
                    Button(
                        onClick = { if (leaving) onLeave(handOverTo) else onDelete() },
                        // Greyed rather than hidden while nobody is picked: the button explains what's missing by
                        // sitting under the list it wants an answer from.
                        enabled = !settings.busy && (!needsHandOver || handOverTo != null),
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

    removing?.let { target ->
        RemoveMemberDialog(
            member = target,
            sharing = state.sharing,
            onDismiss = { removing = null },
            onConfirm = { onRemoveMember(target.userId) { removing = null } },
        )
    }
}

/** Confirm removing someone from the account. Says what survives them (their contributions and expenses) —
 *  without that, "remove" reads like it might take the money history with it. */
@Composable
private fun RemoveMemberDialog(
    member: com.tandemtab.app.data.MemberDto,
    sharing: com.tandemtab.app.SharingUi,
    onDismiss: () -> Unit,
    onConfirm: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = { if (!sharing.busy) onDismiss() },
        title = { Text("Remove ${member.displayName}?") },
        text = {
            Column {
                Text("They lose access to this account. Their recorded contributions and expenses stay.")
                sharing.error?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = onConfirm,
                enabled = !sharing.busy,
                colors = androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
            ) {
                if (sharing.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                else Text("Remove")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !sharing.busy) { Text("Cancel") } },
    )
}

/** The invite control: a "+ Invite someone" row that opens a username field in place. Wears the Pro crown when
 *  the plan doesn't include sharing — but stays fully usable, because the crown is decoration and the server's
 *  402 is the gate. A client that decided this for itself would lock out a paying user on a stale plan string. */
@Composable
private fun InviteRow(
    proLocked: Boolean,
    sharing: com.tandemtab.app.SharingUi,
    onInvite: (String) -> Unit,
    onEdited: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var open by remember { mutableStateOf(false) }
    var username by remember { mutableStateOf("") }

    // Clear the field once the invitation has actually gone, so a second invite starts empty.
    LaunchedEffect(sharing.invited) { if (sharing.invited != null) username = "" }

    if (!open) {
        Row(
            Modifier.fillMaxWidth().clickable { open = true }.padding(vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(TandemIcons.Plus, contentDescription = null, tint = tandem.positive, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(10.dp))
            Text("Invite someone", color = tandem.positive, fontWeight = FontWeight.SemiBold)
            if (proLocked) {
                Spacer(Modifier.width(8.dp))
                Icon(TandemIcons.Crown, contentDescription = "Part of Pro", tint = tandem.warn, modifier = Modifier.size(15.dp))
            }
        }
    } else {
        Spacer(Modifier.height(4.dp))
        Text(
            "Enter the username of someone who already has a TandemTab account. They'll get a prompt to accept; " +
                "once they do, they can edit everything except deleting the account.",
            color = tandem.muted, fontSize = 12.sp,
        )
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(
            value = username,
            onValueChange = { username = it; onEdited() },
            label = { Text("Username") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        sharing.invited?.let {
            Text("Invitation sent to $it.", color = tandem.positive, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
        }
        sharing.error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
        }
        Spacer(Modifier.height(8.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedButton(
                onClick = { open = false; username = ""; onEdited() },
                modifier = Modifier.weight(1f), enabled = !sharing.busy,
            ) { Text("Cancel") }
            Button(
                onClick = { onInvite(username) },
                enabled = !sharing.busy && username.isNotBlank(),
                modifier = Modifier.weight(1f),
            ) {
                if (sharing.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                else Text("Send invite")
            }
        }
    }
}

/** A member's profile picture, falling back to their initial. Shared by the People list and the invitations card. */
@Composable
internal fun MemberAvatar(displayName: String, dataUrl: String?, size: androidx.compose.ui.unit.Dp = 30.dp) {
    val tandem = LocalTandemColors.current
    val bitmap = remember(dataUrl) { decodeDataUrlImage(dataUrl) }
    Box(Modifier.size(size).clip(CircleShape).background(tandem.savingsTileBg), contentAlignment = Alignment.Center) {
        if (bitmap != null) {
            Image(bitmap, contentDescription = null, modifier = Modifier.size(size).clip(CircleShape), contentScale = ContentScale.Crop)
        } else {
            Text(
                displayName.trim().firstOrNull()?.uppercase() ?: "?",
                color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold, fontSize = (size.value / 2.3f).sp,
            )
        }
    }
}

/** The small "you" / "owner" chip on a member row. Uses the tile background, not `hero`: hero is the canvas
 *  tint, so in dark it is the same colour as the sheet and the chip reads as bare text on one theme only. */
@Composable
private fun PersonTag(text: String) {
    val tandem = LocalTandemColors.current
    Text(
        text,
        color = tandem.muted, fontSize = 11.sp, fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(start = 6.dp).background(tandem.savingsTileBg, RoundedCornerShape(6.dp)).padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

// ── Two-factor ──────────────────────────────────────────────────────────────────────────────────────

/** The Two-factor block of the profile: off → Enable; enrolling → QR + code; just-confirmed → recovery codes;
 *  on → status + Turn off (with a code). Mirrors the web MainLayout profile 2FA section. */
@Composable
private fun TwoFactorSection(
    state: UiState,
    onBegin: () -> Unit,
    onConfirm: (String) -> Unit,
    onDisable: (String) -> Unit,
    onSetDisabling: (Boolean) -> Unit,
    onCancelSetup: () -> Unit,
    onDismissRecovery: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val tf = state.twoFactor
    var code by remember(tf.setup, tf.disabling) { mutableStateOf("") }
    SectionTitle("Two-factor authentication")
    tf.error?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(bottom = 6.dp)) }

    when {
        // Just enrolled: show the one-time recovery codes.
        tf.recoveryCodes != null -> {
            Text("Two-factor is on. Save these recovery codes somewhere safe — each works once if you lose your device.", color = tandem.muted, fontSize = 13.sp)
            Spacer(Modifier.height(8.dp))
            Column(
                Modifier.fillMaxWidth().background(tandem.savingsTileBg, RoundedCornerShape(10.dp)).padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) { tf.recoveryCodes.forEach { Text(it, fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp) } }
            Spacer(Modifier.height(8.dp))
            Button(onClick = onDismissRecovery, modifier = Modifier.fillMaxWidth()) { Text("Done") }
        }
        // Enrolling: show the QR to scan + a code field to confirm.
        tf.setup != null -> {
            Text("Scan this in your authenticator app (Google Authenticator, Authy, 1Password…), then enter the 6-digit code.", color = tandem.muted, fontSize = 13.sp)
            Spacer(Modifier.height(10.dp))
            val qr = remember(tf.setup.qrImage) { decodeDataUrlImage(tf.setup.qrImage) }
            if (qr != null) {
                Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    Image(qr, contentDescription = "Two-factor QR code", modifier = Modifier.size(180.dp).background(Color.White, RoundedCornerShape(8.dp)).padding(8.dp))
                }
            }
            // Same-device path: fire the otpauth:// link so the authenticator app (Google Authenticator / Authy /
            // 1Password) opens directly with the account pre-filled — no scanning your own screen.
            val ctx = LocalContext.current
            if (tf.setup.otpauthUri.isNotBlank()) {
                Spacer(Modifier.height(8.dp))
                OutlinedButton(
                    onClick = { runCatching { ctx.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(tf.setup.otpauthUri))) } },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Open in authenticator app") }
            }
            if (tf.setup.secret.isNotBlank()) {
                Spacer(Modifier.height(6.dp))
                Text("Or enter this key manually: ${tf.setup.secret}", color = tandem.muted, fontSize = 12.sp, modifier = Modifier.fillMaxWidth())
            }
            Spacer(Modifier.height(10.dp))
            OutlinedTextField(
                value = code, onValueChange = { code = it.filter(Char::isDigit).take(6) },
                label = { Text("6-digit code") }, singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedButton(onClick = onCancelSetup, modifier = Modifier.weight(1f), enabled = !tf.busy) { Text("Cancel") }
                Button(onClick = { onConfirm(code) }, enabled = !tf.busy && code.length == 6, modifier = Modifier.weight(1f)) {
                    if (tf.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary) else Text("Confirm")
                }
            }
        }
        // On: status + a Turn-off panel that asks for a current code.
        state.twoFactorEnabled -> {
            if (tf.disabling) {
                OutlinedTextField(
                    value = code, onValueChange = { code = it.filter(Char::isDigit).take(6) },
                    label = { Text("Current 6-digit code") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(onClick = { onSetDisabling(false) }, modifier = Modifier.weight(1f), enabled = !tf.busy) { Text("Cancel") }
                    Button(
                        onClick = { onDisable(code) }, enabled = !tf.busy && code.length == 6, modifier = Modifier.weight(1f),
                        colors = androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                    ) { if (tf.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = Color.White) else Text("Turn off") }
                }
            } else {
                Text("✓ Two-factor authentication is on.", color = tandem.positive, fontSize = 13.sp)
                Spacer(Modifier.height(8.dp))
                OutlinedButton(onClick = { onSetDisabling(true) }) { Text("Turn off two-factor", color = MaterialTheme.colorScheme.error) }
            }
        }
        // Off: offer to enable.
        else -> {
            Text("Add a second step at sign-in using an authenticator app.", color = tandem.muted, fontSize = 13.sp)
            Spacer(Modifier.height(8.dp))
            Button(onClick = onBegin, enabled = !tf.busy) {
                if (tf.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary) else Text("Enable two-factor")
            }
        }
    }
}

/** Decode a `data:image/...;base64,...` URL to an ImageBitmap, or null if absent/unparseable. */
private fun decodeDataUrlImage(dataUrl: String?): androidx.compose.ui.graphics.ImageBitmap? {
    if (dataUrl.isNullOrBlank()) return null
    val comma = dataUrl.indexOf(',')
    if (comma < 0) return null
    return runCatching {
        val bytes = android.util.Base64.decode(dataUrl.substring(comma + 1), android.util.Base64.DEFAULT)
        android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
    }.getOrNull()
}

/** Read a picked image, downscale to ≤512px, and return a JPEG data URL suitable for the avatar endpoint. */
private fun encodeAvatarDataUrl(context: android.content.Context, uri: android.net.Uri): String? = runCatching {
    val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return null
    val src = android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return null
    val max = 512
    val scale = minOf(1f, max.toFloat() / maxOf(src.width, src.height))
    val scaled = if (scale < 1f) android.graphics.Bitmap.createScaledBitmap(src, (src.width * scale).toInt(), (src.height * scale).toInt(), true) else src
    val out = java.io.ByteArrayOutputStream()
    scaled.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, out)
    "data:image/jpeg;base64," + android.util.Base64.encodeToString(out.toByteArray(), android.util.Base64.NO_WRAP)
}.getOrNull()

// ── Shared chrome ───────────────────────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SheetShell(
    sheetState: androidx.compose.material3.SheetState,
    onDismiss: () -> Unit,
    title: String,
    // Set to a non-null value when the body reveals a block at its foot (a confirm panel). The sheet's own Done
    // bar is a sibling of the scrolling body, so anything that grows at the bottom lands UNDER it — the exact
    // shape of the two period-sheet bugs in Session 91. Scrolling to it is the fix; hoping the user finds it
    // isn't, since the buttons they need are the part that's hidden.
    scrollToEnd: Any? = null,
    body: @Composable () -> Unit,
) {
    val scroll = rememberScrollState()
    LaunchedEffect(scrollToEnd) {
        if (scrollToEnd == null) return@LaunchedEffect
        // The new block hasn't been measured on this frame, so maxValue is still the pre-growth one. Wait a
        // frame and scroll; do it twice, because a picker's rows settle in a second pass.
        withFrameNanos { }
        scroll.animateScrollTo(scroll.maxValue)
        withFrameNanos { }
        scroll.animateScrollTo(scroll.maxValue)
    }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Box(Modifier.fillMaxWidth().fillMaxHeight()) {
            Column(
                Modifier.fillMaxWidth().imePadding().padding(horizontal = 18.dp).verticalScroll(scroll).padding(bottom = 92.dp),
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
