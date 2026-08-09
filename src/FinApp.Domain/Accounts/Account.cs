using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Savings;

namespace FinApp.Domain.Accounts;

/// <summary>
/// A first-level account (Personal, Shared, Family...). The aggregate root: it owns members,
/// the shared category and savings-category trees, and the ordered list of periods.
/// </summary>
public sealed class Account : Entity
{
    private readonly List<AccountMember> _members = [];
    private readonly List<Category> _categories = [];
    private readonly List<Tag> _tags = [];
    private readonly List<SavingCategory> _savingCategories = [];
    private readonly List<ContributionCategory> _contributionCategories = [];
    private readonly List<Fund> _funds = [];
    private readonly List<Period> _periods = [];
    private readonly List<RecurringItem> _recurring = [];

    public string Name { get; private set; }
    public string Currency { get; }

    /// <summary>
    /// The account's target savings rate (set-aside ÷ contributions), as a fraction 0..1. Drives the Insights
    /// tab's savings gauge and health score. Defaults to 0.20 (20%). Body data — travels in the account snapshot,
    /// not the relational header.
    /// </summary>
    public decimal SavingsRateTarget { get; private set; } = 0.20m;

    /// <summary>
    /// Achievements start counting from this date — set once to the current period's start the first time the
    /// feature runs — so an existing account doesn't retroactively unlock its whole history, and back-/forward-dating
    /// periods can't farm milestones. Null until first anchored. Body data — travels in the snapshot.
    /// </summary>
    public DateOnly? AchievementsAnchor { get; private set; }

    private readonly Dictionary<string, DateOnly> _achievements = new();
    /// <summary>When each achievement was first earned (stable key → date). Body data — travels in the snapshot.</summary>
    public IReadOnlyDictionary<string, DateOnly> AchievementLog => _achievements;

    /// <summary>True once the user closes the "Getting started" checklist on Home, so it doesn't reappear even
    /// before they've logged anything. Body data — travels in the snapshot.</summary>
    public bool OnboardingDismissed { get; private set; }
    public void DismissOnboarding() => OnboardingDismissed = true;

    /// <summary>
    /// F4 round-ups: the step each logged expense is rounded up to before the difference is set aside — 1 or 5 in the
    /// account currency. <b>Zero means off, which is the default</b>: an automatic money movement nobody switched on
    /// would be indistinguishable from a bug.
    /// </summary>
    public decimal RoundUpTo { get; private set; }

    /// <summary>The savings bucket F4 round-ups sweep into. Null (with <see cref="RoundUpTo"/> zero) when off.</summary>
    public Guid? RoundUpBucketId { get; private set; }

    /// <summary>True when round-ups are configured and pointing at a bucket that still exists and is not archived.
    /// Checked at sweep time as well as here — a bucket can be archived long after the switch was flipped.</summary>
    public bool RoundUpsOn =>
        RoundUpTo > 0m && RoundUpBucketId is { } id && FindSavingCategory(id) is { IsArchived: false };

    /// <summary>
    /// Turn round-ups on (a step of 1 or 5 plus the destination bucket) or off (a step of 0). The step is restricted
    /// rather than free-form on purpose: an arbitrary step turns a "spare change" habit into a second, invisible
    /// budgeting lever, and every value would have to be explained on the ledger line it produces.
    /// </summary>
    public void ConfigureRoundUps(decimal roundUpTo, Guid? bucketId)
    {
        if (roundUpTo is not (0m or 1m or 5m))
            throw new ArgumentOutOfRangeException(nameof(roundUpTo), "Round-ups step to 1 or 5, or 0 to turn them off.");
        if (roundUpTo > 0m)
        {
            if (bucketId is not { } id || FindSavingCategory(id) is null)
                throw new InvalidOperationException("Choose a savings bucket for round-ups to go into.");
            RoundUpTo = roundUpTo;
            RoundUpBucketId = id;
        }
        else
        {
            RoundUpTo = 0m;
            RoundUpBucketId = null;
        }
    }

    /// <summary>
    /// What an expense of <paramref name="amount"/> would sweep into savings — the distance up to the next multiple of
    /// <see cref="RoundUpTo"/>, or zero when round-ups are off, the amount is not positive, or it already lands exactly
    /// on the step. Pure, so the UI can preview the figure without performing the sweep.
    /// </summary>
    public decimal RoundUpFor(decimal amount) => RoundUpsOn ? RoundUpForStep(amount, RoundUpTo) : 0m;

    /// <summary>The change an expense of <paramref name="amount"/> rounds up to the next multiple of
    /// <paramref name="step"/> (1 or 5), <b>regardless of whether round-ups are switched on</b> — so a
    /// "what round-ups WOULD have set aside" teaser can be computed for an account that hasn't turned them on.
    /// Zero for a non-positive amount, an invalid step, or an amount already sitting on the step.</summary>
    public static decimal RoundUpForStep(decimal amount, decimal step)
    {
        if (amount <= 0m || step is not (1m or 5m)) return 0m;
        var rounded = Math.Ceiling(amount / step) * step;
        return decimal.Round(rounded - amount, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The user who created this account. Owner-only actions (rename, delete) check this; everything
    /// inside the account may be changed by any contributor. <see cref="Guid.Empty"/> for accounts
    /// created without a signed-in user (e.g. unit tests).
    /// </summary>
    public Guid OwnerUserId { get; private set; }

    public IReadOnlyList<AccountMember> Members => _members;

    /// <summary>All categories, flat. Use <see cref="RootCategories"/> / <see cref="ChildrenOfCategory"/> for the tree.</summary>
    public IReadOnlyList<Category> Categories => _categories;
    public IReadOnlyList<Tag> Tags => _tags;

    /// <summary>All savings buckets, flat.</summary>
    public IReadOnlyList<SavingCategory> SavingCategories => _savingCategories;

    /// <summary>Account-level contribution categories (Salary, Vouchers…), referenced by id from deposits.</summary>
    public IReadOnlyList<ContributionCategory> ContributionCategories => _contributionCategories;

    /// <summary>All funds (places money lives), flat. Referenced by id from expenses, opening balances and transfers.</summary>
    public IReadOnlyList<Fund> Funds => _funds;

    public IEnumerable<Category> RootCategories => _categories.Where(c => c.IsRoot);
    public IEnumerable<Category> ChildrenOfCategory(Guid parentId) => _categories.Where(c => c.ParentId == parentId);
    public IEnumerable<SavingCategory> RootSavingCategories => _savingCategories.Where(c => c.IsRoot);

    /// <summary>Periods ordered oldest → newest.</summary>
    public IReadOnlyList<Period> Periods => _periods;

    /// <summary>Recurring expectations (bills, salary, standing transfers). Body data — travels in the snapshot.</summary>
    public IReadOnlyList<RecurringItem> RecurringItems => _recurring;

    public RecurringItem AddRecurring(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _recurring.Add(item);
        return item;
    }

    public RecurringItem? FindRecurring(Guid id) => _recurring.FirstOrDefault(r => r.Id == id);

    public void RemoveRecurring(Guid id)
    {
        if (_recurring.FirstOrDefault(r => r.Id == id) is { } item) _recurring.Remove(item);
    }

    public Account(string name, string currency)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Account name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        Name = name.Trim();
        Currency = currency.ToUpperInvariant();
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Account name is required.", nameof(name));
        Name = name.Trim();
    }

    /// <summary>Set the target savings rate as a fraction 0..1 (e.g. 0.20 for 20%).</summary>
    public void SetSavingsRateTarget(decimal target)
    {
        if (target < 0m || target > 1m)
            throw new ArgumentOutOfRangeException(nameof(target), "Savings target must be between 0% and 100%.");
        SavingsRateTarget = target;
    }

    /// <summary>Anchor achievement tracking to <paramref name="onDate"/> the first time only (idempotent).</summary>
    public void SetAchievementsAnchor(DateOnly onDate) => AchievementsAnchor ??= onDate;

    /// <summary>Record that an achievement was first earned on <paramref name="onDate"/> — first write wins.
    /// Idempotent, so re-detecting an already-earned achievement never moves its date.</summary>
    public void RecordAchievement(string key, DateOnly onDate)
    {
        if (!string.IsNullOrWhiteSpace(key)) _achievements.TryAdd(key, onDate);
    }

    // --- Membership & sharing --------------------------------------------

    public AccountMember AddMember(Guid userId, string displayName)
    {
        if (_members.Any(m => m.UserId == userId))
            throw new InvalidOperationException("User is already a member of this account.");
        var member = new AccountMember(userId, displayName);
        _members.Add(member);
        return member;
    }

    /// <summary>
    /// Record the creating user as owner and add them as the first contributor (member). Call once,
    /// at account creation, before there are any other members.
    /// </summary>
    public void AssignOwner(Guid userId, string displayName)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("Owner user id is required.", nameof(userId));
        if (OwnerUserId != Guid.Empty)
            throw new InvalidOperationException("This account already has an owner.");
        OwnerUserId = userId;
        AddMember(userId, displayName);
    }

    /// <summary>Add an invited user as a contributor. Contributors are unified with members.</summary>
    public AccountMember AddContributor(Guid userId, string displayName) => AddMember(userId, displayName);

    /// <summary>Remove a member. The owner can't be removed while other members remain — transfer ownership first.</summary>
    public void RemoveMember(Guid userId)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId)
            ?? throw new InvalidOperationException("That user isn't a member of this account.");
        if (IsOwner(userId) && _members.Count > 1)
            throw new InvalidOperationException("Transfer ownership before the owner leaves the account.");
        _members.Remove(member);
    }

    /// <summary>Hand ownership to another existing member (e.g. before the current owner leaves).</summary>
    public void TransferOwnership(Guid toUserId)
    {
        if (!_members.Any(m => m.UserId == toUserId))
            throw new InvalidOperationException("The new owner must already be a member of the account.");
        OwnerUserId = toUserId;
    }

    /// <summary>True for the account creator — gates rename/delete of the account itself.</summary>
    public bool IsOwner(Guid userId) => userId != Guid.Empty && OwnerUserId == userId;

    /// <summary>True for any user who can edit inside the account (owner or invited contributor).</summary>
    public bool IsContributor(Guid userId) => _members.Any(m => m.UserId == userId);

    // --- Categories -------------------------------------------------------

    /// <summary>Add a category. Pass <paramref name="parentId"/> to make it a sub-category (e.g. Kids → Kid1).</summary>
    public Category AddCategory(string name, Guid? parentId = null, string? icon = null)
    {
        if (parentId is { } pid)
        {
            var parent = _categories.FirstOrDefault(c => c.Id == pid)
                ?? throw new InvalidOperationException("Parent category does not exist in this account.");
            // One level of nesting only: a sub-category can't itself be a parent (keeps the tree simple).
            if (parent.ParentId is not null)
                throw new InvalidOperationException("Categories can only be nested one level deep.");
        }
        if (_categories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A category named “{name.Trim()}” already exists.");
        var category = new Category(name, parentId);
        category.SetIcon(icon);
        _categories.Add(category);
        return category;
    }

    /// <summary>Set (or clear) a category's display icon.</summary>
    public void SetCategoryIcon(Guid categoryId, string? icon) =>
        (FindCategory(categoryId) ?? throw new InvalidOperationException("Category not found.")).SetIcon(icon);

    /// <summary>Mark a category as an essential (or discretionary) spend — advisory only.</summary>
    public void SetCategoryEssential(Guid categoryId, bool essential) =>
        (FindCategory(categoryId) ?? throw new InvalidOperationException("Category not found.")).SetEssential(essential);

    /// <summary>Archive (or restore) a category — hides it from pickers/lists while keeping every referencing expense
    /// and budget intact (nothing reassigned or deleted). Unlike <see cref="RemoveCategory"/> there is no blocker.</summary>
    public void SetCategoryArchived(Guid categoryId, bool archived) =>
        (FindCategory(categoryId) ?? throw new InvalidOperationException("Category not found.")).SetArchived(archived);

    // --- Tags: flat, cross-cutting labels attached to expenses (sit alongside sub-categories, not replacing them) ---

    public Tag? FindTag(Guid tagId) => _tags.FirstOrDefault(t => t.Id == tagId);
    public IEnumerable<Tag> ActiveTags => _tags.Where(t => !t.IsArchived);

    /// <summary>Add a tag. Rejects a duplicate name (case-insensitive) within the account.</summary>
    public Tag AddTag(string name, string? icon = null)
    {
        if (_tags.Any(t => NameEquals(t.Name, name)))
            throw new InvalidOperationException($"A tag named “{name.Trim()}” already exists.");
        var tag = new Tag(name);
        tag.SetIcon(icon);
        _tags.Add(tag);
        return tag;
    }

    /// <summary>
    /// The pair of tags a logged installment files its principal and interest rows under, creating them if needed.
    /// <para>
    /// Resolution order matters: <b>whatever this loan's previous rows already used</b> wins, then a tag matching the
    /// supplied name, and only then a fresh one. That first step is what keeps the web (which passes localized names)
    /// and the server's auto-post (which can't know the user's language) filing into the <i>same</i> tag — otherwise a
    /// loan would slowly split into "Loan interest" and "Лихва по заем" and the Breakdown slice would lie.
    /// </para>
    /// </summary>
    public (Guid Principal, Guid Interest) EnsureInstallmentTags(Guid debtBucketId, string principalName, string interestName)
    {
        var rows = _periods.SelectMany(p => p.Expenses).Where(e => e.DebtBucketId == debtBucketId).ToList();

        Guid Resolve(InstallmentPart part, string name)
        {
            var used = rows.Where(e => e.Part == part).OrderByDescending(e => e.Date)
                .Select(e => e.TagId).FirstOrDefault(t => t is { } id && FindTag(id) is not null);
            if (used is { } existing) return existing;
            var byName = _tags.FirstOrDefault(t => NameEquals(t.Name, name));
            if (byName is not null)
            {
                byName.SetArchived(false);   // it's about to be used again; a hidden tag would vanish from Breakdown
                return byName.Id;
            }
            return AddTag(name).Id;
        }

        return (Resolve(InstallmentPart.Principal, principalName), Resolve(InstallmentPart.Interest, interestName));
    }

    public void RenameTag(Guid tagId, string name)
    {
        var tag = FindTag(tagId) ?? throw new InvalidOperationException("Tag not found.");
        if (_tags.Any(t => t.Id != tagId && NameEquals(t.Name, name)))
            throw new InvalidOperationException($"A tag named “{name.Trim()}” already exists.");
        tag.Rename(name);
    }

    /// <summary>Set (or clear) a tag's display icon.</summary>
    public void SetTagIcon(Guid tagId, string? icon) =>
        (FindTag(tagId) ?? throw new InvalidOperationException("Tag not found.")).SetIcon(icon);

    /// <summary>Archive (or restore) a tag — hides it from pickers while leaving every tagged expense intact.</summary>
    public void SetTagArchived(Guid tagId, bool archived) =>
        (FindTag(tagId) ?? throw new InvalidOperationException("Tag not found.")).SetArchived(archived);

    /// <summary>Bind a tag to the category it files into (F2), or clear the binding with null. The category must exist
    /// in this account — a binding pointing at nothing would silently do nothing at entry time, which reads as the
    /// feature being broken rather than as a bad reference.</summary>
    public void SetTagCategory(Guid tagId, Guid? categoryId)
    {
        var tag = FindTag(tagId) ?? throw new InvalidOperationException("Tag not found.");
        if (categoryId is { } id && id != Guid.Empty && FindCategory(id) is null)
            throw new InvalidOperationException("Category does not exist in this account.");
        tag.SetCategory(categoryId);
    }

    /// <summary>Remove a tag outright. Unlike archiving this drops it for good; callers that want to keep the
    /// tag on historical expenses should archive instead. (Expense→tag references are pruned in <c>SetExpenseTags</c>
    /// time; a hard remove here simply deletes the definition.)</summary>
    public void RemoveTag(Guid tagId)
    {
        var tag = FindTag(tagId) ?? throw new InvalidOperationException("Tag not found.");
        _tags.Remove(tag);
    }

    /// <summary>Add a savings bucket. Pass <paramref name="parentId"/> to make it a sub-bucket.</summary>
    public SavingCategory AddSavingCategory(string name, Guid? parentId = null)
    {
        if (parentId is { } pid && _savingCategories.All(c => c.Id != pid))
            throw new InvalidOperationException("Parent saving category does not exist in this account.");
        if (_savingCategories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A savings bucket named “{name.Trim()}” already exists.");
        var category = new SavingCategory(name, parentId);
        _savingCategories.Add(category);
        return category;
    }

    /// <summary>Case-insensitive, trimmed name comparison used to reject duplicate names within the account.</summary>
    private static bool NameEquals(string existing, string candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    // --- Account-to-account transfers: finding the two halves of one movement ---------------------------------
    // Both lookups sweep every period, not just the open one: a transfer made last month still has to be findable
    // from the other account, which may have rolled over since. Callers are responsible for refusing to CHANGE a
    // half that sits in a closed period — Period.EnsureOpen does that for them.

    /// <summary>The outgoing half of an account-to-account transfer, by its shared link id.</summary>
    public (Period Period, ExternalTransfer Transfer)? FindAccountTransferOut(Guid accountTransferId) =>
        _periods.SelectMany(p => p.ExternalTransfers.Select(t => (Period: p, Transfer: t)))
            .Where(x => x.Transfer.AccountTransferId == accountTransferId)
            .Select(x => ((Period, ExternalTransfer)?)x)
            .FirstOrDefault();

    /// <summary>The receiving half (a deposit) of an account-to-account transfer, by its shared link id.</summary>
    public (Period Period, Contribution Deposit)? FindAccountTransferIn(Guid accountTransferId) =>
        _periods.SelectMany(p => p.Contributions.Select(c => (Period: p, Deposit: c)))
            .Where(x => x.Deposit.AccountTransferId == accountTransferId)
            .Select(x => ((Period, Contribution)?)x)
            .FirstOrDefault();

    public Category? FindCategory(Guid categoryId) => _categories.FirstOrDefault(c => c.Id == categoryId);
    public SavingCategory? FindSavingCategory(Guid id) => _savingCategories.FirstOrDefault(c => c.Id == id);

    public void RenameCategory(Guid categoryId, string name)
    {
        var category = FindCategory(categoryId) ?? throw new InvalidOperationException("Category not found.");
        if (_categories.Any(c => c.Id != categoryId && NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A category named “{name.Trim()}” already exists.");
        category.Rename(name);
    }

    /// <summary>Why a category can't be removed, or null when it can.</summary>
    public string? CategoryRemovalBlocker(Guid categoryId)
    {
        if (_categories.Any(c => c.ParentId == categoryId))
            return "it has sub-categories";
        if (_periods.SelectMany(p => p.Budgets).Any(b => b.CategoryId == categoryId))
            return "a budget references it";
        if (_periods.SelectMany(p => p.Expenses).Any(e => e.CategoryId == categoryId))
            return "expenses reference it";
        return null;
    }

    public void RemoveCategory(Guid categoryId)
    {
        var blocker = CategoryRemovalBlocker(categoryId);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove category: {blocker}.");
        var category = FindCategory(categoryId)
            ?? throw new InvalidOperationException("Category not found.");
        // Drop any F2 tag→category bindings that pointed here. Removal is only allowed when nothing references the
        // category, so leaving a dangling binding would make the tag quietly stop filing with no way to see why.
        foreach (var tag in _tags.Where(t => t.CategoryId == categoryId))
            tag.SetCategory(null);
        _categories.Remove(category);
    }

    /// <summary>
    /// Delete a category that history still references, moving everything it holds to <paramref name="targetId"/>
    /// instead of refusing (which is all <see cref="RemoveCategory"/> can do). Its sub-categories go with it.
    /// </summary>
    /// <remarks>
    /// <para><b>Expenses keep their identity</b> — same id, amount, date, member, fund, tags, installment and
    /// settlement links. Only the label changes, so every past total still adds up to the same money; it is
    /// filed somewhere else. That is the honest limit of "delete without affecting previous records": the rows
    /// survive intact, but they cannot go on pointing at a category that no longer exists.</para>
    /// <para><b>Budgets are dropped, not merged.</b> Adding a deleted category's cap onto the target's would
    /// silently raise a limit the user set deliberately — a budget is a decision, and inheriting one is worse
    /// than losing one. Spending moves; the cap does not.</para>
    /// </remarks>
    public void RemoveCategoryReassigning(Guid categoryId, Guid targetId)
    {
        var category = FindCategory(categoryId) ?? throw new InvalidOperationException("Category not found.");
        if (targetId == categoryId)
            throw new InvalidOperationException("Choose a different category to move the expenses to.");
        var target = FindCategory(targetId)
            ?? throw new InvalidOperationException("The category to move the expenses to doesn't exist.");
        // The target must survive the delete, so it can't be one of the sub-categories going down with it.
        var doomed = _categories.Where(c => c.Id == categoryId || c.ParentId == categoryId).Select(c => c.Id).ToHashSet();
        if (doomed.Contains(target.Id))
            throw new InvalidOperationException("That category is being deleted too — pick one that will still exist.");

        foreach (var period in _periods)
        {
            foreach (var expense in period.Expenses.Where(e => doomed.Contains(e.CategoryId)))
                expense.MoveToCategory(targetId);
            foreach (var id in doomed)
                period.RemoveBudgetIfAny(id);
        }
        foreach (var tag in _tags.Where(t => t.CategoryId is { } c && doomed.Contains(c)))
            tag.SetCategory(null);
        _categories.RemoveAll(c => doomed.Contains(c.Id));
    }

    public void RenameSavingCategory(Guid savingCategoryId, string name)
    {
        var bucket = FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.");
        if (_savingCategories.Any(c => c.Id != savingCategoryId && NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A savings bucket named “{name.Trim()}” already exists.");
        bucket.Rename(name);
    }

    /// <summary>Set (or clear) a savings bucket's display icon.</summary>
    public void SetSavingCategoryIcon(Guid savingCategoryId, string? icon) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetIcon(icon);

    /// <summary>Set or clear a savings bucket's goal and alert settings.</summary>
    public void ConfigureSavingGoal(Guid savingCategoryId, decimal? goalAmount, decimal alertThreshold = 0.80m, bool notifyOnMilestone = false) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .SetGoal(goalAmount, alertThreshold, notifyOnMilestone);

    /// <summary>Mark a savings bucket as a debt-payoff envelope with its (projection-only) loan figures. The original
    /// balance (for progress %) is captured the first time; pass <paramref name="originalBalance"/> to set it explicitly.</summary>
    public void ConfigureSavingDebt(Guid savingCategoryId, decimal balance, decimal annualRatePercent, decimal installment,
                                    decimal? originalBalance = null, DateOnly? balanceAsOf = null,
                                    int? installmentDay = null, DateOnly? startDate = null) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .ConfigureDebt(balance, annualRatePercent, installment, originalBalance, balanceAsOf, installmentDay, startDate);

    /// <summary>Set or clear a debt bucket's installment due-day (1–31) — informational + drives recurring due dates.</summary>
    public void SetSavingDebtInstallmentDay(Guid savingCategoryId, int? day) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetDebtInstallmentDay(day);

    /// <summary>Set or clear a debt bucket's origination date — makes "interest paid so far" exact rather than estimated.</summary>
    public void SetSavingDebtStartDate(Guid savingCategoryId, DateOnly? startDate) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetDebtStartDate(startDate);

    /// <summary>Switch a debt bucket between schedule-driven and payment-driven balances, snapshotting what's owed on
    /// <paramref name="today"/> across the change (see <c>SavingCategory.SetPaymentDriven</c>).</summary>
    public void SetSavingDebtPaymentDriven(Guid savingCategoryId, bool paymentDriven, DateOnly today) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetPaymentDriven(paymentDriven, today);

    /// <summary>Set or clear a savings bucket's planned per-period contribution (null/zero → infer pace from history).</summary>
    public void SetSavingPlannedContribution(Guid savingCategoryId, decimal? amount) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .SetPlannedContribution(amount);

    /// <summary>Mark a savings bucket as an investment envelope with its (projection-only) growth figures.</summary>
    public void ConfigureSavingInvestment(Guid savingCategoryId, decimal annualRatePercent, decimal termYears, int compoundsPerYear) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .ConfigureInvestment(annualRatePercent, termYears, compoundsPerYear);

    /// <summary>Revert a savings bucket to an ordinary (common) goal, clearing any debt figures.</summary>
    public void ClearSavingDebt(Guid savingCategoryId) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).ClearDebt();

    /// <summary>Revert a savings bucket to an ordinary (common) goal, clearing any investment figures.</summary>
    public void ClearSavingInvestment(Guid savingCategoryId) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).ClearInvestment();

    /// <summary>Mark a savings bucket as a sinking fund for its listed costs (clears any goal).</summary>
    public void ConfigureSavingExpensesFund(Guid savingCategoryId) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).ConfigureExpensesFund();

    /// <summary>Record an extra payment against a debt bucket — lowers its remaining balance (no-op for common
    /// buckets). Pass <paramref name="asOf"/> to date it, which re-anchors the schedule (see
    /// <see cref="Savings.SavingCategory.DebtBalanceOn"/>).</summary>
    public void RecordSavingDebtPayment(Guid savingCategoryId, decimal amount, DateOnly? asOf = null) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).RecordDebtPayment(amount, asOf);

    /// <summary>Archive (or restore) a savings bucket — hides it from the main lists while keeping its history.</summary>
    public void SetSavingArchived(Guid savingCategoryId, bool archived) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetArchived(archived);

    /// <summary>Attach a savings bucket to a fund (earmark tag), or clear with null.</summary>
    public void SetSavingFund(Guid savingCategoryId, Guid? fundId) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetFund(fundId);

    /// <summary>Replace a savings bucket's list of future costs (the sinking-fund lines).</summary>
    public void SetSavingCosts(Guid savingCategoryId, IEnumerable<PlannedCost> costs) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).ReplaceCosts(costs);

    /// <summary>Set a savings bucket's pre-existing initial balance (setup-time only; see <see cref="SavingCategory.InitialAmount"/>).</summary>
    public void SetSavingInitialAmount(Guid savingCategoryId, decimal amount) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .SetInitialAmount(amount);

    /// <summary>Why a savings bucket can't be removed, or null when it can.</summary>
    public string? SavingCategoryRemovalBlocker(Guid savingCategoryId)
    {
        if (_savingCategories.Any(c => c.ParentId == savingCategoryId))
            return "it has sub-buckets";
        if (_periods.SelectMany(p => p.SavingAllocations).Any(a => a.SavingCategoryId == savingCategoryId))
            return "it has savings activity";
        return null;
    }

    public void RemoveSavingCategory(Guid savingCategoryId)
    {
        var blocker = SavingCategoryRemovalBlocker(savingCategoryId);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove saving bucket: {blocker}.");
        var category = FindSavingCategory(savingCategoryId)
            ?? throw new InvalidOperationException("Saving category not found.");
        // Round-ups pointing here have nowhere to go once it's gone; switch them off rather than leave a setting that
        // reads as "on" while quietly sweeping nothing.
        if (RoundUpBucketId == savingCategoryId) ConfigureRoundUps(0m, null);
        _savingCategories.Remove(category);
    }

    // --- Contribution categories -----------------------------------------

    public ContributionCategory AddContributionCategory(string name)
    {
        if (_contributionCategories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A contribution category named “{name.Trim()}” already exists.");
        var category = new ContributionCategory(name);
        _contributionCategories.Add(category);
        return category;
    }

    public ContributionCategory? FindContributionCategory(Guid id) => _contributionCategories.FirstOrDefault(c => c.Id == id);

    /// <summary>Set (or clear) a contribution category's display icon.</summary>
    public void SetContributionCategoryIcon(Guid id, string? icon) =>
        (FindContributionCategory(id) ?? throw new InvalidOperationException("Contribution category not found.")).SetIcon(icon);

    public void RenameContributionCategory(Guid id, string name)
    {
        var category = FindContributionCategory(id) ?? throw new InvalidOperationException("Contribution category not found.");
        if (_contributionCategories.Any(c => c.Id != id && NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A contribution category named “{name.Trim()}” already exists.");
        category.Rename(name);
    }

    /// <summary>Why a contribution category can't be removed, or null when it can.</summary>
    public string? ContributionCategoryRemovalBlocker(Guid id)
    {
        if (_periods.SelectMany(p => p.Contributions).Any(c => c.CategoryId == id))
            return "deposits reference it";
        return null;
    }

    public void RemoveContributionCategory(Guid id)
    {
        var blocker = ContributionCategoryRemovalBlocker(id);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove contribution category: {blocker}.");
        var category = FindContributionCategory(id) ?? throw new InvalidOperationException("Contribution category not found.");
        _contributionCategories.Remove(category);
    }

    // --- Funds ------------------------------------------------------------

    /// <summary>Add a fund. Pass <paramref name="parentId"/> to nest it as an informational sub-fund.</summary>
    public Fund AddFund(string name, Guid? parentId = null)
    {
        if (parentId is { } pid)
        {
            var parent = FindFund(pid)
                ?? throw new InvalidOperationException("Parent fund does not exist in this account.");
            if (!parent.IsRoot)
                throw new InvalidOperationException("Sub-funds can only be nested one level deep.");
        }
        if (_funds.Any(f => NameEquals(f.Name, name)))
            throw new InvalidOperationException($"A fund named “{name.Trim()}” already exists.");
        var fund = new Fund(name, parentId);
        _funds.Add(fund);
        return fund;
    }

    public IEnumerable<Fund> RootFunds => _funds.Where(f => f.IsRoot);
    public IEnumerable<Fund> ChildFundsOf(Guid parentId) => _funds.Where(f => f.ParentId == parentId);

    /// <summary>Add the standard starter funds to a new account.</summary>
    public void AddDefaultFunds()
    {
        foreach (var name in new[] { "Bank", "Cash", "Digital wallet", "Other" })
            AddFund(name);
    }

    /// <summary>
    /// Seed the starter body of a brand-new account — the default categories, contribution categories and funds,
    /// plus the first (current-month) period dated from <paramref name="today"/>. Shared by the web client
    /// (first-load bootstrap) and the server-side bootstrap endpoint so a native and a web account start identically.
    /// Call once, on an otherwise-empty account.
    /// </summary>
    public void SeedStarter(DateOnly today)
    {
        foreach (var (name, icon) in new[] { ("Food", "🍽️"), ("Bills", "💡"), ("Transport", "🚗"), ("Other", "🏷️") })
            AddCategory(name, icon: icon);
        // No starter savings bucket: creating the first one (with or without a goal) is an onboarding step and earns
        // the "Piggy" achievement itself, so pre-seeding one both robs that moment and misleads the user into
        // thinking they already have a bucket set up.
        foreach (var c in new[] { "Salary", "Other" })
            AddContributionCategory(c);
        AddDefaultFunds();

        var from = new DateOnly(today.Year, today.Month, 1);
        StartPeriod(from, from.AddMonths(1).AddDays(-1));
    }

    public Fund? FindFund(Guid fundId) => _funds.FirstOrDefault(f => f.Id == fundId);
    public string FundName(Guid fundId) => FindFund(fundId)?.Name ?? "—";

    /// <summary>Id of the seeded fund with the given name (case-insensitive); throws if none. Handy for tests/defaults.</summary>
    public Guid FundId(string name) =>
        (_funds.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No fund named '{name}'.")).Id;

    public void RenameFund(Guid fundId, string name)
    {
        var fund = FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.");
        if (_funds.Any(f => f.Id != fundId && NameEquals(f.Name, name)))
            throw new InvalidOperationException($"A fund named “{name.Trim()}” already exists.");
        fund.Rename(name);
    }

    public void SetFundNote(Guid fundId, string? note)
    {
        var fund = FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.");
        fund.SetNote(note);
    }

    /// <summary>Set (or clear) a fund's display icon.</summary>
    public void SetFundIcon(Guid fundId, string? icon) =>
        (FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.")).SetIcon(icon);

    /// <summary>Mark/unmark a fund as synced to a bank account. Forward-only: existing entries keep their markers.</summary>
    public void SetFundSynced(Guid fundId, bool synced) =>
        (FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.")).SetSynced(synced);

    /// <summary>Archive (or restore) a fund — hides it from the pickers and main list while keeping every referencing
    /// transaction intact (nothing is reassigned or deleted). Unlike <see cref="RemoveFund"/> there is no reference
    /// blocker; move any remaining balance out first with a transfer if you don't want it stranded on a hidden fund.</summary>
    public void SetFundArchived(Guid fundId, bool archived) =>
        (FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.")).SetArchived(archived);

    /// <summary>
    /// Why a fund can't be removed, or null when it can. Opening balances are <b>not</b> a hard blocker —
    /// they can be moved to another fund on removal (see <see cref="RemoveFund"/> / <see cref="FundHasOpeningBalance"/>).
    /// </summary>
    public string? FundRemovalBlocker(Guid fundId)
    {
        if (_funds.Any(f => f.ParentId == fundId))
            return "it has sub-funds";
        if (FindFund(fundId)?.IsRoot == true && _funds.Count(f => f.IsRoot) <= 1)
            return "it's the only fund";
        if (_periods.SelectMany(p => p.Expenses).Any(e => e.FundId == fundId))
            return "expenses reference it";
        if (_periods.SelectMany(p => p.FundTransfers).Any(t => t.FromFundId == fundId || t.ToFundId == fundId))
            return "a transfer references it";
        return null;
    }

    /// <summary>True when the fund has a non-zero real opening balance in any period (which must be moved before removal). Zero balances and sub-fund informative balances don't count.</summary>
    public bool FundHasOpeningBalance(Guid fundId) =>
        _periods.SelectMany(p => p.InitialBalances).Any(b => b.FundId == fundId && !b.Informative && !b.Amount.IsZero);

    /// <summary>
    /// Remove a fund. Optionally pass <paramref name="moveOpeningBalancesTo"/> to consolidate its opening
    /// balances onto another (top-level) fund first — total-preserving. When no target is given the balance
    /// is simply dropped along with the fund.
    /// </summary>
    public void RemoveFund(Guid fundId, Guid? moveOpeningBalancesTo = null)
    {
        var blocker = FundRemovalBlocker(fundId);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove fund: {blocker}.");
        var fund = FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.");

        if (moveOpeningBalancesTo is { } targetId)
        {
            if (targetId == fundId)
                throw new InvalidOperationException("Choose a different fund to receive the opening balance.");
            var target = FindFund(targetId) ?? throw new InvalidOperationException("Target fund not found.");
            if (!target.IsRoot)
                throw new InvalidOperationException("Opening balances can only move to a top-level fund.");
            foreach (var period in _periods)
                period.MoveInitialBalance(fundId, targetId);
        }

        // Drop any remaining opening-balance rows (the moved-from rows are already gone; this also discards
        // the balance when the user chose not to transfer, and clears any informative sub-fund rows).
        foreach (var period in _periods)
            period.RemoveInitialBalance(fundId);

        _funds.Remove(fund);
    }

    /// <summary>A category id plus all descendant ids — used to roll expenses up to a parent budget.</summary>
    public IReadOnlyCollection<Guid> CategoryWithDescendantIds(Guid categoryId) =>
        WithDescendants(categoryId, _categories.Select(c => (c.Id, c.ParentId)));

    /// <summary>A savings bucket id plus all descendant ids.</summary>
    public IReadOnlyCollection<Guid> SavingCategoryWithDescendantIds(Guid savingCategoryId) =>
        WithDescendants(savingCategoryId, _savingCategories.Select(c => (c.Id, c.ParentId)));

    private static IReadOnlyCollection<Guid> WithDescendants(Guid rootId, IEnumerable<(Guid Id, Guid? ParentId)> nodes)
    {
        var byParent = nodes.ToLookup(n => n.ParentId);
        // Track visited ids: a corrupt snapshot with a cyclic parent chain (A→B→A) would otherwise spin forever.
        // This walk runs on every render (savings/category rollups), so it must be robust to bad data, not just fast.
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (var child in byParent[id])
                queue.Enqueue(child.Id);
        }
        return visited;
    }

    // --- Periods ----------------------------------------------------------

    public Period? CurrentPeriod => _periods.LastOrDefault();

    /// <summary>
    /// Remove the latest period and re-activate the previous one (which becomes editable again).
    /// Only the most recent period can be removed, so the period chain stays contiguous.
    /// </summary>
    public void RemoveLatestPeriod()
    {
        if (_periods.Count <= 1)
            throw new InvalidOperationException("Cannot remove the only period.");
        _periods.RemoveAt(_periods.Count - 1);
        _periods[^1].Reopen();
    }

    public Period? PreviousPeriodOf(Period period)
    {
        var index = _periods.IndexOf(period);
        return index > 0 ? _periods[index - 1] : null;
    }

    public int IndexOfPeriod(Period period) => _periods.IndexOf(period);

    /// <summary>
    /// Reschedule a period's date range and shift every later period to stay contiguous,
    /// preserving each one's length (feature: "set from/to, all periods shift").
    /// </summary>
    public void ReschedulePeriod(Period period, DateOnly from, DateOnly to)
    {
        period.Reschedule(from, to);

        for (var i = _periods.IndexOf(period) + 1; i > 0 && i < _periods.Count; i++)
        {
            var newFrom = _periods[i - 1].To.AddDays(1);
            var newTo = newFrom.AddDays(_periods[i].LengthInDays);
            _periods[i].Reschedule(newFrom, newTo);
        }
    }

    /// <summary>
    /// Start a new period. Optionally copies the previous period's budget allocations and alert
    /// settings forward (feature 5). Carry-over of opening balances and reconciliation are handled
    /// by the application/reconciliation services, not here, to keep this aggregate pure.
    /// </summary>
    public Period StartPeriod(DateOnly from, DateOnly to, bool copyBudgetsFromPrevious = false, bool adjustToConsumption = false)
    {
        var previous = CurrentPeriod;
        if (previous is not null && from <= previous.From)
            throw new InvalidOperationException("A new period must start after the current period.");

        var period = new Period(Currency, from, to);

        if (copyBudgetsFromPrevious && previous is not null)
        {
            foreach (var b in previous.Budgets)
            {
                var allocated = adjustToConsumption
                    ? AdjustToConsumption(b.Allocated, SpentInCategory(previous, b.CategoryId))
                    : b.Allocated;
                period.AddBudget(b.CategoryId, allocated, b.AlertThreshold, b.NotifyOnEveryExpense);
            }
        }

        _periods.Add(period);
        return period;
    }

    private static Money SpentInCategory(Period period, Guid categoryId) =>
        period.Expenses.Where(e => e.CategoryId == categoryId)
            .Aggregate(Money.Zero(period.Currency), (acc, e) => acc + e.Amount);

    /// <summary>
    /// Nudge a copied-forward budget halfway toward what was actually spent, then round the result up to the next
    /// whole 10. Overspending raises the budget by half the overspend; underspending lowers it by half the slack.
    /// Equivalent to ⌈((budgeted + spent) / 2) / 10⌉ × 10. (Feature 8.)
    /// </summary>
    private static Money AdjustToConsumption(Money budgeted, Money spent)
    {
        var midpoint = (budgeted.Amount + spent.Amount) / 2m;
        var roundedUpToTen = Math.Ceiling(midpoint / 10m) * 10m;
        return new Money(roundedUpToTen < 0m ? 0m : roundedUpToTen, budgeted.Currency);
    }
}
