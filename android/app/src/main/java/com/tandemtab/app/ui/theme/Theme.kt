package com.tandemtab.app.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

private val LightColors = lightColorScheme(
    primary = BrandGreen,
    onPrimary = Color.White,
    primaryContainer = LightSavingsTile,
    onPrimaryContainer = PositiveGreen,
    secondary = PositiveGreen,
    onSecondary = Color.White,
    background = LightCanvas,
    onBackground = LightText,
    surface = LightSurface,
    onSurface = LightText,
    surfaceVariant = LightHero,
    onSurfaceVariant = LightMuted,
    outline = LightBorder,
    outlineVariant = LightHairline,
    error = Amber,
    onError = Color.White,
)

private val DarkColors = darkColorScheme(
    primary = BrandGreen,
    onPrimary = Color.White,
    primaryContainer = DarkSurface,
    onPrimaryContainer = Mint,
    secondary = Mint,
    onSecondary = DarkCanvas,
    background = DarkCanvas,
    onBackground = DarkText,
    surface = DarkSurface,
    onSurface = DarkText,
    surfaceVariant = DarkHero,
    onSurfaceVariant = DarkMuted,
    outline = DarkBorder,
    outlineVariant = DarkBorder,
    error = Color(0xFFF5B24E),
    onError = DarkCanvas,
)

@Composable
fun TandemTabTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val colors = if (darkTheme) DarkColors else LightColors
    val tandem = if (darkTheme) DarkTandemColors else LightTandemColors

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }

    CompositionLocalProvider(LocalTandemColors provides tandem) {
        MaterialTheme(colorScheme = colors, content = content)
    }
}
