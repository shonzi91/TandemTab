namespace FinApp.Contracts;

// --- Path-B thin-client slice: Recurring (docs/MOBILE.md) ----------------------------------------------------
// Bills / income expectations with their due state for the open period, computed server-side.

/// <summary>One recurring expectation. <see cref="Kind"/> is "expense"/"income"; <see cref="Mode"/> is
/// "fixed"/"typical"/"reminder". <see cref="Due"/> means it's due (and unhandled) as of today in the open period;
/// <see cref="Upcoming"/> means within the heads-up window; <see cref="DaysUntilDue"/> is days until its due date this
/// period. <see cref="HasKnownAmount"/> is false for reminder-only items. Category/fund names are resolved.</summary>
public record RecurringRowDto(
    Guid Id,
    string Name,
    string? Icon,
    string Kind,
    string Mode,
    decimal Expected,
    int DayOfMonth,
    Guid CategoryId,
    string CategoryName,
    Guid FundId,
    string FundName,
    bool Active,
    bool Due,
    bool Upcoming,
    int DaysUntilDue,
    bool HasKnownAmount,
    bool AutoPost,
    // R2: set when this bill services a loan — posting it splits into interest/principal rows against that debt.
    Guid? LinkedDebtBucketId = null,
    string? LinkedDebtName = null,
    // Deliberately skipped in the open period (as opposed to posted) — the only state an un-skip may undo.
    bool SkippedThisPeriod = false,
    // Still expected this period: active, started, and neither posted nor skipped. ⚠️ NOT derivable from Due and
    // Upcoming — an item due in three weeks is pending but neither, so a client splitting the list into "coming"
    // and "done" had no way to tell it from one already handled. Trailing and optional; false on an older server,
    // which lands every row in the lower section rather than misfiling any of them (O5).
    bool Pending = false);

/// <summary>A debt bucket a bill can be linked to. <see cref="PaymentDriven"/> mirrors the bucket's "I log each
/// installment here" switch — a linked bill only drives the balance when it's on, which is the user's call, so the
/// client shows a hint rather than flipping it.</summary>
public record DebtOptionDto(Guid Id, string Name, bool PaymentDriven);

/// <summary>The Recurring surface in one read: the known bills still expected this period (<see cref="BillsDue"/>),
/// every recurring item with its due state, and the pickers an editor needs — spend categories (for a bill),
/// contribution categories (for income), funds, and the debts a bill can service.</summary>
public record RecurringViewDto(
    long Version,
    string Currency,
    decimal BillsDue,
    IReadOnlyList<RecurringRowDto> Items,
    IReadOnlyList<CategoryOptionDto> Categories,
    IReadOnlyList<CategoryOptionDto> ContributionCategories,
    IReadOnlyList<FundOptionDto> Funds,
    IReadOnlyList<DebtOptionDto> Debts)
{
    public static readonly RecurringViewDto Empty = new(0, "", 0m, [], [], [], [], []);
}

/// <summary>The delta a recurring mutation returns (superset of <see cref="MutationResultDto"/>): new version, the
/// affected item id, and the refreshed view.</summary>
public record RecurringMutationDto(long Version, Guid? EntityId, RecurringViewDto View);
