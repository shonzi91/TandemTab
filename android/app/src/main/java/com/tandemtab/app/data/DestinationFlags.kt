package com.tandemtab.app.data

import java.util.Locale

/**
 * Guesses a flag emoji from what someone typed as a destination — "Rome, Italy" → 🇮🇹.
 *
 * A port of `FinApp.Shared.UI.Services.DestinationFlags`, keyword table and all.
 *
 * A convenience only: the trip's icon stays free text, and the guess is offered as a chip to tap rather than
 * written into the field. Nothing in the app reads a trip's icon for meaning, so a wrong guess costs a tap.
 *
 * ★ **This lands better here than on the web.** The C# original carries a warning that Windows desktop Chrome
 * renders a regional-indicator pair as the letters "IT" rather than a flag — a platform font decision the app
 * cannot fix. Android draws the flag, so on the phone the suggestion is what it was always meant to be.
 */
object DestinationFlags {

    /**
     * Country and well-known-city keywords → ISO country code. Lower-case, matched as a substring of the
     * destination and then the trip name, so "rome, italy", "Italy" and "Rome" all land on IT.
     */
    private val places: List<Pair<String, String>> = listOf(
        "bulgaria" to "BG", "sofia" to "BG", "italy" to "IT", "rome" to "IT", "milan" to "IT", "venice" to "IT",
        "florence" to "IT", "naples" to "IT", "france" to "FR", "paris" to "FR", "nice" to "FR", "lyon" to "FR",
        "spain" to "ES", "madrid" to "ES", "barcelona" to "ES", "seville" to "ES", "valencia" to "ES",
        "portugal" to "PT", "lisbon" to "PT", "porto" to "PT", "madeira" to "PT",
        "germany" to "DE", "berlin" to "DE", "munich" to "DE", "hamburg" to "DE", "cologne" to "DE",
        "austria" to "AT", "vienna" to "AT", "salzburg" to "AT", "switzerland" to "CH", "zurich" to "CH",
        "geneva" to "CH", "netherlands" to "NL", "amsterdam" to "NL", "belgium" to "BE", "brussels" to "BE",
        "greece" to "GR", "athens" to "GR", "crete" to "GR", "santorini" to "GR", "rhodes" to "GR",
        "turkey" to "TR", "istanbul" to "TR", "antalya" to "TR", "croatia" to "HR", "split" to "HR",
        "dubrovnik" to "HR", "zagreb" to "HR", "serbia" to "RS", "belgrade" to "RS",
        "romania" to "RO", "bucharest" to "RO", "hungary" to "HU", "budapest" to "HU",
        "czech" to "CZ", "prague" to "CZ", "poland" to "PL", "warsaw" to "PL", "krakow" to "PL",
        "united kingdom" to "GB", "england" to "GB", "scotland" to "GB", "london" to "GB", "edinburgh" to "GB",
        "ireland" to "IE", "dublin" to "IE", "iceland" to "IS", "reykjavik" to "IS",
        "norway" to "NO", "oslo" to "NO", "sweden" to "SE", "stockholm" to "SE", "denmark" to "DK",
        "copenhagen" to "DK", "finland" to "FI", "helsinki" to "FI",
        "united states" to "US", "usa" to "US", "new york" to "US", "california" to "US", "florida" to "US",
        "canada" to "CA", "toronto" to "CA", "mexico" to "MX", "brazil" to "BR",
        "japan" to "JP", "tokyo" to "JP", "kyoto" to "JP", "china" to "CN", "korea" to "KR",
        "thailand" to "TH", "bangkok" to "TH", "phuket" to "TH", "vietnam" to "VN", "indonesia" to "ID",
        "bali" to "ID", "singapore" to "SG", "india" to "IN", "dubai" to "AE", "emirates" to "AE",
        "egypt" to "EG", "morocco" to "MA", "south africa" to "ZA", "australia" to "AU", "new zealand" to "NZ",
        "cyprus" to "CY", "malta" to "MT", "montenegro" to "ME", "albania" to "AL", "slovenia" to "SI",
        "slovakia" to "SK", "north macedonia" to "MK", "bosnia" to "BA", "georgia" to "GE", "armenia" to "AM",
    )

    /**
     * Non-flag suggestions, for a trip that isn't about a country — a road trip, a beach week, a conference.
     * Always offered, so there is something to tap even when nothing matches.
     *
     * **Icon names, not emoji.** Everything the app itself picks is drawn from the one line-icon set, so a trip
     * mark sits beside a category mark without looking borrowed from another product. The country flag above stays
     * a real emoji because it is the one case where the glyph *is* the information.
     */
    val generic: List<String> = listOf("plane", "beach", "car", "bus", "globe", "party", "briefcase", "paw")

    /**
     * The flag for the first place named in [destination], else in [name], or null when nothing matches.
     *
     * ⚠️ **Longest key first**, so "new zealand" wins over "new york" and "south africa" over "africa". A shorter
     * key that is a substring of a longer one would otherwise decide by table order — which is to say, by accident.
     */
    fun guess(destination: String?, name: String? = null): String? {
        for (text in listOf(destination, name)) {
            if (text.isNullOrBlank()) continue
            val haystack = text.lowercase(Locale.ROOT)
            val hit = places
                .filter { haystack.contains(it.first) }
                .maxByOrNull { it.first.length }
                ?.second
            if (hit != null) return toFlag(hit)
        }
        return null
    }

    /** Two ASCII letters → the regional-indicator pair the platform draws as a flag. */
    private fun toFlag(cc: String): String =
        cc.uppercase(Locale.ROOT).map { String(Character.toChars(0x1F1E6 + (it - 'A'))) }.joinToString("")
}
