using System.Reflection;
using System.Text.Json;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Savings;

namespace FinApp.Contracts;

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
/// Lives in <c>FinApp.Contracts</c> (Domain-only deps, no EF/SQLite) so both the SQLite-backed MAUI host and
/// the SQLite-free Blazor WASM host can use it.
/// </summary>
public static class AccountSnapshotSerializer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Serialize(Account account)
    {
        var node = new AccountNode(
            account.Id, account.Name, account.Currency, account.OwnerUserId,
            account.Members.Select(m => new MemberNode(m.Id, m.UserId, m.DisplayName)).ToList(),
            account.Funds.Select(f => new FundNode(f.Id, f.Name, f.ParentId, f.Note, f.Icon, f.IsSynced)).ToList(),
            account.Categories.Select(c => new CategoryNode(c.Id, c.Name, c.ParentId, c.Icon, c.IsEssential)).ToList(),
            account.SavingCategories.Select(s => new SavingCategoryNode(s.Id, s.Name, s.ParentId, s.GoalAmount, s.AlertThreshold, s.NotifyOnMilestone, s.InitialAmount, s.Icon, s.Kind, s.DebtBalance, s.DebtAnnualRatePercent, s.DebtInstallment, s.IsArchived, s.DebtOriginalBalance, s.PlannedContribution, s.InvestmentAnnualRatePercent, s.InvestmentTermYears, s.InvestmentCompoundsPerYear)).ToList(),
            account.Periods.Select(ToNode).ToList(),
            account.ContributionCategories.Select(c => new ContributionCategoryNode(c.Id, c.Name, c.Icon)).ToList(),
            account.SavingsRateTarget,
            account.AchievementsAnchor,
            account.AchievementLog.Count == 0 ? null : new Dictionary<string, DateOnly>(account.AchievementLog),
            account.RecurringItems.Count == 0 ? null : account.RecurringItems.Select(r => new RecurringItemNode(
                r.Id, r.Name, r.Kind, r.AmountMode, r.ExpectedAmount, r.DayOfMonth, r.CategoryId, r.FundId, r.Active, r.Icon, r.LastHandledPeriodFrom, r.AutoPost)).ToList());
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
            return fund;
        }).ToList());
        SetField(account, "_categories", node.Categories.Select(c =>
        {
            var category = Build(new Category(c.Name, c.ParentId), c.Id);
            category.SetIcon(c.Icon);
            category.SetEssential(c.IsEssential);
            return category;
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
        if (node.AchievementsAnchor is { } anchor) account.SetAchievementsAnchor(anchor);
        if (node.AchievementLog is { } log)
            foreach (var (key, on) in log) account.RecordAchievement(key, on);
        foreach (var r in node.Recurring ?? [])
        {
            var item = Build(new RecurringItem(r.Name, r.Kind, r.AmountMode, r.ExpectedAmount, r.DayOfMonth, r.CategoryId, r.FundId, r.Icon, r.AutoPost), r.Id);
            if (!r.Active) item.SetActive(false);
            if (r.LastHandledPeriodFrom is { } h) item.MarkHandled(h);
            account.AddRecurring(item);
        }
        return account;
    }

    // --- domain -> node ---------------------------------------------------

    private static PeriodNode ToNode(Period p) => new(
        p.Id, p.Currency, p.From, p.To, p.Status, p.CarriedIn.Amount,
        p.InitialBalances.Select(b => new InitialBalanceNode(b.Id, b.FundId, b.Amount.Amount, b.Informative)).ToList(),
        p.Contributions.Select(c => new ContributionNode(c.Id, c.MemberId, c.Paid.Amount, c.CategoryId, c.FundId, c.Date, c.FundSynced)).ToList(),
        p.Budgets.Select(b => new BudgetNode(b.Id, b.CategoryId, b.Allocated.Amount, b.AlertThreshold, b.NotifyOnEveryExpense)).ToList(),
        p.Expenses.Select(e => new ExpenseNode(e.Id, e.CategoryId, e.Amount.Amount, e.Date, e.MemberId, e.FundId, e.Note, e.SourceSavingCategoryId, e.OnBehalfOfOtherAccount, e.SettlementId, e.SettledToAccountId, e.SettledFromAccountId, e.SettledAmount, e.FundSynced, e.BankExternalId, e.AutoFiled)).ToList(),
        p.SavingAllocations.Select(a => new SavingAllocationNode(a.Id, a.SavingCategoryId, a.Amount.Amount, a.Date, a.Note, a.SourceExpenseId, a.BudgetCategoryId, a.TransferPairId, a.SourceExternalTransferId)).ToList(),
        p.FundTransfers.Select(t => new FundTransferNode(t.Id, t.FromFundId, t.ToFundId, t.Amount.Amount, t.Date, t.Note, t.FromSynced, t.ToSynced, t.BankExternalId, t.AutoFiled)).ToList(),
        p.ExternalTransfers.Select(t => new ExternalTransferNode(t.Id, t.FundId, t.Amount.Amount, t.Date, t.ToAccountId, t.Note, t.FundSynced)).ToList());

    // --- node -> domain ---------------------------------------------------

    private static SavingCategory ToEntity(SavingCategoryNode n)
    {
        var s = Build(new SavingCategory(n.Name, n.ParentId), n.Id);
        s.SetGoal(n.GoalAmount, n.AlertThreshold, n.NotifyOnMilestone);
        if (n.InitialAmount != 0m) s.SetInitialAmount(n.InitialAmount);
        s.SetIcon(n.Icon);
        // Legacy debt nodes have DebtOriginalBalance = 0 → ConfigureDebt back-fills it to the current balance
        // (progress baselines at "today"), so old snapshots don't divide by zero or show bogus progress.
        if (n.Kind == SavingKind.Debt) s.ConfigureDebt(n.DebtBalance, n.DebtAnnualRatePercent, n.DebtInstallment, n.DebtOriginalBalance);
        if (n.Kind == SavingKind.Investment) s.ConfigureInvestment(n.InvestmentAnnualRatePercent, n.InvestmentTermYears, n.InvestmentCompoundsPerYear);
        if (n.PlannedContribution is { } pc) s.SetPlannedContribution(pc);
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
            return contribution;
        }).ToList());
        SetField(p, "_budgets", n.Budgets.Select(b => Build(new Budget(b.CategoryId, M(b.Allocated), b.AlertThreshold, b.NotifyOnEveryExpense), b.Id)).ToList());
        SetField(p, "_expenses", n.Expenses.Select(e =>
        {
            var expense = Build(new Expense(e.CategoryId, M(e.Amount), e.Date, e.MemberId, e.FundId, e.Note, e.SourceSavingCategoryId, e.OnBehalfOfOtherAccount, e.SettlementId, e.SettledToAccountId, e.SettledFromAccountId, e.SettledAmount), e.Id);
            expense.SetFundSynced(e.FundSynced);
            expense.SetBankLink(e.BankExternalId, e.AutoFiled);
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
        List<RecurringItemNode>? Recurring = null);

    private record RecurringItemNode(Guid Id, string Name, RecurringKind Kind, RecurringAmountMode AmountMode,
        decimal ExpectedAmount, int DayOfMonth, Guid CategoryId, Guid FundId, bool Active, string? Icon, DateOnly? LastHandledPeriodFrom,
        bool AutoPost = false);

    private record MemberNode(Guid Id, Guid UserId, string DisplayName);
    private record ContributionCategoryNode(Guid Id, string Name, string? Icon = null);
    private record FundNode(Guid Id, string Name, Guid? ParentId, string? Note = null, string? Icon = null, bool IsSynced = false);
    private record CategoryNode(Guid Id, string Name, Guid? ParentId, string? Icon = null, bool IsEssential = false);
    private record SavingCategoryNode(Guid Id, string Name, Guid? ParentId, decimal? GoalAmount, decimal AlertThreshold, bool NotifyOnMilestone, decimal InitialAmount, string? Icon = null,
        SavingKind Kind = SavingKind.Common, decimal DebtBalance = 0m, decimal DebtAnnualRatePercent = 0m, decimal DebtInstallment = 0m, bool IsArchived = false,
        decimal DebtOriginalBalance = 0m, decimal? PlannedContribution = null,
        decimal InvestmentAnnualRatePercent = 0m, decimal InvestmentTermYears = 0m, int InvestmentCompoundsPerYear = 12);

    private record PeriodNode(Guid Id, string Currency, DateOnly From, DateOnly To, PeriodStatus Status, decimal CarriedIn,
        List<InitialBalanceNode> InitialBalances, List<ContributionNode> Contributions, List<BudgetNode> Budgets,
        List<ExpenseNode> Expenses, List<SavingAllocationNode> SavingAllocations, List<FundTransferNode> FundTransfers,
        List<ExternalTransferNode>? ExternalTransfers = null);

    private record InitialBalanceNode(Guid Id, Guid FundId, decimal Amount, bool Informative);
    private record ContributionNode(Guid Id, Guid MemberId, decimal Paid,
        Guid CategoryId = default, Guid FundId = default, DateOnly Date = default, bool FundSynced = false);
    private record BudgetNode(Guid Id, Guid CategoryId, decimal Allocated, decimal AlertThreshold, bool NotifyOnEveryExpense);
    private record ExpenseNode(Guid Id, Guid CategoryId, decimal Amount, DateOnly Date, Guid MemberId, Guid FundId, string? Note, Guid? SourceSavingCategoryId, bool OnBehalfOfOtherAccount = false,
        Guid? SettlementId = null, Guid? SettledToAccountId = null, Guid? SettledFromAccountId = null, decimal SettledAmount = 0m, bool FundSynced = false, string? BankExternalId = null, bool AutoFiled = false);
    private record SavingAllocationNode(Guid Id, Guid SavingCategoryId, decimal Amount, DateOnly Date, string? Note, Guid? SourceExpenseId, Guid? BudgetCategoryId = null, Guid? TransferPairId = null, Guid? SourceExternalTransferId = null);
    private record FundTransferNode(Guid Id, Guid FromFundId, Guid ToFundId, decimal Amount, DateOnly Date, string? Note, bool FromSynced = false, bool ToSynced = false, string? BankExternalId = null, bool AutoFiled = false);
    private record ExternalTransferNode(Guid Id, Guid FundId, decimal Amount, DateOnly Date, Guid? ToAccountId, string? Note, bool FundSynced = false);
}
