package com.tandemtab.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * The TandemTab brand mark — two stems + heads under a shared beam — reproduced from
 * Components/TandemLogo.razor (viewBox 0 0 64 64), so the native mark matches the web exactly.
 */
@Composable
fun TandemLogo(size: Dp = 44.dp, modifier: Modifier = Modifier) {
    Canvas(modifier = modifier.size(size)) {
        val s = this.size.minDimension / 64f
        fun p(x: Float, y: Float) = Offset(x * s, y * s)
        fun sz(w: Float, h: Float) = Size(w * s, h * s)
        val grad = Brush.linearGradient(
            colors = listOf(Color(0xFF34D399), Color(0xFF13A06E)),
            start = p(6f, 6f),
            end = p(58f, 58f),
        )
        val head = Color(0xFF34D399)
        val r4 = CornerRadius(4f * s, 4f * s)
        // shared beam
        drawRoundRect(grad, topLeft = p(12f, 22f), size = sz(40f, 8f), cornerRadius = r4)
        // two stems (the tandem pair / two T's)
        drawRoundRect(grad, topLeft = p(20f, 27f), size = sz(8f, 25f), cornerRadius = r4)
        drawRoundRect(grad, topLeft = p(36f, 27f), size = sz(8f, 25f), cornerRadius = r4)
        // two heads
        drawCircle(head, radius = 6f * s, center = p(24f, 14f))
        drawCircle(head, radius = 6f * s, center = p(40f, 14f))
    }
}
