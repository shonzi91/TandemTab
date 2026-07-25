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
val Coral = Color(0xFFFF7A66)            // "spent" partner accent
val Amber = Color(0xFFB45309)            // warnings (light)

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
)

val LightTandemColors = TandemColors(
    canvas = LightCanvas,
    hero = LightHero,
    hairline = LightHairline,
    muted = LightMuted,
    positive = PositiveGreen,
    saved = SavingsGreen,
    spent = Coral,
    savingsTileBg = LightSavingsTile,
    savingsTileBorder = LightSavingsTileBorder,
    warn = Amber,
    alertBg = LightAlertBg,
    alertBorder = LightAlertBorder,
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
)

val LocalTandemColors = staticCompositionLocalOf { LightTandemColors }
