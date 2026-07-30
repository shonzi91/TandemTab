package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.KeyboardArrowUp
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DatePicker
import androidx.compose.material3.Button
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.AddExpenseRequest
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.DepositRowDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.FundOptionDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale

/** One parked expense in the staged multi-add batch (S68). Keeps only what a POST needs. */
private data class ExpenseDraft(
    val categoryId: String,
    val fundId: String,
    val amount: Double,
    val note: String,
    val date: String,
)

private enum class AddMode { Expense, Income }

/**
 * The unified add sheet — one front door for both money-out and money-in, opened by the centre FAB on every tab.
 * A segmented **Expense / Income** toggle switches the editor. Expense is the S68 rework (amount · searchable
 * category with "most-used" chips · fund chips · date · note · staged multi-add). Income is a single deposit
 * (amount · source · fund · date). Pickers come from /spending (+ /income for the source chips).
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun AddSheet(
    spending: SpendingUi,
    startWithIncome: Boolean = false,
    editing: ExpenseDto? = null,
    editingDeposit: DepositRowDto? = null,
    onEditLast: (() -> Unit)? = null,
    onEditLastIncome: (() -> Unit)? = null,
    onDismiss: () -> Unit,
    onSaveExpenses: (List<AddExpenseRequest>, onDone: () -> Unit) -> Unit,
    onEditExpense: (String, AddExpenseRequest, onDone: () -> Unit) -> Unit,
    onAddIncome: (fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
    onEditDeposit: (depositId: String, fundId: String, categoryId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit,
    onAddCategory: (name: String, parentId: String?, icon: String?, onDone: (String?) -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = sheetMoney(spending.currency)
    val editingMode = editing != null
    val incomeEdit = editingDeposit != null

    val cats = spending.categories
    val funds = spending.funds
    val catById = remember(cats) { cats.associateBy { it.id } }
    val fundById = remember(funds) { funds.associateBy { it.id } }

    // Editing an expense forces Expense; editing a deposit forces Income; otherwise honour the requested start tab.
    var mode by remember(editing, editingDeposit, startWithIncome) {
        mutableStateOf(when {
            editingDeposit != null -> AddMode.Income
            editing == null && startWithIncome -> AddMode.Income
            else -> AddMode.Expense
        })
    }
    // Income editor state (pre-filled when editing a deposit).
    var incAmountText by remember(editingDeposit) { mutableStateOf(editingDeposit?.let { trimAmount(it.amount) } ?: "") }
    var incSource by remember(editingDeposit) { mutableStateOf(editingDeposit?.categoryId ?: GENERAL_INCOME) }
    var incFundId by remember(spending.loaded, editingDeposit) { mutableStateOf(editingDeposit?.fundId ?: funds.firstOrNull { !it.synced }?.id ?: funds.firstOrNull()?.id) }
    val incParsed = incAmountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    // Most-used categories (distinct, newest-first from the recent history), capped — the chips above the picker.
    val recentCats = remember(spending.recent, cats) {
        spending.recent.map { it.categoryId }.distinct().filter { catById.containsKey(it) }.take(6)
    }

    fun defaultFundFor(catId: String?): String? =
        spending.recent.firstOrNull { it.categoryId == catId && fundById.containsKey(it.fundId) }?.fundId
            ?: funds.firstOrNull { !it.synced }?.id
            ?: funds.firstOrNull()?.id

    val today = remember { LocalDate.now().toString() }

    // Editor state — pre-filled from the row being edited, else keyed to `loaded` so it initialises once the
    // pickers arrive and then stays put.
    var categoryId by remember(spending.loaded, editing) { mutableStateOf(editing?.categoryId ?: cats.firstOrNull()?.id) }
    var fundId by remember(spending.loaded, editing) { mutableStateOf(editing?.fundId ?: defaultFundFor(cats.firstOrNull()?.id)) }
    var amountText by remember(editing) { mutableStateOf(editing?.let { trimAmount(it.amount) } ?: "") }
    var note by remember(editing) { mutableStateOf(editing?.note ?: "") }
    var date by remember(spending.loaded, editing, editingDeposit) { mutableStateOf(editing?.date ?: editingDeposit?.date ?: today) }
    var staged by remember { mutableStateOf(listOf<ExpenseDraft>()) }
    var catExpanded by remember { mutableStateOf(false) }
    var catSearch by remember { mutableStateOf("") }
    var newCatName by remember { mutableStateOf("") }   // inline "new category" name (empty = the +New row is collapsed)
    var creatingCat by remember { mutableStateOf(false) }
    var hint by remember { mutableStateOf<String?>(null) }

    val parsed = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }
    val currentValid = parsed != null && categoryId != null && fundId != null
    fun currentDraft(): ExpenseDraft? =
        if (currentValid) ExpenseDraft(categoryId!!, fundId!!, parsed!!, note.trim(), date) else null

    val pendingCount = staged.size + (if (currentValid) 1 else 0)
    val pendingTotal = staged.sumOf { it.amount } + (if (currentValid) parsed!! else 0.0)

    fun stageCurrent(): Boolean {
        val d = currentDraft() ?: run {
            hint = if (parsed == null) "Enter an amount." else "Pick a category and fund."
            return false
        }
        staged = staged + d
        amountText = ""; note = ""   // keep category/fund/date for the next row
        hint = null
        return true
    }

    val title = when {
        editingMode -> "Edit expense"
        mode == AddMode.Expense -> "Add expense"
        else -> "Add income"
    }
    val canSave = !spending.saving && when {
        editingMode -> currentValid
        mode == AddMode.Expense -> pendingCount > 0
        else -> incParsed != null && incFundId != null
    }
    fun doSave() {
        when {
            editingMode -> {
                val d = currentDraft() ?: run { hint = "Enter an amount, category and fund."; return }
                onEditExpense(editing!!.id, AddExpenseRequest(d.categoryId, d.amount, d.fundId, d.date, d.note.ifBlank { null })) { onDismiss() }
            }
            mode == AddMode.Expense -> {
                val batch = buildList {
                    addAll(staged)
                    currentDraft()?.let { add(it) }
                }.map { AddExpenseRequest(it.categoryId, it.amount, it.fundId, it.date, it.note.ifBlank { null }) }
                if (batch.isEmpty()) { hint = "Enter an amount."; return }
                onSaveExpenses(batch) { staged = emptyList(); amountText = ""; note = ""; onDismiss() }
            }
            else -> {
                val f = incFundId; val amt = incParsed
                if (amt == null || f == null) { hint = "Enter an amount and pick a fund."; return }
                if (incomeEdit) onEditDeposit(editingDeposit!!.id, f, incSource, amt, date) { onDismiss() }
                else onAddIncome(f, incSource, amt, date) { incAmountText = ""; onDismiss() }
            }
        }
    }

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
            // No "Add expense/income" title — the segment below already says which. When editing (no segment), keep
            // a short title so the mode is clear.
            Spacer(Modifier.height(4.dp))
            if (editingMode || incomeEdit) {
                Text(if (incomeEdit) "Edit income" else "Edit expense", fontSize = 19.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
            } else {
                // Expense / Income segment, with a "recall last" icon button on the right (recalls the last of that kind).
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.weight(1f)) { Segment(mode) { mode = it; hint = null } }
                    val recall = if (mode == AddMode.Expense) onEditLast else onEditLastIncome
                    if (recall != null) {
                        Spacer(Modifier.width(8.dp))
                        IconButton(onClick = recall) {
                            Icon(TandemIcons.Rotate, contentDescription = "Edit last", tint = MaterialTheme.colorScheme.primary)
                        }
                    }
                }
            }
            Spacer(Modifier.height(10.dp))

            if (mode == AddMode.Income) {
                IncomeEditor(
                    spending = spending,
                    amountText = incAmountText, onAmount = { incAmountText = it; hint = null },
                    source = incSource, onSource = { incSource = it },
                    fundId = incFundId, onFund = { incFundId = it },
                    date = date, onDate = { date = it },
                    hint = hint,
                )
                return@Column
            }

            when {
                cats.isEmpty() && spending.loading ->
                    Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
                    }

                cats.isEmpty() || funds.isEmpty() ->
                    Text(
                        "Add a category and a fund first, then you can log expenses here.",
                        color = tandem.muted, modifier = Modifier.padding(vertical = 24.dp),
                    )

                else -> {
                    Spacer(Modifier.height(8.dp))

                    // Amount.
                    OutlinedTextField(
                        value = amountText,
                        onValueChange = { amountText = it; hint = null },
                        label = { Text("Amount") },
                        prefix = { Text(currencySymbol(spending.currency)) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(14.dp))

                    // Category — most-used chips + a searchable picker.
                    FieldLabel("Category")
                    if (recentCats.isNotEmpty()) {
                        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            recentCats.forEach { id ->
                                val c = catById[id] ?: return@forEach
                                PickChip(
                                    label = c.name,
                                    icon = c.icon,
                                    catName = c.name,
                                    selected = categoryId == id,
                                    onClick = { categoryId = id; catExpanded = false },
                                )
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                    }
                    CategorySelector(
                        selected = categoryId?.let { catById[it] },
                        expanded = catExpanded,
                        onToggle = { catExpanded = !catExpanded },
                    )
                    if (catExpanded) {
                        if (cats.size > 8) {
                            OutlinedTextField(
                                value = catSearch,
                                onValueChange = { catSearch = it },
                                placeholder = { Text("Search categories") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                            )
                        }
                        val filtered = cats.filter { catSearch.isBlank() || it.name.contains(catSearch, ignoreCase = true) }
                        Column(
                            Modifier
                                .fillMaxWidth()
                                .padding(top = 8.dp)
                                .heightIn(max = 240.dp)
                                .verticalScroll(rememberScrollState())
                                .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp)),
                        ) {
                            filtered.forEach { c ->
                                CategoryOptionRow(c, selected = categoryId == c.id) {
                                    categoryId = c.id; catExpanded = false; catSearch = ""
                                }
                            }
                            if (filtered.isEmpty()) Text("No matches", color = tandem.muted, modifier = Modifier.padding(14.dp))

                            // Can't find one? Create it inline (name only; the icon is guessed, editable later in Manage).
                            if (creatingCat) {
                                Row(
                                    Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                                ) {
                                    OutlinedTextField(
                                        value = newCatName, onValueChange = { newCatName = it },
                                        placeholder = { Text("New category name") }, singleLine = true,
                                        modifier = Modifier.weight(1f),
                                    )
                                    Button(
                                        onClick = {
                                            val nm = newCatName.trim()
                                            if (nm.isNotBlank()) onAddCategory(nm, null, null) { newId ->
                                                if (newId != null) categoryId = newId
                                                creatingCat = false; newCatName = ""; catExpanded = false; catSearch = ""
                                            }
                                        },
                                        enabled = !spending.saving && newCatName.isNotBlank(),
                                    ) { Text("Add") }
                                }
                            } else {
                                Row(
                                    Modifier.fillMaxWidth().clickable { creatingCat = true }.padding(horizontal = 14.dp, vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Icon(TandemIcons.Plus, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
                                    Spacer(Modifier.width(8.dp))
                                    Text("New category", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold)
                                }
                            }
                        }
                    }
                    Spacer(Modifier.height(14.dp))

                    // Fund chips.
                    FieldLabel("Fund")
                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        funds.forEach { f ->
                            PickChip(
                                label = if (f.synced) "🏦 ${f.name}" else f.name,
                                icon = null,
                                selected = fundId == f.id,
                                onClick = { fundId = f.id },
                            )
                        }
                    }
                    Spacer(Modifier.height(14.dp))

                    // Date + note.
                    FieldLabel("Date")
                    DateField(date) { date = it }
                    Spacer(Modifier.height(14.dp))

                    OutlinedTextField(
                        value = note,
                        onValueChange = { note = it },
                        label = { Text("Note (optional)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )

                    hint?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(it, color = tandem.warn, fontSize = 13.sp)
                    }
                    spending.saveError?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }

                    // Multi-add (stage more rows, batch total) — only when adding, not when editing one row.
                    if (!editingMode) {
                        Spacer(Modifier.height(14.dp))
                        // "+ Add another expense" — parks the current row.
                        TextButton(onClick = { stageCurrent() }, modifier = Modifier.fillMaxWidth()) {
                            Icon(TandemIcons.Plus, null, tint = MaterialTheme.colorScheme.primary)
                            Spacer(Modifier.width(6.dp))
                            Text("Add another expense", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold)
                        }

                        // Staged rows.
                        if (staged.isNotEmpty()) {
                            Spacer(Modifier.height(6.dp))
                            staged.forEachIndexed { i, d ->
                                StagedRow(
                                    category = catById[d.categoryId]?.name ?: "—",
                                    icon = catById[d.categoryId]?.icon,
                                    fund = fundById[d.fundId]?.name ?: "—",
                                    amount = fmt(d.amount),
                                    onEdit = {
                                        // Park any valid in-progress row first, then pull the tapped one back into the editor.
                                        val cur = currentDraft()
                                        val base = if (cur != null) staged + cur else staged
                                        staged = base.filterIndexed { idx, _ -> idx != i }
                                        categoryId = d.categoryId; fundId = d.fundId
                                        amountText = trimAmount(d.amount); note = d.note; date = d.date
                                    },
                                    onRemove = { staged = staged.filterIndexed { idx, _ -> idx != i } },
                                )
                            }
                        }

                        // Batch footer.
                        if (pendingCount > 0) {
                            Spacer(Modifier.height(12.dp))
                            Text(
                                "$pendingCount to add · ${fmt(pendingTotal)}",
                                fontWeight = FontWeight.Bold,
                                color = tandem.muted,
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                    }
                }
            }
        }
        SheetActionBar(
            saving = spending.saving,
            canSave = canSave,
            onDismiss = onDismiss,
            onSave = { doSave() },
            modifier = Modifier.align(Alignment.BottomCenter),
            saveLabel = if (editingMode) "Save changes" else "Save",
        )
      }
    }
}

@Composable
private fun CategorySelector(selected: CategoryOptionDto?, expanded: Boolean, onToggle: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
            .clickable(onClick = onToggle)
            .padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (selected != null) { CatIcon(selected.icon, selected.name, 16.dp); Spacer(Modifier.width(8.dp)) }
        Text(
            selected?.name ?: "Choose a category",
            modifier = Modifier.weight(1f),
            color = if (selected == null) LocalTandemColors.current.muted else MaterialTheme.colorScheme.onSurface,
            fontWeight = FontWeight.SemiBold,
        )
        // The web line-chevron points right; rotate it down (collapsed) / up (expanded).
        Icon(
            TandemIcons.Chevron,
            null, tint = LocalTandemColors.current.muted,
            modifier = Modifier.size(18.dp).rotate(if (expanded) -90f else 90f),
        )
    }
}

@Composable
private fun CategoryOptionRow(c: CategoryOptionDto, selected: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(if (selected) tandem.savingsTileBg else MaterialTheme.colorScheme.surface)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // Indent sub-categories a touch so the hierarchy reads.
        if (c.parentId != null) Spacer(Modifier.width(16.dp))
        CatIcon(c.icon, c.name, 16.dp); Spacer(Modifier.width(8.dp))
        Text(
            c.name,
            color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
            fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
        )
    }
}

@Composable
private fun StagedRow(category: String, icon: String?, fund: String, amount: String, onEdit: () -> Unit, onRemove: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp)
            .background(tandem.hero, RoundedCornerShape(12.dp))
            .border(1.dp, tandem.hairline, RoundedCornerShape(12.dp))
            .clickable(onClick = onEdit)
            .padding(start = 14.dp, top = 8.dp, bottom = 8.dp, end = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CatIcon(icon, category, 15.dp); Spacer(Modifier.width(8.dp))
        Text(
            "$category · $fund",
            modifier = Modifier.weight(1f),
            color = MaterialTheme.colorScheme.onSurface,
            fontSize = 13.sp,
            maxLines = 1,
        )
        Text(amount, fontWeight = FontWeight.Bold, color = tandem.spent, fontSize = 13.sp)
        IconButton(onClick = onRemove) { Icon(TandemIcons.Trash, "Remove", tint = tandem.muted) }
    }
}

@Composable
private fun Segment(mode: AddMode, onSelect: (AddMode) -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth().background(tandem.segmentTrack, RoundedCornerShape(12.dp)).padding(3.dp),
    ) {
        SegmentButton("Expense", mode == AddMode.Expense, Modifier.weight(1f)) { onSelect(AddMode.Expense) }
        SegmentButton("Income", mode == AddMode.Income, Modifier.weight(1f)) { onSelect(AddMode.Income) }
    }
    Spacer(Modifier.height(12.dp))
}

@Composable
private fun SegmentButton(label: String, selected: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    Box(
        modifier
            .background(if (selected) MaterialTheme.colorScheme.surface else androidx.compose.ui.graphics.Color.Transparent, RoundedCornerShape(9.dp))
            .clickable(onClick = onClick)
            .padding(vertical = 9.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            label,
            color = if (selected) MaterialTheme.colorScheme.primary else tandem.muted,
            fontWeight = if (selected) FontWeight.Bold else FontWeight.SemiBold,
            fontSize = 14.sp,
        )
    }
}

/** The Income tab of the add sheet: a single deposit into a fund (amount · source · fund · date). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun IncomeEditor(
    spending: SpendingUi,
    amountText: String, onAmount: (String) -> Unit,
    source: String, onSource: (String) -> Unit,
    fundId: String?, onFund: (String) -> Unit,
    date: String, onDate: (String) -> Unit,
    hint: String?,
) {
    val tandem = LocalTandemColors.current
    val funds = spending.funds
    if (funds.isEmpty()) {
        Text("Add a fund first, then you can record income here.", color = tandem.muted, modifier = Modifier.padding(vertical = 24.dp))
        return
    }
    Spacer(Modifier.height(2.dp))
    OutlinedTextField(
        value = amountText,
        onValueChange = onAmount,
        label = { Text("Amount") },
        prefix = { Text(currencySymbol(spending.currency)) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
        modifier = Modifier.fillMaxWidth(),
    )
    Spacer(Modifier.height(14.dp))

    FieldLabel("Source")
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        PickChip(label = "General income", icon = null, selected = source == GENERAL_INCOME) { onSource(GENERAL_INCOME) }
        spending.incomeCategories.forEach { c ->
            PickChip(label = c.name, icon = c.icon, catName = c.name, selected = source == c.id) { onSource(c.id) }
        }
    }
    Spacer(Modifier.height(14.dp))

    FieldLabel("Into fund")
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        funds.forEach { f ->
            PickChip(label = if (f.synced) "🏦 ${f.name}" else f.name, icon = null, selected = fundId == f.id) { onFund(f.id) }
        }
    }
    Spacer(Modifier.height(14.dp))

    FieldLabel("Date")
    DateField(date, onDate)
    Hints(hint, spending.saveError)
}
