namespace FinApp.Contracts;

// --- Path-B thin-client slice (docs/MOBILE.md) --------------------------------------------------------------
// A vertical slice proving a *thin* client for the Spending surface: the client holds these read-model DTOs and
// renders them directly — no on-device domain, no snapshot to deserialize. Mutations return a delta (below) so the
// client reconciles its cache from a small response instead of re-fetching the ~260KB snapshot. Display fields
// (category name/icon, fund name) are resolved server-side so a thin client needs no lookups of its own.

/// <summary>One spending row with its display fields resolved server-side. <see cref="CategoryIcon"/> is the
/// category's raw stored icon (the client applies its own name-based fallback for display); the settlement/savings
/// flags let the row badge itself without the client knowing the money model.</summary>
public record ExpenseDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string? CategoryIcon,
    Guid FundId,
    string FundName,
    decimal Amount,
    DateOnly Date,
    string? Note,
    bool AutoFiled,
    bool FromSavings,
    bool OnBehalfOfOtherAccount,
    bool IsSettlementSource,
    bool IsSettlementDestination,
    // R2 installment split: rows sharing an InstallmentGroupId are one logged loan payment. Part is
    // "principal"/"interest"/"additional"; DebtBucketId names the loan. All null on an ordinary expense.
    Guid? InstallmentGroupId = null,
    string? InstallmentPart = null,
    Guid? DebtBucketId = null,
    // The journey this expense points at, if any — a March flight can belong to a June trip, so this is a link,
    // never a date test. Null on ordinary spending.
    Guid? TripId = null,
    // The labels on it. A list because the field has always been one (see Expense.TagIds), though the UI has
    // settled on at most one; a client that assumes a scalar here would break the day that changes back.
    IReadOnlyList<Guid>? TagIds = null,
    // The time of day, when anything recorded one. Null on most rows — a bank that reports a booking date only, or
    // an entry typed days later — and null must stay null on the client too, not become midnight.
    TimeOnly? Time = null,
    // ★ Who the settlement is with, and for how much. The two booleans above are enough to *mark* a row, but not
    // enough to act on it or to label it the way the thick client does ("🤝 €40 → Household"). More to the point,
    // `DELETE /expenses/{id}/settle` is addressed by the destination account id — so without SettledToAccountId
    // here, the undo was unreachable by construction from any thin client. The route's own comment assumed "the
    // caller holds it as the expense's SettledToAccountId", which was true of the thick client only.
    // Null/zero on an ordinary expense; the source side fills To + Amount, the destination side fills From.
    Guid? SettledToAccountId = null,
    Guid? SettledFromAccountId = null,
    decimal SettledAmount = 0m,
    // ★ How much has come back on this expense — a refund, or a friend's share of a split bill paid back into the
    // wallet it was paid from. Amount above is ALREADY the reduced figure, so a client that ignores this still
    // totals correctly; what it cannot do without it is explain why the row says €40 when the receipt said €60.
    // Sent for the same reason SettledAmount is: the S108 lesson is that a row you can only *mark* is a row whose
    // undo is unreachable, and undoing a refund needs to know there is one.
    decimal RefundedAmount = 0m);

/// <summary>A spend category as a picker option — id, label, stored icon, and parent for indentation. No money.</summary>
public record CategoryOptionDto(Guid Id, string Name, string? Icon, Guid? ParentId);

/// <summary>A fund as a picker option. <see cref="Synced"/> funds are bank-driven and excluded from manual pickers.
/// <para><see cref="Currency"/>/<see cref="Rate"/> carry the foreign cash a wallet holds and what one unit of it is
/// worth in the <b>account's</b> currency. Both null on an ordinary wallet. ⚠️ They mean nothing apart — check for
/// both, as <c>Fund.HasRate</c> does — and they are on the <b>option</b>, not only the wallets row, because picking
/// the wallet is what changes the meaning of the Amount field on the entry form. A picker that cannot see them
/// cannot label the field or convert what is typed.</para></summary>
public record FundOptionDto(Guid Id, string Name, bool Synced, string? Currency = null, decimal? Rate = null);

/// <summary>A tag as a picker option — the flat, cross-cutting label axis that sits alongside categories.
/// <see cref="CategoryId"/> is the category picking it files the expense into (F2), and <see cref="TripTag"/> marks
/// the seeded trip label set, which the trip entry form offers and the everyday one does not.</summary>
public record TagOptionDto(Guid Id, string Name, string? Icon, Guid? CategoryId, bool TripTag);

/// <summary>The whole Spending surface in one read — the thin client's initial load: the current period's expenses,
/// the balance-header figures, and the category/fund options its pickers need. <see cref="Version"/> is the account
/// snapshot version, carried so the client can tell a delta apart from a stale response.</summary>
public record SpendingViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    IReadOnlyList<ExpenseDto> Expenses,
    IReadOnlyList<CategoryOptionDto> Categories,
    IReadOnlyList<FundOptionDto> Funds,
    // Active tags, so a thin client can label an expense and read the labels back. Trailing + optional: an older
    // client deserializes this response unchanged.
    IReadOnlyList<TagOptionDto>? Tags = null)
{
    public static readonly SpendingViewDto Empty = new(0, "", AccountOverviewDto.Empty, [], [], []);

    /// <summary>Tags as a list, never null — an account with no tags is an empty picker, not a missing one.</summary>
    public IReadOnlyList<TagOptionDto> TagOptions => Tags ?? [];
}

/// <summary>The delta an expense mutation returns: the new snapshot <see cref="Version"/>, the affected row's id, the
/// authoritative <see cref="Expense"/> (null on a delete), and the recomputed <see cref="Overview"/>. Enough for the
/// client to reconcile its cache with no re-fetch. A structural superset of <see cref="MutationResultDto"/> (same
/// <c>Version</c>/<c>EntityId</c> lead), so a caller that only wants those two deserializes it unchanged.</summary>
public record ExpenseMutationDto(long Version, Guid? EntityId, ExpenseDto? Expense, AccountOverviewDto Overview);

/// <summary>The delta a logged-installment mutation returns: the shared <see cref="GroupId"/>, every row it posted
/// (principal, interest and any additional lines — so the client can splice them all into its ledger from one
/// response), and the recomputed bank-adjusted <see cref="Overview"/>. <see cref="Rows"/> is empty on a removal.</summary>
public record InstallmentMutationDto(long Version, Guid GroupId, IReadOnlyList<ExpenseDto> Rows, AccountOverviewDto Overview);

/// <summary>The delta an income (deposit) mutation returns: new <see cref="Version"/>, the deposit row's id, and the
/// recomputed bank-adjusted <see cref="Overview"/> (deposits move Contributed/Current/Free, not the expense list).
/// A superset of <see cref="MutationResultDto"/>. Lets a thin Home/Spending reflect income without a re-fetch.</summary>
public record DepositMutationDto(long Version, Guid? EntityId, AccountOverviewDto Overview);
