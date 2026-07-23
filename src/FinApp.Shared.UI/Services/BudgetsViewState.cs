using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A thin, domain-free view-model for the Budgets surface (Path-B — docs/MOBILE.md). Holds the read-model DTOs
/// (each budgeted category with its coverage) and upserts/removes through the delta-returning budget endpoints.
/// Budgets are an occasional, considered action, so there's no echo — the write reconciles from the delta's
/// refreshed view (coverage is server-computed and can't be echoed precisely anyway). A failed write reloads.
/// </summary>
public sealed class BudgetsViewState(FinAppApiClient api)
{
    private Guid _accountId;
    private long _version;

    public bool IsReady { get; private set; }
    public string Currency { get; private set; } = "";
    public decimal TotalBudgeted { get; private set; }
    public decimal TotalSpent { get; private set; }
    public List<BudgetRowDto> Budgets { get; private set; } = [];
    public IReadOnlyList<CategoryOptionDto> Categories { get; private set; } = [];

    /// <summary>Categories without a budget yet — what the "add a budget" picker offers.</summary>
    public IReadOnlyList<CategoryOptionDto> UnbudgetedCategories =>
        Categories.Where(c => Budgets.All(b => b.CategoryId != c.Id)).ToList();

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId)
    {
        _accountId = accountId;
        Apply(await api.GetBudgetsAsync(accountId));
        IsReady = true;
        Raise();
    }

    private async Task ReloadAsync() => Apply(await api.GetBudgetsAsync(_accountId));

    private void Apply(BudgetsViewDto v)
    {
        _version = v.Version;
        Currency = v.Currency;
        TotalBudgeted = v.TotalBudgeted;
        TotalSpent = v.TotalSpent;
        Budgets = v.Budgets.ToList();
        Categories = v.Categories;
    }

    public async Task SetAsync(Guid categoryId, decimal amount, decimal thresholdPercent, bool notifyEvery)
    {
        try { Apply((await api.SetBudgetDeltaAsync(_accountId, categoryId, new SetBudgetRequest(amount, thresholdPercent, notifyEvery))).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    public async Task RemoveAsync(Guid categoryId)
    {
        try { Apply((await api.RemoveBudgetDeltaAsync(_accountId, categoryId)).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }
}
