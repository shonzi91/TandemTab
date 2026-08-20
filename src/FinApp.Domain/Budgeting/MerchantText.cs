namespace FinApp.Domain.Budgeting;

/// <summary>
/// One definition of "what words in this text name a merchant". Bank descriptions arrive as
/// <c>"TESCO STORES 4471 LONDON GB"</c> and an expense note as <c>"Tesco"</c>; deciding whether two such strings
/// talk about the same shop is a text question, not a money question, so it lives on its own — used by
/// <see cref="BankDuplicateMatcher"/> to tell a re-imported transaction from a second real charge, and by the
/// merchant-stem rule that keeps "Fantastico 30" and "Fantastico Group Ltd" on one auto-file rule.
/// </summary>
public static class MerchantText
{
    /// <summary>
    /// Words that carry no merchant identity — legal suffixes, payment-terminal noise, common stopwords. Dropped
    /// before comparing two descriptions, so "CARD PAYMENT TESCO" and "CARD PAYMENT SHELL" do not look alike
    /// merely because every card charge says "card payment".
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "and", "for", "ltd", "ltda", "llc", "inc", "gmbh", "plc", "corp", "llp", "group",
        "ad", "ead", "ood", "eood", "jsc", "sa", "bv", "oy", "ab", "co", "com",
        "card", "payment", "pos", "purchase", "pmt", "trans", "www",
    };

    /// <summary>
    /// The identifying words of a description: lowercased, split on any non-letter/non-digit boundary, with noise
    /// words and one- or two-character fragments dropped. Digits survive (a store number is weak identity, but it
    /// is identity); Cyrillic and other scripts survive, because the split is by Unicode category.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string? s) =>
        System.Text.RegularExpressions.Regex
            .Split((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(t => t.Length >= 3 && !NoiseWords.Contains(t))
            .ToList();

    /// <summary>
    /// True when two descriptions share at least one identifying word — the app's test for "these two rows are
    /// probably the same shop". <b>False when either side has no identifying words at all</b>: an expense noted
    /// only as "lunch" (or not noted at all) says nothing about the merchant, and silence must not read as
    /// agreement. Callers decide what to do with a "don't know" — see <see cref="BankDuplicateMatcher.Suggest"/>.
    /// </summary>
    public static bool SameMerchant(string? a, string? b)
    {
        var left = Tokens(a);
        if (left.Count == 0) return false;
        var right = Tokens(b).ToHashSet(StringComparer.Ordinal);
        return right.Count > 0 && left.Any(right.Contains);
    }
}
