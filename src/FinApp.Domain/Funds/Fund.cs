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
