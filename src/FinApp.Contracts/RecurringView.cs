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
    string CategoryName,
    string FundName,
    bool Active,
    bool Due,
    bool Upcoming,
    int DaysUntilDue,
    bool HasKnownAmount,
    // R2: set when this bill services a loan — posting it splits into interest/principal rows against that debt.
    Guid? LinkedDebtBucketId = null,
    string? LinkedDebtName = null);

/// <summary>The Recurring surface in one read: the known bills still expected this period (<see cref="BillsDue"/>) and
/// every recurring item with its due state.</summary>
public record RecurringViewDto(
    long Version,
    string Currency,
    decimal BillsDue,
    IReadOnlyList<RecurringRowDto> Items)
{
    public static readonly RecurringViewDto Empty = new(0, "", 0m, []);
}

/// <summary>The delta a recurring mutation returns (superset of <see cref="MutationResultDto"/>): new version, the
/// affected item id, and the refreshed view.</summary>
public record RecurringMutationDto(long Version, Guid? EntityId, RecurringViewDto View);
