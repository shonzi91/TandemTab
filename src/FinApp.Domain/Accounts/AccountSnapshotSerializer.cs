using System.Reflection;
using System.Text.Json;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Savings;

namespace FinApp.Domain.Accounts;

/// <summary>
/// Serializes a full <see cref="Account"/> aggregate to/from JSON, <b>preserving every entity id</b> so
/// the aggregate's internal references (category/fund/member/saving links) survive a round-trip. Used to
/// move a shared account between client and server as a single <c>AccountSnapshot.Payload</c> string —
/// opaque to the server, so it can later be swapped for an end-to-end-encrypted blob without API changes.
///
/// Entities are rebuilt through their normal constructors (so invariants hold for the simple fields) and a
/// tiny reflection helper restores the bits constructors don't take: the <see cref="Entity.Id"/>, a closed
/// period's status / carried-in amount, and the private child collections.
///
/// Lives in <c>FinApp.Domain</c> (Domain-only deps, no EF/SQLite): moving it here severs the last
/// <c>FinApp.Contracts → FinApp.Domain</c> reference (Path B, docs/DOMAIN-REMOVAL.md). Both the SQLite-backed
/// MAUI host and the SQLite-free Blazor WASM host can use it — it's a server/host concern, never used by a thin
/// client that binds DTOs instead of deserializing the aggregate.
/// </summary>
public static class AccountSnapshotSerializer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Serialize(Account account)
    {
        var node = new AccountNode(
            account.Id, account.Name, account.Currency, account.OwnerUserId,
            account.Members.Select(m => new MemberNode(m.Id, m.UserId, m.DisplayName)).ToList(),
            account.Funds.Select(f => new FundNode(f.Id, f.Name, f.ParentId, f.Note, f.Icon, f.IsSynced, f.IsArchived, f.Currency, f.Rate)).ToList(),
            account.Categories.Select(c => new CategoryNode(c.Id, c.Name, c.ParentId, c.Icon, c.IsEssential, c.IsArchived)).ToList(),
            account.SavingCategories.Select(s => new SavingCategoryNode(s.Id, s.Name, s.ParentId, s.GoalAmount, s.AlertThreshold, s.NotifyOnMilestone, s.InitialAmount, s.Icon, s.Kind, s.DebtBalance, s.DebtAnnualRatePercent, s.DebtInstallment, s.IsArchived, s.DebtOriginalBalance, s.PlannedContribution, s.InvestmentAnnualRatePercent, s.InvestmentTermYears, s.InvestmentCompoundsPerYear, s.FundId, s.Costs.Count == 0 ? null : s.Costs.ToList(), s.DebtBalanceAsOf, s.DebtInstallmentDay, s.DebtStartDate, s.DebtPaymentDriven, s.IsEmergencyFund, s.DebtResidual)).ToList(),
            account.Periods.Select(ToNode).ToList(),
            account.ContributionCategories.Select(c => new ContributionCategoryNode(c.Id, c.Name, c.Icon)).ToList(),
            account.SavingsRateTarget,
            account.AchievementsAnchor,
            account.AchievementLog.Count == 0 ? null : new Dictionary<string, DateOnly>(account.AchievementLog),
            account.RecurringItems.Count == 0 ? null : account.RecurringItems.Select(r => new RecurringItemNode(
                r.Id, r.Name, r.Kind, r.AmountMode, r.ExpectedAmount, r.DayOfMonth, r.CategoryId, r.FundId, r.Active, r.Icon, r.LastHandledPeriodFrom, r.AutoPost, r.CreatedOn, r.LinkedDebtBucketId, r.LastHandledWasSkip)).ToList(),
            account.OnboardingDismissed,
            account.Tags.Count == 0 ? null : account.Tags.Select(t => new TagNode(t.Id, t.Name, t.Icon, t.IsArchived, t.CategoryId, t.IsTripTag)).ToList(),
            account.RoundUpTo, account.RoundUpBucketId, account.HourlyRate,
            account.WorkingDaysPerMonth, account.WorkingHoursPerDay,
            account.Trips.Count == 0 ? null : account.Trips.Select(t => new TripNode(
                t.Id, t.Name, t.From, t.To, t.Destination, t.Icon, t.SavingCategoryId, t.Budget, t.SpendCurrency, t.Rate,
                t.CategoryId, t.FinishedOn, t.SavingsApplied, t.StartedOn)).ToList());
        return JsonSerializer.Serialize(node, Json);
    }

    /// <summary>
    /// Build an empty-bodied <see cref="Account"/> from server header data (id/name/currency/owner/members),
    /// for a freshly-created account that has no snapshot yet. The caller seeds the body and saves.
    /// </summary>
    public static Account CreateForHeader(Guid id, string name, string currency, Guid ownerUserId,
        IEnumerable<(Guid UserId, string DisplayName)> members)
    {
        var account = new Account(name, currency);
        SetId(account, id);
        SetAuto(account, nameof(Account.OwnerUserId), ownerUserId);
        SetField(account, "_members", members.Select(m => new AccountMember(m.UserId, m.DisplayName)).ToList());
        return account;
    }

    public static Account Deserialize(string payload)
    {
        var node = JsonSerializer.Deserialize<AccountNode>(payload, Json)
                   ?? throw new ArgumentException("Snapshot payload is empty.", nameof(payload));

        var account = new Account(node.Name, node.Currency);
        SetId(account, node.Id);
        SetAuto(account, nameof(Account.OwnerUserId), node.OwnerUserId);
        SetField(account, "_members", node.Members.Select(m => Build(new AccountMember(m.UserId, m.DisplayName), m.Id)).ToList());
        SetField(account, "_funds", node.Funds.Select(f =>
        {
            var fund = Build(new Fund(f.Name, f.ParentId), f.Id);
            fund.SetNote(f.Note);
            fund.SetIcon(f.Icon);
            fund.SetSynced(f.IsSynced);
            if (f.IsArchived) fund.SetArchived(true);
            fund.SetCurrency(f.Currency, f.Rate);
            return fund;
        }).ToList());
        SetField(account, "_categories", node.Categories.Select(c =>
        {
            var category = Build(new Category(c.Name, c.ParentId), c.Id);
            category.SetIcon(c.Icon);
            category.SetEssential(c.IsEssential);
            if (c.IsArchived) category.SetArchived(true);
            return category;
        }).ToList());
        SetField(account, "_tags", (node.Tags ?? []).Select(t =>
        {
            var tag = Build(new Tag(t.Name), t.Id);
            tag.SetIcon(t.Icon);
            if (t.IsArchived) tag.SetArchived(true);
            // Restored on the tag directly rather than through Account.SetTagCategory: categories are restored
            // separately and their order relative to tags isn't guaranteed, so a validating call could reject a
            // binding that is in fact sound. Removal already clears bindings, so a stale id can't arise here.
            tag.SetCategory(t.CategoryId);
            tag.SetTripTag(t.IsTripTag);
            return tag;
        }).ToList());
        // Restored directly rather than through AddTrip/SetTripSavingCategory for the same reason as a tag's
        // category binding: the validating calls check collections that may not be restored yet, and the name-clash
        // guard would reject a snapshot that is merely being reloaded.
        SetField(account, "_trips", (node.Trips ?? []).Select(t =>
        {
            var trip = Build(new Trip(t.Name, t.From, t.To), t.Id);
            trip.SetDestination(t.Destination);
            trip.SetIcon(t.Icon);
            trip.SetSavingCategory(t.SavingCategoryId);
            trip.SetBudget(t.Budget);
            trip.SetRate(t.SpendCurrency, t.Rate);
            trip.SetCategory(t.CategoryId);
            // Restored, not replayed: Finish(today) would pull the end date in against TODAY's date on every load,
            // and Start(today) would stamp a confirmation nobody gave.
            if (t.FinishedOn is { } finished) SetAuto(trip, nameof(Trip.FinishedOn), finished);
            if (t.StartedOn is { } started) SetAuto(trip, nameof(Trip.StartedOn), started);
            trip.AddSavingsApplied(t.SavingsApplied);
            return trip;
        }).ToList());
        SetField(account, "_savingCategories", node.SavingCategories.Select(ToEntity).ToList());
        SetField(account, "_contributionCategories",
            (node.ContributionCategories ?? []).Select(c =>
            {
                var cc = Build(new ContributionCategory(c.Name), c.Id);
                cc.SetIcon(c.Icon);
                return cc;
            }).ToList());
        SetField(account, "_periods", node.Periods.Select(p => ToEntity(p, node.Currency)).ToList());
        account.SetSavingsRateTarget(node.SavingsRateTarget);
        account.SetHourlyRate(node.HourlyRate);
        account.SetWorkingPattern(node.WorkingDaysPerMonth, node.WorkingHoursPerDay);
        if (node.AchievementsAnchor is { } anchor) account.SetAchievementsAnchor(anchor);
        if (node.AchievementLog is { } log)
            foreach (var (key, on) in log) account.RecordAchievement(key, on);
        if (node.OnboardingDismissed) account.DismissOnboarding();
        // F4: restored after the savings buckets, which ConfigureRoundUps validates against. Guarded rather than
        // trusted — a snapshot whose target bucket has gone must load with round-ups off, not fail to load at all.
        if (node.RoundUpTo > 0m && node.RoundUpBucketId is { } ruBucket && account.FindSavingCategory(ruBucket) is not null)
            account.ConfigureRoundUps(node.RoundUpTo, ruBucket);
        foreach (var r in node.Recurring ?? [])
        {
            var item = Build(new RecurringItem(r.Name, r.Kind, r.AmountMode, r.ExpectedAmount, r.DayOfMonth, r.CategoryId, r.FundId, r.Icon, r.AutoPost), r.Id);
            if (!r.Active) item.SetActive(false);
            if (r.LastHandledPeriodFrom is { } h) item.MarkHandled(h, r.LastHandledWasSkip);
            // Restored verbatim, including null: a legacy item has no creation date, and stamping today's would
            // suppress it for a period it should genuinely fire in.
            item.SetCreatedOn(r.CreatedOn);
            item.SetLinkedDebtBucket(r.LinkedDebtBucketId);
            account.AddRecurring(item);
        }
        CollapseMultiTags(account);
        return account;
    }

    // One tag per expense: collapse any legacy multi-tag expense to its most-used tag across the whole account
    // (ties → the first tag it lists). Runs on every load so old snapshots read as single-tag; the reduction
    // persists the next time the account is saved.
    private static void CollapseMultiTags(Account account)
    {
        var freq = new Dictionary<Guid, int>();
        foreach (var period in account.Periods)
            foreach (var expense in period.Expenses)
                foreach (var tag in expense.TagIds)
                    freq[tag] = freq.GetValueOrDefault(tag) + 1;
        foreach (var period in account.Periods)
            foreach (var expense in period.Expenses)
                if (expense.TagIds.Count > 1)
                    expense.SetTag(expense.TagIds.OrderByDescending(t => freq.GetValueOrDefault(t)).First());
    }

    // --- domain -> node ---------------------------------------------------

    private static PeriodNode ToNode(Period p) => new(
        p.Id, p.Currency, p.From, p.To, p.Status, p.CarriedIn.Amount,
        p.InitialBalances.Select(b => new InitialBalanceNode(b.Id, b.FundId, b.Amount.Amount, b.Informative)).ToList(),
        p.Contributions.Select(c => new ContributionNode(c.Id, c.MemberId, c.Paid.Amount, c.CategoryId, c.FundId, c.Date, c.FundSynced, c.AccountTransferId, c.FromAccountId)).ToList(),
        p.Budgets.Select(b => new BudgetNode(b.Id, b.CategoryId, b.Allocated.Amount, b.AlertThreshold, b.NotifyOnEveryExpense)).ToList(),
        p.Expenses.Select(e => new ExpenseNode(e.Id, e.CategoryId, e.Amount.Amount, e.Date, e.MemberId, e.FundId, e.Note, e.SourceSavingCategoryId, e.OnBehalfOfOtherAccount, e.SettlementId, e.SettledToAccountId, e.SettledFromAccountId, e.SettledAmount, e.FundSynced, e.BankExternalId, e.AutoFiled, e.TagIds.Count == 0 ? null : e.TagIds.ToList(), e.InstallmentGroupId, e.Part, e.DebtBucketId, e.TripId, e.ForeignAmount, e.ForeignCurrency)).ToList(),
        p.SavingAllocations.Select(a => new SavingAllocationNode(a.Id, a.SavingCategoryId, a.Amount.Amount, a.Date, a.Note, a.SourceExpenseId, a.BudgetCategoryId, a.TransferPairId, a.SourceExternalTransferId)).ToList(),
        p.FundTransfers.Select(t => new FundTransferNode(t.Id, t.FromFundId, t.ToFundId, t.Amount.Amount, t.Date, t.Note, t.FromSynced, t.ToSynced, t.BankExternalId, t.AutoFiled)).ToList(),
        p.ExternalTransfers.Select(t => new ExternalTransferNode(t.Id, t.FundId, t.Amount.Amount, t.Date, t.ToAccountId, t.Note, t.FundSynced, t.AccountTransferId)).ToList());

    // --- node -> domain ---------------------------------------------------

    private static SavingCategory ToEntity(SavingCategoryNode n)
    {
        var s = Build(new SavingCategory(n.Name, n.ParentId), n.Id);
        s.SetGoal(n.GoalAmount, n.AlertThreshold, n.NotifyOnMilestone);
        if (n.InitialAmount != 0m) s.SetInitialAmount(n.InitialAmount);
        s.SetIcon(n.Icon);
        // Legacy debt nodes have DebtOriginalBalance = 0 → ConfigureDebt back-fills it to the current balance
        // (progress baselines at "today"), so old snapshots don't divide by zero or show bogus progress.
        // The anchor is restored verbatim rather than passed here: loading a snapshot must never re-date a loan,
        // or every open would walk the schedule from today and the balance would stop moving.
        if (n.Kind == SavingKind.Debt)
        {
            s.ConfigureDebt(n.DebtBalance, n.DebtAnnualRatePercent, n.DebtInstallment, n.DebtOriginalBalance);
            s.SetDebtBalanceAsOf(n.DebtBalanceAsOf);
            s.SetDebtInstallmentDay(n.DebtInstallmentDay);
            s.SetDebtStartDate(n.DebtStartDate);
            // Restored verbatim, not via SetPaymentDriven: that method re-snapshots and re-anchors the balance, which
            // is right for a user flipping the mode and wrong for merely loading the account.
            s.RestorePaymentDriven(n.DebtPaymentDriven);
            s.SetDebtResidual(n.DebtResidual);
        }
        if (n.Kind == SavingKind.Investment) s.ConfigureInvestment(n.InvestmentAnnualRatePercent, n.InvestmentTermYears, n.InvestmentCompoundsPerYear);
        // Verbatim, and after the kind is known: SetEmergencyFund gates on kind, which would drop the flag here.
        s.RestoreEmergencyFund(n.IsEmergencyFund);
        if (n.PlannedContribution is { } pc) s.SetPlannedContribution(pc);
        s.SetFund(n.FundId);
        if (n.Costs is { Count: > 0 }) s.ReplaceCosts(n.Costs);
        // Costs are restored before the kind is settled, because an expenses fund is defined by them.
        if (n.Kind == SavingKind.Expenses) s.ConfigureExpensesFund();
        // Buckets that listed costs before the kind existed were saved as Common. One that has costs and no goal
        // was already a sinking fund in everything but name, so it adopts the kind now and stops being offered a
        // goal it never had. One that has BOTH is left alone — that's a genuine ambiguity, and the edit modal
        // flags it rather than this quietly picking a side.
        else if (n.Kind == SavingKind.Common && n.Costs is { Count: > 0 } && n.GoalAmount is not > 0m)
            s.ConfigureExpensesFund();
        if (n.IsArchived) s.SetArchived(true);
        return s;
    }

    private static Period ToEntity(PeriodNode n, string currency)
    {
        Money M(decimal v) => new(v, currency);
        var p = Build(new Period(n.Currency, n.From, n.To), n.Id);

        // Carryover now lives signed in CarriedIn. Older snapshots stored it as a CarryoverSource contribution —
        // fold that into CarriedIn and keep it out of the contributions list so it isn't counted twice.
        var legacyCarryover = n.Contributions.FirstOrDefault(c => c.MemberId == Period.CarryoverSource)?.Paid ?? 0m;
        var carriedIn = n.CarriedIn != 0m ? n.CarriedIn : legacyCarryover;
        if (carriedIn != 0m) SetAuto(p, nameof(Period.CarriedIn), M(carriedIn));
        if (n.Status == PeriodStatus.Closed) p.Close();

        SetField(p, "_initialBalances", n.InitialBalances.Select(b => Build(new InitialBalance(b.FundId, M(b.Amount), b.Informative), b.Id)).ToList());
        SetField(p, "_contributions", n.Contributions.Where(c => c.MemberId != Period.CarryoverSource).Select(c =>
        {
            var contribution = Build(new Contribution(c.MemberId, M(c.Paid), c.CategoryId, c.FundId, c.Date), c.Id);
            contribution.SetFundSynced(c.FundSynced);
            contribution.SetAccountTransferLink(c.AccountTransferId, c.FromAccountId);
            return contribution;
        }).ToList());
        SetField(p, "_budgets", n.Budgets.Select(b => Build(new Budget(b.CategoryId, M(b.Allocated), b.AlertThreshold, b.NotifyOnEveryExpense), b.Id)).ToList());
        SetField(p, "_expenses", n.Expenses.Select(e =>
        {
            var expense = Build(new Expense(e.CategoryId, M(e.Amount), e.Date, e.MemberId, e.FundId, e.Note, e.SourceSavingCategoryId, e.OnBehalfOfOtherAccount, e.SettlementId, e.SettledToAccountId, e.SettledFromAccountId, e.SettledAmount), e.Id);
            expense.SetInstallmentLink(e.InstallmentGroupId, e.Part, e.DebtBucketId);
            expense.SetFundSynced(e.FundSynced);
            expense.SetBankLink(e.BankExternalId, e.AutoFiled);
            if (e.TagIds is { Count: > 0 }) expense.SetTags(e.TagIds);
            expense.SetTrip(e.TripId);
            expense.SetForeign(e.ForeignAmount, e.ForeignCurrency);
            return expense;
        }).ToList());
        SetField(p, "_savingAllocations", n.SavingAllocations.Select(a =>
        {
            var alloc = Build(new SavingAllocation(a.SavingCategoryId, M(a.Amount), a.Date, a.Note, a.SourceExpenseId, a.BudgetCategoryId, a.TransferPairId), a.Id);
            if (a.SourceExternalTransferId is { } xid) alloc.MarkDisbursement(xid);
            return alloc;
        }).ToList());
        SetField(p, "_fundTransfers", n.FundTransfers.Select(t =>
        {
            var transfer = Build(new FundTransfer(t.FromFundId, t.ToFundId, M(t.Amount), t.Date, t.Note), t.Id);
            transfer.SetSyncedSides(t.FromSynced, t.ToSynced);
            transfer.SetBankLink(t.BankExternalId, t.AutoFiled);
            return transfer;
        }).ToList());
        SetField(p, "_externalTransfers", (n.ExternalTransfers ?? []).Select(t =>
        {
            var transfer = Build(new ExternalTransfer(t.FundId, M(t.Amount), t.Date, t.ToAccountId, t.Note), t.Id);
            transfer.SetFundSynced(t.FundSynced);
            transfer.SetAccountTransferLink(t.AccountTransferId);
            return transfer;
        }).ToList());
        return p;
    }

    // --- reflection helpers ----------------------------------------------

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static T Build<T>(T entity, Guid id) where T : Entity
    {
        SetId(entity, id);
        return entity;
    }

    private static void SetId(Entity entity, Guid id) => SetAuto(entity, nameof(Entity.Id), id);

    private static void SetAuto(object target, string propertyName, object? value) =>
        FindField(target.GetType(), $"<{propertyName}>k__BackingField").SetValue(target, value);

    private static void SetField(object target, string fieldName, object? value) =>
        FindField(target.GetType(), fieldName).SetValue(target, value);

    private static FieldInfo FindField(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
            if (type.GetField(name, Flags) is { } field)
                return field;
        throw new InvalidOperationException($"Field '{name}' was not found.");
    }

    // --- JSON node shapes (flat, decimals carry the account currency) -----

    private record AccountNode(Guid Id, string Name, string Currency, Guid OwnerUserId,
        List<MemberNode> Members, List<FundNode> Funds, List<CategoryNode> Categories,
        List<SavingCategoryNode> SavingCategories, List<PeriodNode> Periods,
        List<ContributionCategoryNode>? ContributionCategories = null,
        decimal SavingsRateTarget = 0.20m,
        DateOnly? AchievementsAnchor = null,
        Dictionary<string, DateOnly>? AchievementLog = null,
        List<RecurringItemNode>? Recurring = null,
        bool OnboardingDismissed = false,
        List<TagNode>? Tags = null,
        // F4: zero on every node written before round-ups existed → off, which is also the default for a new account.
        decimal RoundUpTo = 0m, Guid? RoundUpBucketId = null,
        // Null on every node written before the time-cost feature, and null is exactly "not set" — nothing to backfill.
        decimal? HourlyRate = null, int? WorkingDaysPerMonth = null, decimal? WorkingHoursPerDay = null,
        // Null on every node written before trips existed — i.e. an account that has never travelled, which is
        // also the state of a brand-new account, so legacy snapshots need no back-fill.
        List<TripNode>? Trips = null);

    // Dates are the trip's own, never a filter over expenses — see Trip for why membership is by link.
    // CategoryId is a TRAILING optional, like every other body-data addition: a snapshot written before it existed
    // deserializes with null, which is exactly "file per label" — the behaviour those trips already had.
    // FinishedOn and SavingsApplied are trailing optionals like everything else here: a snapshot written before them
    // deserializes as "never declared over" and "nothing released", which is exactly what those trips were.
    private record TripNode(Guid Id, string Name, DateOnly From, DateOnly To, string? Destination = null,
        string? Icon = null, Guid? SavingCategoryId = null, decimal? Budget = null,
        string? SpendCurrency = null, decimal? Rate = null, Guid? CategoryId = null,
        DateOnly? FinishedOn = null, decimal SavingsApplied = 0m,
        // ⚠️ StartedOn is the one trailing optional whose default CHANGES behaviour for existing data: a trip written
        // before opt-in start has no confirmation, so a trip that is running right now reads as "awaiting start" on
        // the first load after this ships. That is a one-tap prompt, not lost data — and the alternative (defaulting
        // it to "started") would silently re-introduce the automatic trip mode this exists to remove.
        DateOnly? StartedOn = null);

    private record RecurringItemNode(Guid Id, string Name, RecurringKind Kind, RecurringAmountMode AmountMode,
        decimal ExpectedAmount, int DayOfMonth, Guid CategoryId, Guid FundId, bool Active, string? Icon, DateOnly? LastHandledPeriodFrom,
        bool AutoPost = false, DateOnly? CreatedOn = null,
        // R2: the debt bucket this bill services, when it's a loan installment. Null on every pre-R2 item.
        Guid? LinkedDebtBucketId = null,
        // False on every item stored before skips were told apart from postings — see RecurringItem.LastHandledWasSkip
        // for why "posted" is the safe reading of a legacy handled item.
        bool LastHandledWasSkip = false);

    private record MemberNode(Guid Id, Guid UserId, string DisplayName);
    private record ContributionCategoryNode(Guid Id, string Name, string? Icon = null);
    // Currency/Rate are trailing optionals like every other body-data addition: a snapshot written before the rate
    // moved off the trip deserializes with null, which is exactly "this fund holds account-currency money".
    private record FundNode(Guid Id, string Name, Guid? ParentId, string? Note = null, string? Icon = null, bool IsSynced = false, bool IsArchived = false,
        string? Currency = null, decimal? Rate = null);
    private record CategoryNode(Guid Id, string Name, Guid? ParentId, string? Icon = null, bool IsEssential = false, bool IsArchived = false);
    // F2: CategoryId is null on every node written before the binding existed — i.e. the tag files nothing, which is
    // exactly what an unbound tag means, so legacy snapshots need no back-fill.
    // IsTripTag is false on every node written before trips existed — i.e. an ordinary tag, which is what they all were.
    private record TagNode(Guid Id, string Name, string? Icon = null, bool IsArchived = false, Guid? CategoryId = null,
        bool IsTripTag = false);
    private record SavingCategoryNode(Guid Id, string Name, Guid? ParentId, decimal? GoalAmount, decimal AlertThreshold, bool NotifyOnMilestone, decimal InitialAmount, string? Icon = null,
        SavingKind Kind = SavingKind.Common, decimal DebtBalance = 0m, decimal DebtAnnualRatePercent = 0m, decimal DebtInstallment = 0m, bool IsArchived = false,
        decimal DebtOriginalBalance = 0m, decimal? PlannedContribution = null,
        decimal InvestmentAnnualRatePercent = 0m, decimal InvestmentTermYears = 0m, int InvestmentCompoundsPerYear = 12,
        Guid? FundId = null, IReadOnlyList<PlannedCost>? Costs = null,
        // Null on legacy nodes → the bucket keeps its stored balance as-is, exactly as before the schedule existed.
        DateOnly? DebtBalanceAsOf = null,
        // R1 informative-debt fields; null on nodes written before they existed.
        int? DebtInstallmentDay = null, DateOnly? DebtStartDate = null,
        // R2: false on legacy nodes → every existing debt stays schedule-driven, which is what it has always been.
        bool DebtPaymentDriven = false,
        // False on legacy nodes — no account had an emergency fund before this existed.
        bool IsEmergencyFund = false,
        // A lease's residual / balloon. Zero on legacy nodes → amortise to zero, exactly as before.
        decimal DebtResidual = 0m);

    private record PeriodNode(Guid Id, string Currency, DateOnly From, DateOnly To, PeriodStatus Status, decimal CarriedIn,
        List<InitialBalanceNode> InitialBalances, List<ContributionNode> Contributions, List<BudgetNode> Budgets,
        List<ExpenseNode> Expenses, List<SavingAllocationNode> SavingAllocations, List<FundTransferNode> FundTransfers,
        List<ExternalTransferNode>? ExternalTransfers = null);

    private record InitialBalanceNode(Guid Id, Guid FundId, decimal Amount, bool Informative);
    private record ContributionNode(Guid Id, Guid MemberId, decimal Paid,
        Guid CategoryId = default, Guid FundId = default, DateOnly Date = default, bool FundSynced = false,
        // R2: the receiving half of an account-to-account transfer. Null on every node written before this and on
        // every ordinary deposit — a missing JSON property lands on the default, so old snapshots read unchanged.
        Guid? AccountTransferId = null, Guid? FromAccountId = null);
    private record BudgetNode(Guid Id, Guid CategoryId, decimal Allocated, decimal AlertThreshold, bool NotifyOnEveryExpense);
    private record ExpenseNode(Guid Id, Guid CategoryId, decimal Amount, DateOnly Date, Guid MemberId, Guid FundId, string? Note, Guid? SourceSavingCategoryId, bool OnBehalfOfOtherAccount = false,
        Guid? SettlementId = null, Guid? SettledToAccountId = null, Guid? SettledFromAccountId = null, decimal SettledAmount = 0m, bool FundSynced = false, string? BankExternalId = null, bool AutoFiled = false,
        // Null on legacy nodes (pre-tags) → the expense simply has no tags.
        IReadOnlyList<Guid>? TagIds = null,
        // R2 installment-split fields; null on every node written before them, i.e. every ordinary expense.
        Guid? InstallmentGroupId = null, InstallmentPart? Part = null, Guid? DebtBucketId = null,
        // The trip this expense belongs to. Null on every node written before trips and on every expense logged at
        // home — and note it is stored independently of Date, which is what carries a pre-paid booking into a trip
        // that hasn't happened yet.
        Guid? TripId = null,
        // What was typed before conversion, when the expense was paid from a foreign-cash wallet. Null on every
        // ordinary expense and on every node written before wallets carried a currency — which reads as "there is
        // no second figure to show", exactly right for those.
        decimal? ForeignAmount = null, string? ForeignCurrency = null);
    private record SavingAllocationNode(Guid Id, Guid SavingCategoryId, decimal Amount, DateOnly Date, string? Note, Guid? SourceExpenseId, Guid? BudgetCategoryId = null, Guid? TransferPairId = null, Guid? SourceExternalTransferId = null);
    private record FundTransferNode(Guid Id, Guid FromFundId, Guid ToFundId, decimal Amount, DateOnly Date, string? Note, bool FromSynced = false, bool ToSynced = false, string? BankExternalId = null, bool AutoFiled = false);
    private record ExternalTransferNode(Guid Id, Guid FundId, decimal Amount, DateOnly Date, Guid? ToAccountId, string? Note, bool FundSynced = false,
        // R2: the shared id of the deposit this created in the other account. Null on legacy transfers, which stay
        // one-sided on purpose — see ExternalTransfer.AccountTransferId.
        Guid? AccountTransferId = null);
}
