namespace FinApp.Contracts;

/// <summary>A contributor on a domain account (unified with the money-side "member").</summary>
public record MemberDto(Guid UserId, string DisplayName);

/// <summary>
/// Lightweight view of a domain account the caller can see (owns or was invited to). The full
/// budgeting graph travels as an <see cref="AccountSnapshot"/>; this is the list/header shape.
/// </summary>
public record AccountSummaryDto(
    Guid Id,
    string Name,
    string Currency,
    Guid OwnerUserId,
    bool IsOwner,
    IReadOnlyList<MemberDto> Members);

public record CreateAccountRequest(string Name, string Currency);
public record RenameAccountRequest(string Name);

/// <summary>Leave an account. When the leaver is the owner and others remain, <see cref="NewOwnerUserId"/> must
/// name the member who takes over. Ignored when the caller is the sole member (the account is archived).</summary>
public record LeaveAccountRequest(Guid? NewOwnerUserId = null);

/// <summary>Hand ownership of an account to another current member.</summary>
public record TransferOwnershipRequest(Guid NewOwnerUserId);

/// <summary>Outcome of a leave: the caller left a shared account, or (as sole member) it was archived.</summary>
public enum LeaveAccountResult { Left, Archived }

/// <summary>An archived (soft-deleted) account the caller can still restore until <see cref="PurgeAt"/>.</summary>
public record ArchivedAccountDto(Guid Id, string Name, string Currency, DateTimeOffset ArchivedAt, DateTimeOffset PurgeAt);

/// <summary>
/// Opaque-friendly full snapshot of a domain account aggregate, exchanged when a contributor loads or
/// saves a shared account. <see cref="Payload"/> is the serialized aggregate; keeping it a single blob
/// lets a later milestone swap it for an end-to-end-encrypted ciphertext without changing the contract.
/// </summary>
public record AccountSnapshot(Guid Id, long Version, string Payload);

/// <summary>Push a locally-edited snapshot back to the server. <see cref="ExpectedVersion"/> enables optimistic concurrency.</summary>
public record SaveAccountRequest(string Payload, long ExpectedVersion);

// --- Command writes (Option-A migration, docs/MOBILE.md) ---------------------------------------------------
// The client used to mutate the aggregate locally and PUT the whole snapshot; these let a thin (native) client send
// just the command — the server applies it through the same domain, so the money maths can't drift between clients.

/// <summary>Seed a freshly-created account's starter body server-side (default categories/funds + the first period),
/// so a thin client doesn't need the domain to initialize an account. <see cref="Today"/> dates the first period to
/// the caller's local month; when null the server uses its own UTC date. Fails (409) if the account is already set up.</summary>
public record BootstrapAccountRequest(DateOnly? Today = null);

/// <summary>
/// Log a new expense in the account's open period, computed server-side. The member is the caller and the fund's
/// "synced" flag is derived from the fund itself, so neither is in the request. Bank-import and on-behalf settlement
/// provenance are handled by their own flows, not here. Mirrors <c>BudgetingState.AddExpense</c>.
/// </summary>
public record AddExpenseRequest(Guid CategoryId, decimal Amount, Guid FundId, DateOnly Date, string? Note = null, bool OnBehalfOfOtherAccount = false, Guid? TagId = null,
    // The trip this expense belongs to. Set at entry (trip mode defaults it) so attaching costs no extra round trip.
    // Note it is NOT on the edit request — see EditExpenseRequest for why.
    Guid? TripId = null,
    // What was typed before conversion, when the fund holds foreign cash — Amount is already the converted figure.
    // Display only: see Expense.ForeignAmount for why it is recorded rather than derived from the wallet's rate.
    decimal? ForeignAmount = null, string? ForeignCurrency = null,
    // The time of day, when the client knows one (it stamps "now" on an expense being logged for today). Null is a
    // real answer and stays null — see Expense.Time for why it is never defaulted to midnight.
    TimeOnly? Time = null);

/// <summary>Replace an existing expense's category/amount/fund/note/date (an append-only edit — see
/// <c>Period.EditExpense</c>). The expense id travels in the route. <see cref="TagId"/> sets the expense's single
/// tag; an <b>omitted</b> tag leaves the stored one alone and clearing is explicit (<see cref="ClearTag"/>).
/// One tag per expense. Mirrors <c>BudgetingState.EditExpense</c>.
/// <para>
/// <b>★ There is deliberately no TripId here.</b> Every other field on this request is authoritative — an omitted
/// value means "no longer set" — and a trip link that behaved the same way would be destroyed by any client that
/// hasn't learned about trips yet: correcting an amount from an older app would silently drop the expense out of
/// its recap, with nothing to notice. <c>Period.EditExpense</c> carries the link across the edit instead, and
/// changing it has its own endpoint (<see cref="SetExpenseTripRequest"/>).
/// </para></summary>
/// <para>
/// <see cref="Time"/> follows the same care as the trip, in a cheaper way: <c>Period.EditExpense</c> carries the
/// stored time across, so an omitted value means "leave it alone" rather than "clear it" — an older client
/// correcting an amount can't silently strip the clock off a row. Clearing is therefore explicit
/// (<see cref="ClearTime"/>), because "I don't actually know when" is a real edit and a null can't say it.
/// </para>
/// <para>
/// <b>⚠️ <see cref="TagId"/> used to break that rule, on the same request, two lines up</b> — an omitted tag
/// <i>cleared</i> the label while an omitted time left the clock alone, and the identical argument applies to both.
/// It cost exactly what the trip paragraph predicts: the native edit omitted the tag, so correcting an amount on
/// the phone silently stripped the label, probably from the day tags shipped. Both now follow the same rule, and
/// clearing a tag is <see cref="ClearTag"/>. <b>A request where two neighbouring fields read the same but mean
/// opposite things is a trap whoever writes the next client falls into.</b>
/// </para>
public record EditExpenseRequest(Guid CategoryId, decimal Amount, Guid FundId, DateOnly Date, string? Note = null, Guid? TagId = null,
    TimeOnly? Time = null, bool ClearTime = false, bool ClearTag = false);

/// <summary>
/// Record income (a deposit) for the caller in the open period, computed server-side. The member is the caller and
/// the fund's "synced" flag is derived from the fund. <see cref="CategoryId"/> is an optional <b>contribution</b>
/// category (Salary, Vouchers…); pass <see cref="System.Guid.Empty"/> for general income. Deposits with the same
/// (member, category, fund) <b>merge</b> into one row. Mirrors <c>BudgetingState.RecordDeposit</c>.
/// </summary>
public record AddDepositRequest(Guid CategoryId, Guid FundId, decimal Amount, DateOnly Date);

/// <summary>Overwrite one of the caller's own deposit rows (amount/category/fund/date). The deposit id travels in the
/// route; a caller may only change their own deposits (else 403). Mirrors <c>BudgetingState.EditDeposit</c>.</summary>
public record EditDepositRequest(Guid CategoryId, Guid FundId, decimal Amount, DateOnly Date);

/// <summary>Set money aside into a savings bucket ("Add to savings"), computed server-side. A plain (un-noted) deposit
/// stays editable/removable; a note turns it into a non-editable annotated allocation (as in the web). Amount must be
/// positive (draw down via the spend endpoint). Mirrors <c>BudgetingState.AllocateSaving</c>.</summary>
public record AddSavingDepositRequest(Guid SavingCategoryId, decimal Amount, DateOnly Date, string? Note = null);

/// <summary>Change the amount of a manual savings deposit (append-only — the row is replaced, keeping its original
/// date/bucket). The allocation id travels in the route. Mirrors <c>BudgetingState.EditSavingDeposit</c>.</summary>
public record EditSavingDepositRequest(decimal Amount);

/// <summary>
/// Spend accumulated savings: records a real expense against <see cref="CategoryId"/> plus a matching negative
/// drawdown from <see cref="SavingCategoryId"/>, so the earmark and the balance both fall. The member is the caller.
/// <see cref="FundId"/> is the fund the money physically leaves; pass <see cref="System.Guid.Empty"/> to let the
/// server pick the first spendable (non-synced) fund, matching the web default. Mirrors <c>BudgetingState.SpendFromSavings</c>.
/// </summary>
public record SpendFromSavingsRequest(Guid SavingCategoryId, Guid CategoryId, decimal Amount, DateOnly Date, Guid FundId = default, string? Note = null);

/// <summary>One planned future cost of a sinking-fund bucket (pure planning data — never moves money). <see cref="Cadence"/>
/// is "one-off"/"monthly"/"quarterly"/"yearly"; <see cref="DueDate"/> applies to a one-off target.</summary>
public record PlannedCostDto(string Label, decimal Amount, string Cadence, DateOnly? DueDate = null);

/// <summary>
/// Create or configure a savings bucket in one shot (mirrors <c>BudgetingState.AddSavingBucket</c>/<c>SaveSavingBucket</c>).
/// The bucket's <b>kind</b> is chosen by the flags, in priority order: <see cref="IsDebt"/> (payoff envelope, using
/// Debt* + re-anchored to the server date) → <see cref="IsInvestment"/> (growth envelope, using Inv*) → otherwise an
/// ordinary goal (<see cref="GoalAmount"/> &gt; 0, with <see cref="ThresholdPercent"/> alert 0–100 and
/// <see cref="NotifyOnMilestone"/>). <see cref="IsExpensesFund"/> turns it into a sinking fund for <see cref="Costs"/>
/// and clears any goal. <see cref="PlannedContribution"/>, <see cref="FundId"/> (earmark tag) and <see cref="InitialAmount"/>
/// (only honoured while the account has a single period) apply regardless of kind.
/// </summary>
public record SaveSavingBucketRequest(
    string Name,
    string? Icon = null,
    decimal? GoalAmount = null,
    decimal ThresholdPercent = 80m,
    bool NotifyOnMilestone = false,
    decimal InitialAmount = 0m,
    bool IsDebt = false,
    decimal DebtBalance = 0m,
    decimal DebtRate = 0m,
    decimal DebtInstallment = 0m,
    // R1 informative debt. DebtOriginalBalance lets the client send the "initial + already-paid principal" input mode
    // (original ≥ current); null/0 keeps the current default (original defaults to the current balance). DebtInstallmentDay
    // (1–31) and DebtStartDate are optional — the latter makes "interest paid so far" exact instead of estimated.
    decimal? DebtOriginalBalance = null,
    int? DebtInstallmentDay = null,
    DateOnly? DebtStartDate = null,
    decimal? PlannedContribution = null,
    bool IsInvestment = false,
    decimal InvRate = 0m,
    decimal InvTermYears = 0m,
    int InvCompounds = 12,
    Guid? FundId = null,
    IReadOnlyList<PlannedCostDto>? Costs = null,
    bool IsExpensesFund = false,
    // R2: drive this debt's balance from logged installments instead of walking its schedule. Applied through
    // SavingCategory.SetPaymentDriven, which snapshots today's balance on the way in or out, so flipping it never
    // moves the figure — it only changes what moves it from here on.
    bool DebtPaymentDriven = false,
    // The account's emergency fund. At most one bucket holds it; saving it here clears it from any other. Its goal
    // amount is then derived (3× essential spending, rounded up to 500) rather than taken from GoalAmount.
    bool IsEmergencyFund = false,
    // A lease's residual / balloon: the sum still owed on the last scheduled payment date. 0 = an ordinary loan.
    decimal DebtResidual = 0m);

/// <summary>One non-loan line riding along on an installment (insurance, tax, a fee), with its own category and
/// optional tag so it lands in the right budget and its own Breakdown slice. Sent as a list — see
/// <see cref="LogInstallmentRequest"/>.</summary>
public record InstallmentExtraDto(decimal Amount, Guid CategoryId, Guid? TagId = null, string? Note = null);

/// <summary>
/// Log a loan installment against a debt bucket as its constituent parts: an interest row, a principal row, and one
/// row per <see cref="Additional"/> line, all sharing an installment-group id so they show, edit and remove as one
/// payment. <see cref="Total"/> is what actually left the account; what remains after the additional lines is split
/// by the loan's own schedule. The member is the caller and the fund's "synced" flag is derived from the fund.
/// Mirrors <c>Period.LogInstallment</c>.
/// </summary>
public record LogInstallmentRequest(
    Guid BucketId,
    decimal Total,
    Guid FundId,
    DateOnly Date,
    Guid PrincipalCategoryId,
    Guid InterestCategoryId,
    IReadOnlyList<InstallmentExtraDto>? Additional = null,
    Guid? PrincipalTagId = null,
    Guid? InterestTagId = null,
    string? Note = null);

/// <summary>Archive (hide) or restore a resource that supports it (e.g. a savings bucket). Reversible; keeps history.</summary>
public record SetArchivedRequest(bool Archived);

/// <summary>Deploy a savings bucket to its goal (e.g. a loan prepayment): money leaves the account from <see cref="FundId"/>,
/// recorded as an external-transfer-out (not consumption, so it doesn't hit the expenses ledger) plus a drawdown that
/// drains the bucket. On a debt bucket it also counts as an extra principal payment. Mirrors <c>BudgetingState.DisburseSaving</c>.</summary>
public record DisburseSavingRequest(Guid SavingCategoryId, Guid FundId, decimal Amount, DateOnly Date, string? Note = null);

/// <summary>Mature saved money into a spendable budget for this period: release the earmark from <see cref="SavingCategoryId"/>
/// and add <see cref="Amount"/> to <see cref="CategoryId"/>'s budget (no money physically moves). Mirrors <c>BudgetingState.ConvertSavingToBudget</c>.</summary>
public record ConvertSavingToBudgetRequest(Guid SavingCategoryId, Guid CategoryId, decimal Amount, DateOnly Date, string? Note = null);

/// <summary>Move earmarked money from one savings bucket to another (net-neutral). Mirrors <c>BudgetingState.MoveSavingToBucket</c>.</summary>
public record MoveSavingsRequest(Guid FromBucketId, Guid ToBucketId, decimal Amount, DateOnly Date, string? Note = null);

// --- Account structure: spend categories, funds, contribution categories ------------------------------------

/// <summary>Add a spend category. <see cref="ParentId"/> nests it under another; <see cref="Essential"/> marks it an
/// essential spend (advisory). Mirrors <c>BudgetingState.AddCategory</c>.</summary>
public record CreateCategoryRequest(string Name, Guid? ParentId = null, string? Icon = null, bool Essential = false);

/// <summary>Edit a spend category's name and icon (a null icon clears it). <see cref="Essential"/> is applied only when
/// provided, so an edit that doesn't carry it leaves the flag untouched. Mirrors <c>BudgetingState.EditCategory</c>.</summary>
public record EditCategoryRequest(string Name, string? Icon = null, bool? Essential = null);

/// <summary>Add a fund (a place money lives). <see cref="ParentId"/> nests it as an informational sub-fund. Mirrors
/// <c>BudgetingState.AddFund</c>.</summary>
public record CreateFundRequest(string Name, Guid? ParentId = null, string? Note = null, string? Icon = null);

/// <summary>Edit a fund's name, note and icon (null note/icon clear them). Mirrors <c>BudgetingState.RenameFund</c>+note/icon.</summary>
public record EditFundRequest(string Name, string? Note = null, string? Icon = null);

/// <summary>
/// Set (or clear, with nulls) the foreign currency a fund holds and the rate it was bought at. Its own endpoint
/// rather than two more fields on <see cref="EditFundRequest"/>: that request is a full replace and every
/// single-field setter in the client re-sends the whole triple, so carrying the rate there would let a rename
/// silently wipe a wallet's rate. Mirrors <c>BudgetingState.SetFundCurrency</c>.
/// </summary>
public record SetFundCurrencyRequest(string? Currency, decimal? Rate);

/// <summary>Add a contribution (income) category (Salary, Vouchers…). Mirrors <c>BudgetingState.AddContributionCategory</c>.</summary>
public record CreateContributionCategoryRequest(string Name, string? Icon = null);

/// <summary>Edit a contribution category's name and icon (null icon clears it). Mirrors <c>BudgetingState.SaveContributionCategory</c>.</summary>
public record EditContributionCategoryRequest(string Name, string? Icon = null);

/// <summary>Add a cross-cutting tag (a flat label attached to expenses, alongside sub-categories). Rejects a duplicate
/// name. Mirrors <c>BudgetingState.AddTag</c>.</summary>
/// <summary><see cref="IsTripTag"/> puts the new label on the trip axis rather than the everyday one — set when the
/// tag is created from a form that is filing against a trip. See <c>Tag.IsTripTag</c>.</summary>
public record CreateTagRequest(string Name, string? Icon = null, bool IsTripTag = false);

/// <summary>Rename a tag, set its icon (a null icon clears it) and bind the category it files into (F2 — a null
/// <see cref="CategoryId"/> clears the binding). A full replace, like every other edit request here: the caller sends
/// the tag's whole intended state, so an omitted field means "no longer set", not "leave alone".
/// Mirrors <c>BudgetingState.SaveTag</c>.</summary>
public record EditTagRequest(string Name, string? Icon = null, Guid? CategoryId = null);

// --- Trips ---------------------------------------------------------------------------------------------------

/// <summary>
/// Create a trip — a named journey expenses can be attached to. Rejects a duplicate name.
/// <para>
/// <see cref="From"/>/<see cref="To"/> are the trip's own dates, both inclusive. They say when the app should
/// default new expenses to this trip and when to count down to it; they do <b>not</b> decide what's in it — an
/// expense belongs to a trip because it carries its id, which is what lets a booking paid months early count.
/// </para>
/// Mirrors <c>BudgetingState.AddTrip</c>.
/// </summary>
public record CreateTripRequest(string Name, DateOnly From, DateOnly To, string? Destination = null, string? Icon = null);

/// <summary>
/// Edit a trip. A full replace, like every other edit request here: the caller sends the whole intended state, so an
/// omitted field means "no longer set", not "leave alone". Moving the dates never detaches expenses.
/// <para>
/// <see cref="SavingCategoryId"/> links the savings bucket funding the trip (null clears it; the bucket must exist
/// in this account). <see cref="Budget"/> is what it's expected to cost. <see cref="SpendCurrency"/> +
/// <see cref="Rate"/> are the one fixed conversion for a trip spent in another currency — both or neither, and the
/// conversion applies at entry time only, never to already-stored amounts.
/// </para>
/// Mirrors <c>BudgetingState.SaveTrip</c>.
/// </summary>
public record EditTripRequest(string Name, DateOnly From, DateOnly To, string? Destination = null, string? Icon = null,
    Guid? SavingCategoryId = null, decimal? Budget = null, string? SpendCurrency = null, decimal? Rate = null,
    // The single category this trip's expenses file into; null means file per trip label, the original behaviour.
    // A full-replace request like the rest of this record, so an older client that omits it CLEARS the setting —
    // which is the safe direction: it degrades to per-label filing rather than silently filing to a category the
    // client can't show.
    Guid? CategoryId = null);

/// <summary>Attach an expense to a trip, or detach it with a null <see cref="TripId"/>. Separate from the expense's
/// own edit so that changing what a trip contains never has to re-post its amount, category or date.</summary>
public record SetExpenseTripRequest(Guid? TripId);

/// <summary>Set (or clear) one expense's tag without touching anything else about it. Its own endpoint for the same
/// reason as <see cref="SetExpenseTripRequest"/>: labelling a trip's bookings means reaching into periods that are
/// long closed, and the full expense edit refuses those — rightly, since it would let a closed month's money change.
/// A label moves no money. Mirrors <c>BudgetingState.SetExpenseTag</c>.</summary>
public record SetExpenseTagRequest(Guid? TagId);

/// <summary>Declare a trip over (or put it back on the road). Its own endpoint rather than a field on
/// <see cref="EditTripRequest"/>: the edit form is a full replace, so carrying this there would silently reopen a
/// finished trip every time someone corrected its name. Mirrors <c>BudgetingState.FinishTrip</c>.</summary>
public record FinishTripRequest(bool Finished);

/// <summary>Confirm a trip has actually begun (or take that back). Trip mode is opt-in on the day — see
/// <c>Trip.StartedOn</c> for why a date isn't a departure. Mirrors <c>BudgetingState.StartTrip</c>.</summary>
public record StartTripRequest(bool Started);

/// <summary>
/// Release money the user saved for this trip into the trip's own budget: drops the earmark on the trip's linked
/// savings bucket and adds <see cref="Amount"/> to the trip's category budget for the open period. <b>No money
/// physically moves</b> — see <c>Period.ConvertSavingToBudget</c>. Mirrors <c>BudgetingState.UseTripSavings</c>.
/// </summary>
public record UseTripSavingsRequest(decimal Amount, DateOnly Date, string? Note = null);

/// <summary>
/// Create the trip label set (Stay, Travel, Food &amp; drink…) if it doesn't exist yet. The client sends them because
/// only the client knows the user's language; the server seeds once and ignores every later call, so two languages
/// can't mint two parallel sets. Each label may name the category it files into, so picking it on the expense form
/// also files the expense.
/// </summary>
public record SeedTripTagsRequest(IReadOnlyList<TripTagSeed> Tags);

/// <summary>One trip label to seed — its display name, its emoji, and the category it files into (optional).</summary>
public record TripTagSeed(string Name, string? Icon = null, Guid? CategoryId = null);

// --- Recurring items (bills / income expectations) ----------------------------------------------------------

/// <summary>
/// Add a recurring expectation (a bill or regular income). <see cref="Kind"/> is "expense"/"income";
/// <see cref="Mode"/> is "fixed" (same every time), "typical" (a self-tuning estimate) or "reminder" (no amount,
/// just prompts). <see cref="CategoryId"/> is a spend category for an expense, a contribution category for income;
/// <see cref="DayOfMonth"/> is 1–28. <see cref="AutoPost"/> only applies to a fixed amount. The item is stamped with
/// the server date so it can't fall due before it existed. Mirrors <c>BudgetingState.AddRecurring</c>.
/// </summary>
public record AddRecurringRequest(string Name, string Kind, string Mode, decimal Expected, int DayOfMonth,
    Guid CategoryId, Guid FundId, string? Icon = null, bool AutoPost = false,
    // R2: link an expense item to a debt bucket so posting it logs a split installment rather than a lump expense.
    Guid? LinkedDebtBucketId = null);

/// <summary>Edit a recurring item (its kind can't change). Fields as in <see cref="AddRecurringRequest"/>. Mirrors
/// <c>BudgetingState.UpdateRecurring</c>.</summary>
public record UpdateRecurringRequest(string Name, string Mode, decimal Expected, int DayOfMonth,
    Guid CategoryId, Guid FundId, string? Icon = null, bool AutoPost = false,
    Guid? LinkedDebtBucketId = null);

/// <summary>Pause or resume a recurring item (a paused item never falls due).</summary>
public record SetActiveRequest(bool Active);

/// <summary>Confirm a due recurring item with the real amount: posts a normal expense/income, nudges a "typical"
/// estimate toward the actual, and marks it handled for this period. A zero amount skips (marks handled, posts
/// nothing). Mirrors <c>BudgetingState.ConfirmRecurring</c>.</summary>
public record ConfirmRecurringRequest(decimal ActualAmount);

// --- Settlement / cross-account (money or an expense moved between two of the caller's accounts) ---------------

/// <summary>Send money from a fund on this account to another account the caller also belongs to (same currency): an
/// outflow (external transfer) here and a matching deposit there, in one atomic two-account save. Capped at the source
/// fund's balance. <see cref="DestinationFundId"/> empty picks the destination's first unsynced fund; <see cref="Date"/>
/// defaults to the server date. Mirrors <c>BudgetingState.TransferToAccount</c>.</summary>
public record TransferToAccountRequest(Guid DestinationAccountId, Guid FromFundId, decimal Amount,
    Guid DestinationFundId = default, string? Note = null, DateOnly? Date = null);

/// <summary>Change an account-to-account transfer — <b>both halves at once</b>: the outflow here and the deposit it
/// created in <see cref="DestinationAccountId"/>. Addressed by the pair id both rows carry, so a transfer recorded
/// before that link existed can't be edited (it has no findable counterpart); the UI offers those the old one-sided
/// delete instead. Empty <see cref="FromFundId"/>/<see cref="DestinationFundId"/> and a null <see cref="Date"/> keep
/// what the transfer already has. Mirrors <c>BudgetingState.EditAccountTransfer</c>.</summary>
public record EditAccountTransferRequest(Guid DestinationAccountId, decimal Amount,
    Guid FromFundId = default, Guid DestinationFundId = default, string? Note = null, DateOnly? Date = null);

/// <summary>Settle (or re-settle) part of an "on behalf of another account" expense onto another account: the amount
/// becomes that account's own expense (in the picked fund + category) and this expense is reduced by it, the two linked
/// by a settlement id so either side's edits keep the other in step. Capped at the expense's original amount. Empty
/// destination fund/category resolve to the destination's defaults. Mirrors <c>BudgetingState.SettleExpenseToAccount</c>.</summary>
public record SettleExpenseRequest(Guid DestinationAccountId, Guid DestinationFundId, Guid DestinationCategoryId,
    decimal Amount, string? Note = null);

/// <summary>Record money coming back on an expense — a refund, or someone paying their share of a bill you covered.
/// The expense shrinks by this amount and nothing is booked as income; see <c>Expense.RefundedAmount</c> for why
/// treating it as income would report two wrong figures that happen to net out.
/// <para><b>★ <see cref="Amount"/> is what came back NOW, not the running total.</b> The server adds it to whatever
/// has already come back, inside the same lock that writes it — so two clients acking two credits against one
/// expense both land, instead of the second overwriting the first with a total it computed from a stale read.</para>
/// <para>⚠️ Refunding mints a <b>new expense id</b> (the ledger is append-only). It comes back as the response's
/// <c>Id</c>; the undo, <c>DELETE …/refund</c>, is addressed by that new id.</para></summary>
public record RefundExpenseRequest(decimal Amount);

// --- Statement import (reviewed rows -> real expenses & income in one save) -----------------------------------

/// <summary>One reviewed statement row to import. A <b>negative</b> <see cref="Amount"/> posts an expense (its
/// absolute value) with <see cref="CategoryId"/> read as a spend category; a <b>positive</b> one posts income with
/// <see cref="CategoryId"/> read as a contribution category. Both attribute to <see cref="FundId"/>. Rows with a zero
/// amount or an empty category/fund are skipped. Mirrors a row of <c>BudgetingState.ImportTransactions</c>.</summary>
public record ImportRowDto(decimal Amount, DateOnly Date, Guid CategoryId, Guid FundId, string? Note = null);

/// <summary>Import a batch of reviewed statement rows in one save (all-or-nothing — a row that names a missing
/// category/fund fails the whole batch with 400). When <see cref="SkipDuplicates"/> is true (the default), a row that
/// matches an entry <b>already</b> in the target period (same date + amount + fund) is skipped, so re-importing the
/// same statement is safe; duplicates <i>within</i> one batch still post (they're compared against pre-existing data
/// only). Mirrors <c>BudgetingState.ImportTransactions</c> + the web's <c>ImportLooksDuplicate</c> hint.</summary>
public record ImportTransactionsRequest(IReadOnlyList<ImportRowDto> Rows, bool SkipDuplicates = true);

/// <summary>Result of a statement import: the new snapshot <see cref="Version"/>, how many rows posted
/// (<see cref="Imported"/>), how many were skipped as zero/empty (<see cref="Skipped"/>), and how many were skipped
/// as <see cref="Duplicates"/> of existing entries.</summary>
public record ImportResultDto(long Version, int Imported, int Skipped, int Duplicates = 0);

// --- Reallocation (move a budget's spare toward savings or another budget) ------------------------------------

/// <summary>The web's one-step "Move it to the loan" nudge: trim a category's budget to <see cref="NewBudget"/> (an
/// absolute new allocation, with its alert <see cref="ThresholdPercent"/> 0–100 and <see cref="NotifyEvery"/>) and set
/// <see cref="Amount"/> aside toward the <see cref="SavingCategoryId"/> bucket — one save, so the spare disappears and
/// the earmark grows together. Advisory: saving beyond free cash is allowed. <see cref="Date"/> defaults to the server
/// date. Mirrors <c>BudgetingState.ReallocateBudgetToSaving</c>.</summary>
public record ReallocateToSavingsRequest(Guid CategoryId, decimal NewBudget, decimal ThresholdPercent, bool NotifyEvery,
    Guid SavingCategoryId, decimal Amount, DateOnly? Date = null);

/// <summary>Move the unspent part of one budget into another (both in the open period). Capped at the source budget's
/// leftover (allocated − spent) so it can't be cut below what's already been spent; the amount must be positive and the
/// categories differ. Exposes the domain <c>BudgetReallocationService.ToBudget</c> (no web UI yet).</summary>
public record ReallocateToBudgetRequest(Guid FromCategoryId, Guid ToCategoryId, decimal Amount);

// --- Fund transfers + opening balances (intra-account money placement) ----------------------------------------

/// <summary>Set a fund's opening balance for the open period (what it held at the period's start). Overwrites any
/// existing opening for that fund. Mirrors <c>BudgetingState.SetFundOpeningBalance</c>.</summary>
public record SetFundOpeningBalanceRequest(decimal Amount);

/// <summary>Move money between two funds within the account. Intra-account moves are total-preserving (only where the
/// money sits changes), so the source may go negative — no balance cap. The funds must differ and the amount be
/// positive. <see cref="Date"/> defaults to the server date. Synced sides are recorded but not moved (the real bank
/// balance is authoritative). Mirrors <c>BudgetingState.TransferFunds</c>.</summary>
public record TransferFundsRequest(Guid FromFundId, Guid ToFundId, decimal Amount, DateOnly? Date = null, string? Note = null);

/// <summary>Edit a fund transfer (its original date is preserved). Fields as in <see cref="TransferFundsRequest"/>,
/// minus the date. Bank provenance is kept but the auto-filed badge is cleared. Mirrors
/// <c>BudgetingState.EditFundTransfer</c>.</summary>
public record EditFundTransferRequest(Guid FromFundId, Guid ToFundId, decimal Amount, string? Note = null);

// --- Budgets (per-category spending plans within a period) ----------------------------------------------------

/// <summary>Create or update the budget for a category in the open period (idempotent — no separate add vs. edit).
/// <see cref="Amount"/> is the planned allocation; <see cref="ThresholdPercent"/> (0–100, default 80) is the spend
/// level that fires an alert; <see cref="NotifyEvery"/> pings on every expense. Budgets are advisory and never
/// capped (only savings reserves real cash). Mirrors <c>BudgetingState.SaveBudget</c>.</summary>
public record SetBudgetRequest(decimal Amount, decimal ThresholdPercent = 80m, bool NotifyEvery = false);

// --- Period lifecycle (roll into the next period / reschedule / undo the last) --------------------------------

/// <summary>
/// Roll into the next period: closes the current one and opens the next (its <c>From</c> = the day after the current
/// <c>To</c>, running one calendar month). Only allowed once the current period has actually ended (<c>To</c> before
/// <see cref="Today"/>) — this blocks farming future periods. <see cref="CopyBudgets"/> carries the current budgets
/// forward; <see cref="AdjustBudgets"/> (only with <see cref="CopyBudgets"/>) nudges each toward what was spent.
/// <see cref="FundOpenings"/> is each top-level fund's real current balance, which becomes the new period's opening
/// balance (funds omitted open at zero). <see cref="SyncedFundClosingBalance"/> is the live bank balance captured for
/// a <b>synced</b> fund — stored as an informative-only opening (never entered by hand; the server can't read it, so
/// the caller supplies it or it's skipped). <see cref="Today"/> is the caller's local date (server UTC when omitted).
/// Mirrors <c>BudgetingState.StartNextPeriod</c>.
/// </summary>
public record StartNextPeriodRequest(
    bool CopyBudgets = false,
    bool AdjustBudgets = false,
    IReadOnlyDictionary<Guid, decimal>? FundOpenings = null,
    decimal? SyncedFundClosingBalance = null,
    DateOnly? Today = null);

/// <summary>Reschedule a period's date range; every later period shifts to stay contiguous, each keeping its length.
/// The target period is identified positionally (oldest = 0), matching the web's period navigation. Mirrors
/// <c>BudgetingState.ReschedulePeriod</c>.</summary>
public record ReschedulePeriodRequest(DateOnly From, DateOnly To);

/// <summary>Result of a command write: the account's new snapshot <see cref="Version"/> (so the caller keeps optimistic
/// concurrency in step) and the affected entity's id — the newly-created id on an add, the target id echoed otherwise.</summary>
public record MutationResultDto(long Version, Guid? EntityId);

/// <summary>
/// The Home balance-header figures, computed <b>server-side</b> from the account snapshot — the first read
/// moved off the client under the Option-A migration (docs/MOBILE.md). Amounts are plain decimals in
/// <see cref="Currency"/>. <see cref="Saved"/> = current − free (savings earmarked, this period + prior);
/// <see cref="SafeAfterBills"/> = free − known recurring bills still due this period, and may be negative.
/// </summary>
public record AccountOverviewDto(
    string Currency,
    decimal Current,
    decimal Free,
    decimal Saved,
    decimal Spent,
    decimal Contributed,
    decimal BillsDue,
    decimal SafeAfterBills,
    // The Home hero's remaining three tiles + the savings rate — added for native parity (R2). A thin client
    // cannot derive these: they come from the domain the web client still carries and the native one does not.
    // MoneyIn − Contributed is the carried-over half; Spent + TransfersOut is the hero's "all money out".
    decimal MoneyIn = 0m,
    decimal TransfersOut = 0m,
    decimal SavedThisPeriod = 0m,
    decimal? SavedRate = null)
{
    public static readonly AccountOverviewDto Empty = new("", 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>
/// The cash-runway line on Home, computed server-side (Option-A migration). Returned as <c>null</c> when there's
/// no trustworthy basis to project from (the UI shows no runway). <see cref="Months"/> is the horizon actually
/// projected; <see cref="FirstShortfallMonth"/> is the first month the balance ends below zero, or null if none
/// does. <see cref="BasedOnRecurring"/> distinguishes the young-account fallback (declared recurring bills) from
/// the normal basis (an average of the last <see cref="CompletedPeriodCount"/> closed months).
/// <para>
/// <see cref="OpeningBalance"/>, <see cref="FromMonth"/> and <see cref="MonthlyCommitted"/> are the remaining inputs
/// <c>FinApp.Forecasting.CashFlowForecast.Project</c> needs, so a thin client can reconstruct the whole month-by-month
/// series <b>and</b> re-run the "what if I spent €X less?" slider entirely client-side (basis = <c>Recurring</c> when
/// <see cref="BasedOnRecurring"/>, else <c>Demonstrated</c>) — no per-tick server round-trip.
/// </para>
/// </summary>
public record RunwayDto(
    string Currency,
    int Months,
    DateOnly? FirstShortfallMonth,
    decimal MonthlyIncome,
    decimal MonthlySpending,
    bool BasedOnRecurring,
    int CompletedPeriodCount,
    bool HasUnknownAmounts,
    decimal OpeningBalance,
    DateOnly FromMonth,
    decimal MonthlyCommitted);

/// <summary>
/// The Home "on track for" targets, computed server-side (Option-A migration). Empty when there's nothing to project
/// (the client hides the card). Each target is a debt-free date or a savings goal: <see cref="TargetDto.Kind"/> is
/// "debt-free" or "goal". For a goal, <see cref="TargetDto.Name"/> is the bucket name and <see cref="TargetDto.Icon"/>
/// its stored icon; for debt-free both are empty/null (the client supplies the localized label + flag icon). A
/// reached goal has <see cref="TargetDto.Reached"/> true and <see cref="TargetDto.Months"/> 0.
/// </summary>
public record TargetsDto(IReadOnlyList<TargetDto> Targets)
{
    public static readonly TargetsDto Empty = new([]);
}

public record TargetDto(string Kind, string Name, string? Icon, int Months, bool Reached);

/// <summary>
/// The Home milestone tallies, computed server-side (Option-A migration): how many are <see cref="Earned"/>, the
/// <see cref="Total"/> in the catalogue, and how many are <see cref="InProgress"/> (locked but above 0% — the set the
/// Home "Milestones in progress" strip draws). The full localized catalogue stays a client concern.
/// </summary>
public record MilestonesDto(int Earned, int Total, int InProgress)
{
    public static readonly MilestonesDto Empty = new(0, 0, 0);
}

/// <summary>
/// The Insights health read for the account's latest period, computed server-side (Option-A migration). Carries the
/// <b>structural</b> figures — the gauge <see cref="Score"/> (0–100) and <see cref="Band"/> ("at-risk"/"average"/
/// "healthy"), the savings rate vs target + shortfall, the outgoings trend, and the per-category breakdown — plus the
/// <b>narrative</b> as language-independent <see cref="InsightMessageDto"/>s (code + args): the verdict, the summary
/// fragments, the signal cards, the savings critique, the quick wins, the trend note and the mini-trends. Each client
/// owns the per-language templates and formats the args locally, so no language is baked into the payload.
/// <see cref="Summary"/> and <see cref="SavingsCritique"/> are ordered fragments a client joins with a space.
/// <see cref="HasData"/> is false (and everything zeroed) when the period has nothing to score yet.
/// </summary>
public record InsightsDto(
    bool HasData,
    int Score,
    int? ScoreDelta,
    string Band,
    decimal? SavingsRate,
    decimal SavingsTarget,
    decimal? SavingsShortfall,
    bool TrendUp,
    decimal TrendAverage,
    decimal TrendAvgFraction,
    InsightMessageDto Verdict,
    IReadOnlyList<InsightMessageDto> Summary,
    IReadOnlyList<InsightMessageDto> SavingsCritique,
    InsightMessageDto TrendNote,
    IReadOnlyList<InsightSignalDto> Signals,
    IReadOnlyList<InsightCategoryDto> Breakdown,
    IReadOnlyList<InsightTrendPointDto> Trend,
    IReadOnlyList<InsightMiniTrendDto> MiniTrends,
    IReadOnlyList<InsightMessageDto> QuickWins)
{
    public static readonly InsightMessageDto EmptyVerdict = new("verdict.average", []);
    public static readonly InsightMessageDto EmptyTrendNote = new("trend.none", []);
    public static readonly InsightsDto Empty = new(
        false, 0, null, "average", null, 0.20m, null, false, 0m, 0m,
        EmptyVerdict, [], [], EmptyTrendNote, [], [], [], [], []);
}

/// <summary>One row of the Insights spending breakdown. <see cref="Icon"/> is the category's raw stored icon (the
/// client resolves it to a display icon); <see cref="Dir"/> is "up"/"down"/"flat" vs the prior period.</summary>
public record InsightCategoryDto(string Name, string? Icon, decimal Amount, decimal BarFraction, string Dir);

/// <summary>One bar of the Insights outgoings trend (one recent period).</summary>
public record InsightTrendPointDto(string Label, decimal Outgoings, decimal BarFraction, bool IsCurrent);

/// <summary>One fill value for an <see cref="InsightMessageDto"/> template. <see cref="Kind"/> is "text"/"money"/
/// "percent"/"int": text = verbatim <see cref="Text"/>; money = <see cref="Number"/> as a currency amount (account
/// currency); percent = <see cref="Number"/> as a 0..1 ratio rendered as a whole percent; int = <see cref="Number"/>
/// as an already-rounded whole number.</summary>
public record InsightArgDto(string Kind, decimal Number, string? Text);

/// <summary>A language-independent narrative fragment: a stable <see cref="Code"/> naming the template and the
/// <see cref="Args"/> that fill it. The client maps the code to a localized template and formats the args.</summary>
public record InsightMessageDto(string Code, IReadOnlyList<InsightArgDto> Args);

/// <summary>An Insights signal card. <see cref="Kind"/> is "warn"/"good"/"info"; <see cref="Dir"/> is "up"/"down"/"flat".
/// <see cref="Title"/>, <see cref="Desc"/> and the <see cref="Delta"/> badge are localizable messages.</summary>
public record InsightSignalDto(string Kind, InsightMessageDto Title, InsightMessageDto Desc, InsightMessageDto Delta, string Dir);

/// <summary>One "trends over time" mini-trend (#9). <see cref="Icon"/> is the raw icon (emoji or a category's stored icon,
/// which the client resolves); <see cref="Points"/> is the chronological sparkline series; <see cref="Dir"/> is
/// "up"/"down"/"flat" pre-framed as sentiment. <see cref="Label"/>, <see cref="CurrentText"/> and <see cref="DeltaNote"/>
/// are localizable messages.</summary>
public record InsightMiniTrendDto(InsightMessageDto Label, string? Icon, IReadOnlyList<decimal> Points, InsightMessageDto CurrentText, InsightMessageDto DeltaNote, string Dir);
