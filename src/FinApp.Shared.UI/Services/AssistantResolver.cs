using FinApp.Contracts;

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
    /// does not exist, which the server should already have rejected and this declines to trust anyway.</summary>
    public AssistantSlot? SlotFor(AssistantReplyDto reply, MaskedQuestion masked) =>
        reply.Slot is { } n && n >= 1 && n <= masked.Slots.Count ? masked.Slots[n - 1] : null;

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

        return vocab;

        void Add(string kind, Guid id, string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) vocab.Add(new AssistantSlot(kind, id, name));
        }
    }
}
