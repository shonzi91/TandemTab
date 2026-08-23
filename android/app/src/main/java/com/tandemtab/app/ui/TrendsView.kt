package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
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
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.TrendRowDto
import com.tandemtab.app.data.TrendsViewDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

// The web's Trends palette, kept to the digit rather than mapped onto the app's money tokens — these three
// colours mean "in / out / what was kept" only inside this chart, and the reasoning behind them is specific:
// net is drawn in whichever direction it went, so the COLOUR is the answer, and it is deliberately not the
// money-in green, because a green net bar beside a green in bar reads as the same measurement twice.
private val TrendIn = Color(0xFF13A06E)
private val TrendOut = Color(0xFFFF7A59)
private val TrendNetUp = Color(0xFF0E7C55)
private val TrendNetDown = Color(0xFFDC2626)

/**
 * Money over time — one column per period, and the figures beside them.
 *
 * ★ **Three series, not four.** The web draws Saved and Balance too, on their own axis, because a balance is a
 * STOCK where the rest are FLOWS and sharing one linear scale lets it flatten everything being compared. A phone
 * column is a fraction of that width, so the same problem arrives sooner: the chart keeps in / spent / net, and
 * Saved and Balance are stated as figures in the rows underneath, where they are readable rather than decorative.
 *
 * ⚠️ Every figure is the server's ([TrendsViewDto]) — including `net`, which is sent rather than subtracted here
 * so the two clients cannot come to disagree about what a month kept.
 */
@Composable
fun TrendsView(trends: TrendsViewDto?, loading: Boolean, range: String, onRange: (String) -> Unit) {
    val tandem = LocalTandemColors.current
    val money = moneyFormatter(trends?.currency.orEmpty())

    // The web's ranges, minus the ones a phone has no room to distinguish. "All" sends no window at all — see
    // AppViewModel.loadTrends for why that is not the same as a very wide one.
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        listOf("3m" to "3 months", "12m" to "12 months", "all" to "All time").forEach { (key, label) ->
            PickChip(label = label, icon = null, selected = range == key) { onRange(key) }
        }
    }
    Spacer(Modifier.height(16.dp))

    if (loading && trends == null) {
        Box(Modifier.fillMaxWidth().height(200.dp), contentAlignment = Alignment.Center) {
            CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
        }
        return
    }
    val rows = trends?.rows.orEmpty()
    if (rows.isEmpty()) {
        Text(
            "There's no history to chart yet — this needs a month or two of periods behind it.",
            fontSize = 13.sp, color = tandem.muted,
        )
        return
    }

    // ⚠️ No chart for a single period, and this was found by running it: one row draws a lone orange stick in the
    // middle of 150dp of empty space, which reads as a broken chart rather than as a month. A trend needs two
    // points to be a trend; with one, the row underneath already says everything the bars could.
    if (rows.size >= 2) {
        TrendBars(rows)
        MonthAxis(rows)
        Spacer(Modifier.height(10.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(14.dp), modifier = Modifier.fillMaxWidth()) {
            LegendDot("In", TrendIn)
            LegendDot("Spent", TrendOut)
            // ★ The legend follows the data, because the colour IS the answer here: a month that spent more than
            // it took in is drawn red and below the line. A fixed green "Kept" key beside a red bar would be the
            // legend contradicting the chart it explains.
            LegendDot("Kept", TrendNetUp)
            if (rows.any { it.net < 0 }) LegendDot("Overspent", TrendNetDown)
        }
    }

    Spacer(Modifier.height(14.dp))
    rows.asReversed().forEach { r ->
        // Newest first in the list, oldest-first in the chart. Not an inconsistency: a chart reads left-to-right
        // as time passing, and a list is read from the top, where the month you are actually in belongs.
        Column(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    monthLabel(r.from),
                    fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
                    color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f),
                )
                Text(
                    (if (r.net >= 0) "+" else "") + money(r.net),
                    fontSize = 14.sp, fontWeight = FontWeight.Bold,
                    color = if (r.net >= 0) tandem.positive else tandem.spent,
                )
            }
            Text(
                buildString {
                    append("in ").append(money(r.income))
                    append(" · spent ").append(money(r.spent))
                    if (r.saved > 0.0) append(" · set aside ").append(money(r.saved))
                    if (r.debtPaid > 0.0) append(" · debt ").append(money(r.debtPaid))
                    append(" · left ").append(money(r.balance))
                },
                fontSize = 11.sp, color = tandem.muted,
            )
        }
    }
}

/**
 * The grouped bars. One shared scale across all three series and every period, because the whole question is how
 * these months compare — a per-column scale would make every month look identical.
 *
 * ⚠️ A negative net hangs BELOW the baseline rather than being clamped to it. Drawing "spent more than came in"
 * as a zero-height bar would say the month broke even, which is the one thing it did not do.
 */
@Composable
private fun TrendBars(rows: List<TrendRowDto>) {
    val axis = MaterialTheme.colorScheme.outline
    val scale = rows.flatMap { listOf(it.income, it.spent, kotlin.math.abs(it.net)) }.maxOrNull() ?: 0.0
    // ⚠️ The height and the baseline are tuned together with the axis Row below: the labels sit under the whole
    // canvas, so every pixel of unused space beneath the baseline becomes a gap between the bars and the months
    // they are named by. Observed on a device at 150dp/0.72 — the months floated well clear of the chart.
    Canvas(Modifier.fillMaxWidth().height(132.dp)) {
        if (scale <= 0.0) return@Canvas
        val slot = size.width / rows.size
        val barW = (slot / 4.2f).coerceAtMost(14f * density)
        val gap = barW * 0.18f
        // Most of the height above the line for the flows, a strip below it for a negative net. The baseline is
        // drawn either way, so the reader can see which side a bar is on even when nothing is below it.
        val baseline = size.height * 0.82f
        drawLine(axis, Offset(0f, baseline), Offset(size.width, baseline), strokeWidth = 1f * density)
        rows.forEachIndexed { i, r ->
            val centre = slot * i + slot / 2f
            val left = centre - (barW * 1.5f + gap)
            fun bar(index: Int, value: Double, color: Color) {
                val h = (kotlin.math.abs(value) / scale * (if (value < 0) size.height - baseline else baseline)).toFloat()
                if (h <= 0f) return
                val x = left + index * (barW + gap)
                val y = if (value < 0) baseline else baseline - h
                drawRoundRect(
                    color = color,
                    topLeft = Offset(x, y),
                    size = Size(barW, h),
                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(2f * density, 2f * density),
                )
            }
            bar(0, r.income, TrendIn)
            bar(1, r.spent, TrendOut)
            bar(2, r.net, if (r.net >= 0) TrendNetUp else TrendNetDown)
        }
    }
}

/**
 * Month labels under the columns. A separate Row rather than text inside the Canvas, laid out with equal weights
 * so each label sits under the slot [TrendBars] gave that period — the chart divides its width evenly, so the
 * two agree by construction rather than by a shared magic number.
 *
 * ⚠️ Past six columns only every other label is drawn. Twelve three-letter months across a phone would overlap
 * into an unreadable band, and an axis you cannot read is worse than one that labels half the columns.
 */
@Composable
private fun MonthAxis(rows: List<TrendRowDto>) {
    val muted = LocalTandemColors.current.muted
    val everyOther = rows.size > 6
    Row(Modifier.fillMaxWidth().padding(top = 4.dp)) {
        rows.forEachIndexed { i, r ->
            Box(Modifier.weight(1f), contentAlignment = Alignment.Center) {
                if (!everyOther || i % 2 == 0) {
                    Text(shortMonth(r.from), fontSize = 10.sp, color = muted)
                }
            }
        }
    }
}

@Composable
private fun LegendDot(label: String, color: Color) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(9.dp).clip(RoundedCornerShape(3.dp)).background(color))
        Spacer(Modifier.width(6.dp))
        Text(label, fontSize = 11.sp, color = LocalTandemColors.current.muted)
    }
}

/** "2026-08-01" → "Aug 2026". Falls back to the raw string rather than throwing: a chart with one odd label is
 *  better than a screen that crashes because a date arrived in a shape nobody expected. */
private fun monthLabel(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault()))
}.getOrDefault(iso)

/** "2026-08-01" → "Aug", for the axis under the columns. */
private fun shortMonth(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("MMM", Locale.getDefault()))
}.getOrDefault("")
