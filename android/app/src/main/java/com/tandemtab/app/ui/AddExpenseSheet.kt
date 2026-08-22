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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ButtonDefaults
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
import com.tandemtab.app.TripsUi
import com.tandemtab.app.data.AddExpenseRequest
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.DepositRowDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.FundOptionDto
import com.tandemtab.app.data.TagOptionDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
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
    // Which journey this row is filed to, if any. Carried per staged row rather than per sheet, so a batch can
    // hold both a holiday dinner and the ordinary shopping done the same day.
    val tripId: String? = null,
    // Its label. Per row for the same reason as the trip — a batch is rarely all one thing.
    val tagId: String? = null,
    // Per row too: a batch staged across an evening should not stamp every row with the moment Save was pressed.
    val time: String? = null,
)

private enum class AddMode { Expense, Income }

/** "HH:mm" now when [dateIso] is today, blank otherwise — you know when you bought this morning's coffee, not
 *  what time you bought one last Tuesday, and inventing a time is worse than leaving it out. */
private fun defaultTimeFor(dateIso: String): String =
    if (dateIso == LocalDate.now().toString())
        LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm"))
    else ""

/** "HH:mm" → the wire's "HH:mm:00"; anything not a real time (including blank) → null, which the server keeps
 *  as null rather than turning into midnight. */
private fun timeForWire(text: String): String? = runCatching {
    LocalTime.parse(text.trim(), DateTimeFormatter.ofPattern("HH:mm")).format(DateTimeFormatter.ofPattern("HH:mm:ss"))
}.getOrNull()

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
    trips: TripsUi,
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
    onDeleteDeposit: (depositId: String, onDone: () -> Unit) -> Unit = { _, _ -> },
    onAddCategory: (name: String, parentId: String?, icon: String?, onDone: (String?) -> Unit) -> Unit,
    // Create a tag and hand back its id. Same shape as onAddCategory; used by the find-or-add box when what was
    // typed matches nothing that exists.
    // ⚠️ Named onAddExpenseTag, not onAddTag: the tags-MANAGEMENT sheet already has an onAddTag with a different
    // signature (no id back), and two callbacks one letter apart on the same screen is how the wrong one gets wired.
    onAddExpenseTag: (name: String, isTripTag: Boolean, onDone: (String?) -> Unit) -> Unit = { _, _, done -> done(null) },
    // Null when there is nowhere to settle onto (only one account, or none in this currency), which is why the
    // whole row is absent rather than present-and-disabled — a control that can never work is worse than no control.
    onSettle: ((ExpenseDto) -> Unit)? = null,
    onUndoRefund: ((ExpenseDto) -> Unit)? = null,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = moneyFormatter(spending.currency)
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
    var confirmingUndoRefund by remember { mutableStateOf(false) }
    var catExpanded by remember { mutableStateOf(false) }
    var catSearch by remember { mutableStateOf("") }
    var newCatName by remember { mutableStateOf("") }   // inline "new category" name (empty = the +New row is collapsed)
    var creatingCat by remember { mutableStateOf(false) }
    var hint by remember { mutableStateOf<String?>(null) }

    // The trip a new row is filed to. Defaults to the one being lived — the app knows you're away, so it offers
    // it — but never to a trip that has only *arrived* by date: trip mode is opt-in, and a coffee on the morning
    // of departure is not holiday spending until someone says the trip has started.
    var tripId by remember(editing, trips.live?.id) { mutableStateOf(editing?.tripId ?: trips.live?.id) }

    // The row's label. One tag per expense is the model, so this is a single id rather than a set.
    var tagId by remember(editing) { mutableStateOf(editing?.tagIds?.firstOrNull()) }
    // What is typed in the find-or-add tag box. Doubles as the search query and as the name of a tag about to be
    // created — see commitTypedTag.
    var tagQuery by remember(editing) { mutableStateOf("") }

    // "HH:mm", or blank for none. Editing shows what the row actually carries — including nothing, which most
    // bank-imported rows carry — rather than stamping "now" onto a row that was never timed.
    var timeText by remember(editing, spending.loaded) {
        mutableStateOf(editing?.time?.take(5) ?: if (editing == null) defaultTimeFor(today) else "")
    }

    val parsed = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }
    val currentValid = parsed != null && categoryId != null && fundId != null
    fun currentDraft(): ExpenseDraft? =
        if (currentValid) ExpenseDraft(categoryId!!, fundId!!, parsed!!, note.trim(), date, tripId, tagId, timeForWire(timeText)) else null

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
    /**
     * Resolve whatever is sitting in the find-or-add tag box, then run [then].
     *
     * ⚠️ This is the "sometimes it does not save the tag" bug: typing a name and pressing Save — without picking a
     * suggestion first — dropped the text silently, and the expense saved untagged with nothing said. A field the
     * user has typed into is a stated intention.
     *
     * An existing name SELECTS rather than creating: the domain rejects duplicate tag names, so "create anyway"
     * would surface as an error on a save that looks routine. Matching is case-insensitive, so "food" finds "Food".
     * A creation that fails leaves the expense untagged rather than unsaved — losing the label is better than
     * losing the entry.
     */
    fun commitTypedTag(then: () -> Unit) {
        val name = tagQuery.trim()
        if (name.isEmpty()) { then(); return }
        val existing = spending.tags.firstOrNull { it.name.equals(name, ignoreCase = true) }
        if (existing != null) {
            tagId = existing.id
            tagQuery = ""
            then()
            return
        }
        onAddExpenseTag(name, tripId != null) { newId ->
            if (newId != null) tagId = newId
            tagQuery = ""
            then()
        }
    }

    fun doSave() {
        when {
            editingMode -> {
                val d = currentDraft() ?: run { hint = "Enter an amount, category and fund."; return }
                // ⚠️ clearTime and clearTag are what make emptying either field mean something. On the edit the
                // server treats an omitted value as "leave it alone" — so an older client correcting an amount
                // can't strip the clock or the label off a row — which means null alone can never say "none".
                // The tag followed the opposite rule until S111 and silently cleared on omission; both are the
                // same rule now, and the sheet still sends the current tag back either way.
                onEditExpense(
                    editing!!.id,
                    AddExpenseRequest(
                        d.categoryId, d.amount, d.fundId, d.date, d.note.ifBlank { null },
                        tagId = d.tagId, time = d.time, clearTime = d.time == null && editing.time != null,
                        clearTag = d.tagId == null && editing.tagIds.isNotEmpty(),
                    ),
                ) { onDismiss() }
            }
            mode == AddMode.Expense -> {
                val batch = buildList {
                    addAll(staged)
                    currentDraft()?.let { add(it) }
                }.map { AddExpenseRequest(it.categoryId, it.amount, it.fundId, it.date, it.note.ifBlank { null }, tagId = it.tagId, tripId = it.tripId, time = it.time) }
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
                    onRemove = editingDeposit?.let { d -> { onDeleteDeposit(d.id) { onDismiss() } } },
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

                    // Date + time + note. The clock is stamped as NOW for something logged today and left empty
                    // otherwise: you know what time you bought a coffee this morning, not what time you bought one
                    // last Tuesday, and a made-up midnight would sort ahead of everything real on that day.
                    FieldLabel("Date")
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Box(Modifier.weight(1f)) { DateField(date) { picked -> date = picked; timeText = defaultTimeFor(picked) } }
                        OutlinedTextField(
                            value = timeText,
                            onValueChange = { timeText = it },
                            label = { Text("Time") },
                            placeholder = { Text("--:--") },
                            singleLine = true,
                            modifier = Modifier.width(112.dp),
                        )
                    }
                    // Empty is a real answer and stays one, so it needs saying rather than looking like an omission.
                    if (timeText.isBlank()) {
                        Spacer(Modifier.height(4.dp))
                        Text("No time — it'll sit at the end of its day.", fontSize = 11.sp, color = tandem.muted)
                    }
                    Spacer(Modifier.height(14.dp))

                    OutlinedTextField(
                        value = note,
                        onValueChange = { note = it },
                        label = { Text("Note (optional)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )

                    // On a trip: file this spending to it. Offered only when adding — the server's expense EDIT
                    // deliberately carries no trip field (so a client that knows nothing about trips can correct
                    // an amount without dropping the row out of a recap); changing the link is its own action, on
                    // the Trips tab. Hidden entirely when the account has no trips, rather than shown empty.
                    if (!editingMode && trips.trips.isNotEmpty()) {
                        Spacer(Modifier.height(14.dp))
                        FieldLabel("Trip")
                        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            PickChip(label = "No trip", icon = null, selected = tripId == null) { tripId = null }
                            // Live and upcoming journeys only: a finished trip is history, and offering it here is
                            // how a weekly shop ends up in last summer's holiday.
                            trips.trips.filter { !it.isFinished }.forEach { t ->
                                PickChip(label = t.name, icon = t.icon ?: "plane", catName = t.name, selected = tripId == t.id) { tripId = t.id }
                            }
                        }
                        if (tripId != null && trips.trips.firstOrNull { it.id == tripId }?.isActive != true) {
                            Spacer(Modifier.height(6.dp))
                            Text(
                                "Filed to a trip you haven't left for yet — that's fine, it's how a flight bought " +
                                    "now counts later.",
                                fontSize = 12.sp, color = tandem.muted,
                            )
                        }
                    }

                    // Label. Offered on the edit form too, unlike the trip row: the server's expense edit DOES
                    // carry a tag, and the picker is the only way to correct one that was mis-filed.
                    // Trip labels are left out at home — "Tickets & tours" is noise when you're logging groceries —
                    // and folded in only once the row is filed to a journey, which is the axis its recap splits on.
                    run {
                        val onTrip = tripId != null
                        val offered = spending.tags.filter { !it.tripTag || onTrip }
                        // ★ On a trip the predefined labels ARE the vocabulary and all of them show — each is bound
                        // to a category, so one tap files and labels at once. Off a trip the everyday list grows
                        // for ever, so only the five most-used are chips and the rest is reached by typing.
                        // ⚠️ Ranked by useCount, which the SERVER computes across every period: this client holds
                        // only the current month's rows and would otherwise order by a different question.
                        val shortlist =
                            if (onTrip) offered
                            else offered.sortedWith(compareByDescending<TagOptionDto> { it.useCount }.thenBy { it.name.lowercase() }).take(5)
                        // Whatever is selected is always a chip, even when it is outside the shortlist — otherwise
                        // the form shows no label while the expense carries one.
                        val chips = (shortlist + offered.filter { it.id == tagId }).distinctBy { it.id }
                        val matches =
                            if (onTrip || tagQuery.trim().length < 2) emptyList()
                            else offered.filter {
                                it.name.contains(tagQuery.trim(), ignoreCase = true) && chips.none { c -> c.id == it.id }
                            }.sortedWith(
                                compareByDescending<TagOptionDto> { it.name.startsWith(tagQuery.trim(), ignoreCase = true) }
                                    .thenBy { it.name.lowercase() },
                            ).take(6)
                        val exactExists = offered.any { it.name.equals(tagQuery.trim(), ignoreCase = true) }

                        if (offered.isNotEmpty() || !onTrip) {
                            Spacer(Modifier.height(14.dp))
                            FieldLabel("Label")
                            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                PickChip(label = "No label", icon = null, selected = tagId == null) { tagId = null }
                                chips.forEach { t ->
                                    PickChip(label = t.name, icon = t.icon ?: "tag", catName = t.name, selected = tagId == t.id) {
                                        tagId = if (tagId == t.id) null else t.id
                                        tagQuery = ""
                                        // F2: a bound tag files the expense for you. A DEFAULT at entry time, not a
                                        // rule — it only fires on adding, so correcting a label can't silently move
                                        // an already-filed row into a different budget.
                                        if (!editingMode && tagId == t.id) t.categoryId?.let { categoryId = it }
                                    }
                                }
                            }
                            // One box that both FINDS and MAKES: off a trip the chips are only a shortlist, so the
                            // rest of the vocabulary has to be typeable — and the same gesture invents a new label.
                            // ⚠️ Nothing has to be pressed: doSave commits whatever is in here (see commitTypedTag),
                            // because a field the user has typed into is a stated intention.
                            if (!onTrip) {
                                Spacer(Modifier.height(8.dp))
                                OutlinedTextField(
                                    value = tagQuery,
                                    onValueChange = { tagQuery = it.take(30) },
                                    placeholder = { Text("Find or add a tag…") },
                                    singleLine = true,
                                    modifier = Modifier.fillMaxWidth(),
                                )
                                if (matches.isNotEmpty()) {
                                    Spacer(Modifier.height(8.dp))
                                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                        matches.forEach { m ->
                                            PickChip(label = m.name, icon = m.icon ?: "tag", catName = m.name, selected = tagId == m.id) {
                                                tagId = m.id
                                                tagQuery = ""
                                                if (!editingMode) m.categoryId?.let { categoryId = it }
                                            }
                                        }
                                    }
                                } else if (tagQuery.trim().length >= 2 && !exactExists) {
                                    Spacer(Modifier.height(6.dp))
                                    Text(
                                        "No tag matches — \"${tagQuery.trim()}\" will be created and attached.",
                                        fontSize = 12.sp, color = tandem.muted,
                                    )
                                }
                            }
                            val bound = spending.tags.firstOrNull { it.id == tagId }?.categoryId
                            if (!editingMode && bound != null && bound == categoryId) {
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    "Filed into ${spending.categories.firstOrNull { it.id == bound }?.name ?: "its category"} " +
                                        "because of this label — change the category above if that's not right.",
                                    fontSize = 12.sp, color = tandem.muted,
                                )
                            }
                        }
                    }

                    hint?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(it, color = tandem.warn, fontSize = 13.sp)
                    }
                    spending.saveError?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }

                    // Settling onto another account, reached from the expense being edited — the web's placement
                    // exactly. Not offered on a row that IS the destination of somebody else's settlement: that
                    // expense belongs to the account that sent it, and settling it onward would orphan the link.
                    if (editingMode && onSettle != null && editing != null && !editing.isSettlementDestination) {
                        Spacer(Modifier.height(10.dp))
                        TextButton(onClick = { onSettle(editing) }, modifier = Modifier.fillMaxWidth()) {
                            Icon(TandemIcons.Users, null, tint = MaterialTheme.colorScheme.primary)
                            Spacer(Modifier.width(6.dp))
                            Text(
                                if (editing.isSettlementSource) "Change the settlement on this expense"
                                else "Settle onto another account",
                                color = MaterialTheme.colorScheme.primary,
                                fontWeight = FontWeight.SemiBold,
                            )
                        }
                    }

                    // Undoing money that came back. Here rather than on the row because the bank transaction it came
                    // from is already acknowledged and will not return — the confirm has to say so while the user is
                    // looking at which charge is about to grow again.
                    if (editingMode && onUndoRefund != null && editing != null && editing.refundedAmount > 0) {
                        Spacer(Modifier.height(10.dp))
                        TextButton(onClick = { confirmingUndoRefund = true }, modifier = Modifier.fillMaxWidth()) {
                            Icon(TandemIcons.Swap, null, tint = MaterialTheme.colorScheme.primary)
                            Spacer(Modifier.width(6.dp))
                            Text(
                                "${fmt(editing.refundedAmount)} came back on this — undo",
                                color = MaterialTheme.colorScheme.primary,
                                fontWeight = FontWeight.SemiBold,
                            )
                        }
                        if (confirmingUndoRefund) {
                            AlertDialog(
                                onDismissRequest = { confirmingUndoRefund = false },
                                title = { Text("Put the money back on this expense?") },
                                text = {
                                    Text(
                                        "It goes back to ${fmt(editing.amount + editing.refundedAmount)}. " +
                                            "The bank transaction stays acknowledged — this doesn't return it to the review list.",
                                    )
                                },
                                confirmButton = {
                                    TextButton(onClick = { confirmingUndoRefund = false; onUndoRefund(editing) }) { Text("Put it back") }
                                },
                                dismissButton = { TextButton(onClick = { confirmingUndoRefund = false }) { Text("Cancel") } },
                            )
                        }
                    }

                    // Multi-add (stage more rows, batch total) — only when adding, not when editing one row.
                    if (!editingMode) {
                        Spacer(Modifier.height(14.dp))
                        // "+ Add another expense" — parks the current row.
                        // Through commitTypedTag: staging snapshots the tag id into the draft, so a name still
                        // sitting in the box would be lost with the row.
                        TextButton(onClick = { commitTypedTag { stageCurrent() } }, modifier = Modifier.fillMaxWidth()) {
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
            onSave = { commitTypedTag { doSave() } },
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
    onRemove: (() -> Unit)? = null,
) {
    val tandem = LocalTandemColors.current
    var confirmingRemove by remember { mutableStateOf(false) }
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

    // Removing a recorded deposit is only offered while editing one — there is nothing to remove otherwise. It
    // confirms, like every delete: income is what every other figure on Home is measured against, so dropping a
    // row moves the whole page.
    onRemove?.let { remove ->
        Spacer(Modifier.height(6.dp))
        TextButton(onClick = { confirmingRemove = true }, enabled = !spending.saving, modifier = Modifier.fillMaxWidth()) {
            Text("Remove this income", color = tandem.spent)
        }
        if (confirmingRemove) {
            AlertDialog(
                onDismissRequest = { if (!spending.saving) confirmingRemove = false },
                title = { Text("Remove this income?") },
                text = {
                    Column {
                        Text("It comes off what you had coming in this period, so everything measured against it moves too.")
                        spending.saveError?.let {
                            Spacer(Modifier.height(10.dp))
                            Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                        }
                    }
                },
                confirmButton = {
                    Button(
                        onClick = remove,
                        enabled = !spending.saving,
                        colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                    ) {
                        if (spending.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                        else Text("Remove")
                    }
                },
                dismissButton = { TextButton(onClick = { confirmingRemove = false }, enabled = !spending.saving) { Text("Cancel") } },
            )
        }
    }

    Hints(hint, spending.saveError)
}
