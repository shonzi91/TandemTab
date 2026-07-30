package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.HealthUi
import com.tandemtab.app.data.InsightMiniTrendDto
import com.tandemtab.app.data.InsightSignalDto
import com.tandemtab.app.data.InsightsDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import com.tandemtab.app.ui.theme.TandemColors

private fun bandColor(band: String, tandem: TandemColors): Color = when (band) {
    "healthy" -> tandem.positive
    "at_risk" -> tandem.spent
    else -> tandem.warn
}

private fun bandLabel(band: String): String = when (band) {
    "healthy" -> "Healthy"
    "at_risk" -> "Needs attention"
    else -> "Getting there"
}

/**
 * The Home "Health score & trends" card (S68): the score + verdict + trends-over-time sparklines. The
 * rate/spend detail lives in the modal it opens (tap anywhere on the card).
 */
@Composable
fun HealthCard(health: HealthUi, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    val money = sheetMoney(health.currency)

    when {
        health.loading && health.data == null ->
            Box(Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
            }

        health.data?.hasData != true -> Unit  // nothing to score yet — hide the card

        else -> {
            val d = health.data
            val accent = bandColor(d.band, tandem)
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
                    .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
                    .clickable(onClick = onOpen)
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "HEALTH SCORE & TRENDS",
                        fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold,
                        color = tandem.muted, modifier = Modifier.weight(1f),
                    )
                    OpenChip()
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("${d.score}", fontSize = 40.sp, fontWeight = FontWeight.ExtraBold, color = accent)
                    Text(" / 100", fontSize = 15.sp, color = tandem.muted)
                    Spacer(Modifier.weight(1f))
                    d.scoreDelta?.takeIf { it != 0 }?.let {
                        val up = it > 0
                        Text(
                            (if (up) "▲ +$it" else "▼ $it"),
                            color = if (up) tandem.positive else tandem.spent,
                            fontSize = 13.sp, fontWeight = FontWeight.Bold,
                        )
                    }
                }
                ScoreMeter(d.score, accent)
                Text(InsightNarrator.render(d.verdict, money), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)

                if (d.miniTrends.isNotEmpty()) {
                    Row(
                        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) { d.miniTrends.forEach { MiniTrendChip(it, money) } }
                }
            }
        }
    }
}

/** The full Insights modal: score panel, savings rate, outgoings trend, signals, quick wins, category breakdown. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HealthSheet(health: HealthUi, onDismiss: () -> Unit) {
    val tandem = LocalTandemColors.current
    val money = sheetMoney(health.currency)
    val d = health.data ?: return
    val accent = bandColor(d.band, tandem)
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier
                .fillMaxWidth()
                .imePadding()
                .padding(horizontal = 18.dp)
                .verticalScroll(rememberScrollState())
                .padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("Health score", modifier = Modifier.weight(1f), fontSize = 18.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
                IconButton(onClick = onDismiss) { Icon(TandemIcons.Close, "Close", tint = tandem.muted) }
            }

            // Score panel.
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(verticalAlignment = Alignment.Bottom) {
                    Text("${d.score}", fontSize = 46.sp, fontWeight = FontWeight.ExtraBold, color = accent)
                    Text(" / 100 · ${bandLabel(d.band)}", fontSize = 14.sp, color = tandem.muted, modifier = Modifier.padding(bottom = 8.dp))
                }
                ScoreMeter(d.score, accent)
                Text(InsightNarrator.render(d.verdict, money), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)
                if (d.summary.isNotEmpty()) Text(InsightNarrator.render(d.summary, money), color = tandem.muted, fontSize = 13.sp)
            }

            // Savings rate.
            d.savingsRate?.let { rate ->
                SectionLabel("Savings rate")
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("${(rate * 100).toInt()}%", fontSize = 22.sp, fontWeight = FontWeight.ExtraBold, color = tandem.saved)
                    Text("  target ${(d.savingsTarget * 100).toInt()}%", fontSize = 13.sp, color = tandem.muted)
                }
                Meter(fraction = (rate / d.savingsTarget.coerceAtLeast(0.0001)).toFloat().coerceIn(0f, 1f), color = tandem.saved)
            }
            if (d.savingsCritique.isNotEmpty()) {
                Text(InsightNarrator.render(d.savingsCritique, money), color = tandem.muted, fontSize = 13.sp)
            }

            // Outgoings trend.
            if (d.trend.isNotEmpty()) {
                SectionLabel("Spending trend")
                Row(
                    Modifier.fillMaxWidth().height(96.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.Bottom,
                ) {
                    d.trend.forEach { p ->
                        Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Bottom) {
                            Box(
                                Modifier
                                    .fillMaxWidth()
                                    .height((8 + 70 * p.barFraction).dp.coerceAtLeast(4.dp))
                                    .background(if (p.isCurrent) accent else tandem.segmentTrack, RoundedCornerShape(topStart = 4.dp, topEnd = 4.dp)),
                            )
                            Spacer(Modifier.height(4.dp))
                            Text(p.label, fontSize = 9.sp, color = tandem.muted, maxLines = 1)
                        }
                    }
                }
                Text(InsightNarrator.render(d.trendNote, money), color = tandem.muted, fontSize = 13.sp)
            }

            // Signals.
            if (d.signals.isNotEmpty()) {
                SectionLabel("Signals")
                d.signals.forEach { SignalCard(it, money) }
            }

            // Quick wins.
            if (d.quickWins.isNotEmpty()) {
                SectionLabel("Quick wins")
                d.quickWins.forEach { w ->
                    Row {
                        Text("• ", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold)
                        Text(InsightNarrator.render(w, money), color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
                    }
                }
            }

            // Category breakdown.
            if (d.breakdown.isNotEmpty()) {
                SectionLabel("Where it went")
                d.breakdown.forEach { c ->
                    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            if (!c.icon.isNullOrBlank()) { Text(c.icon, fontSize = 14.sp); Spacer(Modifier.width(6.dp)) }
                            Text(c.name, modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
                            Text(money(c.amount), fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
                        }
                        Meter(fraction = c.barFraction.toFloat().coerceIn(0f, 1f), color = tandem.spent)
                    }
                }
            }
        }
    }
}

@Composable
private fun SectionLabel(text: String) {
    Text(text.uppercase(), fontSize = 10.sp, letterSpacing = 1.2.sp, fontWeight = FontWeight.Bold, color = LocalTandemColors.current.muted)
}

@Composable
private fun ScoreMeter(score: Int, accent: Color) {
    val tandem = LocalTandemColors.current
    Box(Modifier.fillMaxWidth().height(8.dp).background(tandem.segmentTrack, RoundedCornerShape(4.dp))) {
        Box(Modifier.fillMaxWidth((score / 100f).coerceIn(0f, 1f)).height(8.dp).background(accent, RoundedCornerShape(4.dp)))
    }
}

@Composable
private fun Meter(fraction: Float, color: Color) {
    val tandem = LocalTandemColors.current
    Box(Modifier.fillMaxWidth().height(8.dp).background(tandem.segmentTrack, RoundedCornerShape(4.dp))) {
        Box(Modifier.fillMaxWidth(fraction).height(8.dp).background(color, RoundedCornerShape(4.dp)))
    }
}

@Composable
private fun SignalCard(s: InsightSignalDto, money: (Double) -> String) {
    val tandem = LocalTandemColors.current
    val accent = when (s.kind) {
        "good" -> tandem.positive
        "warn" -> tandem.spent
        else -> tandem.muted
    }
    Row(
        Modifier
            .fillMaxWidth()
            .background(tandem.hero, RoundedCornerShape(12.dp))
            .border(1.dp, tandem.hairline, RoundedCornerShape(12.dp))
            .padding(12.dp),
    ) {
        Box(Modifier.width(3.dp).height(38.dp).background(accent, RoundedCornerShape(2.dp)))
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(InsightNarrator.render(s.title, money), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
            Text(InsightNarrator.render(s.desc, money), color = tandem.muted, fontSize = 12.sp)
        }
        val badge = InsightNarrator.render(s.delta, money)
        if (badge.isNotBlank() && badge != "—") {
            Spacer(Modifier.width(8.dp))
            Text(badge, color = accent, fontWeight = FontWeight.Bold, fontSize = 12.sp)
        }
    }
}

@Composable
private fun MiniTrendChip(t: InsightMiniTrendDto, money: (Double) -> String) {
    val tandem = LocalTandemColors.current
    val dirColor = when (t.dir) { "up" -> tandem.positive; "down" -> tandem.spent; else -> tandem.muted }
    Column(
        Modifier
            .width(120.dp)
            .background(tandem.hero, RoundedCornerShape(12.dp))
            .border(1.dp, tandem.hairline, RoundedCornerShape(12.dp))
            .padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!t.icon.isNullOrBlank()) { Text(t.icon, fontSize = 12.sp); Spacer(Modifier.width(4.dp)) }
            Text(InsightNarrator.render(t.label, money), fontSize = 11.sp, color = tandem.muted, maxLines = 1)
        }
        Text(InsightNarrator.render(t.currentText, money), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp, maxLines = 1)
        Sparkline(t.points, dirColor)
    }
}

@Composable
private fun Sparkline(points: List<Double>, color: Color) {
    if (points.size < 2) { Spacer(Modifier.height(22.dp)); return }
    val min = points.min()
    val max = points.max()
    val span = (max - min).takeIf { it > 0 } ?: 1.0
    Canvas(Modifier.fillMaxWidth().height(22.dp)) {
        val stepX = size.width / (points.size - 1)
        var prev: Offset? = null
        points.forEachIndexed { i, v ->
            val x = stepX * i
            val y = size.height - ((v - min) / span).toFloat() * size.height
            val cur = Offset(x, y)
            prev?.let { drawLine(color, it, cur, strokeWidth = 3f) }
            prev = cur
        }
    }
}
