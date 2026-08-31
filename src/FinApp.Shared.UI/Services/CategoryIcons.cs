namespace FinApp.Shared.UI.Services;

/// <summary>
/// The curated set of category icons the user can pick from, plus a name-based guesser so categories without an
/// explicit icon still get something distinctive. Icons are now the app's <b>line-icon set</b> (names resolved by
/// the &lt;Icon&gt; component / sprite), shown on a per-icon colour chip — see <see cref="Color"/>.
/// <para>
/// Stored values used to be emoji (the old palette). New picks store an icon <b>name</b>; <see cref="Effective"/>
/// translates any legacy emoji still on disk via <see cref="EmojiToName"/>, so existing categories render as icons
/// without a data migration. Presentation only — the chosen value is stored on the category's icon field.
/// </para>
/// </summary>
public static class CategoryIcons
{
    /// <summary>Shown when a category has no icon and the name doesn't match anything.</summary>
    public const string Fallback = "tag";

    /// <summary>The predefined icons offered in the add/edit picker (categories, funds, buckets, contributions).</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "utensils", "cart", "burger", "coffee", "beer", "house", "bulb", "droplet", "flame", "car",
        "fuel", "bus", "plane", "bag", "shirt", "pill", "cross", "dumbbell", "film", "gamepad",
        "music", "phone", "laptop", "globe", "graduation", "book", "gift", "paw", "baby", "scissors",
        "receipt", "coins", "bank", "wrench", "plant", "palette", "note", "purse", "briefcase", "card",
        "trending", "party", "beach", "stethoscope", "shower",
    ];

    /// <summary>Every valid icon name (the palette + the fallback) — used to tell a stored name from a legacy emoji.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(Palette, StringComparer.Ordinal) { Fallback };

    /// <summary>Legacy emoji (the old palette) → the icon name that replaced it. Read-time migration for stored data.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmojiToName = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["🍽️"] = "utensils", ["🛒"] = "cart", ["🍔"] = "burger", ["☕"] = "coffee", ["🍺"] = "beer",
        ["🏠"] = "house", ["💡"] = "bulb", ["💧"] = "droplet", ["🔥"] = "flame", ["🚗"] = "car",
        ["⛽"] = "fuel", ["🚌"] = "bus", ["✈️"] = "plane", ["🛍️"] = "bag", ["👕"] = "shirt",
        ["💊"] = "pill", ["🏥"] = "cross", ["💪"] = "dumbbell", ["🎬"] = "film", ["🎮"] = "gamepad",
        ["🎵"] = "music", ["📱"] = "phone", ["💻"] = "laptop", ["🌐"] = "globe", ["🎓"] = "graduation",
        ["📚"] = "book", ["🎁"] = "gift", ["🐶"] = "paw", ["👶"] = "baby", ["💇"] = "scissors",
        ["🧾"] = "receipt", ["💰"] = "coins", ["🏦"] = "bank", ["🔧"] = "wrench", ["🌱"] = "plant",
        ["🎨"] = "palette", ["💵"] = "note", ["👛"] = "purse", ["💼"] = "briefcase", ["🪙"] = "coins",
        ["💳"] = "card", ["📈"] = "trending", ["🎉"] = "party", ["🏖️"] = "beach", ["🩺"] = "stethoscope",
        ["🚿"] = "shower", ["🏷️"] = "tag",
        // The trip labels seeded as emoji before they were seeded as icon names. Mapping them here upgrades every
        // account that already has them on the next render — no data change, no re-seed (EnsureTripTags is
        // idempotent and would never touch them again anyway).
        ["🏨"] = "house", ["🎟️"] = "film", ["🎟"] = "film", ["📦"] = "tag", ["🧳"] = "plane",
        ["🏔️"] = "beach", ["🎿"] = "beach", ["🏕️"] = "beach", ["🚗"] = "car", ["🚢"] = "bus",
    };

    // The colour each icon sits on — semantic and stable (all "cart" categories share the same green, etc.), so a
    // list stays scannable by colour the way emoji were. Dark-theme-friendly saturated tones.
    private static readonly IReadOnlyDictionary<string, string> IconColor = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["utensils"] = "#ea7317", ["cart"] = "#16a34a", ["burger"] = "#f59e0b", ["coffee"] = "#a16207", ["beer"] = "#ca8a04",
        ["house"] = "#6366f1", ["bulb"] = "#eab308", ["droplet"] = "#0ea5e9", ["flame"] = "#ef4444", ["car"] = "#64748b",
        ["fuel"] = "#f97316", ["bus"] = "#0ea5e9", ["plane"] = "#06b6d4", ["bag"] = "#ec4899", ["shirt"] = "#d946ef",
        ["pill"] = "#22c55e", ["cross"] = "#ef4444", ["dumbbell"] = "#8b5cf6", ["film"] = "#a855f7", ["gamepad"] = "#7c3aed",
        ["music"] = "#db2777", ["phone"] = "#3b82f6", ["laptop"] = "#475569", ["globe"] = "#0284c7", ["graduation"] = "#4f46e5",
        ["book"] = "#b45309", ["gift"] = "#e11d48", ["paw"] = "#a16207", ["baby"] = "#f472b6", ["scissors"] = "#db2777",
        ["receipt"] = "#94a3b8", ["coins"] = "#f59e0b", ["bank"] = "#6366f1", ["wrench"] = "#78716c", ["plant"] = "#16a34a",
        ["palette"] = "#f59e0b", ["note"] = "#22c55e", ["purse"] = "#d97706", ["briefcase"] = "#14b8a6", ["card"] = "#dc2626",
        ["trending"] = "#10b981", ["party"] = "#f43f5e", ["beach"] = "#06b6d4", ["stethoscope"] = "#14b8a6", ["shower"] = "#0ea5e9",
        ["tag"] = "#64748b",
    };

    /// <summary>
    /// The keyword rules, exposed for the assistant.
    /// <para>★ These already map an English word to an icon in order to <em>guess</em> one for a new category.
    /// That makes them the only language-independent handle the app has on what a category IS: a user whose
    /// categories are named in Bulgarian still has one wearing the "cart" icon, and "grocery" still points at
    /// it. Reusing the table is what lets somebody ask in one language about a category named in another.</para>
    /// </summary>
    public static IEnumerable<(string[] Keywords, string Icon)> KeywordRules => Rules;

    // Ordered keyword → icon rules; first match wins. Lowercased "contains" matching.
    private static readonly (string[] Keywords, string Icon)[] Rules =
    [
        (["restaurant", "dining", "dine", "meal", "lunch", "dinner", "food", "eat"], "utensils"),
        (["grocer", "supermarket"], "cart"),
        (["fast", "burger", "takeaway", "takeout", "snack"], "burger"),
        (["coffee", "cafe", "café"], "coffee"),
        (["beer", "alcohol", "drink", "bar", "pub", "wine"], "beer"),
        (["salary", "wage", "payroll", "paycheck", "income"], "briefcase"),
        (["bonus", "commission"], "party"),
        (["pension", "dividend", "interest", "investment", "freelance", "side"], "trending"),
        (["cash", "wallet"], "note"),
        (["rent", "mortgage", "housing", "house", "home", "accommodation"], "house"),
        (["electric", "utilit", "bill", "power"], "bulb"),
        (["water"], "droplet"),
        (["heat", "heating"], "flame"),
        (["fuel", "petrol", "diesel", "gasolin"], "fuel"),
        (["car", "auto", "vehicle"], "car"),
        (["bus", "train", "transit", "metro", "subway", "transport", "commut"], "bus"),
        (["flight", "travel", "trip", "vacation", "holiday", "hotel"], "plane"),
        (["cloth", "apparel", "shoe", "fashion"], "shirt"),
        (["shop", "shopping"], "bag"),
        (["pharm", "medic", "medicine", "drug"], "pill"),
        (["health", "doctor", "dentist", "hospital", "clinic"], "cross"),
        (["gym", "fitness", "sport", "workout"], "dumbbell"),
        (["movie", "cinema", "entertain", "netflix"], "film"),
        (["game", "gaming", "playstation", "xbox"], "gamepad"),
        (["music", "spotify", "concert"], "music"),
        (["phone", "mobile"], "phone"),
        (["tech", "computer", "software", "gadget", "electronic", "subscription"], "laptop"),
        (["internet", "wifi", "web", "broadband"], "globe"),
        (["school", "education", "tuition", "course", "class", "study"], "graduation"),
        (["book", "magazine", "news"], "book"),
        (["gift", "present", "donation", "charity"], "gift"),
        (["pet", "dog", "cat", "vet"], "paw"),
        (["kid", "child", "baby", "family"], "baby"),
        (["beauty", "hair", "salon", "cosmetic", "care", "grooming"], "scissors"),
        (["tax", "fee", "fees", "charge"], "receipt"),
        (["saving", "save", "invest"], "coins"),
        (["bank", "loan", "debt", "credit", "insurance"], "bank"),
        (["repair", "maintenance", "fix", "tool", "diy"], "wrench"),
        (["garden", "plant", "flower"], "plant"),
        (["hobby", "hobbies", "craft", "art", "leisure", "fun"], "palette"),
    ];

    /// <summary>The icon name to display given an explicit (maybe-null, maybe legacy-emoji) icon and a name.</summary>
    public static string Effective(string? icon, string? name)
    {
        if (string.IsNullOrWhiteSpace(icon)) return Guess(name);
        if (Names.Contains(icon)) return icon;                                   // already an icon name (new data)
        return EmojiToName.TryGetValue(icon, out var mapped) ? mapped : Guess(name);  // legacy emoji → name
    }

    /// <summary>
    /// The icon name for a stored value, or <c>null</c> when it is a glyph we have no icon for.
    /// <para>
    /// Unlike <see cref="Effective"/> this never guesses from a name and never falls back to "tag" — the caller
    /// wants to know whether an icon genuinely exists, so it can draw the user's own emoji rather than silently
    /// replacing it with a generic label mark.
    /// </para>
    /// </summary>
    public static string? EffectiveOrNull(string? icon) =>
        string.IsNullOrWhiteSpace(icon) ? null
        : Names.Contains(icon) ? icon
        : EmojiToName.TryGetValue(icon, out var mapped) ? mapped
        : null;

    /// <summary>Best-effort icon name for a category name (used when no icon is stored).</summary>
    public static string Guess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fallback;
        var n = name.ToLowerInvariant();
        foreach (var (keywords, icon) in Rules)
            foreach (var k in keywords)
                if (n.Contains(k, StringComparison.Ordinal))
                    return icon;
        return Fallback;
    }

    /// <summary>The chip colour for an icon name (semantic + stable). Falls back to a neutral slate.</summary>
    public static string Color(string? iconName) =>
        iconName is not null && IconColor.TryGetValue(iconName, out var c) ? c : "#64748b";
}
