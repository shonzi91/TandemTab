package com.tandemtab.app.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp

/**
 * The app's shared line-icon set — a 1:1 port of the web's SVG sprite (IconSprite.razor), so the native chrome reads
 * as the exact same product. Geometry only: every icon is a 24×24 stroked path (stroke-width 1.8, round caps/joins,
 * no fill) matching the web `.ic` rule; a few carry an extra filled sub-path (dots, target centre). Colour is applied
 * by the caller via `Icon(tint = …)`, which recolours the whole vector — so the placeholder stroke colour is unused.
 *
 * Category / fund glyphs stay as user-chosen emoji (as on the web); these are chrome/UI icons only.
 */
object TandemIcons {

    // --- builder helpers ----------------------------------------------------------------------------

    private const val PLACEHOLDER = 0xFF111111.toInt()

    /** A stroked line icon (optionally with a second filled path for dots/centres). */
    private fun icon(stroke: String, fill: String? = null): ImageVector =
        ImageVector.Builder(defaultWidth = 24.dp, defaultHeight = 24.dp, viewportWidth = 24f, viewportHeight = 24f).apply {
            if (stroke.isNotBlank()) addPath(
                pathData = PathParser().parsePathString(stroke).toNodes(),
                fill = null,
                stroke = SolidColor(Color(PLACEHOLDER)),
                strokeLineWidth = 1.8f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round,
            )
            if (fill != null) addPath(
                pathData = PathParser().parsePathString(fill).toNodes(),
                fill = SolidColor(Color(PLACEHOLDER)),
            )
        }.build()

    // --- chrome / navigation ------------------------------------------------------------------------

    val House = icon("M3 11l9-7 9 7M5 10v10h14V10M10 20v-6h4v6")
    val Receipt = icon("M6 3.5h12v17l-3-1.8-3 1.8-3-1.8-3 1.8zM9 8h6M9 12h6")
    val Flag = icon("M6 21V4M6 5h11l-2.5 3 2.5 3H6")
    val Wallet = icon("M4 8a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2zM4 8c0-2 2-4 4-4h8M15 13h.01")
    // sliders: two rails + two knob circles (web i-sliders)
    val Sliders = icon("M4 7h10M18 7h2M4 17h2M10 17h10M18.2 4.8a2.2 2.2 0 1 0 0 4.4 2.2 2.2 0 0 0 0-4.4zM10.2 14.8a2.2 2.2 0 1 0 0 4.4 2.2 2.2 0 0 0 0-4.4z")
    // three filled dots (web i-dots)
    val Dots = icon(stroke = "", fill = "M6.6 12a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0zM13.6 12a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0zM20.6 12a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0z")

    // --- actions ------------------------------------------------------------------------------------

    val Plus = icon("M12 5v14M5 12h14")
    val Close = icon("M18 6L6 18M6 6l12 12")
    val Check = icon("M20 6L9 17l-5-5")
    val Pencil = icon("M17 3a2.83 2.83 0 0 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z M15 5l4 4")
    val Trash = icon("M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3")
    val Archive = icon("M3 4h18v4H3zM4 8v11a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1V8M10 12h4")
    val Swap = icon("M8 3L4 7l4 4M4 7h16M16 21l4-4-4-4M20 17H4")
    val Repeat = icon("M4 8a8 8 0 0 1 13.3-3.3L20 7M20 4v3h-3M20 16a8 8 0 0 1-13.3 3.3L4 17M4 20v-3h3")
    val Logout = icon("M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9")
    val Chevron = icon("M9 5l7 7-7 7")

    // --- meta / cards -------------------------------------------------------------------------------

    val User = icon("M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8")
    val Users = icon("M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M22 21v-2a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8")
    val Calendar = icon("M3 6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM3 10h18M8 3v4M16 3v4")
    val Chart = icon("M4 20V4M4 20h16M8 20v-6M13 20V9M18 20v-11")
    val Trending = icon("M3 17l6-6 4 4 8-8M15 7h6M21 7v6")
    val Target = icon(stroke = "M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16zM12 7.7a4.3 4.3 0 1 0 0 8.6 4.3 4.3 0 0 0 0-8.6z", fill = "M12 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2z")
    val Coins = icon("M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM18.09 10.37A6 6 0 1 1 10.34 18M7 6h1v4M16.71 13.88l.7.71-2.82 2.82")
    val Bank = icon("M4 20h16M4 10l8-5 8 5M6 10v8M10 10v8M14 10v8M18 10v8")
    val Shield = icon("M12 3.5l7 2.7v5c0 4.6-3 7.6-7 9.3-4-1.7-7-4.7-7-9.3v-5z M9 12l2 2 4-4.5")
    val Alert = icon("M12 4l9 16H3zM12 10v5M12 17.5h.01")
    val Tag = icon(stroke = "M20.6 13.4L13.4 20.6a2 2 0 0 1-2.8 0l-6.2-6.2a2 2 0 0 1-.6-1.4V5a1 1 0 0 1 1-1h7a2 2 0 0 1 1.4.6l6.4 6.4a2 2 0 0 1 0 2.4z", fill = "M8 7a1 1 0 1 0 0 2 1 1 0 0 0 0-2z")
    val Note = icon("M4 6.5h16v11H4zM4 11h16M8 15h3")
    val Info = icon("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 11v5M12 7.5h.01")
}
