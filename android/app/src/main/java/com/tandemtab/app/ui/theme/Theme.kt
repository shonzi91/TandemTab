package com.tandemtab.app.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

// TandemTab teal accent, roughly matching the web app's hero.
private val Teal = Color(0xFF0E7C66)
private val TealLight = Color(0xFF3FA890)
private val Coral = Color(0xFFE5654B)

private val LightColors = lightColorScheme(
    primary = Teal,
    onPrimary = Color.White,
    secondary = TealLight,
    error = Coral,
    background = Color(0xFFF6F8F7),
    surface = Color.White,
)

private val DarkColors = darkColorScheme(
    primary = TealLight,
    onPrimary = Color(0xFF06251F),
    secondary = Teal,
    error = Coral,
    background = Color(0xFF10161A),
    surface = Color(0xFF171F24),
)

@Composable
fun TandemTabTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val colors = if (darkTheme) DarkColors else LightColors
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            // Edge-to-edge (enabled in MainActivity) keeps the bars transparent; we only steer icon contrast.
            val window = (view.context as Activity).window
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }
    MaterialTheme(colorScheme = colors, content = content)
}
