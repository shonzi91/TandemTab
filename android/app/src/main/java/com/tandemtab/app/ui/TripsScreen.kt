package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.TripsUi
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.ExpenseDto
import com.tandemtab.app.data.TripDetailDto
import com.tandemtab.app.data.TripDto
import com.tandemtab.app.data.TripExpenseRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * Spending → Trips: what each journey cost.
 *
 * ⚠️ **The totals here deliberately don't match any one period's spending.** A trip's expenses are gathered by
 * their *link*, across every period, which is the whole point: the flight bought in March belongs to the June
 * trip. Nothing on this screen re-derives that, or a trip's state — both come from the server (see [TripDto]).
 */
@Composable
fun TripsView(
    trips: TripsUi,
    categories: List<CategoryOptionDto>,
    periodExpenses: List<ExpenseDto>,
    onRetry: () -> Unit,
    onSave: (tripId: String?, name: String, from: String, to: String, destination: String?, icon: String?,
             savingCategoryId: String?, budget: Double?, categoryId: String?, onDone: () -> Unit) -> Unit,
    onDelete: (tripId: String, onDone: () -> Unit) -> Unit,
    onStart: (tripId: String, started: Boolean) -> Unit,
    onFinish: (tripId: String, finished: Boolean) -> Unit,
    onAttachExpense: (expenseId: String, tripId: String?, onDone: () -> Unit) -> Unit,
    onOpen: (tripId: String?) -> Unit,
    onUseSavings: ((tripId: String, amount: Double, date: String, onDone: () -> Unit) -> Unit)? = null,
    onPrepare: () -> Unit,
    // ⚠️ Trips are Pro, and the line is STARTING a journey, not running one. Only the three controls that begin
    // or reshape one are gated here — new, edit (where the dates live), and funding it from a pot. Read, start,
    // finish (early, and the undo), attach/detach while it runs and delete stay open on any plan, because a
    // paywall that strands somebody mid-trip leaves the app wearing trip mode forever. See PlanFeatures.TRIPS.
    proLocked: Boolean = false,
    onProBlocked: () -> Unit = {},
) {
    val tandem = LocalTandemColors.current
    val fmt = moneyFormatter(trips.currency)
    var editing by remember { mutableStateOf<TripEdit?>(null) }
    var deleting by remember { mutableStateOf<TripDto?>(null) }
    var attachingTo by remember { mutableStateOf<TripDto?>(null) }
    var releasingFor by remember { mutableStateOf<TripDto?>(null) }

    when {
        trips.loading && trips.trips.isEmpty() ->
            Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }

        trips.error != null ->
            Column(Modifier.fillMaxWidth().padding(top = 32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(trips.error, color = MaterialTheme.colorScheme.error)
                Spacer(Modifier.height(8.dp))
                Text("Tap to retry", color = tandem.positive, modifier = Modifier.clickable(onClick = onRetry).padding(8.dp))
            }

        else -> Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (trips.trips.isEmpty()) {
                Box(Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                    Text(
                        "No trips yet. Add one and file a flight to it — even one you paid for months ago.",
                        color = tandem.muted, fontSize = 13.sp,
                        modifier = Modifier.padding(horizontal = 24.dp),
                    )
                }
            }
            trips.trips.forEach { trip ->
                val open = trips.detailTripId == trip.id
                TripCard(
                    trip = trip,
                    fmt = fmt,
                    open = open,
                    // The split + ledger are fetched on expand, so they belong to whichever card is open.
                    detail = if (open) trips.detail else null,
                    detailLoading = open && trips.detailLoading,
                    busy = trips.saving,
                    onToggle = { onOpen(if (open) null else trip.id) },
                    onStart = { onStart(trip.id, true) },
                    onFinish = { onFinish(trip.id, true) },
                    onReopen = { onFinish(trip.id, false) },
                    onEdit = { if (proLocked) onProBlocked() else { onPrepare(); editing = TripEdit.of(trip) } },
                    onDelete = { onPrepare(); deleting = trip },
                    onAttach = { onPrepare(); attachingTo = trip },
                    onUseSavings = if (onUseSavings != null) ({ _ ->
                        if (proLocked) onProBlocked() else { onPrepare(); releasingFor = trip }
                    }) else null,
                )
            }
            Spacer(Modifier.height(4.dp))
            Row(
                Modifier.fillMaxWidth()
                    .clip(RoundedCornerShape(12.dp))
                    .border(1.dp, tandem.hairline, RoundedCornerShape(12.dp))
                    .clickable { if (proLocked) onProBlocked() else { onPrepare(); editing = TripEdit.blank() } }
                    .padding(vertical = 13.dp),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(TandemIcons.Plus, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(6.dp))
                Text("New trip", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold, fontSize = 14.sp)
                // The crown shows only when the plan actually can't reach this, so a Pro account never sees one.
                if (proLocked) {
                    Spacer(Modifier.width(8.dp))
                    Icon(TandemIcons.Crown, contentDescription = "Part of Pro", tint = tandem.warn, modifier = Modifier.size(14.dp))
                }
            }
            trips.saveError?.let {
                Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp))
            }
        }
    }

    editing?.let { edit ->
        TripSheet(
            edit = edit,
            categories = categories,
            currency = trips.currency,
            saving = trips.saving,
            saveError = trips.saveError,
            onSave = { e ->
                onSave(e.id, e.name.trim(), e.from, e.to, e.destination, e.icon, e.savingCategoryId, e.budget(), e.categoryId) {
                    editing = null
                }
            },
            onDismiss = { editing = null },
        )
    }

    deleting?.let { trip ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text("Delete “${trip.name}”?") },
            // Said out loud because it is the thing people fear about this button: the journey goes, the money stays.
            text = { Text("The trip goes; its ${trip.expenseCount} expense(s) stay exactly where you logged them — they just stop belonging to a trip.") },
            confirmButton = {
                TextButton(onClick = { val t = trip; deleting = null; onDelete(t.id) {} }) {
                    Text("Delete", color = LocalTandemColors.current.spent)
                }
            },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text("Cancel") } },
        )
    }

    attachingTo?.let { trip ->
        AttachExpenseSheet(
            trip = trip,
            expenses = periodExpenses,
            fmt = fmt,
            saving = trips.saving,
            saveError = trips.saveError,
            onPick = { expenseId -> onAttachExpense(expenseId, trip.id) { attachingTo = null } },
            onDismiss = { attachingTo = null },
        )
    }

    releasingFor?.let { trip ->
        UseTripSavingsDialog(
            trip = trip,
            fmt = fmt,
            saving = trips.saving,
            saveError = trips.saveError,
            onRelease = { amount, date -> onUseSavings?.invoke(trip.id, amount, date) { releasingFor = null } },
            onDismiss = { releasingFor = null },
        )
    }
}

/**
 * Release a linked savings pot into a trip's budget, ahead of the journey.
 *
 * The money does not leave the account and the trip's total does not move — what changes is that the journey now
 * has a budget backed by money already set aside, rather than by this month's income. That distinction is the whole
 * reason to link a pot to a trip, so the dialog says it rather than leaving it to be inferred from a figure.
 */
@Composable
private fun UseTripSavingsDialog(
    trip: TripDto,
    fmt: (Double) -> String,
    saving: Boolean,
    saveError: String?,
    onRelease: (amount: Double, date: String) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var amountText by remember(trip.id) { mutableStateOf("") }
    val date = remember { java.time.LocalDate.now().toString() }
    val amount = amountText.replace(',', '.').toDoubleOrNull()?.takeIf { it > 0 }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        title = { Text("Use the saved money") },
        text = {
            Column {
                Text(
                    "Releases money from ${trip.savingCategoryName} into ${trip.name}'s budget. Nothing leaves the " +
                        "account and the trip's total doesn't change — the journey is just funded by what you set " +
                        "aside instead of by this month.",
                    color = tandem.muted, fontSize = 13.sp,
                )
                if (trip.savingsApplied > 0.0) {
                    Spacer(Modifier.height(8.dp))
                    Text("${fmt(trip.savingsApplied)} has been released already.", color = tandem.muted, fontSize = 12.sp)
                }
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    value = amountText,
                    onValueChange = { amountText = it },
                    label = { Text("Amount") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth(),
                )
                saveError?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            Button(onClick = { amount?.let { onRelease(it, date) } }, enabled = !saving && amount != null) {
                if (saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                else Text("Release")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !saving) { Text("Cancel") } },
    )
}

// --- The card ---------------------------------------------------------------------------------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TripCard(
    trip: TripDto,
    fmt: (Double) -> String,
    open: Boolean,
    detail: TripDetailDto?,
    detailLoading: Boolean,
    busy: Boolean,
    onToggle: () -> Unit,
    onStart: () -> Unit,
    onFinish: () -> Unit,
    onReopen: () -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
    onAttach: () -> Unit,
    onUseSavings: ((TripDto) -> Unit)? = null,
) {
    val tandem = LocalTandemColors.current
    // One mark per state, so the card says which of the three it is before any date is read: a pin for the one
    // you are standing in, a departing plane for one still ahead, a flag for one that is over.
    val statusColor = when {
        trip.isActive -> MaterialTheme.colorScheme.primary
        trip.isFinished -> tandem.muted
        else -> tandem.warn
    }
    val statusIcon = when {
        trip.isActive -> TandemIcons.Pin
        trip.isFinished -> TandemIcons.Flag
        else -> TandemIcons.Plane
    }

    Column(
        Modifier.fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(MaterialTheme.colorScheme.surface)
            .border(1.dp, if (trip.isActive) statusColor.copy(alpha = 0.45f) else tandem.hairline, RoundedCornerShape(14.dp))
            .clickable(onClick = onToggle)
            .padding(14.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(contentAlignment = Alignment.Center) {
                CatIcon(trip.icon ?: "plane", trip.name, size = 20.dp)
            }
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(trip.name, fontWeight = FontWeight.Bold, fontSize = 15.sp, color = MaterialTheme.colorScheme.onSurface)
                    Spacer(Modifier.width(6.dp))
                    Icon(statusIcon, contentDescription = null, tint = statusColor, modifier = Modifier.size(14.dp))
                }
                Spacer(Modifier.height(2.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("${prettyDay(trip.from)}–${prettyDay(trip.to, withYear = true)}", fontSize = 12.sp, color = tandem.muted)
                    tripPill(trip)?.let { pill ->
                        Spacer(Modifier.width(6.dp))
                        Text(
                            pill,
                            fontSize = 10.sp,
                            fontWeight = FontWeight.Bold,
                            color = statusColor,
                            modifier = Modifier
                                .background(statusColor.copy(alpha = 0.14f), RoundedCornerShape(999.dp))
                                .padding(horizontal = 7.dp, vertical = 2.dp),
                        )
                    }
                }
            }
            Text(fmt(trip.spent), fontWeight = FontWeight.Bold, fontSize = 15.sp, color = MaterialTheme.colorScheme.onSurface)
        }

        if (!open) return@Column
        Spacer(Modifier.height(12.dp))

        // ⚠️ The detail wins over the list's count once it has arrived. They come from different reads, and when
        // they disagree the detail is the newer one — otherwise a card can show "nothing logged yet" above a
        // ledger of six expenses, which is what happens the moment anything is logged outside this screen.
        val hasSpend = detail?.expenses?.isNotEmpty() ?: (trip.expenseCount > 0)
        if (!hasSpend) {
            Text(
                "Nothing logged against this trip yet. Log an expense while you're away, or add something you've " +
                    "already paid for — a flight, a hotel.",
                fontSize = 13.sp, color = tandem.muted,
            )
        } else {
            // The three-way split is the point of the whole feature: the pre-paid part is the half people forget,
            // because the trip felt like it cost what they spent while they were there.
            FlowRow(horizontalArrangement = Arrangement.spacedBy(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                SplitFigure("Booked ahead", fmt(trip.prePaid))
                SplitFigure("While away", fmt(trip.onTrip))
                if (trip.afterReturn != 0.0) SplitFigure("After getting back", fmt(trip.afterReturn))
                SplitFigure("A day", fmt(trip.perDay))
            }
            trip.budget?.takeIf { it > 0.0 }?.let { budget ->
                Spacer(Modifier.height(10.dp))
                val delta = trip.spent - budget
                Text(
                    if (delta > 0) "${fmt(delta)} over the ${fmt(budget)} you planned."
                    else "${fmt(-delta)} under the ${fmt(budget)} you planned.",
                    fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                    color = if (delta > 0) tandem.spent else tandem.positive,
                )
            }
            // Two different sentences, and both can be true at once: money taken out of the pot as it was spent,
            // and money released from the pot into the trip's budget in advance. Neither discounts the total —
            // the money left either way.
            if (trip.fundedFromSavings != 0.0 && trip.savingCategoryName != null) {
                Spacer(Modifier.height(8.dp))
                FundedLine(TandemIcons.Target, "${fmt(trip.fundedFromSavings)} of this came out of ${trip.savingCategoryName}.")
            }
            if (trip.savingsApplied > 0.0 && trip.savingCategoryName != null) {
                Spacer(Modifier.height(6.dp))
                FundedLine(TandemIcons.Coins, "${fmt(trip.savingsApplied)} released from ${trip.savingCategoryName} into this trip's budget.")
            }
            // The action those two sentences were missing. A linked pot is money already set aside FOR this journey,
            // so releasing it into the trip's budget is the point of having linked it — offered while the trip is
            // still ahead or running, since releasing money into a journey that is over answers nothing.
            if (trip.savingCategoryName != null && onUseSavings != null && !trip.isFinished) {
                Spacer(Modifier.height(10.dp))
                OutlinedButton(onClick = { onUseSavings(trip) }, modifier = Modifier.fillMaxWidth()) {
                    Icon(TandemIcons.Coins, null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Use the saved money")
                }
            }

            // The split and the ledger arrive on their own read (see openTrip) — the list can't carry every
            // expense of every journey to draw one card.
            if (detailLoading) {
                Spacer(Modifier.height(14.dp))
                Box(Modifier.fillMaxWidth().height(60.dp), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(color = MaterialTheme.colorScheme.primary, modifier = Modifier.size(22.dp), strokeWidth = 2.dp)
                }
            } else if (detail != null) {
                if (detail.slices.isNotEmpty()) {
                    Spacer(Modifier.height(14.dp))
                    TripSplit(detail, fmt)
                }
                detail.biggest?.let { big ->
                    Spacer(Modifier.height(10.dp))
                    Text(
                        "Biggest single thing: ${fmt(big.amount)} — ${big.note?.takeIf { it.isNotBlank() } ?: big.categoryName}",
                        fontSize = 12.5.sp, color = tandem.muted,
                    )
                }
                if (detail.expenses.isNotEmpty()) {
                    Spacer(Modifier.height(14.dp))
                    TripLedger(detail.expenses, fmt)
                }
            } else {
                Spacer(Modifier.height(8.dp))
                Text("${trip.expenseCount} expense(s) on this journey", fontSize = 12.sp, color = tandem.muted)
            }
        }

        Spacer(Modifier.height(14.dp))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            TripAction(TandemIcons.Plus, "Add something already paid", enabled = !busy, onClick = onAttach)
            // The opt-in. Trip mode never switches itself on, so the day the dates arrive this is the tap that
            // does it — put where someone looking at the trip itself would reach for it.
            when {
                trip.isAwaitingStart -> TripAction(TandemIcons.Plane, "Let's go — start the trip", enabled = !busy, onClick = onStart)
                trip.isActive -> TripAction(TandemIcons.Flag, "Finish trip", enabled = !busy, onClick = onFinish)
                trip.finishedOn != null -> TripAction(TandemIcons.Rotate, "Not finished after all", enabled = !busy, onClick = onReopen)
            }
            TripAction(TandemIcons.Pencil, "Edit", enabled = !busy, onClick = onEdit)
            TripAction(TandemIcons.Trash, "Delete", enabled = !busy, danger = true, onClick = onDelete)
        }
    }
}

/**
 * Where the trip's money went: a ring plus its legend.
 *
 * ⚠️ **A single slice has to be a full circle, not an arc.** An arc sweeping 360° starts and ends at the same
 * point, and on some renderers draws nothing at all — which is the *ordinary* case here, not an edge case: a trip
 * that files into one category has exactly one slice unless its labels are used. The web hit this too.
 */
@Composable
private fun TripSplit(detail: TripDetailDto, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    val total = detail.slices.sumOf { it.amount }
    if (total <= 0.0) return
    // Capped at the palette: past ten wedges a ring is unreadable, and the legend below carries the detail anyway.
    val shown = detail.slices.take(SlicePalette.size)

    Row(verticalAlignment = Alignment.CenterVertically) {
        Canvas(Modifier.size(96.dp)) {
            val stroke = size.minDimension * 0.21f
            val inset = stroke / 2f
            val arcSize = Size(size.width - stroke, size.height - stroke)
            if (shown.size == 1) {
                drawCircle(
                    color = SlicePalette[0],
                    radius = (size.minDimension - stroke) / 2f,
                    style = Stroke(width = stroke),
                )
            } else {
                var start = -90f
                shown.forEachIndexed { i, s ->
                    val sweep = (s.amount / total * 360.0).toFloat()
                    drawArc(
                        color = SlicePalette[i % SlicePalette.size],
                        startAngle = start,
                        sweepAngle = sweep,
                        useCenter = false,
                        topLeft = Offset(inset, inset),
                        size = arcSize,
                        style = Stroke(width = stroke),
                    )
                    start += sweep
                }
            }
        }
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            shown.forEachIndexed { i, s ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(9.dp).clip(RoundedCornerShape(3.dp)).background(SlicePalette[i % SlicePalette.size]))
                    Spacer(Modifier.width(7.dp))
                    CatIcon(s.icon, s.label, size = 13.dp)
                    Spacer(Modifier.width(5.dp))
                    Text(s.label, fontSize = 12.5.sp, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
                    Text(fmt(s.amount), fontSize = 12.5.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
                }
            }
        }
    }
    // Said out loud when the ring isn't the split people expect. A trip files into ONE category, so the category
    // axis draws a single full ring: true, and useless. Nothing is wrong with the chart — the trip just isn't
    // labelled, and the label is the only axis that can answer "what did the hotel cost".
    if (detail.sliceAxis != "tag") {
        Spacer(Modifier.height(8.dp))
        Text(
            if (detail.hasTagSlices)
                "Split by category, because most of this trip isn't labelled. Tag as you go and this becomes stay / travel / food / tickets."
            else
                "One ring, because the whole trip files into one category and nothing here is labelled yet.",
            fontSize = 12.sp, color = tandem.muted,
        )
    }
}

/**
 * Everything attached to the trip. Biggest first, matching every other expense list in Spending — the question a
 * recap answers is "what did this cost", which a date order buries.
 *
 * ⚠️ Not the period's expenses: the *trip's*, gathered by link across every period, so March's flight is in this
 * list under a June trip.
 */
@Composable
private fun TripLedger(rows: List<TripExpenseRowDto>, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    Text(
        "EVERYTHING ON THIS TRIP",
        fontSize = 9.sp, letterSpacing = 0.9.sp, fontWeight = FontWeight.Bold, color = tandem.muted,
    )
    Spacer(Modifier.height(4.dp))
    Column {
        rows.forEach { e ->
            Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.width(34.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(dayOfMonth(e.date), fontSize = 13.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
                    Text(monthShort(e.date).uppercase(Locale.getDefault()), fontSize = 8.5.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
                }
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        e.note?.takeIf { it.isNotBlank() } ?: e.categoryName,
                        fontSize = 13.5.sp, color = MaterialTheme.colorScheme.onSurface, maxLines = 1,
                    )
                    // The sub-line carries the trip's OWN axis — the label — falling back to the category. On a
                    // trip filing into one category, printing the category here is the same word on every row.
                    Text(
                        (e.tagName ?: e.categoryName) + " · " + whenLabel(e.`when`),
                        fontSize = 11.sp, color = tandem.muted, maxLines = 1,
                    )
                }
                Text(fmt(e.amount), fontSize = 13.5.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
            }
        }
    }
}

private fun whenLabel(w: String) = when (w) {
    "before" -> "booked ahead"
    "after" -> "after getting back"
    else -> "while away"
}

/** The same categorical palette the web's charts use, so a trip's ring reads as one system across surfaces. */
private val SlicePalette = listOf(
    Color(0xFF13A06E), Color(0xFFFF7A66), Color(0xFFF5A623), Color(0xFF5B8DEF), Color(0xFFA06BD6),
    Color(0xFF22B8CF), Color(0xFFE8590C), Color(0xFF7CB342), Color(0xFFEC407A), Color(0xFF5C6BC0),
)

@Composable
private fun SplitFigure(label: String, value: String) {
    val tandem = LocalTandemColors.current
    Column {
        Text(label.uppercase(Locale.getDefault()), fontSize = 9.sp, letterSpacing = 0.8.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Text(value, fontSize = 14.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
    }
}

@Composable
private fun FundedLine(icon: androidx.compose.ui.graphics.vector.ImageVector, text: String) {
    val tandem = LocalTandemColors.current
    Row(verticalAlignment = Alignment.Top) {
        Icon(icon, contentDescription = null, tint = tandem.saved, modifier = Modifier.size(14.dp).padding(top = 2.dp))
        Spacer(Modifier.width(6.dp))
        Text(text, fontSize = 12.5.sp, color = tandem.muted)
    }
}

@Composable
private fun TripAction(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    enabled: Boolean,
    danger: Boolean = false,
    onClick: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val color = when {
        !enabled -> tandem.muted
        danger -> tandem.spent
        else -> MaterialTheme.colorScheme.primary
    }
    Row(
        Modifier.clip(RoundedCornerShape(8.dp)).clickable(enabled = enabled, onClick = onClick).padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(14.dp))
        Spacer(Modifier.width(5.dp))
        Text(label, color = color, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** The pill beside the dates. "Ready to go" is its own state on purpose: "Day 1" would be a claim nobody has
 *  made yet, and "in 0 days" is not an answer. */
private fun tripPill(trip: TripDto): String? = when {
    trip.isActive -> trip.day?.let { "DAY $it" }
    trip.isAwaitingStart -> "READY TO GO"
    trip.isFinished -> null
    trip.daysUntil == 0 -> "TODAY"
    trip.daysUntil != null -> "IN ${trip.daysUntil} DAYS"
    else -> null
}

// --- Create / edit ----------------------------------------------------------------------------------

/**
 * The edit form's working copy.
 *
 * ⚠️ [savingCategoryId] is carried through untouched. The server's trip edit is a **full replace**, so a form
 * that dropped it would silently unlink the savings pot every time someone corrected a name. Linking a pot is
 * still web-only; this makes sure editing here cannot destroy one.
 */
private data class TripEdit(
    val id: String?,
    val name: String,
    val from: String,
    val to: String,
    val destination: String,
    val icon: String?,
    val savingCategoryId: String?,
    val budgetText: String,
    val categoryId: String?,
) {
    fun budget(): Double? = budgetText.replace(',', '.').trim().toDoubleOrNull()?.takeIf { it > 0.0 }

    companion object {
        fun blank(): TripEdit {
            val today = LocalDate.now()
            return TripEdit(null, "", today.toString(), today.plusDays(6).toString(), "", "plane", null, "", null)
        }

        fun of(t: TripDto) = TripEdit(
            id = t.id, name = t.name, from = t.from, to = t.to, destination = t.destination.orEmpty(),
            icon = t.icon, savingCategoryId = t.savingCategoryId,
            budgetText = t.budget?.takeIf { it > 0.0 }?.let { trimZeros(it) }.orEmpty(),
            categoryId = t.categoryId,
        )

        private fun trimZeros(v: Double) = if (v % 1.0 == 0.0) v.toLong().toString() else v.toString()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TripSheet(
    edit: TripEdit,
    categories: List<CategoryOptionDto>,
    currency: String,
    saving: Boolean,
    saveError: String?,
    onSave: (TripEdit) -> Unit,
    onDismiss: () -> Unit,
) {
    var form by remember { mutableStateOf(edit) }
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tandem = LocalTandemColors.current
    val datesBackwards = runCatching { LocalDate.parse(form.to) < LocalDate.parse(form.from) }.getOrDefault(false)

    SheetScaffold(
        title = if (form.id == null) "New trip" else "Edit trip",
        saving = saving,
        canSave = form.name.isNotBlank() && !datesBackwards,
        onDismiss = onDismiss,
        onSave = { onSave(form) },
        sheetState = sheetState,
    ) {
        OutlinedTextField(
            value = form.name,
            onValueChange = { form = form.copy(name = it) },
            label = { Text("Name") },
            placeholder = { Text("Rome") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            value = form.destination,
            onValueChange = { form = form.copy(destination = it) },
            label = { Text("Where (optional)") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("Leaves")
        DateField(form.from) { form = form.copy(from = it, to = if (LocalDate.parse(it) > LocalDate.parse(form.to)) it else form.to) }
        Spacer(Modifier.height(12.dp))
        FieldLabel("Back")
        DateField(form.to) { form = form.copy(to = it) }
        Spacer(Modifier.height(6.dp))
        Text(
            "These dates say when to default new spending to this trip and when to count down — they don't decide " +
                "what's in it. An expense belongs because you file it here, so a flight paid months early counts.",
            fontSize = 12.sp, color = tandem.muted,
        )
        Spacer(Modifier.height(14.dp))

        OutlinedTextField(
            value = form.budgetText,
            onValueChange = { form = form.copy(budgetText = it) },
            label = { Text("What you expect it to cost (optional)") },
            prefix = { Text(currencySymbol(currency)) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        // One category for the whole journey — the setting that stops a fortnight away from detonating three
        // household budgets at once. "Per label" (null) keeps the old behaviour of filing each label separately.
        FieldLabel("Files into")
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            PickChip(label = "Per label", icon = null, selected = form.categoryId == null) { form = form.copy(categoryId = null) }
            categories.filter { it.parentId == null }.forEach { c ->
                PickChip(label = c.name, icon = c.icon, catName = c.name, selected = form.categoryId == c.id) {
                    form = form.copy(categoryId = c.id)
                }
            }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            "One category keeps the trip a single line in the month, instead of a Roman hotel landing in Rent.",
            fontSize = 12.sp, color = tandem.muted,
        )

        if (form.savingCategoryId != null) {
            Spacer(Modifier.height(14.dp))
            Text(
                "Funded from a savings pot — linked on the web, and kept as it is when you save here.",
                fontSize = 12.sp, color = tandem.muted,
            )
        }
        Hints(if (datesBackwards) "A trip can't end before it starts." else null, saveError)
    }
}

// --- Attaching something already paid -----------------------------------------------------------------

/**
 * Pick an expense already in the ledger and file it to the trip — the flight bought in March.
 *
 * ⚠️ Only this period's expenses are offered, because that is what a thin client currently holds. The link
 * itself has no such limit (that is the whole feature), so a booking from an older month still has to be
 * attached on the web until the ledger read is widened.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AttachExpenseSheet(
    trip: TripDto,
    expenses: List<ExpenseDto>,
    fmt: (Double) -> String,
    saving: Boolean,
    saveError: String?,
    onPick: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tandem = LocalTandemColors.current
    val candidates = remember(expenses, trip.id) { expenses.filter { it.tripId != trip.id }.sortedByDescending { it.amount } }

    SheetScaffold(
        title = "Add to ${trip.name}",
        saving = saving,
        canSave = false,
        onDismiss = onDismiss,
        onSave = {},
        sheetState = sheetState,
        saveLabel = "Done",
    ) {
        if (candidates.isEmpty()) {
            Text("Nothing else in this month to file here.", color = tandem.muted, fontSize = 13.sp)
        } else {
            Text(
                "Anything you already paid for — the flight, the hotel. It stays in the month you paid it; it just " +
                    "starts counting toward this trip.",
                fontSize = 12.sp, color = tandem.muted,
            )
            Spacer(Modifier.height(10.dp))
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                candidates.forEach { e ->
                    Row(
                        Modifier.fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .clickable(enabled = !saving) { onPick(e.id) }
                            .padding(vertical = 10.dp, horizontal = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        CatIcon(e.categoryIcon, e.categoryName, size = 16.dp)
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(e.note?.takeIf { it.isNotBlank() } ?: e.categoryName, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface)
                            Text(
                                buildString {
                                    append(prettyDay(e.date))
                                    if (e.tripId != null) append(" · already on another trip")
                                },
                                fontSize = 11.5.sp,
                                color = if (e.tripId != null) tandem.warn else tandem.muted,
                            )
                        }
                        Text(fmt(e.amount), fontWeight = FontWeight.Bold, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface)
                    }
                }
            }
        }
        Hints(null, saveError)
    }
}

// --- bits -------------------------------------------------------------------------------------------

private fun prettyDay(iso: String, withYear: Boolean = false): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern(if (withYear) "d MMM yyyy" else "d MMM", Locale.getDefault()))
}.getOrDefault(iso)

private fun dayOfMonth(iso: String): String = runCatching { LocalDate.parse(iso).dayOfMonth.toString().padStart(2, '0') }.getOrDefault("--")

private fun monthShort(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("MMM", Locale.getDefault()))
}.getOrDefault("")
