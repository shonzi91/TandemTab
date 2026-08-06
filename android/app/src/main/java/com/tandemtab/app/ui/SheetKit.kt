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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.SheetState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.draw.clip
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Currency
import java.util.Locale

// Shared building blocks for the write-flow bottom sheets (add-expense, transfer, add-income, savings…).
// Kept in one place so every modal reads as the same product and money/date formatting stays identical.

/** The empty Guid — general (uncategorised) income (server treats it as Guid.Empty). */
internal const val GENERAL_INCOME = "00000000-0000-0000-0000-000000000000"

@Composable
internal fun FieldLabel(text: String) {
    Text(
        text.uppercase(),
        fontSize = 10.sp,
        letterSpacing = 1.2.sp,
        fontWeight = FontWeight.Bold,
        color = LocalTandemColors.current.muted,
        modifier = Modifier.padding(bottom = 6.dp),
    )
}

/** A pill toggle used across the sheets (category / fund / bucket pickers). */
/** A subtle round "open" affordance (a chevron chip) used on tappable cards in place of a "Details" text label. */
@Composable
internal fun OpenChip() {
    val tandem = LocalTandemColors.current
    Box(
        Modifier.size(28.dp).clip(RoundedCornerShape(999.dp))
            .background(tandem.savingsTileBg).border(1.dp, tandem.savingsTileBorder, RoundedCornerShape(999.dp)),
        contentAlignment = Alignment.Center,
    ) {
        Icon(TandemIcons.Chevron, contentDescription = "Open", tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(14.dp))
    }
}

@Composable
internal fun PickChip(label: String, icon: String?, selected: Boolean, catName: String? = null, onClick: () -> Unit) {
    val bg = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val fg = if (selected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface
    val border = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline
    Row(
        modifier = Modifier
            .background(bg, RoundedCornerShape(999.dp))
            .border(1.dp, border, RoundedCornerShape(999.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // A category chip shows the resolved line-icon (tinted to match the chip state); other chips keep raw glyphs.
        if (catName != null) { CatIcon(icon, catName, size = 15.dp, tint = if (selected) fg else LocalTandemColors.current.catAccent); Spacer(Modifier.width(6.dp)) }
        else if (!icon.isNullOrBlank()) { Text(icon, fontSize = 14.sp); Spacer(Modifier.width(6.dp)) }
        Text(label, color = fg, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** A tappable date row backed by a Material date-picker dialog. `dateIso` is yyyy-MM-dd. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DateField(dateIso: String, onPick: (String) -> Unit) {
    var show by remember { mutableStateOf(false) }
    Box(
        Modifier
            .fillMaxWidth()
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
            .clickable { show = true }
            .padding(14.dp),
    ) { Text(prettyDateLabel(dateIso), color = MaterialTheme.colorScheme.onSurface) }

    if (show) {
        val initMillis = runCatching {
            LocalDate.parse(dateIso).atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli()
        }.getOrNull()
        val dpState = rememberDatePickerState(initialSelectedDateMillis = initMillis)
        DatePickerDialog(
            onDismissRequest = { show = false },
            confirmButton = {
                TextButton(onClick = {
                    dpState.selectedDateMillis?.let {
                        onPick(Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate().toString())
                    }
                    show = false
                }) { Text("OK") }
            },
            dismissButton = { TextButton(onClick = { show = false }) { Text("Cancel") } },
        ) { DatePicker(state = dpState) }
    }
}

internal fun trimAmount(v: Double): String =
    if (v == v.toLong().toDouble()) v.toLong().toString() else v.toString()

internal fun prettyDateLabel(iso: String): String = runCatching {
    val d = LocalDate.parse(iso)
    when (d) {
        LocalDate.now() -> "Today"
        LocalDate.now().minusDays(1) -> "Yesterday"
        else -> d.format(DateTimeFormatter.ofPattern("EEE, d MMM yyyy", Locale.getDefault()))
    }
}.getOrDefault(iso)

internal fun currencySymbol(code: String): String = runCatching {
    Currency.getInstance(code).symbol
}.getOrDefault(code)

/** Currency formatter for the account's ISO code (device locale for grouping/decimals). */
internal fun sheetMoney(currencyCode: String): (Double) -> String {
    val nf = java.text.NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}

/** Shared chrome for the write sheets: a full-screen sheet with a title, a scrollable body, and Cancel / Save as
 *  floating buttons pinned to the bottom (swipe-down still dismisses). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SheetScaffold(
    title: String,
    saving: Boolean,
    canSave: Boolean,
    onDismiss: () -> Unit,
    onSave: () -> Unit,
    sheetState: SheetState,
    saveLabel: String = "Save",
    body: @Composable () -> Unit,
) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = MaterialTheme.colorScheme.surface,
    ) {
        Box(Modifier.fillMaxWidth().fillMaxHeight()) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .imePadding()
                    .padding(horizontal = 18.dp)
                    .verticalScroll(rememberScrollState())
                    .padding(bottom = 92.dp),   // clear the floating button bar
            ) {
                Text(
                    title,
                    modifier = Modifier.fillMaxWidth().padding(top = 4.dp, bottom = 4.dp),
                    fontSize = 19.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Spacer(Modifier.height(8.dp))
                body()
            }
            SheetActionBar(
                saving = saving,
                canSave = canSave,
                onDismiss = onDismiss,
                onSave = onSave,
                modifier = Modifier.align(Alignment.BottomCenter),
                saveLabel = saveLabel,
            )
        }
    }
}

/** The floating Cancel / Save bar pinned to the bottom of a full-screen sheet. */
@Composable
internal fun SheetActionBar(
    saving: Boolean,
    canSave: Boolean,
    onDismiss: () -> Unit,
    onSave: () -> Unit,
    modifier: Modifier = Modifier,
    saveLabel: String = "Save",
) {
    Row(
        modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface)
            .navigationBarsPadding()
            .imePadding()
            .padding(horizontal = 18.dp, vertical = 12.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        OutlinedButton(onClick = onDismiss, modifier = Modifier.weight(1f)) { Text("Cancel") }
        Button(onClick = onSave, enabled = canSave, modifier = Modifier.weight(1f)) {
            if (saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
            else Text(saveLabel)
        }
    }
}

/** A labelled checkbox row, as the sheets' opt-in switches (copy budgets forward, notify on milestone…). */
@Composable
internal fun CheckRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = checked, onCheckedChange = onChange)
        Text(label, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp)
    }
}

/** An inline validation hint (amber) + a server-side save error (red), shown when present. */
@Composable
internal fun Hints(hint: String?, saveError: String?) {
    hint?.let {
        Spacer(Modifier.height(8.dp))
        Text(it, color = LocalTandemColors.current.warn, fontSize = 13.sp)
    }
    saveError?.let {
        Spacer(Modifier.height(8.dp))
        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
    }
}
