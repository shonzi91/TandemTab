package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.border
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.AchievementsUi
import com.tandemtab.app.data.AchievementDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * The Achievements sheet — the native counterpart of the web's 🏆 modal, reached from the Home milestones line.
 *
 * Read-only, and the whole catalogue (which medals exist, which are earned, how far along the rest are) is
 * computed server-side by the same domain service that answers `/milestones`. Nothing about "have I earned this"
 * is decided in Kotlin: a second reading of those rules would be a second place to disagree with the web about
 * whether someone has achieved something, which is the one promise this screen exists to keep.
 *
 * Layout follows the web's ring grid: earned medals first (a coin struck in the tier's metal, with a tick), then
 * the locked ones ordered by how close they are, so the next one within reach heads what is left.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AchievementsSheet(achievements: AchievementsUi, onDismiss: () -> Unit, onRetry: () -> Unit) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 18.dp)
                .verticalScroll(rememberScrollState())
                .padding(bottom = 28.dp),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Icon(TandemIcons.Trophy, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
                Spacer(Modifier.width(8.dp))
                Text(
                    "Achievements",
                    modifier = Modifier.weight(1f),
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                IconButton(onClick = onDismiss) { Icon(TandemIcons.Close, "Close", tint = tandem.muted) }
            }

            val view = achievements.data
            when {
                // An error is shown rather than swallowed here (unlike the Home line): this screen was asked for,
                // so an empty sheet with no explanation would just look broken.
                achievements.error != null && view == null -> {
                    Spacer(Modifier.height(12.dp))
                    Text(achievements.error, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    Spacer(Modifier.height(12.dp))
                    OutlinedButton(onClick = onRetry) { Text("Try again") }
                    Spacer(Modifier.height(12.dp))
                }

                view == null -> {
                    Spacer(Modifier.height(28.dp))
                    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) { LogoLoader() }
                    Spacer(Modifier.height(28.dp))
                }

                view.items.isEmpty() -> {
                    Spacer(Modifier.height(16.dp))
                    Text("No milestones to show yet.", color = tandem.muted, fontSize = 14.sp)
                }

                else -> {
                    Spacer(Modifier.height(4.dp))
                    Text("${view.earned} of ${view.total} earned", color = tandem.muted, fontSize = 13.sp)
                    Spacer(Modifier.height(18.dp))

                    // Earned first, then everything else by how close it is — the web's order, and the reason the
                    // locked half is worth showing at all: the top of it is what to aim at next.
                    val ordered = view.items.filter { it.earned } +
                        view.items.filter { !it.earned }.sortedByDescending { it.percent ?: 0 }

                    // Three to a row, laid out by hand: the sheet already scrolls, and a lazy grid nested inside a
                    // scrolling column has no height the two can agree on.
                    ordered.chunked(3).forEach { row ->
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            row.forEach { a -> Box(Modifier.weight(1f)) { AchievementCell(a) } }
                            // Keep a short last row's cells the same width as every other row's.
                            repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
                        }
                        Spacer(Modifier.height(18.dp))
                    }
                }
            }
        }
    }
}

/** One catalogue entry: the medal, its name, what it asks for, and either the date won or how far along it is. */
@Composable
private fun AchievementCell(a: AchievementDto) {
    val tandem = LocalTandemColors.current
    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
        AchievementBadge(a)
        Spacer(Modifier.height(8.dp))
        Text(
            maskServerText(a.title),
            fontSize = 12.sp,
            fontWeight = FontWeight.Bold,
            textAlign = TextAlign.Center,
            color = if (a.earned) MaterialTheme.colorScheme.onSurface else tandem.muted,
        )
        Text(maskServerText(a.desc), fontSize = 10.sp, textAlign = TextAlign.Center, color = tandem.muted)
        val sub = when {
            a.earned -> a.earnedOn?.let { prettyEarnedOn(it) }
            (a.percent ?: 0) > 0 -> "${a.percent}%"
            else -> null
        }
        sub?.let {
            Text(it, fontSize = 10.sp, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center, color = tandem.muted)
        }
    }
}

/**
 * The emblem, in the web's three states: an earned medal is a coin struck in the tier's metal and ticked; one in
 * progress keeps the gold arc, so how close it is stays visible; one not started is a flat muted disc.
 *
 * ⚠️ The locked icon is dimmed with alpha where the web desaturates it — an emoji can't be greyscaled in Compose
 * without pushing it through a colour filter, and a half-transparent glyph reads the same way at this size.
 */
@Composable
private fun AchievementBadge(a: AchievementDto) {
    val tandem = LocalTandemColors.current
    Box(Modifier.fillMaxWidth(0.74f).aspectRatio(1f), contentAlignment = Alignment.Center) {
        when {
            a.earned -> {
                val (fill, rim) = tierMetal(a.tier)
                // Drawn rather than backgrounded because the highlight has to sit up and to the left of centre,
                // which is what makes the disc read as struck metal instead of a coloured circle — and a radial
                // brush can only be placed once the pixel size is known.
                Canvas(Modifier.fillMaxWidth().aspectRatio(1f)) {
                    val r = size.minDimension / 2f
                    val rimWidth = r * 0.09f
                    drawCircle(
                        brush = Brush.radialGradient(
                            colorStops = fill,
                            center = Offset(size.width * 0.34f, size.height * 0.28f),
                            radius = r * 1.5f,
                        ),
                        radius = r - rimWidth,
                    )
                    drawCircle(color = rim, radius = r - rimWidth / 2f, style = Stroke(width = rimWidth))
                }
                Text(a.icon, fontSize = 22.sp)
                // The tick sits on the rim, as on the web: the metal alone doesn't say "earned" to anyone who
                // hasn't yet learned what the three metals mean.
                Box(
                    Modifier
                        .align(Alignment.BottomEnd)
                        .size(18.dp)
                        .clip(CircleShape)
                        .background(EarnedTick)
                        // The rim is the sheet's own surface, so the tick reads as lifted off the coin in both themes.
                        .border(2.dp, MaterialTheme.colorScheme.surface, CircleShape),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(TandemIcons.Check, contentDescription = null, tint = Color.White, modifier = Modifier.size(10.dp))
                }
            }

            (a.percent ?: 0) > 0 -> {
                val pct = (a.percent ?: 0).coerceIn(0, 100)
                // Read outside the draw lambda: a DrawScope has no composition to look a theme colour up in.
                val track = tandem.hairline
                Canvas(Modifier.fillMaxWidth().aspectRatio(1f)) {
                    val stroke = size.minDimension * 0.09f
                    val inset = stroke / 2f
                    val arcSize = Size(size.width - stroke, size.height - stroke)
                    drawArc(
                        color = track, startAngle = 0f, sweepAngle = 360f, useCenter = false,
                        topLeft = Offset(inset, inset), size = arcSize, style = Stroke(width = stroke),
                    )
                    drawArc(
                        color = MedalGold, startAngle = -90f, sweepAngle = 360f * pct / 100f, useCenter = false,
                        topLeft = Offset(inset, inset), size = arcSize,
                        style = Stroke(width = stroke, cap = StrokeCap.Round),
                    )
                }
                Text(a.icon, fontSize = 22.sp, modifier = Modifier.alpha(0.5f))
            }

            else -> {
                Box(
                    Modifier
                        .fillMaxWidth()
                        .aspectRatio(1f)
                        .clip(CircleShape)
                        .background(MaterialTheme.colorScheme.surfaceVariant)
                        .border(2.dp, tandem.hairline, CircleShape),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(a.icon, fontSize = 22.sp, modifier = Modifier.alpha(0.45f))
                }
            }
        }
    }
}

/** The web's medal gold (ProgressRing `.gold`) and the green of its earned tick. */
private val MedalGold = Color(0xFFF5B301)
private val EarnedTick = Color(0xFF16A34A)

/** Coin fill + rim per tier, the web's radial gradients ported stop for stop. */
private fun tierMetal(tier: String): Pair<Array<Pair<Float, Color>>, Color> = when (tier.lowercase(Locale.ROOT)) {
    "gold" -> arrayOf(
        0f to Color(0xFFFFE9A3), 0.42f to Color(0xFFF6C945), 0.78f to Color(0xFFE5A913), 1f to Color(0xFFC8890A),
    ) to Color(0xFFF0B429)
    "silver" -> arrayOf(
        0f to Color(0xFFFFFFFF), 0.44f to Color(0xFFD9E0E8), 0.80f to Color(0xFFAAB4C1), 1f to Color(0xFF8892A1),
    ) to Color(0xFFC1C9D4)
    else -> arrayOf(
        0f to Color(0xFFF6D9B0), 0.44f to Color(0xFFCF8F52), 0.80f to Color(0xFFA9662F), 1f to Color(0xFF834A20),
    ) to Color(0xFFBD7C3E)
}

/** "18 Aug 2026" — the web's dd MMM yyyy. Falls back to the raw stamp rather than dropping the date. */
private fun prettyEarnedOn(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("d MMM yyyy", Locale.getDefault()))
}.getOrDefault(iso)
