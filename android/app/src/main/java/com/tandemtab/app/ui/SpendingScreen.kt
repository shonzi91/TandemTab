package com.tandemtab.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.BarChart
import androidx.compose.material.icons.rounded.CalendarMonth
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.runtime.LaunchedEffect
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.TagsUi
import com.tandemtab.app.TripsUi
import com.tandemtab.app.data.BudgetRowDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.TagOptionDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

private enum class SpendView { Categories, ByDate, Trips }

/**
 * The Spending tab: a Categories view (each category as a spent-vs-budget progress-bar row, expandable to its
 * expenses — mirroring the web) and a By-date ledger. Figures resolved server-side (/spending + /budgets).
 */
@Composable
fun SpendingScreen(
    spending: SpendingUi,
    trips: TripsUi,
    tags: TagsUi,
    onRetry: () -> Unit,
    onEdit: (ExpenseDto) -> Unit,
    onDelete: (ExpenseDto) -> Unit,
    onSetBudget: (categoryId: String, amount: Double, onDone: () -> Unit) -> Unit,
    onRemoveBudget: (categoryId: String, onDone: () -> Unit) -> Unit,
    onAddCategory: (name: String, parentId: String?, icon: String?, onDone: (String?) -> Unit) -> Unit,
    onEditCategory: (id: String, name: String, icon: String?, onDone: () -> Unit) -> Unit,
    onArchiveCategory: (id: String, onDone: () -> Unit) -> Unit,
    onLoadTrips: () -> Unit,
    onSaveTrip: (tripId: String?, name: String, from: String, to: String, destination: String?, icon: String?,
                 savingCategoryId: String?, budget: Double?, categoryId: String?, onDone: () -> Unit) -> Unit,
    onDeleteTrip: (tripId: String, onDone: () -> Unit) -> Unit,
    onStartTrip: (tripId: String, started: Boolean) -> Unit,
    onFinishTrip: (tripId: String, finished: Boolean) -> Unit,
    onAttachExpenseToTrip: (expenseId: String, tripId: String?, onDone: () -> Unit) -> Unit,
    onOpenTrip: (tripId: String?) -> Unit,
    onPrepareTrip: () -> Unit,
    onLoadTags: () -> Unit,
    onPrepareTags: () -> Unit,
    onAddTag: (name: String, onDone: () -> Unit) -> Unit,
    onEditTag: (id: String, name: String, icon: String?, categoryId: String?, onDone: () -> Unit) -> Unit,
    onSetTagArchived: (id: String, archived: Boolean) -> Unit,
    onDeleteTag: (id: String, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val fmt = rememberMoney(spending.currency)
    var view by remember { mutableStateOf(SpendView.ByDate) }
    var showManage by remember { mutableStateOf(false) }
    var showTags by remember { mutableStateOf(false) }
    var menuOpen by remember { mutableStateOf(false) }

    // Trips are their own read (they span periods, so they don't ride along with /spending) — fetched the first
    // time the segment is opened rather than on every visit to the tab.
    LaunchedEffect(view) { if (view == SpendView.Trips) onLoadTrips() }

    when {
        spending.loading && spending.expenses.isEmpty() && spending.budgets.isEmpty() ->
            Box(Modifier.fillMaxWidth().height(220.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }

        spending.error != null ->
            Column(Modifier.fillMaxWidth().padding(top = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(spending.error, color = MaterialTheme.colorScheme.error)
                Spacer(Modifier.height(8.dp))
                Text("Tap to retry", color = tandem.positive, modifier = Modifier.clickable(onClick = onRetry).padding(8.dp))
            }

        else -> {
            // The Spending ⋯ overflow, mirroring the web's: one entry per manageable list rather than a row of
            // chips that grows every time another one lands.
            Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Spacer(Modifier.weight(1f))
                Box {
                    Row(
                        Modifier.clip(RoundedCornerShape(8.dp)).clickable { menuOpen = true }.padding(horizontal = 6.dp, vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(TandemIcons.Sliders, contentDescription = "More", tint = tandem.muted, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Manage", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = tandem.muted)
                    }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        DropdownMenuItem(
                            text = { Text("Manage categories") },
                            leadingIcon = { Icon(TandemIcons.Sliders, null, modifier = Modifier.size(18.dp)) },
                            onClick = { menuOpen = false; showManage = true },
                        )
                        DropdownMenuItem(
                            text = { Text("Manage tags") },
                            leadingIcon = { Icon(TandemIcons.Tag, null, modifier = Modifier.size(18.dp)) },
                            onClick = { menuOpen = false; onPrepareTags(); showTags = true },
                        )
                    }
                }
            }
            ViewToggle(view) { view = it }
            Spacer(Modifier.height(14.dp))
            when (view) {
                SpendView.Categories -> CategoriesView(spending, fmt, onEdit, onDelete, onSetBudget, onRemoveBudget)
                SpendView.ByDate -> ByDateView(spending, fmt, onEdit, onDelete)
                SpendView.Trips -> TripsView(
                    trips = trips,
                    categories = spending.categories,
                    periodExpenses = spending.expenses,
                    onRetry = { onLoadTrips() },
                    onSave = onSaveTrip,
                    onDelete = onDeleteTrip,
                    onStart = onStartTrip,
                    onFinish = onFinishTrip,
                    onAttachExpense = onAttachExpenseToTrip,
                    onOpen = onOpenTrip,
                    onPrepare = onPrepareTrip,
                )
            }
        }
    }

    if (showManage) {
        ManageCategoriesSheet(
            categories = spending.categories,
            saving = spending.saving,
            saveError = spending.saveError,
            onAdd = onAddCategory,
            onEdit = onEditCategory,
            onArchive = onArchiveCategory,
            onDismiss = { showManage = false },
        )
    }

    if (showTags) {
        ManageTagsSheet(
            tags = tags,
            onLoad = onLoadTags,
            onAdd = onAddTag,
            onEdit = onEditTag,
            onSetArchived = onSetTagArchived,
            onDelete = onDeleteTag,
            onDismiss = { showTags = false },
        )
    }
}

private data class SpendTab(val view: SpendView, val label: String, val icon: ImageVector)

@Composable
private fun ViewToggle(view: SpendView, onSelect: (SpendView) -> Unit) {
    val tandem = LocalTandemColors.current
    val tabs = listOf(
        SpendTab(SpendView.ByDate, "By date", TandemIcons.Calendar),
        SpendTab(SpendView.Categories, "By budgets", TandemIcons.Chart),
        SpendTab(SpendView.Trips, "Trips", TandemIcons.Plane),
    )
    Row(Modifier.fillMaxWidth().background(tandem.segmentTrack, RoundedCornerShape(12.dp)).padding(3.dp)) {
        tabs.forEach { tab ->
            val selected = view == tab.view
            val fg = if (selected) MaterialTheme.colorScheme.primary else tandem.muted
            Row(
                Modifier.weight(1f)
                    .background(if (selected) MaterialTheme.colorScheme.surface else androidx.compose.ui.graphics.Color.Transparent, RoundedCornerShape(9.dp))
                    .clickable { onSelect(tab.view) }
                    .padding(vertical = 9.dp),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(tab.icon, contentDescription = null, tint = fg, modifier = Modifier.height(18.dp).width(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(tab.label, color = fg, fontWeight = if (selected) FontWeight.Bold else FontWeight.SemiBold, fontSize = 14.sp)
            }
        }
    }
}

// --- Categories view --------------------------------------------------------------------------------

// A category being budgeted in the inline sheet: which one, its label, and its current cap (null = none yet).
private data class BudgetTarget(val categoryId: String, val name: String, val icon: String?, val current: Double?)

@Composable
private fun CategoriesView(
    spending: SpendingUi,
    fmt: (Double) -> String,
    onEdit: (ExpenseDto) -> Unit,
    onDelete: (ExpenseDto) -> Unit,
    onSetBudget: (String, Double, () -> Unit) -> Unit,
    onRemoveBudget: (String, () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val budgetedIds = remember(spending.budgets) { spending.budgets.map { it.categoryId }.toSet() }
    val catParent = remember(spending.categories) { spending.categories.associate { it.id to it.parentId } }
    var budgeting by remember { mutableStateOf<BudgetTarget?>(null) }

    fun expensesFor(catId: String): List<ExpenseDto> =
        spending.expenses.filter { it.categoryId == catId || catParent[it.categoryId] == catId }

    // How many rows one installment payment has. Counted across the WHOLE period, never the drawer's slice: a
    // web-logged installment puts principal and interest in different categories, so a per-category count would
    // report 1 and the confirm would understate what the delete is about to remove.
    val groupSize: (String) -> Int = { g -> spending.expenses.count { it.installmentGroupId == g } }

    // Top-level categories with spend but no budget row — the "other spending" list.
    val unbudgeted = remember(spending.expenses, spending.budgets, spending.categories) {
        spending.categories
            .filter { it.parentId == null && it.id !in budgetedIds }
            .mapNotNull { c ->
                val spent = expensesFor(c.id).sumOf { it.amount }
                if (spent > 0.0) Triple(c.id, c.name to c.icon, spent) else null
            }
            .sortedByDescending { it.third }
    }

    // Budget coverage summary (shared with the By-date view).
    SpendingSummary(spending, fmt)
    Spacer(Modifier.height(14.dp))

    if (spending.budgets.isEmpty() && unbudgeted.isEmpty()) {
        Box(Modifier.fillMaxWidth().height(140.dp), contentAlignment = Alignment.Center) {
            Text("No spending this period yet.", color = tandem.muted)
        }
        return
    }

    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        spending.budgets.sortedByDescending { it.spent }.forEach { b ->
            BudgetRow(
                b, fmt, expenses = { expensesFor(b.categoryId) }, groupSize = groupSize, onEdit = onEdit, onDelete = onDelete,
                onEditBudget = { budgeting = BudgetTarget(b.categoryId, b.name, b.icon, b.allocated) },
            )
        }
    }

    if (unbudgeted.isNotEmpty()) {
        Spacer(Modifier.height(18.dp))
        Text("OTHER SPENDING", fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = tandem.muted, modifier = Modifier.padding(start = 4.dp, bottom = 8.dp))
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            unbudgeted.forEach { (id, nameIcon, spent) ->
                UnbudgetedRow(
                    nameIcon.first, nameIcon.second, spent, fmt, expenses = { expensesFor(id) }, groupSize = groupSize, onEdit = onEdit, onDelete = onDelete,
                    onSetBudget = { budgeting = BudgetTarget(id, nameIcon.first, nameIcon.second, null) },
                )
            }
        }
    }

    budgeting?.let { target ->
        BudgetSheet(
            target = target,
            currency = spending.currency,
            saving = spending.saving,
            onSave = { amount -> onSetBudget(target.categoryId, amount) { budgeting = null } },
            onRemove = { onRemoveBudget(target.categoryId) { budgeting = null } },
            onDismiss = { budgeting = null },
        )
    }
}

/** Inline budget editor: one amount field, Save (upsert) and — when a cap already exists — Remove. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BudgetSheet(
    target: BudgetTarget,
    currency: String,
    saving: Boolean,
    onSave: (Double) -> Unit,
    onRemove: () -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var amountText by remember(target.categoryId) { mutableStateOf(target.current?.let { trimAmount(it) } ?: "") }
    val parsed = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(Modifier.fillMaxWidth().imePadding().padding(horizontal = 18.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (!target.icon.isNullOrBlank()) { Text(target.icon, fontSize = 20.sp); Spacer(Modifier.width(8.dp)) }
                Text(
                    if (target.current != null) "Edit budget · ${target.name}" else "Set budget · ${target.name}",
                    fontSize = 18.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface,
                )
            }
            OutlinedTextField(
                value = amountText,
                onValueChange = { amountText = it },
                label = { Text("Monthly budget") },
                prefix = { Text(currencySymbol(currency)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                modifier = Modifier.fillMaxWidth(),
            )
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                if (target.current != null) {
                    OutlinedButton(onClick = onRemove, enabled = !saving, modifier = Modifier.weight(1f)) {
                        Text("Remove", color = tandem.spent)
                    }
                }
                Button(onClick = { parsed?.let(onSave) }, enabled = !saving && parsed != null, modifier = Modifier.weight(1f)) {
                    if (saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    else Text(if (target.current != null) "Save" else "Set budget")
                }
            }
        }
    }
}

@Composable
private fun BudgetRow(
    b: BudgetRowDto,
    fmt: (Double) -> String,
    expenses: () -> List<ExpenseDto>,
    groupSize: (String) -> Int,
    onEdit: (ExpenseDto) -> Unit,
    onDelete: (ExpenseDto) -> Unit,
    onEditBudget: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var open by remember { mutableStateOf(false) }
    val fraction = if (b.allocated > 0) (b.spent / b.allocated).toFloat().coerceIn(0f, 1f) else if (b.spent > 0) 1f else 0f

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable { open = !open }
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            CatIcon(b.icon, b.name); Spacer(Modifier.width(8.dp))
            Text(b.name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
            Text("${fmt(b.spent)} / ${fmt(b.allocated)}", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
            Spacer(Modifier.width(6.dp))
            Box(Modifier.size(30.dp).clip(RoundedCornerShape(8.dp)).clickable(onClick = onEditBudget), contentAlignment = Alignment.Center) {
                Icon(TandemIcons.Pencil, contentDescription = "Edit budget", tint = tandem.muted, modifier = Modifier.size(17.dp))
            }
        }
        SpendBar(fraction)
        Text(
            if (b.over) "${fmt(-b.remaining)} over budget" else "${fmt(b.remaining)} left",
            fontSize = 12.sp, color = if (b.over) tandem.spent else tandem.muted,
        )
        ExpenseDrawer(open, expenses, fmt, groupSize, onEdit, onDelete)
    }
}

@Composable
private fun UnbudgetedRow(
    name: String,
    icon: String?,
    spent: Double,
    fmt: (Double) -> String,
    expenses: () -> List<ExpenseDto>,
    groupSize: (String) -> Int,
    onEdit: (ExpenseDto) -> Unit,
    onDelete: (ExpenseDto) -> Unit,
    onSetBudget: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var open by remember { mutableStateOf(false) }
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .clickable { open = !open }
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            CatIcon(icon, name); Spacer(Modifier.width(8.dp))
            Text(name, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
            Text(fmt(spent), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = tandem.spent)
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("No budget set", fontSize = 12.sp, color = tandem.muted, modifier = Modifier.weight(1f))
            Text(
                "Set budget",
                fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.clip(RoundedCornerShape(6.dp)).clickable(onClick = onSetBudget).padding(horizontal = 6.dp, vertical = 2.dp),
            )
        }
        ExpenseDrawer(open, expenses, fmt, groupSize, onEdit, onDelete)
    }
}

@Composable
private fun ExpenseDrawer(open: Boolean, expenses: () -> List<ExpenseDto>, fmt: (Double) -> String, groupSize: (String) -> Int, onEdit: (ExpenseDto) -> Unit, onDelete: (ExpenseDto) -> Unit) {
    val tandem = LocalTandemColors.current
    AnimatedVisibility(open) {
        Column(Modifier.padding(top = 4.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            val rows = expenses().sortedByDescending { it.date }
            if (rows.isEmpty()) {
                Text("No expenses yet.", fontSize = 12.sp, color = tandem.muted)
            } else rows.forEach { e ->
                Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(e.note?.takeIf { it.isNotBlank() } ?: e.categoryName, fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                        // An installment row says so, because otherwise two or three rows on one date read as two
                        // or three unexplained expenses rather than one loan payment.
                        val part = installmentPartLabel(e)
                        Text(
                            if (part != null) "${shortDate(e.date)} · ${e.fundName} · $part"
                            else "${shortDate(e.date)} · ${e.fundName}",
                            fontSize = 11.sp, color = tandem.muted, maxLines = 1,
                        )
                    }
                    Text(fmt(e.amount), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = tandem.spent)
                    EditExpenseButton(e, groupSize, onEdit, onDelete)
                }
            }
        }
    }
}

/** The part of a loan payment this row is, or null on an ordinary expense. The 🧾 marks it as one of several rows
 *  belonging to a single installment.
 *
 *  It deliberately doesn't try to name the loan: `ExpenseDto` carries `debtBucketId` but no debt *name*. In
 *  practice the name shows anyway, because the log sheet writes the bucket's name as each row's **note** and the
 *  note is what the sub-line leads with — so a row reads "Car loan · 🧾 Principal". A payment logged with a
 *  custom note will show that note instead, which is correct: the note is the user's words. */
internal fun installmentPartLabel(e: ExpenseDto): String? = when (e.installmentPart?.lowercase()) {
    "principal" -> "🧾 Principal"
    "interest" -> "🧾 Interest"
    "additional" -> "🧾 Extra"
    else -> if (e.installmentGroupId != null) "🧾 Loan payment" else null
}

/** Inline edit + delete actions on an expense row. Auto-filed / from-savings rows aren't hand-editable (matches the
 *  web), so they reserve the same-width blank slot — keeping the icons (and amounts to their left) lined up across
 *  every row. Delete always asks to confirm.
 *
 *  ⚠️ An installment row is never deleted alone. Its rows are one payment and the server removes them as a unit
 *  (`DELETE /installments/{groupId}`), because dropping the principal while keeping the interest leaves a payment
 *  that reconciles to nothing. So the trash on such a row raises the *group* confirm and calls [onDelete] with a
 *  row that still carries its `installmentGroupId` — the caller routes on that, exactly as the web does. */
@Composable
private fun EditExpenseButton(e: ExpenseDto, groupSize: (String) -> Int, onEdit: (ExpenseDto) -> Unit, onDelete: (ExpenseDto) -> Unit) {
    val tandem = LocalTandemColors.current
    var confirmDelete by remember { mutableStateOf(false) }
    Spacer(Modifier.width(6.dp))
    if (e.autoFiled || e.fromSavings) {
        Spacer(Modifier.width(66.dp))   // reserve pencil + trash width so nothing shifts
    } else {
        Box(
            Modifier.size(30.dp).clip(RoundedCornerShape(8.dp)).clickable { onEdit(e) },
            contentAlignment = Alignment.Center,
        ) {
            Icon(TandemIcons.Pencil, contentDescription = "Edit expense", tint = tandem.muted, modifier = Modifier.size(17.dp))
        }
        Spacer(Modifier.width(6.dp))
        Box(
            Modifier.size(30.dp).clip(RoundedCornerShape(8.dp)).clickable { confirmDelete = true },
            contentAlignment = Alignment.Center,
        ) {
            Icon(TandemIcons.Trash, contentDescription = "Delete expense", tint = tandem.spent, modifier = Modifier.size(17.dp))
        }
    }
    if (confirmDelete) {
        val group = e.installmentGroupId
        val rows = group?.let { groupSize(it) } ?: 0
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text(if (group != null) "Remove this installment?" else "Delete expense?") },
            text = {
                Text(
                    if (group != null)
                        "This is part of one loan payment, so all ${maxOf(rows, 1)} of its rows go together — and " +
                            "a payment-driven loan gets its principal back."
                    else "This removes “${e.note?.takeIf { it.isNotBlank() } ?: e.categoryName}” from this period.",
                )
            },
            confirmButton = {
                TextButton(onClick = { confirmDelete = false; onDelete(e) }) {
                    Text(if (group != null) "Remove" else "Delete", color = tandem.spent)
                }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

// The web budget-bar gradient (.cbar-grad): a single mint→amber→coral ramp spanning the FULL track, revealed as the
// bar fills — so the colour at the fill's leading edge encodes how close to (or over) budget the category is.
private val budgetStops = arrayOf(
    0.0f to Color(0xFF2FB99A), 0.68f to Color(0xFF2FB99A),
    0.88f to Color(0xFFFFAB73), 1.0f to Color(0xFFFF7A59),
)

@Composable
private fun SpendBar(fraction: Float) {
    val tandem = LocalTandemColors.current
    val f = fraction.coerceIn(0f, 1f)
    Canvas(Modifier.fillMaxWidth().height(8.dp)) {
        val w = size.width; val h = size.height; val r = CornerRadius(h / 2f, h / 2f)
        drawRoundRect(color = tandem.segmentTrack, cornerRadius = r)
        if (f > 0f) {
            // endX = full track width anchors the gradient to the whole bar; the fill reveals only its left portion.
            val brush = Brush.horizontalGradient(colorStops = budgetStops, startX = 0f, endX = w)
            drawRoundRect(brush = brush, size = Size(w * f, h), cornerRadius = r)
        }
    }
}

/** The period summary card shown atop both Spending views: budget-used-with-progress when budgets exist, else spent. */
@Composable
private fun SpendingSummary(spending: SpendingUi, fmt: (Double) -> String) {
    if (spending.budgets.isNotEmpty()) {
        SummaryHeader(
            "BUDGET USED", fmt(spending.totalSpent), " of ${fmt(spending.totalBudgeted)}",
            fraction = if (spending.totalBudgeted > 0) (spending.totalSpent / spending.totalBudgeted).toFloat() else 0f,
        )
    } else {
        SpentHeader(fmt(spending.spent))
    }
}

@Composable
private fun SummaryHeader(label: String, value: String, suffix: String, fraction: Float) {
    val tandem = LocalTandemColors.current
    val over = fraction >= 1f
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(label, fontSize = 10.sp, letterSpacing = 1.3.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Row(verticalAlignment = Alignment.Bottom) {
            Text(value, fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = tandem.spent)
            Text(suffix, fontSize = 14.sp, color = tandem.muted, modifier = Modifier.padding(bottom = 3.dp))
        }
        SpendBar(fraction.coerceIn(0f, 1f))
    }
}

// --- By-date view -----------------------------------------------------------------------------------

@Composable
private fun ByDateView(spending: SpendingUi, fmt: (Double) -> String, onEdit: (ExpenseDto) -> Unit, onDelete: (ExpenseDto) -> Unit) {
    val tandem = LocalTandemColors.current
    val groupSize: (String) -> Int = { g -> spending.expenses.count { it.installmentGroupId == g } }
    val byId = remember(spending.tags) { spending.tags.associateBy { it.id } }
    val tagOf: (ExpenseDto) -> TagOptionDto? = { e -> e.tagIds.firstOrNull()?.let { byId[it] } }
    // By date is a ledger — its summary is "spent this period"; the budget-used progress lives in the Categories view.
    SpentHeader(fmt(spending.spent))
    Spacer(Modifier.height(16.dp))
    if (spending.expenses.isEmpty()) {
        Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
            Text("No expenses this period yet.", color = tandem.muted)
        }
        return
    }
    val byDay = spending.expenses.sortedByDescending { it.date }.groupBy { it.date }
    byDay.forEach { (day, rows) ->
        DayHeader(day)
        Column(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp))
                .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp)),
        ) {
            rows.forEachIndexed { i, e ->
                ExpenseRow(e, fmt, groupSize, tagOf, onEdit, onDelete)
                if (i < rows.lastIndex) {
                    Box(Modifier.fillMaxWidth().height(1.dp).padding(horizontal = 14.dp).background(tandem.hairline))
                }
            }
        }
        Spacer(Modifier.height(14.dp))
    }
}

@Composable
private fun SpentHeader(spent: String) {
    val tandem = LocalTandemColors.current
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(18.dp),
    ) {
        Text("SPENT THIS PERIOD", fontSize = 10.sp, letterSpacing = 1.3.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Spacer(Modifier.height(4.dp))
        Text(spent, fontSize = 26.sp, fontWeight = FontWeight.ExtraBold, color = tandem.spent)
    }
}

@Composable
private fun DayHeader(iso: String) {
    val tandem = LocalTandemColors.current
    Text(formatDay(iso), fontSize = 11.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.8.sp, color = tandem.muted, modifier = Modifier.padding(start = 4.dp, bottom = 6.dp))
}

@Composable
private fun ExpenseRow(
    e: ExpenseDto,
    fmt: (Double) -> String,
    groupSize: (String) -> Int,
    tagOf: (ExpenseDto) -> TagOptionDto?,
    onEdit: (ExpenseDto) -> Unit,
    onDelete: (ExpenseDto) -> Unit,
) {
    val tandem = LocalTandemColors.current
    Row(modifier = Modifier.fillMaxWidth().padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
        CatIcon(e.categoryIcon, e.categoryName)
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(e.categoryName, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
                // The label sits beside the category, not under it: a tag cuts ACROSS categories, so it reads as a
                // second axis rather than as a detail of the first. Resolves to nothing for a deleted tag, whose id
                // survives on the row — a hard delete leaves the expense pointing at something that is gone.
                tagOf(e)?.let { t ->
                    Spacer(Modifier.width(6.dp))
                    Row(
                        Modifier.clip(RoundedCornerShape(6.dp)).background(tandem.savingsTileBg).padding(horizontal = 6.dp, vertical = 1.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(t.name, fontSize = 10.sp, fontWeight = FontWeight.Medium, color = tandem.muted, maxLines = 1)
                    }
                }
            }
            val sub = e.note?.takeIf { it.isNotBlank() } ?: e.fundName
            // An installment row names the part it is, so two rows on one date read as one loan payment.
            val part = installmentPartLabel(e)
            Text(if (part != null) "$sub · $part" else sub, fontSize = 12.sp, color = tandem.muted, maxLines = 1)
        }
        Spacer(Modifier.width(8.dp))
        Column(horizontalAlignment = Alignment.End) {
            Text(fmt(e.amount), fontWeight = FontWeight.Bold, color = tandem.spent)
            if (e.autoFiled || e.fromSavings) {
                Text(if (e.fromSavings) "from savings" else "auto", fontSize = 10.sp, color = tandem.muted)
            }
        }
        EditExpenseButton(e, groupSize, onEdit, onDelete)
    }
}

private fun shortDate(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("d MMM", Locale.getDefault()))
}.getOrDefault(iso)

internal fun formatDay(iso: String): String = runCatching {
    val d = LocalDate.parse(iso)
    val today = LocalDate.now()
    when (d) {
        today -> "TODAY"
        today.minusDays(1) -> "YESTERDAY"
        else -> d.format(DateTimeFormatter.ofPattern("EEE, d MMM", Locale.getDefault())).uppercase()
    }
}.getOrDefault(iso)

private fun rememberMoney(currencyCode: String): (Double) -> String {
    val nf = java.text.NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = java.util.Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}
