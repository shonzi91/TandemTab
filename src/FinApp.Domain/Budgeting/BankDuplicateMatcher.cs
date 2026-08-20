namespace FinApp.Domain.Budgeting;

/// <summary>
/// Pure reconciliation helper for the bank-import de-duplication flow. When sync is down a user may log an expense
/// manually; when the same transaction later arrives from the bank, this pairs the incoming debit with the existing
/// un-linked entry so the UI can offer to replace it instead of double-counting. It only takes numbers, dates and
/// descriptions and returns suggested pairings — it touches nothing in the money model.
/// </summary>
public static class BankDuplicateMatcher
{
    /// <summary>How far apart two rows may be dated and still be suggested as the same transaction, when their
    /// descriptions agree that they are the same merchant. Card charges book one to three days after they are made,
    /// so the window has to be wider than "today".</summary>
    public const int DefaultWindowDays = 4;

    /// <summary>The window that applies when the descriptions <b>don't</b> vouch for each other — a manual entry
    /// noted "dinner" against a bank row reading "SUMUP *TRATTORIA". Same amount on the same day (or either side of
    /// midnight, which is how a late-evening charge books) is still worth suggesting; four days apart, with nothing
    /// but a round number in common, is how two genuinely separate spends get called the same one.</summary>
    public const int UnmatchedTextWindowDays = 1;

    /// <summary>
    /// The window used to hold an <b>auto-filed</b> transaction back into manual review. Deliberately the tight one
    /// and nothing else: unlike the review hint below, that suppression is silent and undoes a rule the user set up
    /// on purpose. Two €10 charges at the same shop two days apart are two charges, and the merchant rule the user
    /// wrote should file both. A false negative here costs one deletable duplicate; a false positive costs the
    /// feature.
    /// </summary>
    public const int AutoFileWindowDays = UnmatchedTextWindowDays;

    /// <summary>An incoming bank debit (its <see cref="Amount"/> is negative, as banks report outflows).
    /// <see cref="Description"/> is the merchant text the bank sent, and may be empty.</summary>
    public readonly record struct Pending(string ExternalId, decimal Amount, DateOnly Date, string? Description = null);

    /// <summary>An existing, not-yet-bank-linked expense (its <see cref="Amount"/> is the positive spend).
    /// <see cref="Text"/> is the expense's note — the bank's own description when the row came from an import,
    /// whatever the user typed when it didn't, and often nothing at all.</summary>
    public readonly record struct Entry(Guid ExpenseId, decimal Amount, DateOnly Date, string? Text = null);

    /// <summary>A suggested pairing (a bank debit ↔ an existing entry) and how many days apart they are.</summary>
    public readonly record struct Suggestion(string ExternalId, Guid ExpenseId, int DayGap);

    /// <summary>
    /// Greedy 1:1 suggestions: each pending debit is paired with at most one un-linked entry of the <b>same
    /// amount</b>, nearest date first. Each entry is claimed for only one debit, so two identical spends need two
    /// matching entries to both pair (matching is per-occurrence, not by existence).
    /// <para>
    /// ★ The amount and the date used to be the whole test, and a round number is not rare: two €10 spends two days
    /// apart paired, and the review list told the user they had already logged something they hadn't. The
    /// descriptions now have a say. When they name the same merchant the full <paramref name="windowDays"/> applies
    /// — that is the case this feature was built for, the same transaction reaching the app twice, and the second
    /// copy carries the bank's own wording. When they don't (or one side is silent), the pair has to be within
    /// <see cref="UnmatchedTextWindowDays"/> day of itself to be suggested at all.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Suggestion> Suggest(IEnumerable<Pending> debits, IEnumerable<Entry> entries, int windowDays = DefaultWindowDays)
    {
        var open = entries.ToList();
        var result = new List<Suggestion>();
        // Oldest debit first so earlier spends claim their nearest entry before later ones compete for it.
        foreach (var d in debits.OrderBy(x => x.Date).ThenBy(x => x.ExternalId, StringComparer.Ordinal))
        {
            var target = Math.Abs(d.Amount);
            Entry? best = null;
            var bestGap = int.MaxValue;
            foreach (var e in open)
            {
                if (e.Amount != target) continue;
                var gap = Math.Abs(e.Date.DayNumber - d.Date.DayNumber);
                if (gap > AllowedGap(d.Description, e.Text, windowDays) || gap >= bestGap) continue;
                best = e;
                bestGap = gap;
            }
            if (best is { } m)
            {
                result.Add(new Suggestion(d.ExternalId, m.ExpenseId, bestGap));
                open.Remove(m);
            }
        }
        return result;
    }

    /// <summary>True when these two rows are close enough, in days, to be the same transaction — given what their
    /// descriptions do or don't say about each other. The one place the text rule is applied, so the review hint and
    /// the auto-file guard cannot drift apart.</summary>
    public static bool CouldBeSame(decimal amountA, DateOnly dateA, string? textA,
                                   decimal amountB, DateOnly dateB, string? textB,
                                   int windowDays = DefaultWindowDays)
    {
        if (Math.Abs(amountA) != Math.Abs(amountB)) return false;
        return Math.Abs(dateA.DayNumber - dateB.DayNumber) <= AllowedGap(textA, textB, windowDays);
    }

    private static int AllowedGap(string? a, string? b, int windowDays) =>
        MerchantText.SameMerchant(a, b) ? windowDays : Math.Min(windowDays, UnmatchedTextWindowDays);
}
