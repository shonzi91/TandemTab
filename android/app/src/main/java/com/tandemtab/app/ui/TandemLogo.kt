package com.tandemtab.app.ui

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.RoundRect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * The TandemTab brand mark — one rounded tile split corner-to-corner by an S-curve, each field carrying a dot
 * in the other's tone. Reproduced from Components/TandemLogo.razor (viewBox 0 0 64 64), so the native mark
 * matches the web exactly; every coordinate below is in that 64-unit space and scaled by [s].
 *
 * Replaced the two-stems-and-heads mark, which read as two people.
 *
 * ⚠ Unlike the web mark this one does NOT follow the theme — it renders the light-theme pair in both. That is
 * not a regression: the previous native mark was a fixed gradient too. The web's `--tt-logo-*` brightening has
 * never had a native counterpart.
 */
@Composable
fun TandemLogo(size: Dp = 44.dp, modifier: Modifier = Modifier) {
    Canvas(modifier = modifier.size(size)) {
        val s = this.size.minDimension / 64f
        fun p(x: Float, y: Float) = Offset(x * s, y * s)
        val side = 64f * s
        val tile = Path().apply {
            addRoundRect(RoundRect(Rect(0f, 0f, side, side), CornerRadius(17f * s, 17f * s)))
        }
        clipPath(tile) {
            drawRect(Color(0xFF13A06E), size = Size(side, side))
            // The split, drawn well past the tile on both ends; the clip trims it. Its endpoints are the tile's
            // own corners, which sit inside the corner radius — hence the clip rather than a closed outline.
            val split = Path().apply {
                moveTo(96f * s, -32f * s)
                lineTo(64f * s, 0f)
                cubicTo(62.7f * s, 20f * s, 52f * s, 30.7f * s, 32f * s, 32f * s)
                cubicTo(12f * s, 33.3f * s, 1.3f * s, 44f * s, 0f, 64f * s)
                lineTo(-32f * s, 96f * s)
                lineTo(-32f * s, -32f * s)
                close()
            }
            drawPath(split, Color(0xFF34D399))
            drawCircle(Color(0xFF0B6B4A), radius = 8f * s, center = p(20f, 20f))
            drawCircle(Color(0xFFA7F3D0), radius = 8f * s, center = p(44f, 44f))
        }
    }
}

/** The brand-mark loading indicator: the TandemTab logo gently breathing (scale + fade), used in place of a plain
 *  spinner on the splash and full-screen loading states. */
@Composable
fun LogoLoader(size: Dp = 56.dp, modifier: Modifier = Modifier) {
    val t = rememberInfiniteTransition(label = "logo-loader")
    val scale by t.animateFloat(
        initialValue = 0.86f, targetValue = 1.08f,
        animationSpec = infiniteRepeatable(tween(780, easing = FastOutSlowInEasing), RepeatMode.Reverse), label = "scale",
    )
    val alpha by t.animateFloat(
        initialValue = 0.5f, targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(780, easing = FastOutSlowInEasing), RepeatMode.Reverse), label = "alpha",
    )
    TandemLogo(size = size, modifier = modifier.scale(scale).alpha(alpha))
}
