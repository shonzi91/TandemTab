package com.tandemtab.app.ui

import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * Category / fund / bucket icon — a **line icon** (not emoji), matching the web after its emoji→line-icon migration.
 * A category stores either a new icon *name* ("cart") or a legacy *emoji* ("🛒"); [CategoryIcons.effective] resolves
 * either (plus a name-based guess when nothing is stored) to a palette name, which [TandemIcons.forCategory] renders.
 * Tinted with the web `.cat-ic` accent (brand green in light, mint in dark).
 */
@Composable
fun CatIcon(icon: String?, name: String, size: Dp = 18.dp, tint: Color? = null) {
    val resolved = CategoryIcons.effective(icon, name)
    Icon(
        TandemIcons.forCategory(resolved),
        contentDescription = null,
        tint = tint ?: LocalTandemColors.current.catAccent,
        modifier = Modifier.size(size),
    )
}

/**
 * Resolver mirroring FinApp.Shared.UI CategoryIcons: turns a category's stored icon (a new name, a legacy emoji, or
 * nothing) plus its name into a palette icon name. Kept in sync with the web so both clients show the same glyph.
 */
object CategoryIcons {
    const val FALLBACK = "tag"

    val palette: List<String> = listOf(
        "utensils", "cart", "burger", "coffee", "beer", "house", "bulb", "droplet", "flame", "car",
        "fuel", "bus", "plane", "bag", "shirt", "pill", "cross", "dumbbell", "film", "gamepad",
        "music", "phone", "laptop", "globe", "graduation", "book", "gift", "paw", "baby", "scissors",
        "receipt", "coins", "bank", "wrench", "plant", "palette", "note", "purse", "briefcase", "card",
        "trending", "party", "beach", "stethoscope", "shower",
    )
    private val names: Set<String> = palette.toSet() + FALLBACK

    private val emojiToName: Map<String, String> = mapOf(
        "🍽️" to "utensils", "🛒" to "cart", "🍔" to "burger", "☕" to "coffee", "🍺" to "beer",
        "🏠" to "house", "💡" to "bulb", "💧" to "droplet", "🔥" to "flame", "🚗" to "car",
        "⛽" to "fuel", "🚌" to "bus", "✈️" to "plane", "🛍️" to "bag", "👕" to "shirt",
        "💊" to "pill", "🏥" to "cross", "💪" to "dumbbell", "🎬" to "film", "🎮" to "gamepad",
        "🎵" to "music", "📱" to "phone", "💻" to "laptop", "🌐" to "globe", "🎓" to "graduation",
        "📚" to "book", "🎁" to "gift", "🐶" to "paw", "👶" to "baby", "💇" to "scissors",
        "🧾" to "receipt", "💰" to "coins", "🏦" to "bank", "🔧" to "wrench", "🌱" to "plant",
        "🎨" to "palette", "💵" to "note", "👛" to "purse", "💼" to "briefcase", "🪙" to "coins",
        "💳" to "card", "📈" to "trending", "🎉" to "party", "🏖️" to "beach", "🩺" to "stethoscope",
        "🚿" to "shower", "🏷️" to "tag",
    )

    // Ordered keyword → icon; first "contains" match wins. Used only when no icon is stored.
    private val rules: List<Pair<List<String>, String>> = listOf(
        listOf("restaurant", "dining", "dine", "meal", "lunch", "dinner", "food", "eat") to "utensils",
        listOf("grocer", "supermarket") to "cart",
        listOf("fast", "burger", "takeaway", "takeout", "snack") to "burger",
        listOf("coffee", "cafe", "café") to "coffee",
        listOf("beer", "alcohol", "drink", "bar", "pub", "wine") to "beer",
        listOf("salary", "wage", "payroll", "paycheck", "income") to "briefcase",
        listOf("bonus", "commission") to "party",
        listOf("pension", "dividend", "interest", "investment", "freelance", "side") to "trending",
        listOf("cash", "wallet") to "note",
        listOf("rent", "mortgage", "housing", "house", "home", "accommodation") to "house",
        listOf("electric", "utilit", "bill", "power") to "bulb",
        listOf("water") to "droplet",
        listOf("heat", "heating") to "flame",
        listOf("fuel", "petrol", "diesel", "gasolin") to "fuel",
        listOf("car", "auto", "vehicle") to "car",
        listOf("bus", "train", "transit", "metro", "subway", "transport", "commut") to "bus",
        listOf("flight", "travel", "trip", "vacation", "holiday", "hotel") to "plane",
        listOf("cloth", "apparel", "shoe", "fashion") to "shirt",
        listOf("shop", "shopping") to "bag",
        listOf("pharm", "medic", "medicine", "drug") to "pill",
        listOf("health", "doctor", "dentist", "hospital", "clinic") to "cross",
        listOf("gym", "fitness", "sport", "workout") to "dumbbell",
        listOf("movie", "cinema", "entertain", "netflix") to "film",
        listOf("game", "gaming", "playstation", "xbox") to "gamepad",
        listOf("music", "spotify", "concert") to "music",
        listOf("phone", "mobile") to "phone",
        listOf("tech", "computer", "software", "gadget", "electronic", "subscription") to "laptop",
        listOf("internet", "wifi", "web", "broadband") to "globe",
        listOf("school", "education", "tuition", "course", "class", "study") to "graduation",
        listOf("book", "magazine", "news") to "book",
        listOf("gift", "present", "donation", "charity") to "gift",
        listOf("pet", "dog", "cat", "vet") to "paw",
        listOf("kid", "child", "baby", "family") to "baby",
        listOf("beauty", "hair", "salon", "cosmetic", "care", "grooming") to "scissors",
        listOf("tax", "fee", "fees", "charge") to "receipt",
        listOf("saving", "save", "invest") to "coins",
        listOf("bank", "loan", "debt", "credit", "insurance") to "bank",
        listOf("repair", "maintenance", "fix", "tool", "diy") to "wrench",
        listOf("garden", "plant", "flower") to "plant",
        listOf("hobby", "hobbies", "craft", "art", "leisure", "fun") to "palette",
    )

    /** The display icon name for a (maybe-null / maybe-legacy-emoji) icon + a category name. */
    fun effective(icon: String?, name: String?): String {
        if (icon.isNullOrBlank()) return guess(name)
        if (icon in names) return icon
        return emojiToName[icon] ?: guess(name)
    }

    fun guess(name: String?): String {
        if (name.isNullOrBlank()) return FALLBACK
        val n = name.lowercase()
        for ((keywords, ic) in rules) for (k in keywords) if (n.contains(k)) return ic
        return FALLBACK
    }
}
