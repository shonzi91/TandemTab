namespace FinApp.Shared.UI.Services;

/// <summary>
/// What a currency code looks like when the app prints money in it, plus the shortlist a picker offers.
/// <para>
/// This exists because the app used to know exactly three symbols — EUR, USD, GBP — and printed the bare code for
/// everything else. That was invisible while an account was the only thing with a currency; trips made it visible,
/// since a trip is the one place a user deliberately names a <i>second</i> currency and immediately wants to know
/// what they will get back.
/// </para>
/// <para>
/// <b>★ Ambiguous dollars carry their prefix.</b> CAD, AUD, NZD, SGD, HKD and MXN are all "$" locally, but an app
/// that prints a bare "$" for six different currencies is stating something it cannot back up. "CA$" is longer and
/// true, which beats short and wrong.
/// </para>
/// </summary>
public static class CurrencyInfo
{
    private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EUR"] = "€", ["USD"] = "$", ["GBP"] = "£", ["JPY"] = "¥", ["CHF"] = "CHF ",
        ["SEK"] = "kr", ["NOK"] = "kr", ["DKK"] = "kr", ["ISK"] = "kr",
        ["PLN"] = "zł", ["CZK"] = "Kč", ["HUF"] = "Ft", ["RON"] = "lei", ["BGN"] = "лв",
        ["RSD"] = "дин", ["UAH"] = "₴", ["TRY"] = "₺", ["ILS"] = "₪",
        ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$", ["SGD"] = "S$", ["HKD"] = "HK$", ["MXN"] = "MX$",
        ["BRL"] = "R$", ["ZAR"] = "R", ["INR"] = "₹", ["CNY"] = "¥", ["KRW"] = "₩",
        ["THB"] = "฿", ["VND"] = "₫", ["PHP"] = "₱", ["IDR"] = "Rp", ["MYR"] = "RM",
        ["AED"] = "AED ", ["SAR"] = "SAR ", ["EGP"] = "E£", ["MAD"] = "MAD ",
    };

    /// <summary>The codes a picker offers, ordered by how often a European traveller meets them. Not a closed set —
    /// the field stays free text, because a shortlist that refuses an unlisted currency is worse than no shortlist.</summary>
    public static readonly IReadOnlyList<string> Common =
    [
        "EUR", "USD", "GBP", "CHF", "SEK", "NOK", "DKK", "PLN", "CZK", "HUF", "RON", "BGN", "TRY", "RSD",
        "JPY", "CNY", "THB", "AED", "CAD", "AUD", "NZD", "SGD", "INR", "MXN", "BRL", "ZAR",
    ];

    /// <summary>
    /// What precedes the amount for this code — "€" for EUR, "CA$" for CAD, and the code plus a space for anything
    /// unlisted, which is what the app has always printed as its fallback.
    /// </summary>
    public static string Symbol(string? code) =>
        string.IsNullOrWhiteSpace(code) ? ""
        : Symbols.TryGetValue(code.Trim(), out var s) ? s
        : code.Trim().ToUpperInvariant() + " ";

    /// <summary>True when we hold a real symbol rather than falling back to the code — so a picker can say
    /// "shows as SEK 100.00" honestly instead of implying a "kr" it wouldn't actually print.</summary>
    public static bool HasSymbol(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Symbols.ContainsKey(code.Trim());
}
