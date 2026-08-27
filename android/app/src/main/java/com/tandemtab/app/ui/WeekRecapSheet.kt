package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.RecapSliceDto
import com.tandemtab.app.data.WeeklyRecapViewDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * "Your week in money" — the whole week behind Home's card, ported from the web's `Modal.WeekRecap`.
 *
 * ★ **Nothing here is computed.** Every figure comes off [WeeklyRecapViewDto] exactly as the server sent it, and
 * that is the point of the read existing: the rules behind these numbers — which week is covered, that a
 * disbursement is not negative saving, that carryover is not income, that "left over" is measured against a
 * *typical* week rather than the one salary lands in — live in one `WeeklyRecapService` on the server, not in a
 * second implementation over here that would drift the first time either side was edited.
 *
 * ⚠️ The income tile changes its **label**, not just its number, on [WeeklyRecapViewDto.incomeIsTypical]. The
 * same figure under "Money in" would be a claim that money arrived, which most weeks it did not.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WeekRecapSheet(recap: WeeklyRecapViewDto, onDismiss: () -> Unit) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val money = moneyFormatter(recap.currency)
    val down = recap.change < 0.0

    SheetScaffold(
        title = "Your week in money",
        saving = false,
        canSave = false,
        onDismiss = onDismiss,
        onSave = {},
        sheetState = sheetState,
        saveLabel = "",
    ) {
        Text(
            "${weekDayLabel(recap.from)} – ${weekDayLabel(recap.to)}",
            fontSize = 12.sp, color = tandem.muted,
        )
        Spacer(Modifier.height(14.dp))

        // Four tiles in the order the questions get asked: what went out, what came in, what that left, what was
        // set aside. Two to a row on a phone; the web's grid does the same at this width.
        // ⚠️ IntrinsicSize.Min, so both tiles in a row take the height of the taller one. Only one of them
        // carries a footnote, and without this the row draws one tall box beside one short one — which reads as
        // a layout accident rather than as a pair. The web's grid equalises its cells for the same reason.
        Row(
            Modifier.fillMaxWidth().height(IntrinsicSize.Min),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Tile(
                label = "Spent",
                value = money(recap.spent),
                modifier = Modifier.weight(1f),
                // Only against a week that actually had spending — see hasComparison on the DTO.
                foot = if (!recap.hasComparison) null else
                    "${money(abs(recap.change))} ${if (down) "less" else "more"} than the week before",
                footColor = if (down) tandem.positive else tandem.spent,
            )
            Tile(
                label = if (recap.incomeIsTypical) "Typical income" else "Money in",
                value = money(recap.effectiveIncome),
                modifier = Modifier.weight(1f),
            )
        }
        Spacer(Modifier.height(10.dp))
        Row(
            Modifier.fillMaxWidth().height(IntrinsicSize.Min),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Tile(
                label = "Left over",
                value = money(recap.net),
                modifier = Modifier.weight(1f),
                valueColor = if (recap.net < 0.0) tandem.spent else tandem.positive,
                foot = if (recap.incomeIsTypical) "vs a typical week" else "in minus out",
            )
            Tile(
                label = "Set aside",
                value = money(recap.saved),
                modifier = Modifier.weight(1f),
                valueColor = if (recap.saved > 0.0) tandem.positive else null,
                // The painless slice: money that rounded up into savings without a decision. Drawn only when
                // there is some, so it reads as a reward for using round-ups and not a nag to switch them on.
                foot = if (recap.roundUpsSaved > 0.0) "${money(recap.roundUpsSaved)} via round-ups" else null,
            )
        }

        // How OFTEN, not just how much: one big week and forty small days are different habits and a total alone
        // cannot tell them apart. The average needs more than one entry — "average of 1" is the same number twice.
        Spacer(Modifier.height(16.dp))
        Text(
            buildString {
                append(recap.expenseCount)
                append(if (recap.expenseCount == 1) " transaction" else " transactions")
                if (recap.expenseCount > 1) append("  ·  ${money(recap.spent / recap.expenseCount)} on average")
            },
            fontSize = 12.sp, color = tandem.muted,
        )

        recap.biggest?.let { big ->
            Spacer(Modifier.height(18.dp))
            SectionHeading("Biggest single expense")
            Spacer(Modifier.height(8.dp))
            Row(
                Modifier.fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
                    .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
                    .padding(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                CatIcon(big.categoryIcon, big.categoryName)
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        big.categoryName, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    // The note is usually the only thing that names the purchase.
                    big.note?.takeIf { it.isNotBlank() }?.let {
                        Text(it, fontSize = 11.sp, color = tandem.muted, maxLines = 1)
                    }
                }
                Column(horizontalAlignment = Alignment.End) {
                    Text(
                        money(big.amount), fontSize = 13.sp, fontWeight = FontWeight.Bold,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    Text(prettyDateLabel(big.date), fontSize = 11.sp, color = tandem.muted)
                }
            }
        }

        if (recap.categories.isNotEmpty()) {
            Spacer(Modifier.height(18.dp))
            SectionHeading("Where it went")
            Spacer(Modifier.height(8.dp))
            val max = recap.categories.maxOf { it.total }
            recap.categories.forEach { slice ->
                RecapBar(slice, max, money)
                Spacer(Modifier.height(10.dp))
            }
        }

        // Tags only when the account actually tags. Most do not, and an empty "Labels" heading would read as a
        // feature that is broken rather than one that is unused.
        if (recap.tags.isNotEmpty()) {
            Spacer(Modifier.height(8.dp))
            SectionHeading("Labels")
            Spacer(Modifier.height(8.dp))
            recap.tags.forEach { t ->
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 3.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    CatIcon(t.icon, t.label, size = 15.dp)
                    Spacer(Modifier.width(8.dp))
                    Text(t.label, fontSize = 12.sp, color = tandem.muted, modifier = Modifier.weight(1f))
                    Text(
                        money(t.total), fontSize = 12.sp, fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                }
            }
        }
    }
}

/**
 * "24 Aug" — the short form the week range wants on both the card and the sheet.
 *
 * ⚠️ Not [prettyDateLabel], which is for a single date: it resolves to "Today"/"Yesterday" (meaningless as the
 * end of a *week*) and repeats the year at both ends of a range that never spans one. The web prints `d MMM`
 * here for the same reasons.
 */
internal fun weekDayLabel(iso: String): String = runCatching {
    java.time.LocalDate.parse(iso)
        .format(java.time.format.DateTimeFormatter.ofPattern("d MMM", java.util.Locale.getDefault()))
}.getOrDefault(iso)

@Composable
private fun SectionHeading(text: String) {
    Text(
        text, fontSize = 13.sp, fontWeight = FontWeight.Bold,
        color = MaterialTheme.colorScheme.onSurface,
    )
}

/** One of the four figures at the top. [foot] carries the qualifier the number would be misread without. */
@Composable
private fun Tile(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
    valueColor: androidx.compose.ui.graphics.Color? = null,
    foot: String? = null,
    footColor: androidx.compose.ui.graphics.Color? = null,
) {
    val tandem = LocalTandemColors.current
    Column(
        modifier
            .fillMaxHeight()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
            // ⚠️ The border is not decoration. This sheet's own background is `surface`, and in the dark theme
            // `surfaceVariant` sits so close to it that the four tiles read as loose text on a page rather than
            // as four figures — seen on the emulator, invisible in the code. The web's `.wk-tile` is a bordered
            // card for the same reason.
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Text(label, fontSize = 11.sp, color = tandem.muted)
        Text(
            value, fontSize = 17.sp, fontWeight = FontWeight.Bold,
            color = valueColor ?: MaterialTheme.colorScheme.onSurface,
        )
        foot?.let { Text(it, fontSize = 10.sp, color = footColor ?: tandem.muted) }
    }
}

/** One category's share, as a labelled bar. Floored at 2% so a tiny-but-real slice still draws something rather
 *  than vanishing into the track — the same floor the web's `WeekBarPct` applies, for the same reason. */
@Composable
private fun RecapBar(slice: RecapSliceDto, max: Double, money: (Double) -> String) {
    val tandem = LocalTandemColors.current
    val pct = if (max <= 0.0) 0f else ((slice.total / max) * 100.0).roundToInt().coerceAtLeast(2) / 100f
    Column(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            CatIcon(slice.icon, slice.label, size = 15.dp)
            Spacer(Modifier.width(8.dp))
            Text(slice.label, fontSize = 12.sp, color = tandem.muted, maxLines = 1, modifier = Modifier.weight(1f))
            Text(
                money(slice.total), fontSize = 12.sp, fontWeight = FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
        Spacer(Modifier.height(5.dp))
        Box(
            Modifier.fillMaxWidth().height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(MaterialTheme.colorScheme.surfaceVariant),
        ) {
            Box(
                Modifier.fillMaxWidth(pct).height(6.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(tandem.spent),
            )
        }
    }
}
