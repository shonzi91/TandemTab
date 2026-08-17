package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.OnboardingViewDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * The "Getting started" checklist on Home — the native counterpart of the web's card.
 *
 * Each step's done-ness is resolved **server-side** and simply rendered here. The four rules for "have they done
 * this yet" belong to the domain, and a second reading of them in Kotlin would be a second place to disagree with
 * the web about whether someone has finished setting up.
 *
 * It hides itself once every step is done, without waiting to be dismissed: a checklist with nothing left on it is
 * a congratulation the first time and clutter every time after.
 */
@Composable
fun GettingStartedCard(onboarding: OnboardingViewDto?, onDismiss: () -> Unit) {
    val tandem = LocalTandemColors.current
    // Null means /onboarding hasn't answered — render nothing rather than flashing four un-ticked steps and then
    // correcting them a moment later.
    if (onboarding == null || onboarding.dismissed || onboarding.steps.isEmpty()) return
    val remaining = onboarding.steps.count { !it.done }
    if (remaining == 0) return

    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    "GETTING STARTED", fontSize = 10.sp, letterSpacing = 1.3.sp,
                    fontWeight = FontWeight.Bold, color = tandem.muted,
                )
                Spacer(Modifier.height(3.dp))
                Text(
                    "$remaining ${if (remaining == 1) "thing" else "things"} left",
                    fontSize = 17.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface,
                )
            }
            Icon(
                TandemIcons.Close, contentDescription = "Dismiss getting started", tint = tandem.muted,
                modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = onDismiss).padding(6.dp).size(18.dp),
            )
        }
        Spacer(Modifier.height(10.dp))
        onboarding.steps.forEach { step ->
            Row(Modifier.fillMaxWidth().padding(vertical = 5.dp), verticalAlignment = Alignment.Top) {
                // A tick when done, an empty ring when not — the ring is drawn rather than iconned, since the set
                // has no hollow-circle glyph and a second tick in a muted colour reads as "done, but less so".
                if (step.done) {
                    Icon(
                        TandemIcons.Check, contentDescription = null, tint = tandem.positive,
                        modifier = Modifier.size(17.dp),
                    )
                } else {
                    Box(
                        Modifier.padding(top = 2.dp).size(13.dp)
                            .border(1.5.dp, tandem.muted, CircleShape),
                    )
                }
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        step.title,
                        fontWeight = FontWeight.SemiBold,
                        fontSize = 14.sp,
                        color = if (step.done) tandem.muted else MaterialTheme.colorScheme.onSurface,
                        textDecoration = if (step.done) TextDecoration.LineThrough else null,
                    )
                    // The description is the useful half for a step not yet done, and noise for one that is.
                    if (!step.done) {
                        Text(step.desc, fontSize = 12.sp, color = tandem.muted)
                    }
                }
            }
        }
    }
}
