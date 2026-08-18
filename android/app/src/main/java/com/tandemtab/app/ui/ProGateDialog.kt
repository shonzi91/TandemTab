package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.PlanFeatures
import com.tandemtab.app.data.PlansDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * The upgrade prompt — raised BY A GATE, never shown on arrival.
 *
 * MONETIZATION.md's whole argument is that the upgrade moment is the moment somebody reaches for the feature, not
 * a pricing screen they were shown when they opened the app. So nothing here advertises Pro up front: this speaks
 * only when a gate has actually refused something, and it names what was refused, so it reads as an explanation
 * of what just happened rather than an advert.
 *
 * ★ Both entry points land here — the local gate ahead of the work, and the server's 402 behind it. That matters
 * on the phone specifically: before this existed, a free user could fill in the whole New-trip form, press Save,
 * and get a line of red text under the button. The paywall worked; it just refused rudely, and at the worst
 * possible moment.
 *
 * The tier lists come from the SERVER's catalogue ([PlansDto.features]), never a list written here — that is what
 * keeps what this prompt promises and what the gates enforce from drifting apart. When the catalogue is missing
 * (a failed fetch, an offline phone) the prompt still appears and simply names the blocked feature instead of
 * showing the table.
 */
@Composable
fun ProGateDialog(feature: String, plans: PlansDto?, onDismiss: () -> Unit) {
    val tandem = LocalTandemColors.current
    val pro = plans?.features?.filter { !it.inFree }.orEmpty()

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = {
            // The crown gets its own round badge rather than sitting inline in the heading: an icon inline with
            // text rides its own baseline and pulls the title off-centre.
            Box(
                Modifier.size(38.dp).background(tandem.alertBg, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(TandemIcons.Crown, contentDescription = null, tint = tandem.warn, modifier = Modifier.size(20.dp))
            }
        },
        title = { Text("That one's part of Pro", fontWeight = FontWeight.ExtraBold, fontSize = 19.sp) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                // With the table below, the blocked feature is already picked out in it and a second line
                // restating it reads as a stray highlight. Without the table, naming it is the whole answer.
                if (pro.isEmpty()) {
                    featureLabel(feature)?.let {
                        Text(it, color = MaterialTheme.colorScheme.onSurface, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                    }
                } else {
                    Text("Everything in Free, plus:", color = tandem.muted, fontSize = 13.sp)
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        pro.forEach { f ->
                            val blocked = f.key == feature
                            Row(verticalAlignment = Alignment.Top) {
                                Icon(
                                    TandemIcons.Crown, contentDescription = null,
                                    tint = if (blocked) tandem.warn else tandem.muted,
                                    modifier = Modifier.size(14.dp).padding(top = 3.dp),
                                )
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    featureLabel(f.key) ?: f.key,
                                    color = if (blocked) MaterialTheme.colorScheme.onSurface else tandem.muted,
                                    fontWeight = if (blocked) FontWeight.Bold else FontWeight.Normal,
                                    fontSize = 13.sp,
                                )
                            }
                        }
                    }
                }

                if (plans?.enabled == true) {
                    // Pro is genuinely on sale — but not from here. There is no billing on the phone (see the
                    // Session 107 note), and an "Upgrade" button that started nothing would be a worse promise
                    // than the one this dialog exists to fix. So it says where the door actually is.
                    Text(
                        "Pro is ${plans.currency} ${plans.annualPrice} a year, or ${plans.currency} " +
                            "${plans.monthlyPrice} a month. You can upgrade from the TandemTab web app — " +
                            "everything you unlock there is Pro on this phone too, with the same sign-in.",
                        color = tandem.muted, fontSize = 13.sp,
                    )
                } else {
                    // Billing is off, which is the state during beta: there is nothing to buy and nothing to
                    // sell, so the prompt is purely an explanation of what the plan does and doesn't include.
                    Text(
                        "Pro isn't on sale yet — it's coming after our beta, and this unlocks then.",
                        color = tandem.muted, fontSize = 13.sp,
                    )
                    // The 45-day trial, stated only on this branch — the one where nothing can be bought. Here
                    // it is a promise about the future with no charge standing next to it to contradict it.
                    Text(
                        "🎁 When it does, you'll get 45 days of Pro free — no card, and it simply " +
                            "stops at the end.",
                        color = tandem.muted, fontSize = 13.sp,
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Got it") } },
    )
}

/**
 * The wording for a feature key. The server sends stable KEYS and the client owns the words, so the same list
 * reads correctly here and on the web without the server knowing anything about a display string.
 *
 * An unknown key returns null rather than throwing or showing the raw key: a server that ships a new feature
 * before this app learns its name should degrade to a shorter list, not to a row reading "roundups".
 */
private fun featureLabel(key: String): String? = when (key) {
    PlanFeatures.BUDGETS -> "Budgets, categories and tags"
    PlanFeatures.GOALS -> "Savings goals"
    PlanFeatures.EXPORT -> "Export to Excel"
    PlanFeatures.SECURITY -> "Two-factor security"
    PlanFeatures.SHARE -> "Share with your household"
    PlanFeatures.IMPORT -> "Unlimited statement imports"
    PlanFeatures.DEBT -> "Debt payoff planner"
    PlanFeatures.INSIGHTS -> "Trends and advanced insights"
    PlanFeatures.HISTORY -> "Full history"
    PlanFeatures.CAPS -> "Unlimited accounts and funds"
    PlanFeatures.TRIPS -> "Trips — what a journey really cost, flights and all"
    else -> null
}
