package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.BreakdownSliceDto
import com.tandemtab.app.data.BreakdownViewDto
import com.tandemtab.app.ui.theme.LocalTandemColors

/**
 * Where the money went — the ring, and the four figures that reconcile to what the balance did.
 *
 * ★★ **The ring is SPENDING.** Savings are not slices (the money never left the account) and goal payouts are not
 * slices (a pie is a composition chart; a €12,000 prepayment beside €30 of groceries is not one). Both are stated
 * as figures instead, so nothing is hidden — and the centre total **equals** the Spent figure beside it, because
 * a chart whose total disagrees with its own wedges is the bug all of this was written to prevent.
 *
 * Every figure and every colour comes from the server ([BreakdownViewDto]); this file only draws them.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BreakdownSheet(
    breakdown: BreakdownViewDto?,
    loading: Boolean,
    onDismiss: () -> Unit,
    onGroupBy: (String) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val money = moneyFormatter(breakdown?.currency.orEmpty())

    SheetScaffold(
        title = "Where your money went",
        saving = false,
        canSave = false,
        onDismiss = onDismiss,
        onSave = {},
        sheetState = sheetState,
        saveLabel = "",
    ) {
        if (loading || breakdown == null) {
            Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }
            return@SheetScaffold
        }

        // What the wedges group by. Three answers to "where", not three charts.
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf("category" to "Category", "tag" to "Label", "fund" to "Wallet").forEach { (key, label) ->
                PickChip(label = label, icon = null, selected = breakdown.groupBy == key) { onGroupBy(key) }
            }
        }
        Spacer(Modifier.height(16.dp))

        if (breakdown.slices.isEmpty()) {
            Text("Nothing has left the account in this period yet.", fontSize = 13.sp, color = tandem.muted)
        } else {
            Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) {
                BreakdownRing(breakdown.slices, size = 180.dp, stroke = 26.dp)
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(money(breakdown.spent), fontSize = 22.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onBackground)
                    // "used", not "spent": the word under the total has to move with the figure, and this total is
                    // everything that left the account — expenses and transfers out both.
                    Text("used", fontSize = 11.sp, color = tandem.muted)
                }
            }

            Spacer(Modifier.height(8.dp))
            breakdown.slices.forEach { s ->
                Row(Modifier.fillMaxWidth().padding(vertical = 5.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(10.dp).clip(RoundedCornerShape(3.dp)).background(parseColor(s.color)))
                    Spacer(Modifier.width(10.dp))
                    Text(s.label, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
                    Text(money(s.amount), fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface)
                }
            }
        }

        // ── The four figures ────────────────────────────────────────────────────────────────────
        // They reconcile to what the balance did, which is what lets the ring stay a chart of spending only.
        Spacer(Modifier.height(16.dp))
        SummaryLine("Income", money(breakdown.income), good = true)
        SummaryLine("Spent", money(breakdown.spent))
        if (breakdown.setAside > 0.0) SummaryLine("Set aside", "+" + money(breakdown.setAside), good = true)
        if (breakdown.paidToGoals > 0.0) {
            SummaryLine("Paid to goals", money(breakdown.paidToGoals))
            // Named rather than left as a total — the tooltip's job on the web, a line here.
            Text(
                breakdown.payouts.joinToString(" · ") { "${it.name} ${money(it.amount)}" },
                fontSize = 11.sp, color = tandem.muted, modifier = Modifier.padding(start = 2.dp, bottom = 4.dp),
            )
        }
        Spacer(Modifier.height(8.dp))
    }
}

/**
 * The spending ring itself — every colour and every amount the server's, this only draws them.
 *
 * Shared by the sheet and by Home's card so there is exactly one of these on the phone: the seam rule below is
 * the kind of thing that gets fixed in one copy and not the other. Draw the centre content by stacking it in a
 * [Box] over this — the ring keeps no opinion about what sits in its hole.
 */
@Composable
fun BreakdownRing(slices: List<BreakdownSliceDto>, size: Dp, stroke: Dp) {
    val total = slices.sumOf { it.amount }
    val trackColor = MaterialTheme.colorScheme.outline
    Canvas(Modifier.size(size)) {
        val strokeStyle = Stroke(width = stroke.toPx(), cap = StrokeCap.Butt)
        val inset = stroke.toPx() / 2f
        val arcSize = Size(this.size.width - inset * 2, this.size.height - inset * 2)
        val topLeft = Offset(inset, inset)
        drawArc(trackColor, 0f, 360f, false, topLeft, arcSize, style = strokeStyle)
        if (slices.size == 1) {
            // A full ring, not a 360° arc — two butt caps meeting at the same angle leave a seam.
            drawCircle(parseColor(slices[0].color), radius = arcSize.width / 2f, style = strokeStyle)
        } else {
            var start = -90f
            slices.forEach { s ->
                val sweep = if (total > 0.0) (s.amount / total * 360.0).toFloat() else 0f
                drawArc(parseColor(s.color), start, sweep, false, topLeft, arcSize, style = strokeStyle)
                start += sweep
            }
        }
    }
}

@Composable
private fun SummaryLine(label: String, value: String, good: Boolean = false) {
    val tandem = LocalTandemColors.current
    Row(Modifier.fillMaxWidth().padding(vertical = 3.dp)) {
        Text(label, fontSize = 13.sp, color = tandem.muted, modifier = Modifier.weight(1f))
        Text(
            value,
            fontSize = 13.sp,
            fontWeight = FontWeight.Bold,
            color = if (good) tandem.saved else MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** "#rrggbb" from the server. Falls back to grey rather than throwing — a chart with one odd wedge is far better
 *  than a screen that crashes because a colour string changed shape. Internal because Home's card paints its
 *  legend dots with the same server colours the ring uses; a second parser would be a second fallback. */
internal fun parseColor(hex: String): Color = runCatching {
    Color(android.graphics.Color.parseColor(if (hex.startsWith("#")) hex else "#$hex"))
}.getOrDefault(Color(0xFF9AA5B1))
