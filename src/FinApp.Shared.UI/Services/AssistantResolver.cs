using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// The client half of R3: it supplies this account's vocabulary to <see cref="AssistantMasker"/>, and resolves an
/// answer's slot back to a real entity.
/// <para>
/// ★ <b>The masking lives on the client for a reason:</b> the thick web client already holds the whole account in
/// memory, so it can recognise the user's own words without asking anyone. The server never needs the vocabulary,
/// so it never has it — which is a stronger statement than any promise about what a server does with what it has.
/// </para>
/// </summary>
public sealed class AssistantResolver(BudgetingState state)
{
    public MaskedQuestion Mask(string question) => AssistantMasker.Mask(question, Vocabulary());

    /// <summary>The answer's entity, when it points at one. Null means the reply needs none — or named a slot that
    /// does not exist, which the server should already have rejected and this declines to trust anyway.
    /// <para>⚠️ The name is re-read from the account rather than taken from the slot. A slot may have been matched
    /// through an alias — a singular, or an English keyword standing in for a category named in Bulgarian — and
    /// the answer must say what the category is actually CALLED, not the word that found it.</para>
    /// </summary>
    public AssistantSlot? SlotFor(AssistantReplyDto reply, MaskedQuestion masked)
    {
        if (reply.Slot is not { } n || n < 1 || n > masked.Slots.Count) return null;
        var slot = masked.Slots[n - 1];
        return CanonicalName(slot) is { } name ? slot with { Name = name } : slot;
    }

    private string? CanonicalName(AssistantSlot slot)
    {
        if (state.CurrentAccountId == Guid.Empty) return null;
        var account = state.Account;
        return slot.Kind switch
        {
            AssistantSlotKinds.Category => account.Categories.FirstOrDefault(c => c.Id == slot.Id)?.Name,
            AssistantSlotKinds.Goal => account.SavingCategories.FirstOrDefault(b => b.Id == slot.Id)?.Name,
            AssistantSlotKinds.Wallet => account.Funds.FirstOrDefault(f => f.Id == slot.Id)?.Name,
            AssistantSlotKinds.Trip => account.Trips.FirstOrDefault(t => t.Id == slot.Id)?.Name,
            _ => null,
        };
    }

    /// <summary>Everything the user has named on this account.</summary>
    private List<AssistantSlot> Vocabulary()
    {
        var vocab = new List<AssistantSlot>();
        if (state.CurrentAccountId == Guid.Empty) return vocab;

        var account = state.Account;
        foreach (var c in account.Categories.Where(c => !c.IsArchived))
            Add(AssistantSlotKinds.Category, c.Id, c.Name);
        foreach (var b in account.SavingCategories.Where(b => !b.IsArchived))
            Add(AssistantSlotKinds.Goal, b.Id, b.Name);
        foreach (var f in account.Funds.Where(f => !f.IsArchived))
            Add(AssistantSlotKinds.Wallet, f.Id, f.Name);
        foreach (var t in account.Trips)
        {
            Add(AssistantSlotKinds.Trip, t.Id, t.Name);
            // ★ A journey's destination is a second name for the same thing, and it is the one people say out
            // loud — "what did I spend in Rome" about a trip called "Ski week". It is also free text the user
            // typed, so masking it is not a nicety: leaving it out lets a place name travel.
            Add(AssistantSlotKinds.Trip, t.Id, t.Destination);
        }

        AddIconAliases(account, vocab);
        return vocab;

        void Add(string kind, Guid id, string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) vocab.Add(new AssistantSlot(kind, id, name));
        }
    }

    /// <summary>
    /// Lets an English word find a category whose name is in another language.
    /// <para>
    /// ⭐ <b>The problem this exists for:</b> an account with categories called "Храна" and "Комунални разходи",
    /// and a question typed as "why did my grocery bill jump". No amount of stemming reaches across languages, so
    /// the category simply never matched — and the comparison fell back to an account-wide figure.
    /// </para>
    /// <para>
    /// The bridge is <see cref="CategoryIcons"/>'s keyword table, which already maps "grocer" to the "cart" icon
    /// in order to guess an icon for a new category. The icon is language-independent, so the keyword can find the
    /// category wearing it whatever it is named.
    /// </para>
    /// <para>
    /// ⚠️ <b>Only when exactly one category wears that icon.</b> With two, the word is ambiguous and a guess would
    /// silently answer about the wrong one — which is worse than not matching, because the answer looks right.
    /// </para>
    /// </summary>
    private static void AddIconAliases(Account account, List<AssistantSlot> vocab)
    {
        var live = account.Categories.Where(c => !c.IsArchived).ToList();
        var byIcon = live
            .GroupBy(c => CategoryIcons.Effective(c.Icon, c.Name))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        foreach (var (keywords, icon) in CategoryIcons.KeywordRules)
        {
            if (!byIcon.TryGetValue(icon, out var category)) continue;
            foreach (var keyword in keywords)
            {
                // Skip a keyword that is already the category's own name — it would only duplicate a form the
                // masker has, and the real name should always be what matches first.
                if (keyword.Equals(category.Name, StringComparison.CurrentCultureIgnoreCase)) continue;
                vocab.Add(new AssistantSlot(AssistantSlotKinds.Category, category.Id, keyword));
            }
        }
    }
}
