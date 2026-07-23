using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A <b>thin</b>, domain-free view-model for the Spending surface (the Path-B slice — docs/MOBILE.md). It holds a
/// cache of read-model DTOs and mutates through the delta-returning expense endpoints. There is <b>no</b>
/// <c>FinApp.Domain</c> reference here and no snapshot to deserialize — the server owns the money model.
/// <para>
/// Writes feel instant via <b>echo-optimism</b>: the row the user just entered is appended to the cached list
/// immediately (a display-only echo — the category/fund labels come from a dictionary lookup on the cached picker
/// options, not from any business logic), and the one unambiguous headline figure (<c>Spent</c>, which every expense
/// adds to regardless of fund) is nudged. Everything balance-dependent (<c>Current</c>/<c>Free</c>/…) is left to the
/// server delta, which lands ~one round-trip later and reconciles the whole overview + the row's canonical id.
/// A failed write reloads the surface, rolling the echo back to server truth.
/// </para>
/// </summary>
public sealed class SpendingViewState(FinAppApiClient api)
{
    private Guid _accountId;
    private long _version;

    public bool IsReady { get; private set; }
    public string Currency { get; private set; } = "";
    public AccountOverviewDto Overview { get; private set; } = AccountOverviewDto.Empty;
    public List<ExpenseDto> Expenses { get; private set; } = [];
    public IReadOnlyList<CategoryOptionDto> Categories { get; private set; } = [];
    public IReadOnlyList<FundOptionDto> Funds { get; private set; } = [];

    /// <summary>Funds a manual expense may target — synced (bank-driven) funds are excluded, matching the thick UI.</summary>
    public IReadOnlyList<FundOptionDto> SelectableFunds => Funds.Where(f => !f.Synced).ToList();

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId)
    {
        _accountId = accountId;
        Apply(await api.GetSpendingAsync(accountId));
        IsReady = true;
        Raise();
    }

    private async Task ReloadAsync() => Apply(await api.GetSpendingAsync(_accountId));

    private void Apply(SpendingViewDto v)
    {
        _version = v.Version;
        Currency = v.Currency;
        Overview = v.Overview;
        Expenses = v.Expenses.ToList();
        Categories = v.Categories;
        Funds = v.Funds;
    }

    /// <summary>Add an expense: echo the row instantly, then send and reconcile from the delta.</summary>
    public async Task AddAsync(Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date)
    {
        var tempId = Guid.NewGuid();
        Expenses.Insert(0, Echo(tempId, categoryId, fundId, amount, note, date));
        Overview = BumpSpent(Overview, +amount);
        Raise();
        try
        {
            var delta = await api.AddExpenseDeltaAsync(_accountId, new AddExpenseRequest(categoryId, amount, fundId, date, note));
            ReplaceTemp(tempId, delta);
        }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    /// <summary>Edit an expense (append-only server-side → a new id): echo the change, then reconcile.</summary>
    public async Task EditAsync(Guid expenseId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date)
    {
        var i = Expenses.FindIndex(e => e.Id == expenseId);
        var oldAmount = i >= 0 ? Expenses[i].Amount : 0m;
        var tempId = Guid.NewGuid();
        if (i >= 0) Expenses[i] = Echo(tempId, categoryId, fundId, amount, note, date);
        Overview = BumpSpent(Overview, amount - oldAmount);
        Raise();
        try
        {
            var delta = await api.EditExpenseDeltaAsync(_accountId, expenseId, new EditExpenseRequest(categoryId, amount, fundId, date, note));
            ReplaceTemp(tempId, delta);
        }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    /// <summary>Remove an expense: drop the row instantly, then reconcile the totals from the delta.</summary>
    public async Task RemoveAsync(Guid expenseId)
    {
        var i = Expenses.FindIndex(e => e.Id == expenseId);
        var amount = i >= 0 ? Expenses[i].Amount : 0m;
        if (i >= 0) Expenses.RemoveAt(i);
        Overview = BumpSpent(Overview, -amount);
        Raise();
        try
        {
            var delta = await api.RemoveExpenseDeltaAsync(_accountId, expenseId);
            _version = delta.Version;
            Overview = delta.Overview;   // authoritative totals
            Raise();
        }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    // Build a display-only echo of the row the user just entered. Labels are a dictionary lookup on the cached picker
    // options — no business logic. Flags default off (a fresh manual expense is none of them).
    private ExpenseDto Echo(Guid id, Guid categoryId, Guid fundId, decimal amount, string? note, DateOnly date)
    {
        var cat = Categories.FirstOrDefault(c => c.Id == categoryId);
        var fund = Funds.FirstOrDefault(f => f.Id == fundId);
        return new ExpenseDto(id, categoryId, cat?.Name ?? "…", cat?.Icon, fundId, fund?.Name ?? "…",
            amount, date, string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            AutoFiled: false, FromSavings: false, OnBehalfOfOtherAccount: false,
            IsSettlementSource: false, IsSettlementDestination: false);
    }

    // Swap the optimistic temp row for the server's canonical one (real id, resolved fields) and adopt the
    // authoritative overview + version. The temp may have been re-sorted away, so match by id.
    private void ReplaceTemp(Guid tempId, ExpenseMutationDto delta)
    {
        _version = delta.Version;
        Overview = delta.Overview;
        var i = Expenses.FindIndex(e => e.Id == tempId);
        if (delta.Expense is { } row)
        {
            if (i >= 0) Expenses[i] = row; else Expenses.Insert(0, row);
            // Keep the list ordered the way the server returns it (newest date first).
            Expenses = Expenses.OrderByDescending(e => e.Date).ToList();
        }
        else if (i >= 0) Expenses.RemoveAt(i);
        Raise();
    }

    // Spent is the one figure every expense moves by its full amount regardless of fund, so it's safe to echo; the
    // balance figures depend on synced-ness (the money model) and are left for the server delta to reconcile.
    private static AccountOverviewDto BumpSpent(AccountOverviewDto o, decimal delta) => o with { Spent = o.Spent + delta };
}
