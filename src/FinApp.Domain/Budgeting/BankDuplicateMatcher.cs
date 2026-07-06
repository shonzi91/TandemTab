namespace FinApp.Domain.Budgeting;

/// <summary>
/// Pure reconciliation helper for the bank-import de-duplication flow. When sync is down a user may log an expense
/// manually; when the same transaction later arrives from the bank, this pairs the incoming debit with the existing
/// un-linked entry so the UI can offer to replace it instead of double-counting. It only takes numbers and dates and
/// returns suggested pairings — it touches nothing in the money model.
/// </summary>
public static class BankDuplicateMatcher
{
    /// <summary>An incoming bank debit (its <see cref="Amount"/> is negative, as banks report outflows).</summary>
    public readonly record struct Pending(string ExternalId, decimal Amount, DateOnly Date);

    /// <summary>An existing, not-yet-bank-linked expense (its <see cref="Amount"/> is the positive spend).</summary>
    public readonly record struct Entry(Guid ExpenseId, decimal Amount, DateOnly Date);

    /// <summary>A suggested pairing (a bank debit ↔ an existing entry) and how many days apart they are.</summary>
    public readonly record struct Suggestion(string ExternalId, Guid ExpenseId, int DayGap);

    /// <summary>
    /// Greedy 1:1 suggestions: each pending debit is paired with at most one un-linked entry of the <b>same amount</b>
    /// within ±<paramref name="windowDays"/>, nearest date first. Each entry is claimed for only one debit, so two
    /// identical spends need two matching entries to both pair (matching is per-occurrence, not by existence).
    /// </summary>
    public static IReadOnlyList<Suggestion> Suggest(IEnumerable<Pending> debits, IEnumerable<Entry> entries, int windowDays)
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
                if (gap > windowDays || gap >= bestGap) continue;
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
}
