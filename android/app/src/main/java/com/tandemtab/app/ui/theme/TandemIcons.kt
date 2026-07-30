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
    // "recall / undo-history" circular arrow — used for the add sheet's "edit last" affordance (web i-rotate)
    val Rotate = icon("M21 12a9 9 0 1 1-3-6.7L21 8M21 3v5h-5")

    // --- category-domain icons (the web CategoryIcons palette + guesser) -----------------------------
    // Names match CategoryIcons.Palette; resolve a category's stored icon/name to one via [forCategory].
    private val categoryPaths: Map<String, String> = mapOf(
        "utensils" to "M6 3v5a2 2 0 0 0 4 0V3M8 8v13M16 10.1V21M16 2.9a2.6 3.6 0 1 0 0 7.2 2.6 3.6 0 0 0 0-7.2z",
        "cart" to "M5 6h15l-1.6 8H7L5 6zM5 6 4.2 3H2M8.5 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2M17 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2",
        "burger" to "M4 10a8 8 0 0 1 16 0zM4 14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2M3 14h18M8 10h.01M12 10h.01M16 10h.01",
        "coffee" to "M4 8h13v5a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4zM17 9h2a2 2 0 0 1 0 4h-2M7 2v2M11 2v2",
        "beer" to "M7 5h9v14a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1zM16 8h2a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2h-2M10 9v7M13 9v7",
        "house" to "M3 11l9-7 9 7M5 10v10h14V10M10 20v-6h4v6",
        "bulb" to "M9 18h6M10 21h4M12 3a6 6 0 0 0-4 10.5c.7.7 1 1.5 1 2.5h6c0-1 .3-1.8 1-2.5A6 6 0 0 0 12 3z",
        "droplet" to "M12 3s6 6 6 10a6 6 0 0 1-12 0c0-4 6-10 6-10z",
        "flame" to "M12 2c2.5 3.5 5 6 5 9.5A5 5 0 0 1 7 11.5C7 9 8.5 7 10 5.5c.3 1.2 1 2 2 2.5 0-2-1-4 0-6z",
        "car" to "M5 13l1.5-4.5A2 2 0 0 1 8.4 7h7.2a2 2 0 0 1 1.9 1.5L19 13M4 13h16v4H4zM7.5 19a1 1 0 1 0 0-2 1 1 0 0 0 0 2M16.5 19a1 1 0 1 0 0-2 1 1 0 0 0 0 2",
        "fuel" to "M4 20V6a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v14M3 20h12M4 11h10M16 8l3 3v6a2 2 0 0 0 2 0V8l-3-2",
        "bus" to "M4 6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v10H4zM4 11h16M8 16v2M16 16v2M7.5 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2M16.5 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2",
        "plane" to "M12 2.5c.7 0 1 .8 1 2V9l7 4v2l-7-2v4l2 1.7v1.5L12 19l-3 1.2v-1.5l2-1.7v-4l-7 2v-2l7-4V4.5c0-1.2.3-2 1-2z",
        "bag" to "M6 8h12l-1 12H7L6 8zM9 8V6a3 3 0 0 1 6 0v2",
        "shirt" to "M8 3l4 2 4-2 4 4-3 2v10H7V9L4 7z",
        "pill" to "M10.5 3.5a5 5 0 0 1 7 7l-7 7a5 5 0 0 1-7-7zM7 7l7 7",
        "cross" to "M9 3h6v6h6v6h-6v6H9v-6H3V9h6z",
        "dumbbell" to "M4 9v6M7 8v8M17 8v8M20 9v6M7 12h10",
        "film" to "M4 4h16v16H4zM4 9h16M4 15h16M9 4v16M15 4v16",
        "gamepad" to "M6 11h4M8 9v4M15 11h.01M18 13h.01M7 6h10a5 5 0 0 1 5 5 3 3 0 0 1-5.5 2H7.5A3 3 0 0 1 2 11a5 5 0 0 1 5-5z",
        "music" to "M9 18V5l10-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM19 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z",
        "phone" to "M7 3h10a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zM10 18h4",
        "laptop" to "M4 6a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v10H4zM2 20h20l-2-4H4z",
        "globe" to "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM3 12h18M12 3c2.5 2.5 4 5.5 4 9s-1.5 6.5-4 9c-2.5-2.5-4-5.5-4-9s1.5-6.5 4-9z",
        "graduation" to "M12 4l10 5-10 5L2 9zM6 11v5c0 1.5 2.7 3 6 3s6-1.5 6-3v-5M20 10v5",
        "book" to "M5 4h13a1 1 0 0 1 1 1v15H6a2 2 0 0 0-2 2V5a1 1 0 0 1 1-1zM4 18a2 2 0 0 1 2-2h13",
        "gift" to "M4 12v8h16v-8M3 8h18v4H3zM12 8v12M12 8C11 5 8.5 4.5 7.5 5.5S8.5 8 12 8zM12 8c1-3 3.5-3.5 4.5-2.5S15.5 8 12 8z",
        "paw" to "M9 5a2 2 0 1 1-4 0 2 2 0 0 1 4 0M19 5a2 2 0 1 1-4 0 2 2 0 0 1 4 0M6 12a2 2 0 1 1-4 0 2 2 0 0 1 4 0M22 12a2 2 0 1 1-4 0 2 2 0 0 1 4 0M8 15a4 4 0 0 1 8 0c0 2-1.5 3.5-4 3.5S8 17 8 15z",
        "baby" to "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM9 12h.01M15 12h.01M9 16s1.2 1.5 3 1.5 3-1.5 3-1.5",
        "scissors" to "M6 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM8.5 8.5 20 20M8.5 15.5 20 4M8.5 8.5 13 13",
        "receipt" to "M6 3.5h12v17l-3-1.8-3 1.8-3-1.8-3 1.8zM9 8h6M9 12h6",
        "coins" to "M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM18.09 10.37A6 6 0 1 1 10.34 18M7 6h1v4M16.71 13.88l.7.71-2.82 2.82",
        "bank" to "M4 20h16M4 10l8-5 8 5M6 10v8M10 10v8M14 10v8M18 10v8",
        "wrench" to "M14.7 6.3a4 4 0 0 0-5.4 5.4L4 17l3 3 5.3-5.3a4 4 0 0 0 5.4-5.4l-2.5 2.5-2.5-2.5z",
        "plant" to "M12 22V10M12 10C12 6 9 4 5 4c0 4 3 6 7 6zM12 13c0-3 2.5-5 6-5 0 3.5-2.5 5-6 5z",
        "palette" to "M12 21a9 9 0 1 1 0-18c5 0 9 3.5 9 8 0 2.5-2 3-4 3h-2a2 2 0 0 0-1.5 3.3A1.5 1.5 0 0 1 12 21zM7.5 11a1 1 0 1 0 0-2 1 1 0 0 0 0 2M11 8a1 1 0 1 0 0-2 1 1 0 0 0 0 2M16 9a1 1 0 1 0 0-2 1 1 0 0 0 0 2",
        "note" to "M4 6.5h16v11H4zM4 11h16M8 15h3",
        "purse" to "M4 8a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2zM4 8c0-2 2-4 4-4h8M15 13h.01",
        "briefcase" to "M4 8h16v11H4zM9 8V6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2M4 13h16",
        "card" to "M3 7a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM3 10h18M7 15h4",
        "trending" to "M3 17l6-6 4 4 8-8M15 7h6M21 7v6",
        "party" to "M5 20l3-10 7 7-10 3zM13 15l6-8M11 4l1 2M18 4l-1 2M20 11l-2 1M9 8l1-2",
        "beach" to "M12 3a9 5 0 0 0-9 5h18a9 5 0 0 0-9-5zM12 8v13M9 21h6",
        "stethoscope" to "M5 3v4a4 4 0 0 0 8 0V3M4 3h2M12 3h2M9 11v4a5 5 0 0 0 9 3M19 15a2 2 0 1 0 0-4 2 2 0 0 0 0 4",
        "shower" to "M4 20v-6a5 5 0 0 1 10 0M4 14h10M17 4a3 3 0 0 0-3 3M17 4v3M14 7h6M9 18v.01M12 20v.01M6 20v.01",
        "tag" to "M20.6 13.4L13.4 20.6a2 2 0 0 1-2.8 0l-6.2-6.2a2 2 0 0 1-.6-1.4V5a1 1 0 0 1 1-1h7a2 2 0 0 1 1.4.6l6.4 6.4a2 2 0 0 1 0 2.4z",
    )
    private val categoryIcons: Map<String, ImageVector> = categoryPaths.mapValues { icon(it.value) }

    /** The line-icon for a resolved category icon name (see CategoryIcons.effective), falling back to the tag icon. */
    fun forCategory(name: String): ImageVector = categoryIcons[name] ?: categoryIcons.getValue("tag")
}
