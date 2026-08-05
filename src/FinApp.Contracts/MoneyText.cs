namespace FinApp.Contracts;

/// <summary>
/// How an amount is written when the <b>server</b> has to put one inside a sentence it sends to a client.
///
/// <para>Normally it does not: the money model computes figures and each client formats them for its own locale,
/// which is why this type is tiny and has exactly one caller worth having. The exception is a pre-composed string
/// like a notification — <c>"Food is over budget by …"</c> — where the amount is embedded in prose and the client
/// cannot format what it cannot separate.</para>
///
/// <para>⚠️ <b>It exists because the two disagreed.</b> The thin notifications endpoint wrote <c>"65.4 EUR"</c>
/// while every other figure on the same screen read <c>"€65.40"</c> — invisible on the web, whose thick Home
/// builds its own alert text, and immediately obvious on the native client, which has nothing else to render.
/// Matching <c>Dashboard.FmtCurrency</c> exactly is the whole point: two spellings of the same money in one view
/// is the app looking unfinished.</para>
/// </summary>
public static class MoneyText
{
    /// <summary>"€65.40" — the symbol for the currencies the app offers, and "65.40 XYZ" for anything else, so an
    /// unknown code degrades to something readable rather than to a bare number with no unit at all.</summary>
    public static string Format(decimal amount, string currency)
    {
        var symbol = currency switch { "EUR" => "€", "USD" => "$", "GBP" => "£", _ => currency + " " };
        return $"{symbol}{amount:N2}";
    }
}
