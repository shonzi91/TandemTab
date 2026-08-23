package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.RecurringUi
import com.tandemtab.app.data.NotificationDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * Everything the app wants to tell you about this period, reached by pulling down on Home.
 *
 * ⚠️ **The phone has been showing only the urgent subset.** `AlertStrip` on Home filters to `urgent`, which is
 * right for a strip that must not become wallpaper — but it meant the rest were computed, sent, and unreachable.
 * This is the surface that makes them readable, which is also why the pull-down is worth a gesture: there was
 * previously nowhere for it to go.
 *
 * Urgent items lead, because a list that mixes "you are over budget" into the middle of routine notes buries the
 * one that needed reading.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NotificationsSheet(
    alerts: List<NotificationDto>,
    onDismiss: () -> Unit,
    onOpenTab: (String) -> Unit,
    // Bills live here now rather than in a Home card (owner's call, S117 — the web's answer, which its own
    // "no Home link" comment had been stating alone for months). The summary line below is what that card
    // carried that the alerts do not: the reassurance when nothing is due, and the door to managing them.
    recurring: RecurringUi = RecurringUi(),
    onManageBills: () -> Unit = {},
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val ordered = alerts.sortedByDescending { it.urgent }

    SheetScaffold(
        title = "Needs attention",
        saving = false,
        canSave = false,
        onDismiss = onDismiss,
        onSave = {},
        sheetState = sheetState,
        saveLabel = "",
    ) {
        BillsLine(recurring, tandem.muted, onManageBills)
        if (ordered.isEmpty()) {
            Text(
                "Nothing else needs your attention right now.",
                fontSize = 13.sp, color = tandem.muted,
            )
            return@SheetScaffold
        }
        ordered.forEach { n ->
            val tappable = !n.targetTab.isNullOrBlank()
            Row(
                Modifier
                    .fillMaxWidth()
                    .then(if (tappable) Modifier.clickable { onOpenTab(n.targetTab!!) } else Modifier)
                    .padding(vertical = 10.dp),
                verticalAlignment = Alignment.Top,
            ) {
                // The server sends an icon name; falling back to the alert glyph keeps an unknown one from
                // rendering as a gap where the reader expects a mark.
                CatIcon(n.icon.ifBlank { null }, n.text, size = 18.dp, tint = if (n.urgent) tandem.spent else tandem.catAccent)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        // `NotificationsMap` bakes amounts into prose server-side, so no client formatter ever
                        // sees them and privacy mode has to mask here — the same reason the Home alert strip
                        // does. This sheet is the *full* list the strip shows a subset of; masking one and not
                        // the other would hide a figure and then show it one tap later.
                        maskServerText(n.text),
                        fontSize = 14.sp,
                        fontWeight = if (n.urgent) FontWeight.Bold else FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    n.desc?.takeIf { it.isNotBlank() }?.let {
                        Spacer(Modifier.height(2.dp))
                        Text(maskServerText(it), fontSize = 12.sp, color = tandem.muted)
                    }
                }
                if (tappable) {
                    Icon(TandemIcons.Chevron, contentDescription = null, tint = tandem.muted, modifier = Modifier.size(14.dp))
                }
            }
        }
    }
}

/**
 * The bills line at the head of the list: what is due, and the way in to managing them.
 *
 * ★ It leads rather than trails. Bills are the only thing on this sheet you can act on *from here*, and the
 * alerts underneath already repeat the urgent ones in the server's own words — so the line that opens the
 * editor belongs above them, not after a list you have to scroll past.
 *
 * ⚠️ It is drawn even when nothing is due. "All bills handled for now" is the half of the old Home card that
 * the alerts cannot carry: an empty alert list says nothing about whether the bills were *checked*.
 */
@Composable
private fun BillsLine(recurring: RecurringUi, muted: Color, onManage: () -> Unit) {
    val active = recurring.items.filter { it.active }
    val due = active.count { it.due }
    val upcoming = active.count { it.upcoming && !it.due }
    val summary = when {
        !recurring.loaded -> "Bills & income"
        active.isEmpty() -> "No bills declared yet"
        due > 0 -> "$due due now" + (if (upcoming > 0) " · $upcoming upcoming" else "")
        upcoming > 0 -> "$upcoming upcoming"
        else -> "All bills handled for now"
    }
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onManage).padding(vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(TandemIcons.Repeat, contentDescription = null, tint = muted, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text("BILLS & INCOME", fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = muted)
            Spacer(Modifier.height(2.dp))
            Text(
                summary,
                fontSize = 14.sp, fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
        Text("Manage", color = MaterialTheme.colorScheme.primary, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
        Icon(TandemIcons.Chevron, contentDescription = null, tint = muted, modifier = Modifier.size(14.dp))
    }
    Box(Modifier.fillMaxWidth().height(1.dp).background(muted.copy(alpha = 0.25f)))
    Spacer(Modifier.height(6.dp))
}
