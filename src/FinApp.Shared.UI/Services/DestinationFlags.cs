namespace FinApp.Shared.UI.Services;

/// <summary>
/// Guesses a flag emoji from what someone typed as a destination — "Rome, Italy" → 🇮🇹.
/// <para>
/// A convenience only: the trip's icon stays free text, and the guess is offered as a chip to tap rather than
/// written into the field. Nothing in the app reads a trip's icon for meaning, so a wrong guess costs a tap.
/// </para>
/// <para>
/// ⚠️ <b>A flag emoji is a pair of regional-indicator letters, and Windows desktop Chrome renders that pair as the
/// letters themselves</b> — 🇮🇹 shows as "IT". Phones and macOS show the flag. That is a platform font decision, not
/// something the app can fix, which is why the non-flag suggestions below are offered alongside and why the
/// fallback icon is a suitcase rather than a flag.
/// </para>
/// </summary>
public static class DestinationFlags
{
    /// <summary>Country and well-known-city keywords → ISO country code. Lower-case, matched as a substring of the
    /// destination and then the trip name, so "rome, italy", "Italy" and "Rome" all land on IT.</summary>
    private static readonly (string Key, string Cc)[] Places =
    [
        ("bulgaria", "BG"), ("sofia", "BG"), ("italy", "IT"), ("rome", "IT"), ("milan", "IT"), ("venice", "IT"),
        ("florence", "IT"), ("naples", "IT"), ("france", "FR"), ("paris", "FR"), ("nice", "FR"), ("lyon", "FR"),
        ("spain", "ES"), ("madrid", "ES"), ("barcelona", "ES"), ("seville", "ES"), ("valencia", "ES"),
        ("portugal", "PT"), ("lisbon", "PT"), ("porto", "PT"), ("madeira", "PT"),
        ("germany", "DE"), ("berlin", "DE"), ("munich", "DE"), ("hamburg", "DE"), ("cologne", "DE"),
        ("austria", "AT"), ("vienna", "AT"), ("salzburg", "AT"), ("switzerland", "CH"), ("zurich", "CH"),
        ("geneva", "CH"), ("netherlands", "NL"), ("amsterdam", "NL"), ("belgium", "BE"), ("brussels", "BE"),
        ("greece", "GR"), ("athens", "GR"), ("crete", "GR"), ("santorini", "GR"), ("rhodes", "GR"),
        ("turkey", "TR"), ("istanbul", "TR"), ("antalya", "TR"), ("croatia", "HR"), ("split", "HR"),
        ("dubrovnik", "HR"), ("zagreb", "HR"), ("serbia", "RS"), ("belgrade", "RS"),
        ("romania", "RO"), ("bucharest", "RO"), ("hungary", "HU"), ("budapest", "HU"),
        ("czech", "CZ"), ("prague", "CZ"), ("poland", "PL"), ("warsaw", "PL"), ("krakow", "PL"),
        ("united kingdom", "GB"), ("england", "GB"), ("scotland", "GB"), ("london", "GB"), ("edinburgh", "GB"),
        ("ireland", "IE"), ("dublin", "IE"), ("iceland", "IS"), ("reykjavik", "IS"),
        ("norway", "NO"), ("oslo", "NO"), ("sweden", "SE"), ("stockholm", "SE"), ("denmark", "DK"),
        ("copenhagen", "DK"), ("finland", "FI"), ("helsinki", "FI"),
        ("united states", "US"), ("usa", "US"), ("new york", "US"), ("california", "US"), ("florida", "US"),
        ("canada", "CA"), ("toronto", "CA"), ("mexico", "MX"), ("brazil", "BR"),
        ("japan", "JP"), ("tokyo", "JP"), ("kyoto", "JP"), ("china", "CN"), ("korea", "KR"),
        ("thailand", "TH"), ("bangkok", "TH"), ("phuket", "TH"), ("vietnam", "VN"), ("indonesia", "ID"),
        ("bali", "ID"), ("singapore", "SG"), ("india", "IN"), ("dubai", "AE"), ("emirates", "AE"),
        ("egypt", "EG"), ("morocco", "MA"), ("south africa", "ZA"), ("australia", "AU"), ("new zealand", "NZ"),
        ("cyprus", "CY"), ("malta", "MT"), ("montenegro", "ME"), ("albania", "AL"), ("slovenia", "SI"),
        ("slovakia", "SK"), ("north macedonia", "MK"), ("bosnia", "BA"), ("georgia", "GE"), ("armenia", "AM"),
    ];

    /// <summary>
    /// Non-flag suggestions, for a trip that isn't about a country — a road trip, a beach week, a conference.
    /// Always offered, so there is something to tap even when nothing matches.
    /// <para>
    /// <b>Sprite names, not emoji.</b> Everything the app itself picks is drawn from the one line-icon set, so a
    /// trip mark sits beside a category mark without looking borrowed from another product. The country flag above
    /// stays a real emoji because it is the one case where the glyph <i>is</i> the information.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Generic =
        ["plane", "beach", "car", "bus", "globe", "party", "briefcase", "paw"];

    /// <summary>
    /// The flag for the first place named in <paramref name="destination"/>, else in <paramref name="name"/>, or
    /// null when nothing matches. Longest key first, so "new zealand" wins over "new york" and "south africa"
    /// over "africa" — a shorter key that is a substring of a longer one would otherwise decide by table order.
    /// </summary>
    public static string? Guess(string? destination, string? name = null)
    {
        foreach (var text in new[] { destination, name })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var haystack = text.ToLowerInvariant();
            var hit = Places
                .Where(p => haystack.Contains(p.Key, StringComparison.Ordinal))
                .OrderByDescending(p => p.Key.Length)
                .Select(p => p.Cc)
                .FirstOrDefault();
            if (hit is not null) return ToFlag(hit);
        }
        return null;
    }

    /// <summary>Two ASCII letters → the regional-indicator pair browsers draw as a flag.</summary>
    private static string ToFlag(string cc) =>
        string.Concat(cc.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + (c - 'A'))));
}
