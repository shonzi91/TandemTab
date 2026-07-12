using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Application state the Blazor UI binds to, now backed by the sync server. Holds the signed-in user's
/// account summaries, the loaded full aggregate for the selected account, and the period being viewed.
/// The UI mutates the loaded aggregate through domain methods; every mutation re-serializes the account
/// and pushes the snapshot to the server (which relays the change to other contributors).
/// </summary>
public sealed class BudgetingState(FinAppApiClient api, AuthState auth, SyncClient sync)
{
    private readonly BudgetCoverageService _coverage = new();
    private readonly SavingsReportService _savings = new();

    private List<AccountSummaryDto> _summaries = [];
    private Account? _account;
    private long _version;
    private int _accountIndex;
    private int _selectedIndex;
    private bool _syncStarted;
    private List<InvitationDto> _pendingInvitations = [];

    // Per-account aggregate cache so switching back to an already-loaded account is instant (no re-fetch).
    // It's only trusted while live sync is connected: AccountChanged drops a changed account's entry, and a
    // reconnect clears everything (events during the outage are missed). Falls back to always-fetch when offline.
    private sealed class CachedAccount(Account account, long version)
    {
        public Account Account { get; } = account;
        public long Version { get; set; } = version;
    }
    private readonly Dictionary<Guid, CachedAccount> _cache = [];

    public bool IsReady { get; private set; }
    public event Action? Changed;

    // Monotonic counter bumped on every state change (every Changed). Lets consumers cheaply memoize expensive
    // derived views (e.g. the Insights report) and recompute only when the underlying data actually changed —
    // not on every render caused by a UI-only toggle (opening a modal, the bell, switching tabs).
    private long _revision;
    public long Revision => _revision;

    /// <summary>Bump the change counter and raise <see cref="Changed"/>. All internal mutations go through this.</summary>
    private void RaiseChanged() { _revision++; Changed?.Invoke(); }

    public async Task InitializeAsync()
    {
        if (IsReady || !auth.IsAuthenticated) return;

        if (!_syncStarted)
        {
            sync.AccountChanged += OnAccountChanged;
            sync.InvitationReceived += OnInvitationReceived;
            sync.Reconnected += OnReconnected;
            try { await sync.StartAsync(); } catch { /* live sync is best-effort; REST still works */ }
            _syncStarted = true;
        }

        _summaries = await api.GetAccountsAsync();
        _accountIndex = 0;
        await SubscribeAllAsync();   // so AccountChanged invalidates cached background accounts too
        await LoadSelectedAccountAsync();
        await RefreshInvitationsAsync();

        IsReady = true;
        RaiseChanged();
    }

    /// <summary>Clear all session state on sign-out.</summary>
    public async Task ResetAsync()
    {
        IsReady = false;
        _summaries = [];
        _account = null;
        _version = 0;
        _accountIndex = 0;
        _selectedIndex = 0;
        _pendingInvitations = [];
        _syncStarted = false;
        _cache.Clear();
        sync.AccountChanged -= OnAccountChanged;
        sync.InvitationReceived -= OnInvitationReceived;
        sync.Reconnected -= OnReconnected;
        await sync.StopAsync();
        RaiseChanged();
    }

    // --- Accounts ---------------------------------------------------------

    public bool HasAccounts => _summaries.Count > 0;
    public Account Account => _account!;
    public IReadOnlyList<AccountSummaryDto> Accounts => _summaries;
    public Guid CurrentAccountId => _account?.Id ?? Guid.Empty;

    /// <summary>True when the signed-in user owns the current account (gates rename/delete/member removal). Trusts the
    /// server-authoritative summary owner (<see cref="CurrentOwnerId"/>) rather than the opaque snapshot's OwnerUserId:
    /// an ownership transfer updates the relational header but not the client-owned snapshot blob, so a new owner would
    /// otherwise stay locked out of owner actions. Falls back to the snapshot only when no summary is loaded yet.</summary>
    public bool IsOwnerOfCurrent => auth.UserId != Guid.Empty &&
        (CurrentOwnerId != Guid.Empty ? CurrentOwnerId == auth.UserId : _account?.IsOwner(auth.UserId) == true);

    public async Task SwitchAccount(Guid accountId)
    {
        var index = _summaries.FindIndex(a => a.Id == accountId);
        if (index < 0 || index == _accountIndex) return;
        _accountIndex = index;
        await LoadSelectedAccountAsync();
        RaiseChanged();
    }

    public async Task AddAccount(string name, string currency, decimal savingsRateTarget = 0.20m)
    {
        if (_summaries.Any(a => NameEquals(a.Name, name)))
            throw new InvalidOperationException($"You already have an account named “{name.Trim()}”.");
        var summary = await api.CreateAccountAsync(new CreateAccountRequest(name, currency));
        _summaries.Add(summary);
        _accountIndex = _summaries.Count - 1;
        await LoadSelectedAccountAsync(); // empty snapshot -> seeds the starter body and saves
        if (savingsRateTarget != _account!.SavingsRateTarget)
        {
            _account.SetSavingsRateTarget(savingsRateTarget);
            await PushSnapshotAsync();
        }
        RaiseChanged();
    }

    /// <summary>The account's target savings rate (fraction 0..1) — drives the Insights gauge/score.</summary>
    public decimal SavingsRateTarget => Account.SavingsRateTarget;

    /// <summary>Set the account's target savings rate (fraction 0..1) and push the snapshot.</summary>
    public Task SetSavingsRateTarget(decimal target)
    {
        Account.SetSavingsRateTarget(target);
        return SaveAsync();
    }

    /// <summary>User closed the Home "Getting started" checklist — persist so it stays gone.</summary>
    public Task DismissOnboarding()
    {
        Account.DismissOnboarding();
        return SaveAsync();
    }

    public async Task RenameAccount(string name)
    {
        var id = CurrentAccountId;
        if (_summaries.Any(a => a.Id != id && NameEquals(a.Name, name)))
            throw new InvalidOperationException($"You already have an account named “{name.Trim()}”.");
        await api.RenameAccountAsync(id, name);
        _account!.Rename(name);
        _summaries[_accountIndex] = _summaries[_accountIndex] with { Name = name };
        RaiseChanged();
    }

    public async Task RemoveAccount(Guid accountId)
    {
        await api.DeleteAccountAsync(accountId);
        _cache.Remove(accountId);
        var index = _summaries.FindIndex(a => a.Id == accountId);
        if (index >= 0) _summaries.RemoveAt(index);
        if (_accountIndex >= _summaries.Count)
            _accountIndex = Math.Max(0, _summaries.Count - 1);
        await LoadSelectedAccountAsync();
        RaiseChanged();
    }

    // --- Membership / archiving -------------------------------------------

    /// <summary>The current account's other members (everyone but the signed-in user).</summary>
    public IReadOnlyList<MemberDto> OtherMembers => RealUsers.Where(m => m.UserId != auth.UserId).ToList();

    public Guid MyUserId => auth.UserId;

    /// <summary>The current account's owner (from the server-authoritative summary).</summary>
    public Guid CurrentOwnerId => _summaries.ElementAtOrDefault(_accountIndex)?.OwnerUserId ?? Guid.Empty;

    /// <summary>Leave the current account. Returns whether it was archived (you were the last member) or just left.</summary>
    public async Task<LeaveAccountResult> LeaveCurrentAccount(Guid? newOwnerUserId)
    {
        var id = CurrentAccountId;
        var result = await api.LeaveAccountAsync(id, newOwnerUserId);
        _cache.Remove(id);
        var index = _summaries.FindIndex(a => a.Id == id);
        if (index >= 0) _summaries.RemoveAt(index);   // dropped from the active list either way
        if (_accountIndex >= _summaries.Count)
            _accountIndex = Math.Max(0, _summaries.Count - 1);
        await LoadSelectedAccountAsync();
        RaiseChanged();
        return result;
    }

    /// <summary>Owner removes another member from the current account.</summary>
    public async Task RemoveMember(Guid memberUserId)
    {
        var id = CurrentAccountId;
        await api.RemoveMemberAsync(id, memberUserId);
        await ReloadSummariesKeepingAsync(id);
        await LoadSelectedAccountAsync(forceRefresh: true);
        RaiseChanged();
    }

    /// <summary>Owner hands ownership of the current account to another member.</summary>
    public async Task TransferOwnership(Guid newOwnerUserId)
    {
        var id = CurrentAccountId;
        await api.TransferOwnershipAsync(id, newOwnerUserId);
        await ReloadSummariesKeepingAsync(id);
        await LoadSelectedAccountAsync(forceRefresh: true);
        RaiseChanged();
    }

    public Task<List<ArchivedAccountDto>> GetArchivedAccounts() => api.GetArchivedAccountsAsync();

    public async Task ReactivateAccount(Guid accountId)
    {
        await api.ReactivateAccountAsync(accountId);
        await ReloadSummariesKeepingAsync(accountId);
        await LoadSelectedAccountAsync(forceRefresh: true);
        RaiseChanged();
    }

    private async Task ReloadSummariesKeepingAsync(Guid accountId)
    {
        _summaries = await api.GetAccountsAsync();
        var idx = _summaries.FindIndex(a => a.Id == accountId);
        _accountIndex = idx >= 0 ? idx : Math.Max(0, Math.Min(_accountIndex, _summaries.Count - 1));
    }

    private async Task LoadSelectedAccountAsync(bool forceRefresh = false)
    {
        if (_summaries.Count == 0) { _account = null; _version = 0; return; }

        var summary = _summaries[_accountIndex];

        // Load member profile pictures for this account (fire-and-forget so account switching stays instant).
        if (_avatarsAccountId != summary.Id)
        {
            _avatarsAccountId = summary.Id;
            _ = RefreshMemberAvatarsAsync(summary.Id);
        }

        // Warm-cache hit: render the already-loaded aggregate instantly, no server round-trip. Only trusted
        // while live sync is connected (otherwise we can't know if a contributor changed it behind our back).
        if (!forceRefresh && sync.IsConnected && _cache.TryGetValue(summary.Id, out var hit))
        {
            _account = hit.Account;
            _version = hit.Version;
            ReconcileHeader(_account, summary);
            _selectedIndex = _account.Periods.Count - 1;
            return;
        }

        var snapshot = await api.GetSnapshotAsync(summary.Id);
        _version = snapshot.Version;

        if (string.IsNullOrEmpty(snapshot.Payload))
        {
            // Brand-new account: build from the header, seed the starter body, and save v1.
            _account = AccountSnapshotSerializer.CreateForHeader(
                summary.Id, summary.Name, summary.Currency, summary.OwnerUserId,
                summary.Members.Select(m => (m.UserId, m.DisplayName)));
            SeedStarterBody(_account);
            await PushSnapshotAsync();
        }
        else
        {
            _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
            ReconcileHeader(_account, summary);
        }

        _cache[summary.Id] = new CachedAccount(_account, _version);
        _selectedIndex = _account.Periods.Count - 1;
        // Anchor achievements to "now" the first time this account is ever loaded, so they count from the current
        // period onward (not retroactively) and can't be farmed by back-dating periods. Persisted on the next save.
        if (_account.AchievementsAnchor is null && _account.CurrentPeriod is { } cp)
            _account.SetAchievementsAnchor(cp.From);
        await sync.SubscribeAsync(summary.Id);
    }

    /// <summary>Stamp any newly-earned achievement with today's date and persist — but only when something actually
    /// changed, so it's safe to call after every render (it converges). Returns true if the snapshot was saved.</summary>
    private (Guid acct, long rev)? _stampedAt;

    public async Task<bool> StampAchievementsAsync(AchievementsService svc, Func<Money, string> fmt, Func<string, string> t)
    {
        if (!IsReady || !HasAccounts) return false;
        var acct = Account;
        // Achievements are entirely data-derived, so they can only newly-earn when the aggregate changes. Skip the
        // (periods × achievements) rebuild when nothing has changed since the last stamp — this runs after every render.
        var mark = (acct.Id, _revision);
        if (_stampedAt == mark) return false;
        _stampedAt = mark;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var changed = false;
        foreach (var a in svc.Build(acct, fmt, t))
            if (a.Earned && !acct.AchievementLog.ContainsKey(a.Key)) { acct.RecordAchievement(a.Key, today); changed = true; }
        if (changed) await SaveAsync();
        return changed;
    }

    /// <summary>Ensure the loaded aggregate reflects server-authoritative header data (name + members).</summary>
    private static void ReconcileHeader(Account account, AccountSummaryDto summary)
    {
        if (account.Name != summary.Name) account.Rename(summary.Name);
        foreach (var m in summary.Members)
            if (!account.IsContributor(m.UserId))
                account.AddMember(m.UserId, m.DisplayName);
    }

    /// <summary>Serialize the current aggregate and push it to the server, advancing the version.</summary>
    private async Task PushSnapshotAsync()
    {
        var payload = AccountSnapshotSerializer.Serialize(_account!);
        var saved = await api.SaveSnapshotAsync(_account!.Id, new SaveAccountRequest(payload, _version));
        _version = saved.Version;
        // Keep the cache entry's version in step with our own push (the Account is the same live instance).
        if (_cache.TryGetValue(_account.Id, out var c)) c.Version = _version;
        else _cache[_account.Id] = new CachedAccount(_account, _version);
    }

    // --- Period navigation ------------------------------------------------

    public Period Period => Account.Periods[_selectedIndex];
    public int PeriodNumber => _selectedIndex + 1;
    public int PeriodCount => Account.Periods.Count;
    public bool CanGoPrev => _selectedIndex > 0;
    public bool CanGoNext => _selectedIndex < Account.Periods.Count - 1;
    public bool IsLatestPeriod => _selectedIndex == Account.Periods.Count - 1;

    /// <summary>You can only roll into the next period once the current one has actually ended — this blocks creating
    /// future periods in advance (which would let milestones/streaks be farmed). Viewing past periods stays allowed.</summary>
    public bool CanStartNextPeriod =>
        Account.CurrentPeriod is { } p && p.To < DateOnly.FromDateTime(DateTime.Today);

    public void GoPrev() { if (CanGoPrev) { _selectedIndex--; RaiseChanged(); } }
    public void GoNext() { if (CanGoNext) { _selectedIndex++; RaiseChanged(); } }

    public string Currency => Account.Currency;
    public Money Money(decimal amount) => new(amount, Currency);

    // --- Funds ------------------------------------------------------------

    public IReadOnlyList<Fund> Funds => Account.Funds;
    /// <summary>All funds (flat). Kept as <c>RootFunds</c> for call-site compatibility.</summary>
    public IReadOnlyList<Fund> RootFunds => Account.RootFunds.ToList();
    public Fund? FindFund(Guid fundId) => Account.FindFund(fundId);
    public string? FundNote(Guid fundId) => Account.FindFund(fundId)?.Note;
    public string FundName(Guid fundId) => Account.FundName(fundId);
    public Money FundBalance(Guid fundId) => Period.FundBalance(fundId);
    public Money FundOpeningBalance(Guid fundId) =>
        Period.InitialBalances.FirstOrDefault(b => b.FundId == fundId)?.Amount ?? Money(0);
    public string? FundRemovalBlocker(Guid fundId) => Account.FundRemovalBlocker(fundId);

    public IReadOnlyList<FundTransfer> FundTransfers =>
        Period.FundTransfers.OrderByDescending(t => t.Date).ToList();

    private Guid DefaultFundId => SelectableFunds.FirstOrDefault()?.Id ?? DefaultFundIdRaw;
    private Guid DefaultFundIdRaw => Account.RootFunds.FirstOrDefault()?.Id ?? Guid.Empty;

    /// <summary>The period's opening balance: the sum of the real (non-informative) initial fund values.
    /// Independent of how the money is later budgeted/saved (unallocations never change it).</summary>
    public Money OpeningBalance => Period.InitialTotal;

    /// <summary>Physical money expected to carry into the next period.</summary>
    public Money ClosingBalance => Period.ExpectedClosingBalance;

    /// <summary>The account total (and free-to-allocate) for display, with the synced fund's ledger position swapped
    /// for its <b>live</b> bank balance so the header reflects real external money (incl. transactions not yet
    /// imported). Display-only: allocation caps keep using the conservative ledger figures. No-op without a synced
    /// fund / live balance, and skipped when the bank reports a different currency (we don't add across currencies).</summary>
    public Money DisplayClosingBalance(decimal? liveBankBalance, string? bankCurrency) => BankAdjust(ClosingBalance, liveBankBalance, bankCurrency);
    public Money DisplayFreeToAllocate(decimal? liveBankBalance, string? bankCurrency) => BankAdjust(FreeToAllocate, liveBankBalance, bankCurrency);

    /// <summary>The cash you can still move into savings, shown in the Add-to-savings modal. Same basis as the header
    /// "free" (bank-adjusted) but floored at 0 — you can't set aside a negative amount. That floor is the only reason
    /// it can read lower than "free": when you've already earmarked more than you hold, free goes negative but this
    /// stays 0.</summary>
    public Money AvailableToSaveDisplay(decimal? liveBankBalance, string? bankCurrency)
    {
        var free = DisplayFreeToAllocate(liveBankBalance, bankCurrency);
        return free.IsNegative ? Money(0m) : free;
    }
    private Money BankAdjust(Money baseAmount, decimal? liveBankBalance, string? bankCurrency)
    {
        if (!HasSyncedFund || liveBankBalance is not { } live) return baseAmount;
        if (!string.IsNullOrEmpty(bankCurrency) && !string.Equals(bankCurrency, Account.Currency, StringComparison.OrdinalIgnoreCase))
            return baseAmount;
        return baseAmount + Money(live - Period.LedgerFundBalance(SyncedFundId).Amount);
    }

    /// <summary>This period's transfers sent out to other accounts (newest first).</summary>
    public IReadOnlyList<ExternalTransfer> ExternalTransfers =>
        Period.ExternalTransfers.OrderByDescending(t => t.Date).ToList();

    // --- Category tree & budgets (reads) ----------------------------------

    public IEnumerable<Category> RootCategories => Account.RootCategories;
    public IEnumerable<Category> ChildrenOf(Guid parentId) => Account.ChildrenOfCategory(parentId);
    public IReadOnlyList<Category> AllCategories => Account.Categories;

    /// <summary>Categories in tree order with their depth, for an indented &lt;select&gt; (parents above their children).</summary>
    public IReadOnlyList<(Category Category, int Depth)> CategoryOptions
    {
        get
        {
            var result = new List<(Category, int)>();
            void Walk(IEnumerable<Category> nodes, int depth)
            {
                foreach (var c in nodes)
                {
                    result.Add((c, depth));
                    Walk(Account.ChildrenOfCategory(c.Id), depth + 1);
                }
            }
            Walk(Account.RootCategories, 0);
            return result;
        }
    }
    public Budget? BudgetFor(Guid categoryId) => Period.FindBudget(categoryId);
    public bool HasBudget(Guid categoryId) => Period.FindBudget(categoryId) is not null;
    public BudgetCoverage Coverage(Guid categoryId) => _coverage.ForCategory(Account, Period, categoryId);
    public Money Leftover(Guid categoryId) => Coverage(categoryId).Remaining;
    public string? CategoryRemovalBlocker(Guid categoryId) => Account.CategoryRemovalBlocker(categoryId);
    public string CategoryName(Guid categoryId) => Account.FindCategory(categoryId)?.Name ?? "—";
    public string? ParentName(Guid? parentId) => parentId is { } p ? Account.FindCategory(p)?.Name : null;

    public IEnumerable<Category> BudgetedCategories =>
        Period.Budgets.Select(b => Account.FindCategory(b.CategoryId)!).Where(c => c is not null);

    /// <summary>Total spent in a category and its sub-categories this period (works without a budget).</summary>
    public Money SpentInCategory(Guid categoryId)
    {
        var ids = Account.CategoryWithDescendantIds(categoryId).ToHashSet();
        return Period.Expenses.Where(e => ids.Contains(e.CategoryId))
            .Select(e => e.Amount)
            .Aggregate(Money(0), (acc, m) => acc + m);
    }

    // --- Totals & reports -------------------------------------------------

    public Money TotalBudgeted => Period.BudgetedTotal;
    public Money TotalSpent => Period.ExpensesTotal;

    /// <summary>New member deposits this period (the contributed pool).</summary>
    public Money TotalContributed => Period.ContributionsPaidTotal;

    /// <summary>Savings earmarked beyond actual cash left — overspend to reconcile next period.</summary>
    public Money Deficit => Period.Deficit;
    public bool HasDeficit => Period.Deficit.Amount > 0m;

    /// <summary>
    /// Savings accumulated <b>before</b> this period — total saved across the whole account (incl. pre-app initial
    /// balances) minus this period's own net. The opening balances carry that money forward, so the planning caps
    /// must reserve it: otherwise previously-saved money looks freshly available to budget, save or transfer again.
    /// </summary>
    private Money PriorSaved => _savings.AccumulatedTotal(Account) - Period.SavingsNetTotal;

    /// <summary>Most that can be sent to another account without breaking the savings earmark.</summary>
    public Money AvailableToTransferOut => Period.AvailableToTransferOutAfter(PriorSaved);

    /// <summary>Most that can be sent to another account from a specific fund (≤ that fund's balance).</summary>
    public Money AvailableToTransferOutFromFund(Guid fundId) => Period.AvailableToTransferOutFromFundAfter(fundId, PriorSaved);
    // Display figures ("saved this period" / "total saved") exclude disbursements — deploying a save to its goal
    // counts as saved. The money model above (PriorSaved) keeps using the earmark totals, which do drop.
    public Money SavingsThisPeriod => Period.SavingsSetAsideTotal;
    public Money SavingsAccumulated => _savings.LifetimeSaved(Account);
    public Money MaxAdditionalSavings => Period.MaxAdditionalSavingsAfter(PriorSaved);
    public Money AvailableToSave => Period.AvailableToSaveAfter(PriorSaved);

    /// <summary>Unallocated cash this period (closing − all savings). Negative = over-allocated. Advisory only.</summary>
    public Money FreeToAllocate => Period.FreeToAllocateAfter(PriorSaved);
    public bool IsOverAllocated => Period.FreeToAllocateAfter(PriorSaved).IsNegative;

    /// <summary>The most a single category's budget can be set to (Current − savings + spent, minus other budgets). Caps budgeting.</summary>
    public Money MaxBudgetFor(Guid categoryId) => Period.MaxBudgetFor(categoryId, PriorSaved);

    public IReadOnlyList<Expense> AllExpenses =>
        Period.Expenses.OrderByDescending(e => e.Date).ToList();

    public IReadOnlyList<Expense> ExpensesFor(Guid categoryId) =>
        Period.Expenses.Where(e => e.CategoryId == categoryId).OrderByDescending(e => e.Date).ToList();

    public bool IsPeriodOpen => Period.Status == PeriodStatus.Open;

    public Expense? FindExpense(Guid id) => Period.Expenses.FirstOrDefault(e => e.Id == id);

    // --- Faster expense entry (#11) --------------------------------------------------------------
    // Manual entries only (AutoFiled bank rows reflect the bank, not a deliberate choice), newest first:
    // periods newest→oldest, and within a period the last-added expense first (list order ≈ entry order).
    private IEnumerable<Expense> ManualExpensesNewestFirst =>
        Enumerable.Reverse(Account.Periods.ToList())
            .SelectMany(p => Enumerable.Reverse(p.Expenses.ToList()))
            .Where(e => !e.AutoFiled);

    /// <summary>The most recent manual expense, for the "repeat last" quick action. Null when none logged yet.</summary>
    public Expense? LastExpense => ManualExpensesNewestFirst.FirstOrDefault();

    /// <summary>The fund last used for a category (across all periods), to default the fund picker to what the user
    /// actually tends to pay it from. Only returns a fund that's still selectable; null when there's no usable history.</summary>
    public Guid? LastFundForCategory(Guid categoryId)
    {
        var selectable = SelectableFunds.Select(f => f.Id).ToHashSet();
        return ManualExpensesNewestFirst
            .Where(e => e.CategoryId == categoryId && selectable.Contains(e.FundId))
            .Select(e => (Guid?)e.FundId)
            .FirstOrDefault();
    }

    /// <summary>Recent distinct merchants (expense notes), newest first, each carrying the category/fund/amount from
    /// its most recent use — for one-tap "recent merchant" chips in the add-expense modal.</summary>
    public IReadOnlyList<Expense> RecentMerchants(int max = 6) =>
        ManualExpensesNewestFirst
            .Where(e => !string.IsNullOrWhiteSpace(e.Note))
            .GroupBy(e => e.Note!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(max)
            .ToList();

    /// <summary>The category to pre-fill for an imported expense with this description: reuse the one from the most
    /// recent past expense whose note matches (same normalization the bank sync uses), so a merchant you've filed
    /// before auto-files again. Null when nothing matches — the review then leaves it on the default.</summary>
    public Guid? SuggestExpenseCategory(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var key = BankMatchKey(description);
        return key.Length == 0 ? null : ManualExpensesNewestFirst
            .Where(e => !string.IsNullOrWhiteSpace(e.Note) && BankMatchKey(e.Note!) == key)
            .Select(e => (Guid?)e.CategoryId)
            .FirstOrDefault();
    }

    public decimal? PeriodSavingsRate => _savings.PeriodSavingsRate(Period);
    public decimal? AccountSavingsRate => _savings.AccountSavingsRate(Account);

    public SavingCategory? FindSavingBucket(Guid id) => Account.FindSavingCategory(id);
    public SavingGoalProgress SavingGoal(Guid bucketId) => _savings.GoalProgress(Account, bucketId);
    public string? SavingBucketRemovalBlocker(Guid id) => Account.SavingCategoryRemovalBlocker(id);

    public string MemberName(Guid memberId) =>
        Account.Members.FirstOrDefault(m => m.UserId == memberId)?.DisplayName ?? "—";

    /// <summary>The real signed-in users on this account (the server-authoritative header members — owner + invited
    /// contributors). Excludes members that only exist inside the imported snapshot (no real user behind them).</summary>
    public IReadOnlyList<MemberDto> RealUsers =>
        _summaries.ElementAtOrDefault(_accountIndex)?.Members ?? [];

    /// <summary>True when this member id belongs to a real signed-in user (vs a snapshot-imported placeholder).</summary>
    public bool IsRealUser(Guid memberId) => RealUsers.Any(m => m.UserId == memberId);

    // Member profile pictures (server-stored), loaded per account.
    private Guid _avatarsAccountId;
    private Dictionary<Guid, string> _memberAvatars = [];

    /// <summary>The member's profile picture (data-URL), or null to fall back to initials.</summary>
    public string? MemberAvatar(Guid memberId) =>
        _memberAvatars.TryGetValue(memberId, out var v) ? v : null;

    private async Task RefreshMemberAvatarsAsync(Guid accountId)
    {
        try
        {
            var avatars = await api.GetAccountAvatarsAsync(accountId);
            if (_avatarsAccountId == accountId) { _memberAvatars = avatars; RaiseChanged(); }
        }
        catch { /* best effort — fall back to initials */ }
    }

    /// <summary>Drop cached member avatars (e.g. after the signed-in user changes their own picture).</summary>
    public void InvalidateMemberAvatars() => _avatarsAccountId = Guid.Empty;

    public IReadOnlyList<(SavingCategory Bucket, Money Total)> SavingBuckets =>
        Account.SavingCategories
            .Select(b => (b, _savings.ForBucket(Account, Period, b.Id).AccumulatedTotal))
            .ToList();

    public string SavingBucketName(Guid id) => FindSavingBucket(id)?.Name ?? "—";

    /// <summary>This period's manual "Add to savings" deposits, newest first (editable/removable).</summary>
    public IReadOnlyList<SavingAllocation> SavingDepositsThisPeriod =>
        Period.ManualSavingDeposits().OrderByDescending(a => a.Date).ToList();

    public SavingAllocation? FindSavingDeposit(Guid id) =>
        Period.ManualSavingDeposits().FirstOrDefault(a => a.Id == id);

    /// <summary>This period's savings spendings (money matured into a budget, or moved between buckets), newest first.</summary>
    public IReadOnlyList<SavingAllocation> SavingMovementsThisPeriod =>
        Period.SavingMovements().OrderByDescending(a => a.Date).ToList();

    public SavingAllocation? FindSavingMovement(Guid id) =>
        Period.SavingMovements().FirstOrDefault(a => a.Id == id);

    /// <summary>A human-readable destination for a savings movement row (a budget category, or another bucket).</summary>
    public string SavingMovementTarget(SavingAllocation movement)
    {
        if (movement.BudgetCategoryId is { } categoryId)
            return $"{SavingBucketName(movement.SavingCategoryId)} → {CategoryName(categoryId)} (budget)";
        if (movement.TransferPairId is { } pairId)
        {
            var toId = Period.SavingAllocations
                .Where(a => a.TransferPairId == pairId && !a.Amount.IsNegative)
                .Select(a => a.SavingCategoryId)
                .FirstOrDefault();
            return $"{SavingBucketName(movement.SavingCategoryId)} → {SavingBucketName(toId)} (bucket)";
        }
        if (movement.IsDisbursement)
            return $"{SavingBucketName(movement.SavingCategoryId)} → {(string.IsNullOrWhiteSpace(movement.Note) ? "goal" : movement.Note)}";
        return SavingBucketName(movement.SavingCategoryId);
    }

    public Task EditSavingMovement(Guid allocationId, decimal amount)
    {
        Period.EditSavingMovement(allocationId, Money(amount));
        return SaveAsync();
    }

    public Task RemoveSavingMovement(Guid allocationId)
    {
        Period.RemoveSavingMovement(allocationId);
        return SaveAsync();
    }

    public IReadOnlyList<AccountMember> Members => Account.Members;
    public Contribution? ContributionFor(Guid memberId) =>
        Period.Contributions.FirstOrDefault(c => c.MemberId == memberId);

    /// <summary>Who the current actions are attributed to — the signed-in user (a member of the account).</summary>
    private Guid CurrentMemberId => auth.UserId;

    // --- Contribution categories + itemized deposits ----------------------
    public IReadOnlyList<ContributionCategory> ContributionCategories => Account.ContributionCategories;
    public string ContributionCategoryName(Guid id) =>
        Account.FindContributionCategory(id)?.Name ?? "—";
    public string? ContributionCategoryRemovalBlocker(Guid id) => Account.ContributionCategoryRemovalBlocker(id);

    /// <summary>This period's real member deposits (excludes the carryover sentinel), newest first.</summary>
    public IReadOnlyList<Contribution> ContributionsThisPeriod =>
        Period.Contributions.Where(c => c.MemberId != Period.CarryoverSource)
            .OrderByDescending(c => c.Date).ToList();

    public Contribution? FindContribution(Guid id) => Period.FindContribution(id);

    // --- Commands ---------------------------------------------------------

    /// <summary>Whether a fund is currently synced to a bank account (its balance is externally authoritative).</summary>
    public bool FundIsSynced(Guid fundId) => _account?.Funds.FirstOrDefault(f => f.Id == fundId)?.IsSynced ?? false;

    /// <summary>The account's synced fund (the one mirroring the linked bank account), or empty if none is marked.
    /// Bank-imported records route here automatically. First synced fund wins if several are marked.</summary>
    public Guid SyncedFundId => _account?.Funds.FirstOrDefault(f => f.IsSynced)?.Id ?? Guid.Empty;
    public bool HasSyncedFund => SyncedFundId != Guid.Empty;
    public string SyncedFundName => HasSyncedFund ? FundName(SyncedFundId) : "";

    /// <summary>Funds the user may target manually (expenses/transfers/deposits) — synced funds are excluded;
    /// they're driven only by the bank import flow.</summary>
    public IReadOnlyList<Fund> SelectableFunds => Account.RootFunds.Where(f => !f.IsSynced).ToList();

    public Task AddExpense(Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date, bool onBehalfOfOtherAccount = false,
        string? bankExternalId = null, bool autoFiled = false)
    {
        var expense = new Expense(categoryId, Money(amount), date, CurrentMemberId, fundId, note,
            onBehalfOfOtherAccount: onBehalfOfOtherAccount);
        expense.SetFundSynced(FundIsSynced(fundId));   // synced funds aren't debited (real bank balance handles it)
        if (bankExternalId is not null || autoFiled) expense.SetBankLink(bankExternalId, autoFiled);
        Period.AddExpense(expense);
        return SaveAsync();
    }

    public async Task EditExpense(Guid expenseId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date)
    {
        var before = Period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        var edited = Period.EditExpense(expenseId, categoryId, Money(amount), fundId, note, date);
        edited.SetFundSynced(FundIsSynced(fundId));   // recompute at edit time (moving to/from a synced fund)
        // Keep the bank provenance (for dedupe) but clear the auto-filed badge — editing means the user reviewed it.
        edited.SetBankLink(before?.BankExternalId, autoFiled: false);
        await SaveAsync();
        // Editing a settlement-destination expense mirrors the new amount back to the source expense.
        if (before is { IsSettlementDestination: true, SettlementId: { } sid, SettledFromAccountId: { } sourceAccount })
            await SyncSourceSettlementAmount(sourceAccount, sid, amount);
    }

    public async Task RemoveExpense(Guid expenseId)
    {
        var before = Period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        Period.RemoveExpense(expenseId);
        await SaveAsync();
        // Removing one side of a settlement reverses the other: deleting the source drops the destination expense;
        // deleting the destination un-settles the source (restores its full amount).
        if (before is { IsSettlementSource: true, SettledToAccountId: { } destAccount, SettlementId: { } sid })
            await RemoveLinkedSettlementExpense(destAccount, sid);
        else if (before is { IsSettlementDestination: true, SettledFromAccountId: { } sourceAccount, SettlementId: { } sid2 })
            await SyncSourceSettlementAmount(sourceAccount, sid2, 0m);
    }

    /// <summary>Record a deposit for the signed-in user, classified by category and attributed to a fund.</summary>
    public Task RecordDeposit(Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        var contribution = Period.Deposit(CurrentMemberId, Money(amount), categoryId, fundId, date);
        contribution.SetFundSynced(FundIsSynced(fundId));   // synced destination fund isn't credited here
        return SaveAsync();
    }

    /// <summary>Edit one of the signed-in user's own deposit rows.</summary>
    public Task EditDeposit(Guid contributionId, Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        EnsureOwnContribution(contributionId);
        Period.EditContribution(contributionId, Money(amount), categoryId, fundId, date);
        Period.FindContribution(contributionId)?.SetFundSynced(FundIsSynced(fundId));   // recompute at edit time
        return SaveAsync();
    }

    /// <summary>Remove one of the signed-in user's own deposit rows.</summary>
    public Task RemoveDeposit(Guid contributionId)
    {
        EnsureOwnContribution(contributionId);
        Period.RemoveContribution(contributionId);
        return SaveAsync();
    }

    // --- Recurring items (BACKLOG #13, phase 1) — expectations that post a real expense/deposit on confirm ------
    public IReadOnlyList<RecurringItem> RecurringItems => Account.RecurringItems;

    /// <summary>Recurring items due (and not yet handled) in the open period as of today — what the bell prompts for.</summary>
    public IReadOnlyList<RecurringItem> DueRecurring() =>
        IsPeriodOpen ? Account.RecurringItems.Where(r => r.IsDue(Period.From, Period.To, Today())).ToList() : [];

    /// <summary>Recurring items coming up within <paramref name="windowDays"/> days (pending, not yet due) — a heads-up.</summary>
    public IReadOnlyList<RecurringItem> UpcomingRecurring(int windowDays = 5) =>
        IsPeriodOpen ? Account.RecurringItems.Where(r => r.IsUpcoming(Period.From, Period.To, Today(), windowDays)).ToList() : [];

    /// <summary>How many days until a recurring item is due, within the current period.</summary>
    public int RecurringDaysUntilDue(RecurringItem r) => r.DaysUntilDue(Period.From, Period.To, Today());

    /// <summary>Total of the known-amount recurring <b>bills</b> still expected (unhandled) this period — money that's
    /// effectively already spoken for, even though it hasn't been logged yet. Reminder-only items are excluded (no
    /// predictable amount). Keeps "free to allocate" honest.</summary>
    public Money BillsDueThisPeriod =>
        !IsPeriodOpen ? Money(0m)
        : Money(Account.RecurringItems
            .Where(r => r.Kind == RecurringKind.Expense && r.HasKnownAmount && r.IsPending(Period.From))
            .Sum(r => r.ExpectedAmount));

    public Task AddRecurring(string name, RecurringKind kind, RecurringAmountMode mode, decimal expected, int dayOfMonth, Guid categoryId, Guid fundId, string? icon, bool autoPost = false)
    {
        Account.AddRecurring(new RecurringItem(name, kind, mode, expected, dayOfMonth, categoryId, fundId, icon, autoPost));
        return SaveAsync();
    }

    public Task UpdateRecurring(Guid id, string name, RecurringAmountMode mode, decimal expected, int dayOfMonth, Guid categoryId, Guid fundId, string? icon, bool autoPost = false)
    {
        Account.FindRecurring(id)?.Update(name, mode, expected, dayOfMonth, categoryId, fundId, icon, autoPost);
        return SaveAsync();
    }

    public Task RemoveRecurring(Guid id) { Account.RemoveRecurring(id); return SaveAsync(); }
    public Task SetRecurringActive(Guid id, bool active) { Account.FindRecurring(id)?.SetActive(active); return SaveAsync(); }

    // Post a recurring item's amount as a real expense/contribution (shared by confirm + auto-post). Marks it handled.
    private void PostRecurring(RecurringItem item, decimal amount)
    {
        if (amount > 0m)
        {
            var date = item.DueDateWithin(Period.From, Period.To);
            if (item.Kind == RecurringKind.Expense)
            {
                var expense = new Expense(item.CategoryId, Money(amount), date, CurrentMemberId, item.FundId, item.Name);
                expense.SetFundSynced(FundIsSynced(item.FundId));
                Period.AddExpense(expense);
            }
            else
            {
                var contribution = Period.Deposit(CurrentMemberId, Money(amount), item.CategoryId, item.FundId, date);
                contribution.SetFundSynced(FundIsSynced(item.FundId));
            }
        }
        item.MarkHandled(Period.From);
    }

    /// <summary>Confirm a due recurring item with the <b>real</b> amount: posts a normal expense/contribution, nudges a
    /// Typical estimate toward the actual, and marks it handled for this period — all in a single save.</summary>
    public Task ConfirmRecurring(Guid id, decimal actualAmount)
    {
        if (Account.FindRecurring(id) is not { } item) return Task.CompletedTask;
        if (actualAmount > 0m) item.LearnFromActual(actualAmount);
        PostRecurring(item, actualAmount);
        return SaveAsync();
    }

    // --- Statement file import (CSV/OFX/QIF → real expenses & income) ------

    /// <summary>Import chosen statement rows in one save: a negative amount becomes an expense, a positive one a
    /// contribution (income). Each row carries the category/fund the user picked in the review step.</summary>
    public Task ImportTransactions(IReadOnlyList<(decimal Amount, DateOnly Date, Guid CategoryId, Guid FundId, string Note)> rows)
    {
        foreach (var (amount, date, categoryId, fundId, note) in rows)
        {
            if (amount == 0m || categoryId == Guid.Empty || fundId == Guid.Empty) continue;
            if (amount < 0m)
            {
                var expense = new Expense(categoryId, Money(Math.Abs(amount)), date, CurrentMemberId, fundId, string.IsNullOrWhiteSpace(note) ? null : note);
                expense.SetFundSynced(FundIsSynced(fundId));
                Period.AddExpense(expense);
            }
            else
            {
                var contribution = Period.Deposit(CurrentMemberId, Money(amount), categoryId, fundId, date);
                contribution.SetFundSynced(FundIsSynced(fundId));
            }
        }
        return SaveAsync();
    }

    /// <summary>Does an expense/contribution with this date and (absolute) amount already exist this period? Used to
    /// pre-flag likely duplicates when re-importing an overlapping statement, so nothing double-counts.</summary>
    public bool ImportLooksDuplicate(DateOnly date, decimal amount)
    {
        var abs = Math.Abs(amount);
        return amount < 0m
            ? Period.Expenses.Any(e => e.Date == date && e.Amount.Amount == abs)
            : Period.Contributions.Any(c => c.MemberId != FinApp.Domain.Periods.Period.CarryoverSource && c.Date == date && c.Paid.Amount == abs);
    }

    /// <summary>Whether a date falls inside the current open period (imports can only post to the active period).</summary>
    public bool ImportInPeriod(DateOnly date) => date >= Period.From && date <= Period.To;

    /// <summary>Skip a due recurring item this period (marks it handled without posting anything).</summary>
    public Task SkipRecurring(Guid id) { Account.FindRecurring(id)?.MarkHandled(Period.From); return SaveAsync(); }

    /// <summary>Auto-post every due Fixed item flagged for it (with its fixed amount), marking each handled. Converges:
    /// it marks items handled synchronously before the save, so a re-invocation during the save is a no-op. Returns
    /// what it posted so the UI can show a "posted automatically" notice.</summary>
    public async Task<IReadOnlyList<(string Name, Money Amount, RecurringKind Kind)>> AutoPostDueRecurringAsync()
    {
        if (!IsPeriodOpen) return [];
        var due = Account.RecurringItems.Where(r => r.AutoPost && r.IsDue(Period.From, Period.To, Today())).ToList();
        if (due.Count == 0) return [];
        var posted = new List<(string, Money, RecurringKind)>();
        foreach (var item in due)
        {
            PostRecurring(item, item.ExpectedAmount);
            posted.Add((item.Name, Money(item.ExpectedAmount), item.Kind));
        }
        await SaveAsync();
        return posted;
    }

    /// <summary>Log per-fund reconciliation drift as recategorizable <b>Adjustment</b> entries in the current
    /// period so its books reconcile to reality: an <i>expense</i> where a fund holds less than the ledger expected,
    /// a money-in <i>deposit</i> where it holds more. Reuses (or creates once) an "Adjustment" category.</summary>
    public async Task RecordReconciliationAdjustments(IReadOnlyList<(Guid FundId, decimal Gap)> gaps, DateOnly date)
    {
        Guid? expenseCat = null, contribCat = null;
        foreach (var (fundId, gap) in gaps)
        {
            if (gap < 0)
            {
                expenseCat ??= AllCategories.FirstOrDefault(c => string.Equals(c.Name, "Adjustment", StringComparison.OrdinalIgnoreCase))?.Id
                               ?? await AddCategory("Adjustment", null, "⚖️");
                await AddExpense(expenseCat.Value, Math.Abs(gap), fundId, "Reconciliation", date);
            }
            else if (gap > 0)
            {
                contribCat ??= ContributionCategories.FirstOrDefault(c => string.Equals(c.Name, "Adjustment", StringComparison.OrdinalIgnoreCase))?.Id
                               ?? await AddContributionCategory("Adjustment", "⚖️");
                await RecordDeposit(contribCat.Value, fundId, gap, date);
            }
        }
    }

    /// <summary>True when the deposit belongs to the signed-in user (only they may edit/remove it).</summary>
    public bool CanHandleContribution(Contribution c) => c.MemberId == CurrentMemberId;

    private void EnsureOwnContribution(Guid contributionId)
    {
        var c = Period.FindContribution(contributionId);
        if (c is null || !CanHandleContribution(c))
            throw new InvalidOperationException("You can only change your own contributions.");
    }

    public async Task<Guid> AddContributionCategory(string name, string? icon = null)
    {
        var c = Account.AddContributionCategory(name);
        Account.SetContributionCategoryIcon(c.Id, icon);
        await SaveAsync();
        return c.Id;
    }

    public Task RenameContributionCategory(Guid id, string name)
    {
        Account.RenameContributionCategory(id, name);
        return SaveAsync();
    }

    /// <summary>Rename a contribution category and set its icon in one save.</summary>
    public Task SaveContributionCategory(Guid id, string name, string? icon)
    {
        Account.RenameContributionCategory(id, name);
        Account.SetContributionCategoryIcon(id, icon);
        return SaveAsync();
    }

    public string ContributionCategoryIcon(Guid id) =>
        CategoryIcons.Effective(Account.FindContributionCategory(id)?.Icon, Account.FindContributionCategory(id)?.Name);
    public string? ContributionCategoryStoredIcon(Guid id) => Account.FindContributionCategory(id)?.Icon;

    public Task RemoveContributionCategory(Guid id)
    {
        Account.RemoveContributionCategory(id);
        return SaveAsync();
    }

    public Task AllocateSaving(Guid savingCategoryId, decimal amount, string? note)
    {
        Period.AllocateToSavings(savingCategoryId, Money(amount), Today(), note, PriorSaved);
        return SaveAsync();
    }

    public Task EditSavingDeposit(Guid allocationId, decimal amount)
    {
        Period.EditSavingDeposit(allocationId, Money(amount), PriorSaved);
        return SaveAsync();
    }

    public Task RemoveSavingDeposit(Guid allocationId)
    {
        Period.RemoveSavingAllocation(allocationId);
        return SaveAsync();
    }

    public Task SpendFromSavings(Guid savingCategoryId, Guid categoryId, decimal amount, string? note)
    {
        Period.ConvertSavingToExpense(savingCategoryId, categoryId, Money(amount), Today(),
            CurrentMemberId, DefaultFundId, note);
        return SaveAsync();
    }

    public Task ConvertSavingToBudget(Guid savingCategoryId, Guid categoryId, decimal amount, string? note)
    {
        Period.ConvertSavingToBudget(savingCategoryId, categoryId, Money(amount), Today(), note);
        return SaveAsync();
    }

    /// <summary>Deploy a bucket to its goal (e.g. a loan prepayment) from a chosen fund: money leaves the account but
    /// it's not an expense and doesn't dent the savings figures. The fund is the one physically holding the money.</summary>
    public Task DisburseSaving(Guid savingCategoryId, Guid fundId, decimal amount, string? note)
    {
        var transfer = Period.DisburseSaving(savingCategoryId, fundId, Money(amount), Today(), note);
        transfer.SetFundSynced(FundIsSynced(fundId));   // a synced fund's real balance already reflects the outflow
        // On a debt bucket, dispatching to the bank is a payment — lower what's still owed (projection metadata only).
        Account.RecordSavingDebtPayment(savingCategoryId, amount);
        return SaveAsync();
    }

    // --- Bucket lifecycle (archive a paid-off debt / reached goal) ---
    public bool SavingBucketIsArchived(Guid id) => FindSavingBucket(id)?.IsArchived ?? false;
    public bool SavingBucketIsDebtCleared(Guid id) => FindSavingBucket(id)?.IsDebtCleared ?? false;
    public Task SetSavingBucketArchived(Guid id, bool archived)
    {
        Account.SetSavingArchived(id, archived);
        return SaveAsync();
    }

    /// <summary>Move earmarked money from one savings bucket to another (net-neutral).</summary>
    public Task MoveSavingToBucket(Guid fromBucketId, Guid toBucketId, decimal amount, string? note)
    {
        Period.TransferSavings(fromBucketId, toBucketId, Money(amount), Today(), note);
        return SaveAsync();
    }

    /// <summary>True during initial setup (only the first period exists) — when a bucket's pre-existing initial balance may be set.</summary>
    public bool CanSetInitialSavings => PeriodCount == 1;

    // Saving bucket CRUD
    public async Task<Guid> AddSavingBucket(string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon = null,
        bool isDebt = false, decimal debtBalance = 0m, decimal debtRate = 0m, decimal debtInstallment = 0m, decimal? plannedContribution = null,
        bool isInvestment = false, decimal invRate = 0m, decimal invTermYears = 0m, int invCompounds = 12, bool isPlannedExpense = false)
    {
        var bucket = Account.AddSavingCategory(name);
        Account.SetSavingCategoryIcon(bucket.Id, icon);
        if (isDebt)
            Account.ConfigureSavingDebt(bucket.Id, debtBalance, debtRate, debtInstallment);
        else if (isInvestment)
            Account.ConfigureSavingInvestment(bucket.Id, invRate, invTermYears, invCompounds);
        else if (isPlannedExpense)
        {
            Account.MarkSavingPlannedExpense(bucket.Id);
            if (goalAmount is > 0m) Account.ConfigureSavingGoal(bucket.Id, goalAmount, thresholdPercent / 100m, notifyOnMilestone);
        }
        else if (goalAmount is > 0m)
            Account.ConfigureSavingGoal(bucket.Id, goalAmount, thresholdPercent / 100m, notifyOnMilestone);
        Account.SetSavingPlannedContribution(bucket.Id, plannedContribution);
        if (CanSetInitialSavings && initialAmount > 0m)
            Account.SetSavingInitialAmount(bucket.Id, initialAmount);
        await SaveAsync();
        return bucket.Id;
    }

    public Task SaveSavingBucket(Guid savingCategoryId, string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon = null,
        bool isDebt = false, decimal debtBalance = 0m, decimal debtRate = 0m, decimal debtInstallment = 0m, decimal? plannedContribution = null,
        bool isInvestment = false, decimal invRate = 0m, decimal invTermYears = 0m, int invCompounds = 12, bool isPlannedExpense = false)
    {
        Account.RenameSavingCategory(savingCategoryId, name);
        Account.SetSavingCategoryIcon(savingCategoryId, icon);
        if (isDebt)
        {
            Account.ConfigureSavingDebt(savingCategoryId, debtBalance, debtRate, debtInstallment);
            Account.ConfigureSavingGoal(savingCategoryId, null);   // debt uses its own figures, not a savings goal
        }
        else if (isInvestment)
        {
            Account.ConfigureSavingInvestment(savingCategoryId, invRate, invTermYears, invCompounds);
            Account.ConfigureSavingGoal(savingCategoryId, null);   // investment uses its own figures, not a savings goal
        }
        else if (isPlannedExpense)
        {
            Account.MarkSavingPlannedExpense(savingCategoryId);   // clears any debt/investment figures; goal = target cost
            Account.ConfigureSavingGoal(savingCategoryId, goalAmount is > 0m ? goalAmount : null, thresholdPercent / 100m, notifyOnMilestone);
        }
        else
        {
            Account.ClearSavingDebt(savingCategoryId);
            Account.ClearSavingInvestment(savingCategoryId);
            Account.ConfigureSavingGoal(savingCategoryId, goalAmount is > 0m ? goalAmount : null, thresholdPercent / 100m, notifyOnMilestone);
        }
        Account.SetSavingPlannedContribution(savingCategoryId, plannedContribution);
        if (CanSetInitialSavings)
            Account.SetSavingInitialAmount(savingCategoryId, initialAmount);
        return SaveAsync();
    }

    /// <summary>Debt-payoff buckets vs ordinary savings buckets (each with its accumulated total), for the two
    /// Savings-tab sections. Reads the same <see cref="SavingBuckets"/> data — purely a split by kind.</summary>
    public bool SavingBucketIsDebt(Guid id) => FindSavingBucket(id)?.IsDebt ?? false;
    public decimal SavingBucketDebtBalance(Guid id) => FindSavingBucket(id)?.DebtBalance ?? 0m;
    public decimal SavingBucketDebtRate(Guid id) => FindSavingBucket(id)?.DebtAnnualRatePercent ?? 0m;
    public decimal SavingBucketDebtInstallment(Guid id) => FindSavingBucket(id)?.DebtInstallment ?? 0m;

    // --- Progress over time (#7): the original owed vs what's left, and how much has been cleared ---
    /// <summary>Debt buckets: the balance owed when the debt was first set up (the "€Y" in "paid off €X of €Y").</summary>
    public decimal SavingBucketDebtOriginal(Guid id) => FindSavingBucket(id)?.DebtOriginalBalance ?? 0m;
    /// <summary>Debt buckets: how much of the original balance has been paid off so far.</summary>
    public decimal SavingBucketDebtPaidOff(Guid id) => FindSavingBucket(id)?.DebtPaidOff ?? 0m;
    /// <summary>Debt buckets: fraction (0..1) of the original balance paid off, or null when there's no baseline.</summary>
    public decimal? SavingBucketDebtProgress(Guid id) => FindSavingBucket(id)?.DebtProgressRatio;

    /// <summary>User-set planned per-period contribution to a bucket (#8), or null when pace is inferred from history.</summary>
    public decimal? SavingBucketPlannedContribution(Guid id) => FindSavingBucket(id)?.PlannedContribution;

    // --- Investment buckets (compound-growth projection) ---
    public bool SavingBucketIsInvestment(Guid id) => FindSavingBucket(id)?.IsInvestment ?? false;
    public decimal SavingBucketInvestmentRate(Guid id) => FindSavingBucket(id)?.InvestmentAnnualRatePercent ?? 0m;
    public decimal SavingBucketInvestmentTermYears(Guid id) => FindSavingBucket(id)?.InvestmentTermYears ?? 0m;
    public int SavingBucketInvestmentCompounds(Guid id) => FindSavingBucket(id)?.InvestmentCompoundsPerYear ?? 12;

    /// <summary>Project an investment bucket's future value — present value is its accumulated balance, adding
    /// <paramref name="extraPerMonth"/> each month over its term at its rate/compounding. Null when it isn't an investment.</summary>
    public FinApp.Domain.Forecasting.InvestmentForecast.Projection? ProjectInvestment(Guid id, decimal extraPerMonth)
    {
        var bucket = FindSavingBucket(id);
        if (bucket is null || !bucket.IsInvestment) return null;
        return FinApp.Domain.Forecasting.InvestmentForecast.Project(
            SavingBucketSaved(id).Amount, bucket.InvestmentAnnualRatePercent, bucket.InvestmentTermYears, bucket.InvestmentCompoundsPerYear, extraPerMonth);
    }

    // --- Forecasting projections (read-only; never touch the money model) ---
    /// <summary>Average amount added to a bucket per active period — the demonstrated saving pace, for projections.</summary>
    public Money? SavingBucketPace(Guid id) => _savings.AverageDepositPace(Account, id);

    /// <summary>The pace projections should use: the user's planned contribution (#8) when set, else the demonstrated
    /// pace from deposit history. Null only when neither exists (no plan and no deposits yet).</summary>
    public Money? EffectiveSavingPace(Guid id)
    {
        var planned = FindSavingBucket(id)?.PlannedContribution;
        return planned is > 0m ? Money(planned.Value) : SavingBucketPace(id);
    }

    /// <summary>Debt buckets: the shrinking remaining-balance series (original owed → balance after each paying period),
    /// for a sparkline (#7). Fewer than 2 points means there's nothing to draw yet.</summary>
    public IReadOnlyList<decimal> SavingBucketDebtHistory(Guid id) => _savings.DebtBalanceHistory(Account, id);

    /// <summary>Debt buckets: projected whole months saved versus paying only the contractual installment, at the
    /// bucket's effective pace (planned contribution, or demonstrated pace). Null when there's no meaningful speed-up.</summary>
    public int? SavingBucketMonthsAhead(Guid id)
    {
        var bucket = FindSavingBucket(id);
        if (bucket is null || !bucket.IsDebt || bucket.DebtOriginalBalance <= 0m) return null;
        var pace = EffectiveSavingPace(id)?.Amount ?? 0m;
        var extra = pace - bucket.DebtInstallment;
        if (extra <= 0m) return null;
        var sim = FinApp.Domain.Forecasting.LoanForecast.SimulateExtra(
            bucket.DebtOriginalBalance, bucket.DebtAnnualRatePercent, bucket.DebtInstallment, extra);
        return sim is { MonthsSaved: > 0 } s ? s.MonthsSaved : null;
    }

    /// <summary>How much is currently set aside in a bucket (its accumulated balance).</summary>
    public Money SavingBucketSaved(Guid id)
    {
        var found = SavingBuckets.FirstOrDefault(x => x.Bucket.Id == id);
        return found.Bucket is null ? Money(0m) : found.Total;
    }

    /// <summary>Debt buckets mapped to loan-forecast inputs (balance/rate/installment) for the multi-debt planner.</summary>
    public IReadOnlyList<FinApp.Domain.Forecasting.LoanForecast.LoanInput> DebtLoanInputs =>
        SavingBuckets.Where(x => x.Bucket.IsDebt && !x.Bucket.IsArchived && x.Bucket.DebtBalance > 0m)
            .Select(x => new FinApp.Domain.Forecasting.LoanForecast.LoanInput(
                x.Bucket.Id, x.Bucket.Name, x.Bucket.DebtBalance, x.Bucket.DebtAnnualRatePercent, x.Bucket.DebtInstallment))
            .ToList();

    public string SavingBucketIcon(Guid id) =>
        CategoryIcons.Effective(FindSavingBucket(id)?.Icon, FindSavingBucket(id)?.Name);
    public string? SavingBucketStoredIcon(Guid id) => FindSavingBucket(id)?.Icon;

    public decimal SavingInitialAmount(Guid savingCategoryId) => FindSavingBucket(savingCategoryId)?.InitialAmount ?? 0m;

    public Task RemoveSavingBucket(Guid savingCategoryId)
    {
        Account.RemoveSavingCategory(savingCategoryId);
        return SaveAsync();
    }

    // Fund CRUD + transfers
    public async Task<Guid> AddFund(string name, string? note = null, string? icon = null)
    {
        var fund = Account.AddFund(name);
        if (!string.IsNullOrWhiteSpace(note))
            Account.SetFundNote(fund.Id, note);
        Account.SetFundIcon(fund.Id, icon);
        await SaveAsync();
        return fund.Id;
    }

    public Task RenameFund(Guid fundId, string name)
    {
        Account.RenameFund(fundId, name);
        return SaveAsync();
    }

    public Task SetFundIcon(Guid fundId, string? icon)
    {
        Account.SetFundIcon(fundId, icon);
        return SaveAsync();
    }

    /// <summary>Toggle a fund's bank-synced flag (forward-only — see <see cref="Fund.IsSynced"/>).</summary>
    public Task SetFundSynced(Guid fundId, bool synced)
    {
        Account.SetFundSynced(fundId, synced);
        return SaveAsync();
    }

    /// <summary>Bind a fund to a specific bank account on the connection (the synced fund + which bank account it
    /// mirrors). Exactly one fund can be bound, so any other synced fund is cleared.</summary>
    public async Task BindFundToBank(Guid fundId, string? bankAccountRef = null)
    {
        foreach (var f in Account.Funds.Where(f => f.IsSynced && f.Id != fundId).ToList())
            Account.SetFundSynced(f.Id, false);
        Account.SetFundSynced(fundId, true);
        await SaveAsync();
        await api.SetBankFundAsync(CurrentAccountId, fundId);
        if (!string.IsNullOrEmpty(bankAccountRef))
            await api.SelectBankAccountAsync(CurrentAccountId, bankAccountRef);   // map the fund to this bank account
    }

    /// <summary>Unbind a fund from the bank connection (stops routing imports; existing entries keep their markers).</summary>
    public async Task UnbindFundFromBank(Guid fundId)
    {
        Account.SetFundSynced(fundId, false);
        await SaveAsync();
        await api.SetBankFundAsync(CurrentAccountId, null);
        try { await api.RecordConsentAsync("bank_sync", CurrentAccountId, granted: false); } catch { /* audit best-effort */ }
    }

    public string FundIcon(Guid fundId) =>
        CategoryIcons.Effective(Account.FindFund(fundId)?.Icon, Account.FindFund(fundId)?.Name);
    public string? FundStoredIcon(Guid fundId) => Account.FindFund(fundId)?.Icon;

    public Task SetFundNote(Guid fundId, string? note)
    {
        Account.SetFundNote(fundId, note);
        return SaveAsync();
    }

    public bool FundHasOpeningBalance(Guid fundId) => Account.FundHasOpeningBalance(fundId);

    public Task RemoveFund(Guid fundId, Guid? moveOpeningBalancesTo = null)
    {
        Account.RemoveFund(fundId, moveOpeningBalancesTo);
        return SaveAsync();
    }

    public Task SetFundOpeningBalance(Guid fundId, decimal amount)
    {
        Period.SetInitialBalance(fundId, Money(amount));
        return SaveAsync();
    }

    public Task TransferFunds(Guid fromFundId, Guid toFundId, decimal amount, string? note)
    {
        var transfer = Period.TransferFunds(fromFundId, toFundId, Money(amount), Today(), note);
        transfer.SetSyncedSides(FundIsSynced(fromFundId), FundIsSynced(toFundId));   // synced sides aren't moved
        return SaveAsync();
    }

    public FundTransfer? FindFundTransfer(Guid id) => Period.FundTransfers.FirstOrDefault(t => t.Id == id);

    public Task EditFundTransfer(Guid id, Guid fromFundId, Guid toFundId, decimal amount, string? note)
    {
        var before = FindFundTransfer(id);
        var transfer = Period.EditFundTransfer(id, fromFundId, toFundId, Money(amount), note);
        transfer.SetSyncedSides(FundIsSynced(fromFundId), FundIsSynced(toFundId));
        // Keep the bank provenance (dedupe) but clear the auto-filed badge — editing means the user reviewed it.
        transfer.SetBankLink(before?.BankExternalId, autoFiled: false);
        return SaveAsync();
    }

    public Task RemoveFundTransfer(Guid id)
    {
        Period.RemoveFundTransfer(id);
        return SaveAsync();
    }

    // --- Cross-account transfers (money out -> a contribution in another account) ---

    /// <summary>Other accounts the money could be sent to: the user's other accounts in the same currency.</summary>
    public IReadOnlyList<AccountSummaryDto> TransferableAccounts =>
        _summaries.Where(a => a.Id != CurrentAccountId && a.Currency == Currency).ToList();

    public string AccountName(Guid accountId) =>
        _summaries.FirstOrDefault(a => a.Id == accountId)?.Name ?? "another account";

    /// <summary>Download the current account as an .xlsx (one sheet per period). Returns the file bytes + name.</summary>
    public Task<(byte[] Bytes, string FileName)> ExportCurrentAccountAsync() => api.ExportAccountAsync(CurrentAccountId);

    public ExternalTransfer? FindExternalTransfer(Guid id) =>
        Period.ExternalTransfers.FirstOrDefault(t => t.Id == id);

    /// <summary>The (root) funds of another account, for picking a transfer/settlement destination fund. Uses the
    /// warm cache when available, else fetches and deserializes the snapshot (read-only — not cached here).</summary>
    public async Task<IReadOnlyList<Fund>> LoadAccountFundsAsync(Guid accountId)
    {
        if (accountId == Guid.Empty) return [];
        if (_cache.TryGetValue(accountId, out var hit)) return hit.Account.RootFunds.ToList();
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (string.IsNullOrEmpty(snapshot.Payload)) return [];
        return AccountSnapshotSerializer.Deserialize(snapshot.Payload).RootFunds.ToList();
    }

    /// <summary>
    /// Send money from one of this account's funds to another account. The source records a real outflow
    /// (lowering the fund and the closing balance); the destination's current period receives it as a deposit
    /// from the signed-in user into the chosen fund. Two snapshots are pushed — this account's, then the destination's.
    /// </summary>
    public async Task TransferToAccount(Guid destinationAccountId, Guid fromFundId, decimal amount, string? note, Guid destinationFundId = default)
    {
        if (amount <= 0m) return;
        var destination = _summaries.FirstOrDefault(a => a.Id == destinationAccountId)
            ?? throw new InvalidOperationException("Destination account not found.");
        if (destination.Currency != Currency)
            throw new InvalidOperationException("Both accounts must use the same currency.");

        // 1) Record the outflow on this account and push it. A synced source fund keeps its real balance, so the
        //    outflow is informational only (marker true) — the row still shows what happened.
        var outflow = Period.TransferOut(fromFundId, Money(amount), Today(), destinationAccountId, note, PriorSaved);
        outflow.SetFundSynced(FundIsSynced(fromFundId));
        await SaveAsync();

        // 2) Load the destination, deposit into its current period for the signed-in user, and push it. Each side
        //    carries its own marker based on its own fund, so only the unsynced side actually moves (no double count).
        var snapshot = await api.GetSnapshotAsync(destinationAccountId);
        if (string.IsNullOrEmpty(snapshot.Payload))
            throw new InvalidOperationException($"Open “{destination.Name}” once before transferring into it.");
        var destAccount = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        var destPeriod = destAccount.CurrentPeriod
            ?? throw new InvalidOperationException($"“{destination.Name}” has no open period to receive the transfer.");
        var destFundId = ResolveDestinationFund(destAccount, destinationFundId);
        var destDeposit = destPeriod.Deposit(auth.UserId, new Money(amount, destAccount.Currency), fundId: destFundId, date: Today());
        destDeposit.SetFundSynced(destAccount.Funds.FirstOrDefault(f => f.Id == destFundId)?.IsSynced ?? false);
        var payload = AccountSnapshotSerializer.Serialize(destAccount);
        await api.SaveSnapshotAsync(destinationAccountId, new SaveAccountRequest(payload, snapshot.Version));
        _cache.Remove(destinationAccountId); // its snapshot changed under us — drop so a switch refetches (feature 5)
    }

    /// <summary>The (root) funds and categories of another account, for the settle-onto-account pickers.</summary>
    public async Task<(IReadOnlyList<Fund> Funds, IReadOnlyList<Category> Categories)> LoadAccountStructureAsync(Guid accountId)
    {
        if (accountId == Guid.Empty) return ([], []);
        var account = _cache.TryGetValue(accountId, out var hit)
            ? hit.Account
            : await DeserializeAccountAsync(accountId);
        return account is null ? ([], []) : (account.RootFunds.ToList(), account.Categories.ToList());
    }

    private async Task<Account?> DeserializeAccountAsync(Guid accountId)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        return string.IsNullOrEmpty(snapshot.Payload) ? null : AccountSnapshotSerializer.Deserialize(snapshot.Payload);
    }

    /// <summary>
    /// Settle (or re-settle) a portion of an "on behalf of another account" expense onto another account: the
    /// chosen amount becomes that account's own expense (in the picked fund + category) and the source expense is
    /// reduced by that amount. The two are linked by a settlement id so edits/removals on either side keep the
    /// other in step. (Feature 1.)
    /// </summary>
    public async Task SettleExpenseToAccount(Guid sourceExpenseId, Guid destinationAccountId, Guid destinationFundId, Guid destinationCategoryId, decimal amount, string? note)
    {
        if (amount <= 0m) return;
        var source = Period.Expenses.FirstOrDefault(e => e.Id == sourceExpenseId)
            ?? throw new InvalidOperationException("Expense not found in this period.");
        var destination = _summaries.FirstOrDefault(a => a.Id == destinationAccountId)
            ?? throw new InvalidOperationException("Destination account not found.");
        if (destination.Currency != Currency)
            throw new InvalidOperationException("Both accounts must use the same currency.");
        if (Money(amount) > source.OriginalAmount)
            throw new InvalidOperationException($"You can settle at most {source.OriginalAmount}.");

        var settlementId = source.SettlementId ?? Guid.NewGuid();
        var settleNote = string.IsNullOrWhiteSpace(note) ? $"On behalf — from {Account.Name}" : note;
        var thisAccountId = CurrentAccountId;

        // 1) Create or update the linked destination expense.
        await MutateOtherAccountAsync(destinationAccountId, dest =>
        {
            var destPeriod = dest.CurrentPeriod
                ?? throw new InvalidOperationException($"“{destination.Name}” has no open period to receive the expense.");
            var categoryId = ResolveCategory(dest, destinationCategoryId);
            var fundId = ResolveDestinationFund(dest, destinationFundId);
            if (destPeriod.Expenses.FirstOrDefault(e => e.SettlementId == settlementId) is { } existing)
                destPeriod.RemoveExpense(existing.Id);
            destPeriod.AddExpense(new Expense(categoryId, new Money(amount, dest.Currency), Today(), auth.UserId, fundId,
                settleNote, settlementId: settlementId, settledFromAccountId: thisAccountId));
        });

        // 2) Reduce the source expense and tag the link.
        Period.SetSettlement(sourceExpenseId, settlementId, destinationAccountId, Money(amount));
        await SaveAsync();
    }

    /// <summary>Undo a settlement from the source side: remove the linked destination expense and restore the source's full amount.</summary>
    public async Task UnsettleExpense(Guid sourceExpenseId)
    {
        var source = Period.Expenses.FirstOrDefault(e => e.Id == sourceExpenseId);
        if (source is not { IsSettlementSource: true, SettledToAccountId: { } destAccount, SettlementId: { } sid }) return;
        await RemoveLinkedSettlementExpense(destAccount, sid);
        Period.SetSettlement(sourceExpenseId, sid, destAccount, Money(0));
        await SaveAsync();
    }

    /// <summary>Mirror a new settled amount onto the source expense in another account (0 un-settles it).</summary>
    private Task SyncSourceSettlementAmount(Guid sourceAccountId, Guid settlementId, decimal newAmount) =>
        MutateOtherAccountAsync(sourceAccountId, source =>
        {
            foreach (var p in source.Periods)
                if (p.Expenses.FirstOrDefault(e => e.SettlementId == settlementId && e.IsSettlementSource) is { } ex)
                {
                    p.SetSettlement(ex.Id, settlementId, CurrentAccountId, new Money(newAmount, source.Currency));
                    return;
                }
        });

    private Task RemoveLinkedSettlementExpense(Guid accountId, Guid settlementId) =>
        MutateOtherAccountAsync(accountId, account =>
        {
            foreach (var p in account.Periods)
                if (p.Expenses.FirstOrDefault(e => e.SettlementId == settlementId) is { } ex)
                {
                    p.RemoveExpense(ex.Id);
                    return;
                }
        });

    /// <summary>Load another account, apply a mutation, push it, and drop its cache entry so a switch refetches.</summary>
    private async Task MutateOtherAccountAsync(Guid accountId, Action<Account> mutate)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (string.IsNullOrEmpty(snapshot.Payload))
            throw new InvalidOperationException("Open that account once before linking to it.");
        var account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        mutate(account);
        var payload = AccountSnapshotSerializer.Serialize(account);
        await api.SaveSnapshotAsync(accountId, new SaveAccountRequest(payload, snapshot.Version));
        _cache.Remove(accountId);
    }

    private static Guid ResolveCategory(Account account, Guid requestedCategoryId)
    {
        if (requestedCategoryId != Guid.Empty && account.Categories.Any(c => c.Id == requestedCategoryId))
            return requestedCategoryId;
        return account.RootCategories.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("That account has no category to record the expense against.");
    }

    private static Guid ResolveDestinationFund(Account destAccount, Guid requestedFundId)
    {
        if (requestedFundId != Guid.Empty && destAccount.RootFunds.Any(f => f.Id == requestedFundId))
            return requestedFundId;
        // Prefer an unsynced fund — a synced fund's balance is bank-managed and shouldn't receive a manual deposit.
        return (destAccount.RootFunds.FirstOrDefault(f => !f.IsSynced) ?? destAccount.RootFunds.FirstOrDefault())?.Id ?? Guid.Empty;
    }

    public Task RemoveExternalTransfer(Guid id)
    {
        Period.RemoveExternalTransfer(id);
        return SaveAsync();
    }

    // --- Bank sync (Open Banking) -----------------------------------------
    // The server stages raw bank transactions; the client turns a confirmed one into a real domain expense
    // (the account body is client-owned) and then acks it so a later sync won't resurface it.

    public Task<BankSyncStatusDto> GetBankStatus() => api.GetBankStatusAsync(CurrentAccountId);

    public Task<List<BankInstitutionDto>> GetBankInstitutions(string country = "GB") =>
        api.GetBankInstitutionsAsync(CurrentAccountId, country);

    /// <summary>Begin linking: returns the bank's consent URL for the UI to navigate to.</summary>
    public async Task<string> StartBankLink(string institutionName, string country, string? logo = null)
    {
        var resp = await api.StartBankLinkAsync(CurrentAccountId, new StartBankLinkRequest(institutionName, country, logo));
        return resp.LinkUrl;
    }

    public Task SyncBank() => api.SyncBankAsync(CurrentAccountId);

    public Task<List<PendingBankTransactionDto>> GetPendingBankTransactions() =>
        api.GetPendingBankTransactionsAsync(CurrentAccountId);

    public Task<List<BankAccountDto>> GetBankAccounts() => api.GetBankAccountsAsync(CurrentAccountId);
    public Task SelectBankAccount(string bankAccountRef) => api.SelectBankAccountAsync(CurrentAccountId, bankAccountRef);

    /// <summary>Turn a staged bank transaction into an expense in the given category/fund, then mark it handled.</summary>
    public async Task ConfirmBankTransaction(string externalId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date, bool autoFiled = false)
    {
        await AddExpense(categoryId, amount, fundId, note, date, bankExternalId: externalId, autoFiled: autoFiled);
        await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
    }

    public Task DismissBankTransaction(string externalId) =>
        api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: false);

    // --- Bank de-duplication: pair incoming debits with existing un-linked entries (e.g. a manual log made while
    //     sync was down) so the user can replace one instead of double-counting. ---
    public record DuplicateSuggestion(Guid ExpenseId, Money Amount, Guid CategoryId, string Category, string CategoryIcon, string Fund, DateOnly Date);

    /// <summary>For the given pending bank debits, suggest an existing un-linked expense that looks like the same
    /// transaction (same amount, within a few days). Keyed by bank ExternalId. Read-only — suggests nothing binding.</summary>
    public IReadOnlyDictionary<string, DuplicateSuggestion> BankDuplicateSuggestions(IEnumerable<PendingBankTransactionDto> pendingDebits)
    {
        var entries = Period.Expenses
            .Where(e => e.BankExternalId is null && e.SourceSavingCategoryId is null)
            .Select(e => new BankDuplicateMatcher.Entry(e.Id, e.Amount.Amount, e.Date));
        var debits = pendingDebits.Where(t => t.Amount < 0m)
            .Select(t => new BankDuplicateMatcher.Pending(t.ExternalId, t.Amount, t.Date));

        var map = new Dictionary<string, DuplicateSuggestion>();
        foreach (var s in BankDuplicateMatcher.Suggest(debits, entries, windowDays: 4))
        {
            var e = Period.Expenses.First(x => x.Id == s.ExpenseId);
            map[s.ExternalId] = new DuplicateSuggestion(e.Id, e.Amount, e.CategoryId,
                CategoryName(e.CategoryId), CategoryIcon(e.CategoryId), FundName(e.FundId), e.Date);
        }
        return map;
    }

    /// <summary>The incoming bank debit is the same as a manual entry: drop the manual (often mis-filed) expense and
    /// confirm the bank row into that entry's category on the synced fund — one clean, bank-linked expense, no double.</summary>
    public async Task ReplaceWithBankTransaction(string externalId, Guid manualExpenseId, decimal amount, DateOnly date, string? note)
    {
        var exp = Period.Expenses.FirstOrDefault(e => e.Id == manualExpenseId);
        var categoryId = exp?.CategoryId ?? AllCategories.FirstOrDefault()?.Id ?? Guid.Empty;
        var fund = HasSyncedFund ? SyncedFundId : (exp?.FundId ?? Guid.Empty);
        if (exp is not null) Period.RemoveExpense(manualExpenseId);
        // ConfirmBankTransaction's AddExpense does the single SaveAsync (covering the removal too) then acks the row.
        await ConfirmBankTransaction(externalId, categoryId, amount, fund, note, date);
    }

    /// <summary>Turn a bank money-in into a movement into the synced fund: the destination is the synced fund
    /// (not credited — the real balance handles it); the <paramref name="source"/> ("fund:{id}" or
    /// "contributor:{id}") is where it came from and is the side that actually moves. Then acks the row.</summary>
    public async Task ConfirmBankMoneyIn(string externalId, string source, decimal amount, string? note, DateOnly date, bool autoFiled = false)
    {
        if (!HasSyncedFund) throw new InvalidOperationException("Mark a fund as synced to your bank first (Edit fund).");
        var parts = (source ?? "").Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var targetId))
            throw new InvalidOperationException("Pick where this money came from.");

        if (parts[0] == "fund")
        {
            if (targetId == SyncedFundId) throw new InvalidOperationException("The source can't be the synced fund itself.");
            var transfer = Period.TransferFunds(targetId, SyncedFundId, Money(amount), date, note);
            transfer.SetSyncedSides(FundIsSynced(targetId), toSynced: true);   // synced destination isn't credited
            transfer.SetBankLink(externalId, autoFiled);                       // bank provenance + auto-filed badge
        }
        else if (parts[0] == "contributor")
        {
            var deposit = Period.Deposit(targetId, Money(amount), fundId: SyncedFundId, date: date);
            deposit.SetFundSynced(true);   // counts as a contribution, but the synced fund isn't credited
        }
        else throw new InvalidOperationException("Unknown money-in source.");

        await SaveAsync();
        await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
    }

    /// <summary>Route a bank money-out (debit) as a <b>transfer</b> instead of an expense: from the synced fund to
    /// another fund in this account (<c>"fund:{id}"</c>) or to another account (<c>"acct:{accountId}:{fundId}"</c>).
    /// The synced source isn't debited (the real bank balance handles it); the destination side actually moves. Then
    /// acks the row so it drops from review.</summary>
    public async Task ConfirmBankMoneyOutAsTransfer(string externalId, string destination, decimal amount, string? note, DateOnly date)
    {
        if (!HasSyncedFund) throw new InvalidOperationException("Mark a fund as synced to your bank first (Edit fund).");
        if (amount <= 0m) throw new InvalidOperationException("Amount must be positive.");
        var parts = (destination ?? "").Split(':');

        if (parts.Length == 2 && parts[0] == "fund" && Guid.TryParse(parts[1], out var destFund))
        {
            if (destFund == SyncedFundId) throw new InvalidOperationException("The destination can't be the synced fund itself.");
            var transfer = Period.TransferFunds(SyncedFundId, destFund, Money(amount), date, note);
            transfer.SetSyncedSides(true, FundIsSynced(destFund));   // synced source isn't debited — the bank balance handles it
            transfer.SetBankLink(externalId, autoFiled: false);
            await SaveAsync();
            await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
        }
        else if (parts.Length == 3 && parts[0] == "acct"
                 && Guid.TryParse(parts[1], out var destAcct) && Guid.TryParse(parts[2], out var destAcctFund))
        {
            await TransferToAccount(destAcct, SyncedFundId, amount, note, destAcctFund);   // outflow here, deposit there
            await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
        }
        else throw new InvalidOperationException("Pick where this money went.");
    }

    // --- Consent (audit-logged) -------------------------------------------
    public Task RecordConsent(string scope, Guid? accountId) => api.RecordConsentAsync(scope, accountId, granted: true);
    public Task WithdrawConsent(string scope, Guid? accountId) => api.RecordConsentAsync(scope, accountId, granted: false);

    /// <summary>Drop the current account's bank connection so it can be linked again. Withdraws link + sync consent.</summary>
    public async Task DisconnectBank()
    {
        var id = CurrentAccountId;
        await api.DisconnectBankAsync(id);
        try { await api.RecordConsentAsync("bank_sync", id, granted: false); await api.RecordConsentAsync("bank_link", id, granted: false); }
        catch { /* audit best-effort */ }
    }

    /// <summary>Re-open handled bank rows in a date range (e.g. after a period is deleted) so they resurface.</summary>
    public Task ResetBankRange(DateOnly from, DateOnly to) => api.ResetBankRangeAsync(CurrentAccountId, from, to);

    public Task<List<BankMappingDto>> GetBankMappings() => api.GetBankMappingsAsync(CurrentAccountId);
    public Task SetBankMapping(string description, string kind, Guid targetId) =>
        api.SetBankMappingAsync(CurrentAccountId, description, kind, targetId);
    public Task RemoveBankMapping(string description) => api.RemoveBankMappingAsync(CurrentAccountId, description);

    /// <summary>Normalize a bank description to the same key the server matches rules against (MatchKeyOf).</summary>
    public static string BankMatchKey(string description) =>
        string.Join(' ', (description ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Words that carry no merchant identity — legal suffixes, payment noise, common stopwords — dropped when
    // reducing a description to its stem so store numbers and legal forms don't split one merchant into many rules.
    private static readonly HashSet<string> BankStemStop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "ltd", "ltda", "llc", "inc", "gmbh", "plc", "corp", "llp", "group",
        "ad", "ead", "ood", "eood", "jsc", "sa", "bv", "oy", "ab", "co", "com",
        "card", "payment", "pos", "purchase", "pmt", "trans", "www",
    };

    /// <summary>An aggressive merchant "stem": the first significant word of a description (letters only, 3+ chars, not
    /// a legal suffix / payment stopword), lowercased. Lets variants like "Fantastico 30" and "Fantastico Group Ltd"
    /// share one auto-map rule. Empty when there's no significant word (then only exact matching applies).</summary>
    public static string BankMatchStem(string description)
    {
        foreach (var raw in (description ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = new string(raw.Where(char.IsLetter).ToArray());
            if (tok.Length >= 3 && !BankStemStop.Contains(tok)) return tok;
        }
        return "";
    }

    public Task ReschedulePeriod(DateOnly from, DateOnly to)
    {
        Account.ReschedulePeriod(Period, from, to);
        return SaveAsync();
    }

    // Category CRUD
    public async Task<Guid> AddCategory(string name, Guid? parentId, string? icon = null, bool essential = false)
    {
        var category = Account.AddCategory(name, parentId, icon);
        if (essential) Account.SetCategoryEssential(category.Id, true);
        await SaveAsync();
        return category.Id;
    }

    /// <summary>Whether a category is flagged essential (rent/groceries/health...). Advisory only.</summary>
    public bool CategoryIsEssential(Guid categoryId) => Account.FindCategory(categoryId)?.IsEssential ?? false;

    /// <summary>Each <b>discretionary</b> (non-essential) category that carries its own budget and has spare this period
    /// (allocated − spent, where positive), biggest first. Advisory input for the "put spare toward a debt" nudge;
    /// moves nothing.</summary>
    public IReadOnlyList<(Guid Id, string Name, decimal Amount)> DiscretionaryLeftovers()
    {
        var list = new List<(Guid Id, string Name, decimal Amount)>();
        foreach (var c in AllCategories)
        {
            if (c.IsEssential || !HasBudget(c.Id)) continue;
            var cov = Coverage(c.Id);
            var left = cov.Allocated.Amount - cov.Spent.Amount;
            if (left > 0m) list.Add((c.Id, c.Name, left));
        }
        return list.OrderByDescending(x => x.Amount).ToList();
    }

    /// <summary>Best-guess "is this essential?" from the name, to pre-tick the flag on new categories (user can change it).</summary>
    private static readonly string[] EssentialWords =
        { "rent", "mortgage", "housing", "grocer", "food", "health", "medic", "pharmac", "doctor", "dental",
          "utilit", "electric", "water", "gas", "heating", "insurance", "transport", "fuel", "petrol", "commute",
          "childcare", "school", "tuition", "nursery", "loan", "debt", "council tax", "bills" };
    public static bool GuessEssential(string? name) =>
        !string.IsNullOrWhiteSpace(name) && EssentialWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    public Task RenameCategory(Guid categoryId, string name)
    {
        Account.RenameCategory(categoryId, name);
        return SaveAsync();
    }

    /// <summary>Rename a category and set its icon in one save.</summary>
    public Task EditCategory(Guid categoryId, string name, string? icon)
    {
        Account.RenameCategory(categoryId, name);
        Account.SetCategoryIcon(categoryId, icon);
        return SaveAsync();
    }

    /// <summary>Set a category's essential/discretionary flag (advisory only).</summary>
    public Task SetCategoryEssential(Guid categoryId, bool essential)
    {
        Account.SetCategoryEssential(categoryId, essential);
        return SaveAsync();
    }

    /// <summary>The icon to show for a category — its explicit choice, or one guessed from the name.</summary>
    public string CategoryIcon(Guid categoryId) => CategoryIcons.Effective(Account.FindCategory(categoryId));

    /// <summary>The category's explicitly-stored icon (null when none) — for pre-selecting the edit picker.</summary>
    public string? CategoryStoredIcon(Guid categoryId) => Account.FindCategory(categoryId)?.Icon;

    public Task RemoveCategory(Guid categoryId)
    {
        Account.RemoveCategory(categoryId);
        return SaveAsync();
    }

    // Budget CRUD
    public Task SaveBudget(Guid categoryId, decimal amount, decimal thresholdPercent, bool notifyEvery)
    {
        Period.SetBudget(categoryId, Money(amount), thresholdPercent / 100m, notifyEvery);
        return SaveAsync();
    }

    /// <summary>Reallocate spare budget toward a debt in one step: trim <paramref name="categoryId"/>'s budget to
    /// <paramref name="newBudget"/> and set <paramref name="amount"/> aside toward <paramref name="savingCategoryId"/>.
    /// Backs the "Move it to the loan" nudge action — one save, so the spare disappears and the earmark grows together.</summary>
    public Task ReallocateBudgetToSaving(Guid categoryId, decimal newBudget, decimal thresholdPercent, bool notifyEvery,
        Guid savingCategoryId, decimal amount)
    {
        Period.SetBudget(categoryId, Money(newBudget), thresholdPercent / 100m, notifyEvery);
        Period.AllocateToSavings(savingCategoryId, Money(amount), Today(), null, PriorSaved);
        return SaveAsync();
    }

    public Task RemoveBudget(Guid categoryId)
    {
        Period.RemoveBudget(categoryId);
        return SaveAsync();
    }

    /// <summary>Remove the latest period and make the previous one active again.</summary>
    public Task RemoveLatestPeriod()
    {
        Account.RemoveLatestPeriod();
        _selectedIndex = Account.Periods.Count - 1;
        return SaveAsync();
    }

    /// <summary>
    /// Start the next period. The caller passes each top-level fund's real current balance, which becomes the
    /// new period's opening balance. That carried money is immediately allocatable (opening balances count toward
    /// what you can budget/save), so there's no separate carryover entry — what you actually have is what you have.
    /// </summary>
    public Task StartNextPeriod(bool copyBudgets, IReadOnlyDictionary<Guid, decimal> realFundOpenings,
        bool adjustBudgets = false, decimal? syncedFundClosingBalance = null)
    {
        // Re-entrancy guard against a double-submit (e.g. double-click): the first call synchronously closes the
        // current period and opens the next one below, so a second call sees a current period that hasn't ended yet
        // (To is in the future) and bails here — no accidental extra period.
        if (!CanStartNextPeriod) return Task.CompletedTask;

        var previous = Account.CurrentPeriod!;
        previous.Close();

        var from = previous.To.AddDays(1);
        var to = from.AddMonths(1).AddDays(-1);
        var next = Account.StartPeriod(from, to, copyBudgets, adjustBudgets && copyBudgets);

        foreach (var f in Account.RootFunds)
        {
            // A synced fund isn't entered by hand — capture the live bank balance at rollover as an INFORMATIVE
            // opening: shown later as this period's "balance at close" for the closed period we just left, but kept
            // out of the real opening total (InitialTotal) so it doesn't shift the account's money-model figures.
            if (f.IsSynced)
            {
                if (syncedFundClosingBalance is { } bal) next.SetInitialBalance(f.Id, Money(bal), informative: true);
                continue;
            }
            var amount = Money(realFundOpenings.TryGetValue(f.Id, out var v) ? v : 0m);
            next.SetInitialBalance(f.Id, amount);
        }

        _selectedIndex = Account.Periods.Count - 1;
        return SaveAsync();
    }

    /// <summary>For the currently-viewed CLOSED period, the synced fund's balance captured at that period's rollover
    /// (= the following period's recorded informative opening). Null on the open period, or when no snapshot exists
    /// (e.g. a period closed before this was captured) — the caller then falls back to the live balance.</summary>
    public Money? SyncedFundClosingBalance(Guid fundId)
    {
        if (_selectedIndex >= Account.Periods.Count - 1) return null;   // open/latest period → use the live balance
        var successorOpening = Account.Periods[_selectedIndex + 1].InitialBalances.FirstOrDefault(b => b.FundId == fundId);
        return successorOpening is null || successorOpening.Amount.IsZero ? null : successorOpening.Amount;
    }

    // --- Invitations ------------------------------------------------------

    public IReadOnlyList<InvitationDto> PendingInvitations => _pendingInvitations;
    public int PendingInvitationCount => _pendingInvitations.Count;

    public async Task RefreshInvitationsAsync()
    {
        _pendingInvitations = await api.GetPendingInvitationsAsync();
        RaiseChanged();
    }

    public Task InviteToCurrentAccount(string username) => api.InviteAsync(CurrentAccountId, username);

    public async Task AcceptInvitation(Guid invitationId)
    {
        var accountId = await api.AcceptInvitationAsync(invitationId);
        await sync.SubscribeAsync(accountId);
        _summaries = await api.GetAccountsAsync();
        _accountIndex = Math.Max(0, _summaries.FindIndex(a => a.Id == accountId));
        await LoadSelectedAccountAsync();
        await RefreshInvitationsAsync();
        RaiseChanged();
    }

    public async Task DeclineInvitation(Guid invitationId)
    {
        await api.DeclineInvitationAsync(invitationId);
        await RefreshInvitationsAsync();
    }

    // --- Live sync handlers (fire on a background thread) ------------------

    private async void OnAccountChanged(AccountChangedEvent e)
    {
        if (e.ChangedByUserId == auth.UserId) return; // our own change is already applied locally + cached

        _cache.Remove(e.AccountId); // a contributor changed it — drop the stale entry (re-fetched on next view)

        if (_account is null || e.AccountId != _account.Id) return; // not the account in view: lazy refresh later
        try
        {
            var snapshot = await api.GetSnapshotAsync(e.AccountId);
            if (!string.IsNullOrEmpty(snapshot.Payload))
            {
                _version = snapshot.Version;
                _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
                ReconcileHeader(_account, _summaries[_accountIndex]);
                _cache[e.AccountId] = new CachedAccount(_account, _version);
                _selectedIndex = Math.Min(_selectedIndex, _account.Periods.Count - 1);
                RaiseChanged();
            }
        }
        catch { /* a transient reload failure shouldn't crash the UI */ }
    }

    /// <summary>On reconnect the hub's group memberships are gone and changes during the outage were missed, so
    /// drop the whole cache, re-join every account's channel, and refresh the one in view from the server.</summary>
    private async void OnReconnected()
    {
        try
        {
            _cache.Clear();
            await SubscribeAllAsync();
            if (_account is not null) { await LoadSelectedAccountAsync(forceRefresh: true); RaiseChanged(); }
        }
        catch { /* best effort */ }
    }

    /// <summary>Join the live channel for every account the user belongs to, so AccountChanged fires for all of
    /// them (and can invalidate their cache entries) — not just the one currently open.</summary>
    private async Task SubscribeAllAsync()
    {
        foreach (var s in _summaries)
        {
            try { await sync.SubscribeAsync(s.Id); } catch { /* best effort */ }
        }
    }

    private async void OnInvitationReceived(InvitationReceivedEvent e)
    {
        try { await RefreshInvitationsAsync(); } catch { /* best effort */ }
    }

    // --- Helpers ----------------------------------------------------------

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

    private static bool NameEquals(string existing, string candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    private Task SaveAsync()
    {
        RaiseChanged();
        return PushSnapshotAsync();
    }

    /// <summary>A fresh, usable account body: starter categories/buckets, default funds, and the current month's period.</summary>
    private static void SeedStarterBody(Account account)
    {
        foreach (var (name, icon) in new[] { ("Food", "🍽️"), ("Bills", "💡"), ("Transport", "🚗"), ("Other", "🏷️") })
            account.AddCategory(name, icon: icon);
        account.AddSavingCategory("General");
        foreach (var c in new[] { "Salary", "Other" })
            account.AddContributionCategory(c);
        account.AddDefaultFunds();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        account.StartPeriod(from, from.AddMonths(1).AddDays(-1));
    }
}
