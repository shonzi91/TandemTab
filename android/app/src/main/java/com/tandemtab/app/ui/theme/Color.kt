package com.tandemtab.app.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

// Raw web tokens (from src/FinApp.App.Web/wwwroot/css/app.css + Dashboard.razor.css), kept 1:1 so the
// native app reads as the same product.

// Brand
val BrandGreen = Color(0xFF13A06E)       // primary CTA / active accent
val BrandGreenDark = Color(0xFF0E7C55)   // hover/pressed
val PositiveGreen = Color(0xFF0F7A54)    // "free"/positive figures (light)
val SavingsGreen = Color(0xFF0E9E6D)     // savings tile value (light)
val Mint = Color(0xFF3FE0C5)             // dark-theme accent / saved
val Coral = Color(0xFFFF7A66)            // "spent" partner accent (dark)
// Coral was the only accent without a darkened LIGHT variant, and it is the colour of every money-out figure and
// every destructive label in the app. On white it measures 2.55:1 — below AA *and* below AA-large, on 13sp text.
// This is the same hue (hsl 7 vs coral's 8) taken down in lightness, exactly how BrandGreen→PositiveGreen and the
// amber pair already work: 5.68:1 on a card, ≥5.0 on every other light surface. Dark keeps the bright coral,
// which measures 6.70:1 and was never the problem. (The web colours the same figures #dc2626 in light.)
val CoralDeep = Color(0xFFB93A28)        // "spent" partner accent (light)
val Amber = Color(0xFFB45309)            // warnings (light)
// Danger — destructive actions and error text. Distinct from the warning amber above: an amber "Delete account"
// reads as a caution, not a destruction. Matches the web's .danger-btn / .amt.debit pair.
val DangerRed = Color(0xFFDC2626)        // danger (light)
val DangerRedDark = Color(0xFFF87171)    // danger (dark)

// Light surfaces & text
val LightCanvas = Color(0xFFEEF3F0)
val LightSurface = Color(0xFFFFFFFF)
val LightHero = Color(0xFFF4F7F6)
val LightBorder = Color(0xFFE6ECE9)
val LightHairline = Color(0xFFE3E9EE)
val LightText = Color(0xFF16202B)
val LightMuted = Color(0xFF6B7280)
val LightSavingsTile = Color(0xFFEEF9F3)
val LightSavingsTileBorder = Color(0xFFD6F0E3)
val LightAlertBg = Color(0xFFFFF7ED)
val LightAlertBorder = Color(0xFFFED7AA)

// Dark surfaces & text
val DarkCanvas = Color(0xFF0F1322)
val DarkSurface = Color(0xFF161B2C)
val DarkHero = Color(0xFF161B2C)
val DarkBorder = Color(0xFF293049)
val DarkText = Color(0xFFE8EAF0)
val DarkMuted = Color(0xFFAAB0C0)

/**
 * Web design tokens that Material 3's ColorScheme has no slot for (hero surface, positive/saved/spent
 * accents, hairline, alert colours). Provided alongside MaterialTheme via [LocalTandemColors].
 */
@Immutable
data class TandemColors(
    val canvas: Color,
    val hero: Color,
    val hairline: Color,
    val muted: Color,
    val positive: Color,
    val saved: Color,
    val spent: Color,
    val savingsTileBg: Color,
    val savingsTileBorder: Color,
    val warn: Color,
    val alertBg: Color,
    val alertBorder: Color,
    val segmentTrack: Color,
    val catAccent: Color,   // category line-icon colour (web .cat-ic): brand green in light, mint in dark
    // The auth error box. These belong to the theme rather than to the composable because this app carries its
    // OWN theme preference (AppViewModel.darkTheme, mirroring the web's localStorage 'finapp-theme') and
    // deliberately does not follow the system. A composable that asks isSystemInDarkTheme() therefore paints the
    // other theme's palette on top of this one whenever the two disagree — which is the default state, since the
    // preference defaults to dark on a phone that is usually light.
    val dangerBg: Color,
    val dangerBorder: Color,
    val dangerText: Color,
)

val LightTandemColors = TandemColors(
    canvas = LightCanvas,
    hero = LightHero,
    hairline = LightHairline,
    muted = LightMuted,
    positive = PositiveGreen,
    saved = SavingsGreen,
    spent = CoralDeep,
    savingsTileBg = LightSavingsTile,
    savingsTileBorder = LightSavingsTileBorder,
    warn = Amber,
    alertBg = LightAlertBg,
    alertBorder = LightAlertBorder,
    segmentTrack = Color(0xFFF1F3F6),
    catAccent = BrandGreen,
    dangerBg = Color(0x14EF4444),
    dangerBorder = Color(0x33EF4444),
    dangerText = Color(0xFF991B1B),
)

val DarkTandemColors = TandemColors(
    canvas = DarkCanvas,
    hero = DarkHero,
    hairline = DarkBorder,
    muted = DarkMuted,
    positive = Mint,
    saved = Mint,
    spent = Coral,
    savingsTileBg = Color(0x1733E0C5),
    savingsTileBorder = Color(0x4733E0C5),
    warn = Color(0xFFF5B24E),
    alertBg = Color(0xFF2A2417),
    alertBorder = Color(0xFF4A3D22),
    segmentTrack = Color(0xFF0E1220),
    catAccent = Mint,
    dangerBg = Color(0x21EF4444),
    dangerBorder = Color(0x61EF4444),
    dangerText = Color(0xFFFCA5A5),
)

val LocalTandemColors = staticCompositionLocalOf { LightTandemColors }
