package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.RecurringUi
import com.tandemtab.app.data.AddRecurringRequest
import com.tandemtab.app.data.RecurringRowDto
import com.tandemtab.app.data.UpdateRecurringRequest
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * The list in two sections (O5), the same split the web list uses: what is still expected this period, soonest
 * first, and what is behind you, most recent first. It used to be one flat sorted list here (and no ordering at all
 * on the web) — sorting answers "which is next", sectioning answers "is there anything left", which is the question
 * people actually open this sheet with.
 *
 * An overdue item is pending with a negative `daysUntilDue`, so it leads "Coming up" — where something missed
 * belongs. A **paused** item is not pending and sits below; its row already says so, and it is not coming.
 *
 * ⚠️ The lower section holds THREE populations, not one, and the heading is only literally true of the first:
 * handled, paused, and **not yet started** (`startsLater` — added after this month's day had passed). The design
 * that settles this is "loose heading, precise row": each of the other two names itself in `stateLine`, which is
 * why neither may be filed here silently. The third had no marker until this was written, so a bill that has never
 * been paid rendered identically to one that had.
 */
private fun sections(items: List<RecurringRowDto>): Pair<List<RecurringRowDto>, List<RecurringRowDto>> {
    val coming = items.filter { it.pending }.sortedWith(compareBy({ it.daysUntilDue }, { it.name.lowercase() }))
    // Most recent first: a due date further in the past is a *more* negative gap, so descending is newest-first.
    val past = items.filterNot { it.pending }.sortedWith(compareByDescending<RecurringRowDto> { it.daysUntilDue }.thenBy { it.name.lowercase() })
    return coming to past
}

/** The three ways a recurring amount can be stated, as the wire strings the server maps to its enums. */
private val MODES = listOf(
    Triple("fixed", "Fixed", "The same amount every month."),
    Triple("typical", "Typical", "An estimate that self-tunes toward what you actually pay."),
    Triple("reminder", "Reminder only", "No amount — you'll enter the real figure each time it's due (good for a variable salary)."),
)

/** The Home "Bills & income" card — a compact due/upcoming summary that opens the full sheet. Hidden when there
 *  are no recurring items at all. */
@Composable
fun RecurringCard(recurring: RecurringUi, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    if (!recurring.loaded || recurring.items.isEmpty()) return
    val active = recurring.items.filter { it.active }
    val due = active.count { it.due }
    val upcoming = active.count { it.upcoming && !it.due }

    Column(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .clickable(onClick = onOpen)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("BILLS & INCOME", fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = tandem.muted, modifier = Modifier.weight(1f))
            Text("Manage ›", color = MaterialTheme.colorScheme.primary, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
        }
        val summary = when {
            due > 0 -> "$due due now" + (if (upcoming > 0) " · $upcoming upcoming" else "")
            upcoming > 0 -> "$upcoming upcoming"
            else -> "All bills handled for now"
        }
        Text(summary, fontWeight = FontWeight.Bold, color = if (due > 0) tandem.spent else MaterialTheme.colorScheme.onSurface, fontSize = 16.sp)
    }
}

/** What the sheet is showing: the list, or the editor on a new item / an existing one. Kept *inside* the one
 *  ModalBottomSheet rather than stacking a second sheet on top — Compose only reliably drives one at a time, and
 *  the web does the same thing (Modal.Recurring → Modal.RecurringEdit). */
private sealed interface RecurringMode {
    data object List : RecurringMode

    /** Editing carries the item's **id**, not the row: pausing from inside the editor refreshes the list, and a
     *  captured row would keep saying "Pause" after the item was already paused. The live row is looked up each
     *  recomposition; a null id is a new item. */
    data class Edit(val id: String?) : RecurringMode
}

/**
 * The Recurring sheet: every bill/income expectation with its due state. Due items can be confirmed or skipped
 * inline; tapping any row opens the editor, where it can also be paused or removed. "Add" declares a new one.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RecurringSheet(
    recurring: RecurringUi,
    onConfirm: (id: String, amount: Double) -> Unit,
    onSkip: (id: String) -> Unit,
    onUnskip: (id: String) -> Unit,
    onAdd: (AddRecurringRequest, onDone: () -> Unit) -> Unit,
    onUpdate: (id: String, UpdateRecurringRequest, onDone: () -> Unit) -> Unit,
    onSetActive: (id: String, active: Boolean) -> Unit,
    onDelete: (id: String, onDone: () -> Unit) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val money = moneyFormatter(recurring.currency)
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var mode by remember { mutableStateOf<RecurringMode>(RecurringMode.List) }
    var deleting by remember { mutableStateOf<RecurringRowDto?>(null) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        when (val m = mode) {
            is RecurringMode.List -> Column(
                Modifier
                    .fillMaxWidth()
                    .imePadding()
                    .padding(horizontal = 18.dp)
                    .verticalScroll(rememberScrollState())
                    .padding(bottom = 28.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("Bills & income", modifier = Modifier.weight(1f), fontSize = 18.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
                    IconButton(onClick = onDismiss) { Icon(TandemIcons.Close, "Close", tint = tandem.muted) }
                }
                if (recurring.billsDue > 0) {
                    Text("${money(recurring.billsDue)} in bills still expected this period.", color = tandem.muted, fontSize = 13.sp)
                }
                recurring.actionError?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp) }

                AddRecurringButton { mode = RecurringMode.Edit(id = null) }

                if (recurring.items.isEmpty()) {
                    Text(
                        "Nothing recurring yet. Add your rent, salary or a monthly bill — they remind you when due, " +
                            "and you confirm the real amount so bills that vary stay accurate.",
                        color = tandem.muted, fontSize = 13.sp,
                    )
                }
                val (coming, past) = sections(recurring.items)
                if (coming.isNotEmpty()) {
                    RecurringSectionHeading("Coming up", coming.size, tandem.muted)
                    coming.forEach { item ->
                        RecurringRow(
                            item, money,
                            busy = recurring.busyId == item.id,
                            onConfirm = { onConfirm(item.id, item.expected) },
                            onSkip = { onSkip(item.id) },
                            onUnskip = { onUnskip(item.id) },
                            onEdit = { mode = RecurringMode.Edit(item.id) },
                        )
                    }
                }
                if (past.isNotEmpty()) {
                    RecurringSectionHeading("Already this period", past.size, tandem.muted)
                    past.forEach { item ->
                        RecurringRow(
                            item, money,
                            busy = recurring.busyId == item.id,
                            onConfirm = { onConfirm(item.id, item.expected) },
                            onSkip = { onSkip(item.id) },
                            onUnskip = { onUnskip(item.id) },
                            onEdit = { mode = RecurringMode.Edit(item.id) },
                        )
                    }
                }
            }

            is RecurringMode.Edit -> {
                val row = m.id?.let { id -> recurring.items.firstOrNull { it.id == id } }
                // A removed item's row is gone the moment the list refreshes, a beat before the confirm dialog
                // hands us back to the list — render nothing rather than an empty "new item" form.
                if (m.id != null && row == null) Spacer(Modifier.height(1.dp))
                else RecurringEditor(
                    existing = row,
                    recurring = recurring,
                    onCancel = { mode = RecurringMode.List },
                    onAdd = { req -> onAdd(req) { mode = RecurringMode.List } },
                    onUpdate = { id, req -> onUpdate(id, req) { mode = RecurringMode.List } },
                    onSetActive = onSetActive,
                    onDelete = { deleting = row },
                )
            }
        }
    }

    deleting?.let { item ->
        AlertDialog(
            onDismissRequest = { if (!recurring.saving) deleting = null },
            title = { Text("Remove ${item.name}?") },
            text = {
                Column {
                    Text(
                        "It stops recurring and disappears from this list. Anything it has already posted stays — " +
                            "your history doesn't change.",
                    )
                    recurring.saveError?.let {
                        Spacer(Modifier.height(10.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = { onDelete(item.id) { deleting = null; mode = RecurringMode.List } },
                    enabled = !recurring.saving,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                ) {
                    if (recurring.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                    else Text("Remove")
                }
            },
            dismissButton = { TextButton(onClick = { deleting = null }, enabled = !recurring.saving) { Text("Cancel") } },
        )
    }
}

/** The add/edit form, rendered inside the Recurring sheet with its own pinned Cancel / Save bar. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun RecurringEditor(
    existing: RecurringRowDto?,
    recurring: RecurringUi,
    onCancel: () -> Unit,
    onAdd: (AddRecurringRequest) -> Unit,
    onUpdate: (id: String, UpdateRecurringRequest) -> Unit,
    onSetActive: (id: String, active: Boolean) -> Unit,
    onDelete: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    // A manual bill can't be paid out of a bank-synced fund (its balance mirrors the bank), so those aren't offered
    // — the same filter the web's SelectableFunds applies.
    val funds = remember(recurring.funds) { recurring.funds.filter { !it.synced } }

    // Every field is seeded once per item — keyed on the *id*, not the row, so a refresh (a pause, say) doesn't
    // reseed the form and throw away what's being typed.
    val key = existing?.id
    // Kind is fixed once created: the server refuses to change it, and the category picker hangs off it.
    var kind by remember(key) { mutableStateOf(existing?.kind ?: "expense") }
    var name by remember(key) { mutableStateOf(existing?.name.orEmpty()) }
    var icon by remember(key) { mutableStateOf(existing?.icon.orEmpty()) }
    var mode by remember(key) { mutableStateOf(existing?.mode ?: "fixed") }
    var amount by remember(key) { mutableStateOf(existing?.expected?.takeIf { it > 0 }?.let { trimAmount(it) }.orEmpty()) }
    var day by remember(key) { mutableStateOf((existing?.dayOfMonth ?: 1).coerceIn(1, 28).toString()) }
    var autoPost by remember(key) { mutableStateOf(existing?.autoPost ?: false) }
    var debtId by remember(key) { mutableStateOf(existing?.linkedDebtBucketId) }
    var hint by remember { mutableStateOf<String?>(null) }

    val cats = if (kind == "income") recurring.contributionCategories else recurring.categories
    // The picked category has to follow the kind — a spend category is not a valid income source.
    var categoryId by remember(key) { mutableStateOf(existing?.categoryId) }
    if (categoryId != null && cats.none { it.id == categoryId }) categoryId = null
    var fundId by remember(key) { mutableStateOf(existing?.fundId ?: funds.firstOrNull()?.id) }

    val parsedAmount = amount.replace(',', '.').trim().toDoubleOrNull() ?: 0.0
    val parsedDay = day.trim().toIntOrNull() ?: 0
    val needsAmount = mode != "reminder"
    val canSave = name.isNotBlank() && categoryId != null && fundId != null &&
        parsedDay in 1..28 && (!needsAmount || parsedAmount > 0) && !recurring.saving

    fun save() {
        val cat = categoryId ?: return
        val fund = fundId ?: return
        val expected = if (needsAmount) parsedAmount else 0.0
        // Only an expense can service a loan, and auto-post is a fixed-amount idea (the server enforces both, but
        // sending a stale value would show as a field the user never set).
        val debt = debtId.takeIf { kind == "expense" }
        val auto = autoPost && mode == "fixed"
        if (existing == null) {
            onAdd(AddRecurringRequest(name.trim(), kind, mode, expected, parsedDay, cat, fund, icon.ifBlank { null }, auto, debt))
        } else {
            onUpdate(existing.id, UpdateRecurringRequest(name.trim(), mode, expected, parsedDay, cat, fund, icon.ifBlank { null }, auto, debt))
        }
    }

    Box(Modifier.fillMaxWidth().fillMaxHeight()) {
        Column(
            Modifier
                .fillMaxWidth()
                .imePadding()
                .padding(horizontal = 18.dp)
                .verticalScroll(rememberScrollState())
                // More clearance than the shared scaffold's 92dp: the last thing here is a one-line hint, and on the
                // shorter forms (income has no loan section) it ends exactly where the floating bar starts.
                .padding(bottom = 116.dp),
        ) {
            Text(
                if (existing == null) "New bill or income" else "Edit ${existing.name}",
                modifier = Modifier.fillMaxWidth().padding(top = 4.dp, bottom = 12.dp),
                fontSize = 19.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface,
            )

            if (existing == null) {
                FieldLabel("What is it")
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    PickChip("Bill", "🧾", kind == "expense") { kind = "expense"; categoryId = null; debtId = null }
                    PickChip("Income", "💰", kind == "income") { kind = "income"; categoryId = null; debtId = null }
                }
                Spacer(Modifier.height(14.dp))
            }

            OutlinedTextField(
                value = name,
                onValueChange = { name = it; hint = null },
                label = { Text("Name") },
                placeholder = { Text(if (kind == "income") "Salary" else "Rent") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(10.dp))
            OutlinedTextField(
                value = icon,
                onValueChange = { icon = it },
                label = { Text("Icon (optional)") },
                placeholder = { Text("e.g. 🏠") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )

            Spacer(Modifier.height(14.dp))
            FieldLabel(if (kind == "income") "Source" else "Category")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                cats.forEach { c ->
                    PickChip(c.name, c.icon, categoryId == c.id, catName = c.name) { categoryId = c.id; hint = null }
                }
            }

            Spacer(Modifier.height(14.dp))
            FieldLabel(if (kind == "income") "Paid into" else "Paid from")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                funds.forEach { f -> PickChip(f.name, null, fundId == f.id) { fundId = f.id } }
            }

            Spacer(Modifier.height(14.dp))
            FieldLabel("Amount")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                MODES.forEach { (wire, label, _) -> PickChip(label, null, mode == wire) { mode = wire; hint = null } }
            }
            Spacer(Modifier.height(10.dp))
            if (needsAmount) {
                OutlinedTextField(
                    value = amount,
                    onValueChange = { amount = it; hint = null },
                    label = { Text(if (mode == "typical") "Typical amount" else "Amount") },
                    prefix = { Text(currencySymbol(recurring.currency)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(6.dp))
            }
            Text(MODES.first { it.first == mode }.third, color = tandem.muted, fontSize = 12.sp)

            Spacer(Modifier.height(14.dp))
            OutlinedTextField(
                value = day,
                onValueChange = { day = it; hint = null },
                label = { Text("Day of month") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(6.dp))
            Text("1–28, so it lands in every month.", color = tandem.muted, fontSize = 12.sp)

            if (mode == "fixed") {
                Spacer(Modifier.height(8.dp))
                CheckRow("Post automatically when due (don't ask me)", autoPost) { autoPost = it }
            }

            // A bill can service a loan. Only for expenses, and only when there's a debt to point at.
            if (kind == "expense" && recurring.debts.isNotEmpty()) {
                Spacer(Modifier.height(14.dp))
                FieldLabel("This is a loan installment for")
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    PickChip("Not a loan payment", null, debtId == null) { debtId = null }
                    recurring.debts.forEach { d -> PickChip(d.name, "🧾", debtId == d.id) { debtId = d.id } }
                }
                debtId?.let { picked ->
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "Each payment will be split into interest and principal rows against that loan, instead of " +
                            "one lump expense.",
                        color = tandem.muted, fontSize = 12.sp,
                    )
                    // Not flipped for them: it changes how the balance is derived, which is the user's call.
                    if (recurring.debts.firstOrNull { it.id == picked }?.paymentDriven == false) {
                        Spacer(Modifier.height(6.dp))
                        Text(
                            "That loan still follows its own schedule. To have logged payments drive its balance " +
                                "instead, turn on “I log each installment here” when you edit it.",
                            color = tandem.muted, fontSize = 12.sp,
                        )
                    }
                }
            }

            // Pause / remove live at the foot, well below Save — they're the exits, not the point of the form.
            existing?.let { item ->
                Spacer(Modifier.height(22.dp))
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    TextButton(onClick = { onSetActive(item.id, !item.active) }, enabled = !recurring.saving) {
                        Text(if (item.active) "Pause" else "Resume", color = tandem.muted)
                    }
                    TextButton(onClick = onDelete, enabled = !recurring.saving) {
                        Text("Remove", color = MaterialTheme.colorScheme.error)
                    }
                }
                Text(
                    if (item.active) "Paused items stay in the list but never fall due."
                    else "This one is paused — it won't fall due until you resume it.",
                    color = tandem.muted, fontSize = 12.sp,
                )
            }

            Hints(
                hint ?: when {
                    name.isBlank() -> null
                    categoryId == null -> "Pick a ${if (kind == "income") "source" else "category"}."
                    fundId == null -> "Pick a fund."
                    parsedDay !in 1..28 -> "Day of month has to be between 1 and 28."
                    needsAmount && parsedAmount <= 0 -> "Enter how much it is, or make it a reminder."
                    else -> null
                },
                recurring.saveError,
            )
        }
        SheetActionBar(
            saving = recurring.saving,
            canSave = canSave,
            onDismiss = onCancel,
            onSave = ::save,
            modifier = Modifier.align(Alignment.BottomCenter),
            saveLabel = if (existing == null) "Add" else "Save",
        )
    }
}

/** A section heading over one half of the split list (O5) — the label and how many rows are under it, so the count
 *  answers "is there anything left this month" without reading the rows. Mirrors the web's `.detail-sub`. */
@Composable
private fun RecurringSectionHeading(label: String, count: Int, muted: androidx.compose.ui.graphics.Color) {
    Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, fontSize = 13.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
        Spacer(Modifier.width(6.dp))
        Text("· $count", fontSize = 12.sp, color = muted)
    }
}

/** The list's "declare a new one" affordance, styled like the other add rows in the app. */
@Composable
private fun AddRecurringButton(onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier
            .fillMaxWidth()
            .border(1.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(12.dp))
            .clickable(onClick = onClick)
            .padding(vertical = 12.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("+", color = MaterialTheme.colorScheme.primary, fontSize = 16.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.width(8.dp))
        Text("Add a bill or income", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun RecurringRow(
    item: RecurringRowDto,
    money: (Double) -> String,
    busy: Boolean,
    onConfirm: () -> Unit,
    onSkip: () -> Unit,
    onUnskip: () -> Unit,
    onEdit: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val income = item.kind == "income"
    val amountColor = if (income) tandem.positive else tandem.spent
    Column(
        Modifier
            .fillMaxWidth()
            .background(tandem.hero, RoundedCornerShape(12.dp))
            .border(1.dp, if (item.active && item.due) amountColor.copy(alpha = 0.5f) else tandem.hairline, RoundedCornerShape(12.dp))
            .clickable(onClick = onEdit)
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!item.icon.isNullOrBlank()) { Text(item.icon, fontSize = 16.sp); Spacer(Modifier.width(8.dp)) }
            Column(Modifier.weight(1f)) {
                Text(item.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                Text(stateLine(item), fontSize = 12.sp, color = if (item.active && item.due) amountColor else tandem.muted, maxLines = 1)
            }
            Spacer(Modifier.width(8.dp))
            if (item.hasKnownAmount) {
                Text((if (income) "+" else "") + money(item.expected), fontWeight = FontWeight.Bold, color = amountColor)
            }
            Spacer(Modifier.width(8.dp))
            OpenChip()
        }
        // Actions only for a due, active item.
        if (item.active && item.due) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                if (busy) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.primary)
                } else {
                    // "Skip this month", not "Skip": it declares the bill unpaid for the period, which drops it from
                    // "still due" and raises safe-to-spend. The scope belongs in the label.
                    TextButton(onClick = onSkip) { Text("Skip this month", color = tandem.muted, fontWeight = FontWeight.SemiBold) }
                    if (item.hasKnownAmount) {
                        TextButton(onClick = onConfirm) { Text("Confirm", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold) }
                    } else {
                        TextButton(onClick = onSkip) { Text("Mark handled", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold) }
                    }
                }
            }
        } else if (item.active && item.skippedThisPeriod) {
            // Skipped, and it stays visible with a way back — the figure it changed (safe-to-spend after bills) is
            // not one the app should move and then forget it moved.
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                if (busy) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.primary)
                } else {
                    Text("Skipped this month", fontSize = 12.sp, color = tandem.muted, modifier = Modifier.weight(1f))
                    TextButton(onClick = onUnskip) { Text("Undo", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold) }
                }
            }
        } else if (busy) {
            // A pause/resume has no inline buttons to swap for a spinner, so it gets its own row.
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.primary)
            }
        }
    }
}

private fun stateLine(item: RecurringRowDto): String {
    val what = if (item.kind == "income") "income" else "bill"
    val linked = item.linkedDebtName?.let { " · 🧾 $it" } ?: ""
    return when {
        !item.active -> "Paused"
        // Sits under "Already this period" because it is not pending — so the row has to be the thing that says it
        // never happened, exactly as a paused one does. Without this it reads "Day N · bill", the same as a bill
        // that really was paid this month.
        item.startsLater -> "Starts next period · day ${item.dayOfMonth}"
        item.due -> "Due now · $what$linked"
        item.upcoming -> if (item.daysUntilDue <= 0) "Due today" else "In ${item.daysUntilDue} day${if (item.daysUntilDue == 1) "" else "s"}"
        item.dayOfMonth > 0 -> "Day ${item.dayOfMonth} · $what$linked"
        else -> what.replaceFirstChar { it.uppercase() } + linked
    }
}
