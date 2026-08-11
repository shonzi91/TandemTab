using FinApp.Domain.Common;

namespace FinApp.Domain.Funds;

/// <summary>
/// A place money physically lives (Bank, Cash, a digital wallet…). Account-level and user-managed:
/// stored flat on the <c>Account</c> and referenced by id from expenses, opening balances and transfers
/// — the same pattern as budget categories. Replaces the old fixed <c>FundType</c> enum.
///
/// Funds are flat. An optional free-text <see cref="Note"/> can describe a fund. <see cref="ParentId"/> is
/// vestigial (sub-funds were removed) and retained only so older persisted snapshots keep deserializing.
/// </summary>
public sealed class Fund : Entity
{
    public string Name { get; private set; }

    /// <summary>Vestigial: sub-funds were removed. Always null for funds created now.</summary>
    public Guid? ParentId { get; private set; }

    /// <summary>Optional free-text note describing the fund.</summary>
    public string? Note { get; private set; }

    /// <summary>Optional display icon (emoji). Null → the UI derives one from the name. Body data (in the snapshot, not EF).</summary>
    public string? Icon { get; private set; }

    /// <summary>
    /// When true, this fund mirrors a linked bank account (e.g. Revolut) whose real balance is authoritative,
    /// so the app never mutates it directly: entries created while synced carry a per-entry marker that keeps
    /// them out of this fund's balance math. Toggling this only affects entries created afterwards — history is
    /// preserved. Body data (in the snapshot, not EF).
    /// </summary>
    public bool IsSynced { get; private set; }

    /// <summary>Archived funds are hidden from the pickers and the main "where your money is" list but keep all their
    /// history — past expenses/transfers/contributions still resolve their name. A fund is archived (not hard-deleted)
    /// so referencing transactions are never orphaned. Body data (in the snapshot, not EF).</summary>
    public bool IsArchived { get; private set; }

    /// <summary>
    /// The currency the money in this fund is actually held in, when it isn't the account's ("GBP"). Null — the
    /// ordinary case — means the account currency.
    /// <para>
    /// <b>★ Why the rate lives on the FUND and not on the trip.</b> A trip-wide rate converted <i>everything</i>,
    /// which is wrong the moment you pay by card: the bank has already converted, your statement is in your own
    /// currency, and applying the trip's rate on top inflates the entry — so every card payment on a rated trip was
    /// silently overstated. The rate belongs to the money, and that is also how it works in life. A card payment
    /// leaves the bank fund, which is in your currency, so nothing converts. Cash from an exchange office is a
    /// specific pile of foreign notes bought at a specific rate — which is a <i>wallet</i>, and this app already
    /// models a wallet as a fund. Choosing the currency therefore becomes choosing the fund, a choice the user is
    /// already making on every expense, so the form gains no new question.
    /// </para>
    /// </summary>
    public string? Currency { get; private set; }

    /// <summary>
    /// How many units of the <i>account</i> currency one unit of <see cref="Currency"/> is worth (1 GBP = 1.17 EUR
    /// → <c>1.17</c>). Set together with <see cref="Currency"/>; neither means anything without the other.
    /// <para>
    /// Best derived from the transfer that loaded the wallet ("€234 out of Bank → £200 into Lisbon cash" gives
    /// 1.17 exactly) rather than typed: the user holds an exchange receipt, not a rate, and a derived rate is the
    /// true rate for that pile of cash including the office's margin, which a typed mid-market rate never is.
    /// </para>
    /// </summary>
    public decimal? Rate { get; private set; }

    /// <summary>True when this fund holds foreign money and can convert it — i.e. the entry form should label its
    /// Amount in <see cref="Currency"/> and convert what is typed.</summary>
    public bool HasRate => Rate is > 0m && !string.IsNullOrEmpty(Currency);

    /// <summary>Set (or clear, by passing null for either half) the foreign currency this fund holds and its rate.
    /// Clearing does not touch anything already recorded: amounts are stored converted, at entry time, so a rate
    /// change can never rewrite what past expenses cost.</summary>
    public void SetCurrency(string? currency, decimal? rate)
    {
        if (string.IsNullOrWhiteSpace(currency) || rate is not > 0m)
        {
            Currency = null;
            Rate = null;
            return;
        }
        Currency = currency.Trim().ToUpperInvariant();
        Rate = rate;
    }

    /// <summary>Convert an amount typed in this fund's currency into the account currency, rounded to the cent.
    /// Returns it unchanged when the fund holds account-currency money, so callers need no branch.</summary>
    public decimal ToAccountCurrency(decimal amountInFundCurrency) =>
        Rate is { } r && r > 0m
            ? decimal.Round(amountInFundCurrency * r, 2, MidpointRounding.AwayFromZero)
            : amountInFundCurrency;

    public Fund(string name, Guid? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Fund name is required.", nameof(name));
        Name = name.Trim();
        ParentId = parentId;
    }

    public bool IsRoot => ParentId is null;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Fund name is required.", nameof(name));
        Name = name.Trim();
    }

    public void SetNote(string? note) => Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public void SetIcon(string? icon) => Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    public void SetSynced(bool synced) => IsSynced = synced;

    /// <summary>Hide/show this fund in the pickers and main list (its history is kept regardless).</summary>
    public void SetArchived(bool archived) => IsArchived = archived;
}
