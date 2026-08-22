package com.tandemtab.app.ui

import android.content.Context
import android.content.SharedPreferences
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.text.NumberFormat
import java.util.Currency
import java.util.Locale

/**
 * Shoulder-surfing mode — the phone's half of the web's Ctrl/⌘+Shift+H.
 *
 * ★ **Every figure the app renders is masked, and nothing else is.** Category names, bucket names and merchants
 * stay legible so the screen is still navigable while the mode is on; what leaks a life is the amounts.
 *
 * ⚠️ **Numbers only — the charts are NOT covered.** The web shipped with the bars and rings blurred as well, on
 * the argument that a Breakdown ring tells the story without a single digit. The owner used it and the blur read
 * as damage rather than as cover, so both surfaces now mask the digits and leave the shapes alone. The honest cost
 * is written down rather than smoothed over: a masked screen still shows the *proportions* — which category is the
 * big one, whether a goal is nearly full. The indicator's wording is what keeps this truthful; it says "Figures
 * hidden", which is exactly and only what the mode does. Do not let it grow into "Private mode".
 *
 * ★ **A global, like the web's static `Dashboard.PrivacyMode`, and for the same reason.** The money formatters are
 * plain top-level functions called from a dozen screens; threading a flag to them would mean another parameter on
 * [HomeScreen]'s already enormous signature and one more on every screen below it — and the first screen anyone
 * forgot would silently render real figures under a bar that promises they are hidden. It is a property of the
 * SCREEN, not of an account or a session, so a global is the honest model. Compose state, so reading it inside a
 * formatter lambda recomposes whatever rendered the figure.
 */
object Privacy {

    /** Five dots — a fixed width whatever the figure, so the layout does not twitch as values change behind the
     *  mask and no digit count leaks (a masked "€1,234.56" wider than "€9.99" tells a watcher the magnitude). */
    const val MASKED = "•••••"

    private var prefs: SharedPreferences? = null

    /** Whether figures are currently masked. */
    var masked by mutableStateOf(false)
        private set

    /** The face-down gesture, opt-in and off by default (see [FlipToHideWatcher]). Writing it persists it — a
     *  plain `by mutableStateOf` would let a caller set it without the write, which is the whole point of it. */
    private var flipToHideState by mutableStateOf(false)
    var flipToHide: Boolean
        get() = flipToHideState
        set(value) {
            flipToHideState = value
            prefs?.edit()?.putBoolean("privacy_flip", value)?.apply()
        }

    /** Raised the first time the gesture ever fires, so the first occurrence is not a mystery. */
    var explainFlip by mutableStateOf(false)
        private set

    private var flipExplained = false

    /** Read the persisted state once, at startup, before the first frame. */
    fun attach(store: SharedPreferences) {
        prefs = store
        // ⚠️ Masking PERSISTS, exactly as the web's does. The alternative — unmask on relaunch — fails in the one
        // situation the mode exists for: you mask in a café, the phone locks, Android kills the process to reclaim
        // memory, and unlocking hands the screen back with every figure on it. Persisting can only ever cost a tap.
        masked = store.getBoolean("privacy_masked", false)
        flipToHideState = store.getBoolean("privacy_flip", false)
        flipExplained = store.getBoolean("privacy_flip_explained", false)
    }

    fun mask() {
        if (masked) return
        masked = true
        prefs?.edit()?.putBoolean("privacy_masked", true)?.apply()
    }

    fun unmask() {
        if (!masked) return
        masked = false
        prefs?.edit()?.putBoolean("privacy_masked", false)?.apply()
    }

    /** The gesture fired. One-directional by construction — see [FlipToHideWatcher]. */
    internal fun onFlippedFaceDown() {
        val first = !flipExplained
        mask()
        if (first) {
            flipExplained = true
            prefs?.edit()?.putBoolean("privacy_flip_explained", true)?.apply()
            explainFlip = true
        }
    }

    fun dismissFlipExplainer() { explainFlip = false }

    /** Whether this device can do the gesture at all. A toggle for a sensor that isn't there is a promise the
     *  phone cannot keep, so Settings hides the row rather than offering a switch that does nothing. */
    fun hasFlipSensor(context: Context): Boolean =
        context.getSystemService(Context.SENSOR_SERVICE)
            ?.let { it as SensorManager }
            ?.getDefaultSensor(Sensor.TYPE_ACCELEROMETER) != null
}

/**
 * The app's one money formatter.
 *
 * ★ There used to be six of these — byte-identical copies named `sheetMoney`, `rememberCurrency`, `rememberMoney`,
 * `rememberGoalsMoney`, `rememberTripMoney` and `rememberWalletsMoney`, one per screen. Collapsing them is not
 * tidying: it is what makes privacy mode true. The web can mask in one place because every money string in the app
 * is built by one method; with six copies the phone would mask five screens and quietly render the sixth, and the
 * next screen anyone adds would start out unmasked. One function, one branch, nothing can escape it later.
 *
 * The mask is read INSIDE the returned lambda, not when the lambda is built — that is what puts the snapshot read
 * in the composable that renders the figure, so toggling the mode recomposes the screen.
 */
internal fun moneyFormatter(currencyCode: String): (Double) -> String {
    val nf = NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = Currency.getInstance(currencyCode) }
    return { amount -> if (Privacy.masked) Privacy.MASKED else nf.format(amount) }
}

/**
 * A percentage, masked with the rest.
 *
 * ⚠️ Percentages never touch [moneyFormatter] and were the one class of figure still legible on a masked screen.
 * They are not harmless: a savings rate and a loan's APR are both facts about the money the mode exists to hide,
 * and "3%" beside a masked column is an invitation to ask what it is 3% of. Mirrors the web's `FmtPct`.
 */
internal fun maskPct(text: String): String = if (Privacy.masked) Privacy.MASKED else text

/**
 * Mask the figures inside a sentence the SERVER already formatted.
 *
 * ⚠️ Two payloads reach the phone with amounts baked into prose — `NotificationsMap` ("Off balance — overspent by
 * €4,760.00.") and `AchievementsMap` ("€2,400.00 of €8,000.00 paid off so far."). No client formatter touches
 * them, so [moneyFormatter] cannot reach them and the alert strip sat on the Home screen showing a real balance
 * under a bar that said "Figures hidden". Found on the emulator, not by reading the code.
 *
 * ★ This is a regex over prose, which is normally a bad idea, and here it is not: both strings are built by
 * `MoneyText.Format`, whose entire output is `{€|$|£}{amount:N2}` or `{CODE} {amount:N2}`. The pattern below is
 * that contract written out, not a guess at what money looks like. ⚠️ If `MoneyText.Format` ever changes shape,
 * this is the other end of that change.
 *
 * The right fix is for those two payloads to carry code + args like `InsightMessageDto` already does, so the
 * phone formats them and inherits masking for free. That is a contract change across the server mapper and every
 * string in it, and it belongs in its own session rather than smuggled into this one.
 */
internal fun maskServerText(text: String): String {
    if (!Privacy.masked) return text
    return text.replace(SERVER_MONEY, Privacy.MASKED).replace(SERVER_PERCENT, Privacy.MASKED)
}

private val SERVER_MONEY = Regex("""(?:[€${'$'}£]|\b[A-Z]{3} )-?\d[\d,]*\.\d{2}""")
private val SERVER_PERCENT = Regex("""\b\d{1,3}%""")

/**
 * The indicator. Mandatory, not decoration: an app that has hidden its own numbers with no explanation reads as
 * broken, and on a phone — where the gesture can fire by accident, from a pocket — the person seeing it is the one
 * least likely to know the mode exists.
 *
 * ⚠️ It is the ONLY visible control for the mode, and it only ever turns masking OFF. A control that could turn it
 * on would be one more thing to hit by accident on a screen full of money.
 */
@Composable
internal fun PrivacyBar() {
    if (!Privacy.masked) return
    val tandem = LocalTandemColors.current
    Row(
        Modifier
            .fillMaxWidth()
            .background(tandem.privacyBg)
            .clickable { Privacy.unmask() }
            .padding(horizontal = 14.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(TandemIcons.Lock, contentDescription = null, tint = tandem.privacyFg, modifier = Modifier.size(15.dp))
        Text("Figures hidden", color = tandem.privacyFg, fontSize = 12.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
        Text("Tap to show", color = tandem.privacyFg, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** ~9.0 m/s² down the negative z-axis. Gravity is 9.81, so this is "the screen is facing the floor within about
 *  23° of flat" — loose enough for a phone laid on an uneven table, tight enough that carrying it does not count. */
private const val FACE_DOWN_Z = -9f

/** The gesture must hold for a second. Without it a pocket, a bag, or the arc of putting the phone down fires it. */
private const val FACE_DOWN_HOLD_MS = 1_000L

/**
 * Flip the phone face-down to hide the figures.
 *
 * ★ **One-directional: face-down turns masking ON, never off.** This is the whole design, not a simplification.
 * A toggle would mean that laying the phone face-UP on a café table *unmasks* it — precisely backwards. And
 * face-down is not reliably a privacy signal: people put phones face-down on tables all day. So the gesture may
 * only ever make the screen more private, and coming back is a deliberate tap on the indicator. That asymmetry is
 * what makes a false positive cost one tap instead of exposing the screen.
 *
 * ⚠️ **Foreground only.** Registration is bound to ON_START/ON_STOP rather than to composition: Compose does not
 * dispose the composition when the activity stops, so an effect keyed on composition alone would keep the phone's
 * accelerometer running — and would mask the screen in response to a phone lying still in a bag with the app
 * merely backgrounded.
 *
 * Nothing is registered at all while the setting is off; the whole effect leaves the composition with it.
 */
@Composable
internal fun FlipToHideWatcher() {
    if (!Privacy.flipToHide) return
    val context = LocalContext.current
    val owner = LocalLifecycleOwner.current
    DisposableEffect(owner) {
        val sensors = context.getSystemService(Context.SENSOR_SERVICE) as? SensorManager
        val accelerometer = sensors?.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        if (sensors == null || accelerometer == null) return@DisposableEffect onDispose { }

        var faceDownSince = 0L
        val listener = object : SensorEventListener {
            override fun onSensorChanged(event: SensorEvent) {
                val z = event.values.getOrNull(2) ?: return
                if (z >= FACE_DOWN_Z) { faceDownSince = 0L; return }
                val now = android.os.SystemClock.elapsedRealtime()
                if (faceDownSince == 0L) { faceDownSince = now; return }
                if (now - faceDownSince < FACE_DOWN_HOLD_MS) return
                // Already masked? Then this is a no-op, and resetting the clock keeps it one: nothing here can
                // ever unmask, so a second flip has nothing left to do.
                faceDownSince = now
                Privacy.onFlippedFaceDown()
            }

            override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit
        }

        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                // SENSOR_DELAY_UI (~60ms) is far finer than a one-second hold needs, and is the slowest rate the
                // platform still delivers promptly; anything coarser risks the flip being noticed a beat late.
                Lifecycle.Event.ON_START ->
                    sensors.registerListener(listener, accelerometer, SensorManager.SENSOR_DELAY_UI)
                Lifecycle.Event.ON_STOP -> {
                    sensors.unregisterListener(listener)
                    faceDownSince = 0L
                }
                else -> Unit
            }
        }
        owner.lifecycle.addObserver(observer)
        onDispose {
            owner.lifecycle.removeObserver(observer)
            sensors.unregisterListener(listener)
        }
    }
}

/**
 * The one-time explanation, raised the first time the gesture ever fires.
 *
 * ⚠️ Without it the first occurrence is a mystery: the user put the phone down, picked it up, and the app's numbers
 * are gone. It names the gesture, names the way back, and names where to switch it off — because someone who did
 * not want this needs the exit more than the explanation.
 */
@Composable
internal fun FlipExplainerDialog() {
    if (!Privacy.explainFlip) return
    AlertDialog(
        onDismissRequest = { Privacy.dismissFlipExplainer() },
        title = { Text("Figures hidden") },
        text = {
            Text(
                "You turned the phone face-down, so TandemTab hid every amount on screen. " +
                    "Tap the purple bar at the top to show them again — turning the phone back over " +
                    "deliberately won't, so nothing is uncovered by accident.\n\n" +
                    "You can switch this off under Profile → Appearance.",
            )
        },
        confirmButton = { TextButton(onClick = { Privacy.dismissFlipExplainer() }) { Text("Got it") } },
    )
}
