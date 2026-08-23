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
    private readonly List<Trip> _trips = [];
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
    /// What an hour of the user's time earns, in the account currency, so an amount can also be read as the time it
    /// costs. Null = not set, and that is the deliberate default: this must be a number the user types.
    /// </summary>
    /// <remarks>
    /// It is <b>not</b> derived from income ÷ working days. That division is wrong for anyone freelance, part-time,
    /// on shifts or on irregular pay — a large slice of the people this app is for — and being quietly wrong about
    /// what someone's hour is worth is worse than not saying. Body data; travels in the snapshot.
    /// </remarks>
    public decimal? HourlyRate { get; private set; }

    /// <summary>Days worked in a typical month, for deriving the rate from income. Null = not deriving.</summary>
    public int? WorkingDaysPerMonth { get; private set; }

    /// <summary>Hours worked on a typical working day, for deriving the rate from income. Null = not deriving.</summary>
    public decimal? WorkingHoursPerDay { get; private set; }

    /// <summary>
    /// The rate actually used to price time: a rate typed by hand wins; otherwise it is derived from this period's
    /// income over the working pattern; otherwise null and no time cost is shown anywhere.
    /// </summary>
    /// <remarks>
    /// <b>The derived figure moves with income</b>, which is the honest consequence of asking for it: a thin month
    /// makes every hour look cheaper and each purchase look longer. That is arithmetic, not a bug — but it is why
    /// the UI shows the computed number rather than hiding the division, and why typing a rate stays available and
    /// takes precedence. Anyone freelance, part-time or on irregular pay should use the typed one.
    /// </remarks>
    public decimal? EffectiveHourlyRate => HourlyRate is { } manual && manual > 0m ? manual : DerivedHourlyRate;

    /// <summary>
    /// What this period's income actually worked out at per hour, given the working pattern — computed whether or
    /// not a rate was also typed, so the two can be compared. Null without a full pattern or without income.
    /// </summary>
    /// <remarks>
    /// Keeping this alongside a typed rate is the point, not redundancy. They answer different questions: a typed
    /// rate is what an hour is <i>worth</i> (roughly, what one more would earn), while this is what an hour actually
    /// <i>paid</i> — unpaid overtime, a lean month and time off all land here and nowhere else. When they disagree
    /// the app should say so rather than quietly pricing everything off the one the user forgot they set.
    /// </remarks>
    public decimal? DerivedHourlyRate
    {
        get
        {
            if (WorkingDaysPerMonth is not { } days || days <= 0) return null;
            if (WorkingHoursPerDay is not { } hours || hours <= 0m) return null;
            var income = CurrentPeriod?.ContributionsPaidTotal.Amount ?? 0m;
            if (income <= 0m) return null;   // nothing came in yet — say nothing rather than divide by hope
            return income / (days * hours);
        }
    }

    /// <summary>
    /// How far a typed rate sits from what the hours actually paid, as a fraction (0.5 = the typed rate is half
    /// what was earned). Null unless both are known. The UI raises it past a threshold — a small gap is normal.
    /// </summary>
    public decimal? HourlyRateDrift =>
        HourlyRate is { } typed && typed > 0m && DerivedHourlyRate is { } derived && derived > 0m
            ? typed / derived
            : null;

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

    /// <summary>Set (or clear, with null or 0) what an hour of the user's time earns. See <see cref="HourlyRate"/>.</summary>
    public void SetHourlyRate(decimal? rate)
    {
        if (rate is { } r && r < 0m)
            throw new ArgumentOutOfRangeException(nameof(rate), "An hourly rate can't be negative.");
        HourlyRate = rate is { } value && value > 0m ? value : null;
    }

    /// <summary>Set (or clear, with nulls/zeros) the working pattern the rate is derived from when no rate is typed.</summary>
    public void SetWorkingPattern(int? daysPerMonth, decimal? hoursPerDay)
    {
        if (daysPerMonth is { } d && (d < 0 || d > 31))
            throw new ArgumentOutOfRangeException(nameof(daysPerMonth), "Working days must be between 0 and 31.");
        if (hoursPerDay is { } h && (h < 0m || h > 24m))
            throw new ArgumentOutOfRangeException(nameof(hoursPerDay), "Working hours must be between 0 and 24.");
        WorkingDaysPerMonth = daysPerMonth is { } days && days > 0 ? days : null;
        WorkingHoursPerDay = hoursPerDay is { } hours && hours > 0m ? hours : null;
    }

    /// <summary>
    /// How long someone works to afford <paramref name="amount"/>, or null when no rate is available. Rounded to
    /// the minute — the rate is an estimate either way, and seconds would dress it up as a measurement.
    /// </summary>
    public TimeSpan? TimeCostOf(decimal amount) =>
        EffectiveHourlyRate is { } rate && rate > 0m && amount > 0m
            ? TimeSpan.FromMinutes(Math.Round((double)(amount / rate) * 60d))
            : null;

    /// <summary>
    /// <see cref="TimeCostOf"/> written the way a person thinks about it — "35m", "6h 20m", "2d 4h" — or null when
    /// there is no rate to price time with.
    /// <para>
    /// <b>★ It rolls up into working DAYS, measured in the user's own working day, not in 24 hours.</b> "160h" is a
    /// number nobody can feel; "20d" is a month of your life, and making the figure felt is the only reason it is
    /// shown at all. The day length comes from <see cref="WorkingHoursPerDay"/> — the same pattern the derived rate
    /// is built from — falling back to 8 when only a manual <see cref="HourlyRate"/> was typed, since a rate can be
    /// set without ever stating a pattern.
    /// </para>
    /// <para>
    /// Minutes are dropped once days are involved: "2d 4h 37m" is precision an estimated rate does not have.
    /// </para>
    /// Lives here rather than in the client so the arithmetic has one home and can be tested; the unit letters are
    /// the same in every language the app ships.
    /// </summary>
    public string? TimeCostText(decimal amount)
    {
        if (TimeCostOf(amount) is not { } span || span.TotalMinutes < 1) return null;
        var dayHours = WorkingHoursPerDay is { } h && h > 0m ? (double)h : 8d;
        var totalHours = span.TotalHours;

        if (totalHours >= dayHours)
        {
            var days = (int)(totalHours / dayHours);
            var restHours = (int)Math.Round(totalHours - days * dayHours);
            // Rounding the remainder up can land on a whole day ("2d 8h" on an 8-hour day) — carry it instead.
            if (restHours >= (int)Math.Round(dayHours)) { days++; restHours = 0; }
            return restHours == 0 ? $"{days}d" : $"{days}d {restHours}h";
        }

        var hours = (int)totalHours;
        var minutes = span.Minutes;
        return hours == 0 ? $"{minutes}m" : minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
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

    /// <summary>
    /// Add a category. Always top-level: sub-categories were removed, and a tag is what splits a category now.
    /// </summary>
    /// <param name="parentId">
    /// Ignored. Kept on the signature so an older client posting a parent still gets its category created —
    /// filed at the top level — instead of a 400 it can't act on. Nothing in the app sends one any more.
    /// </param>
    /// <summary>
    /// Add a top-level spend category.
    /// <para>
    /// ⚠️ <b>There is deliberately no <c>parentId</c>.</b> It used to take one and silently drop it, which is worse
    /// than not offering it: a caller could pick a parent, get no error, and end up with a top-level category —
    /// which is exactly what the phone's category editor was doing. Nesting cannot be created at all, because
    /// <see cref="FlattenCategoryTree"/> runs inside <c>AccountSnapshotSerializer.Deserialize</c>, so a
    /// sub-category would be turned into a <see cref="Tag"/> the first time the account was loaded. The parameter
    /// is gone so the impossibility is a compile error rather than a surprise.
    /// </para>
    /// <para>Tags are the axis sub-categories used to be — and a better one, since a tag can span two categories,
    /// which a sub-category never could.</para>
    /// </summary>
    public Category AddCategory(string name, string? icon = null)
    {
        if (_categories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A category named “{name.Trim()}” already exists.");
        var category = new Category(name);
        category.SetIcon(icon);
        _categories.Add(category);
        return category;
    }

    /// <summary>
    /// Collapse the legacy sub-category tree: each sub-category becomes a <see cref="Tag"/> bound to its old
    /// parent, its expenses re-file to that parent (carrying the new tag), and its budget merges into the
    /// parent's. Idempotent — an account with no sub-categories left is untouched, so this can run on every load.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing is lost that the app could otherwise show.</b> The sub-category's name survives as the tag,
    /// its history survives on the expenses, and the <see cref="Tag.CategoryId"/> binding preserves the one
    /// convenience nesting actually bought: picking "Groceries" on a new expense still files it under Food.</para>
    /// <para><b>Budgets merge rather than drop</b>, which is the opposite of <see cref="RemoveCategoryReassigning"/>
    /// and deliberately so: there the user is deleting a category and inheriting its cap would raise a limit they
    /// set on purpose, while here the two budgets were always one plan — Food €600 was already the sum of its
    /// children. Merging keeps the total the user is used to; dropping would quietly free €400 of budget.</para>
    /// <para><b>The tag is applied to stored rows</b>, which a tag→category binding is otherwise forbidden from
    /// doing (see <see cref="Tag.CategoryId"/>). The rule protects history from a binding made <i>later</i>; this
    /// is the same fact the expense already carried, written on the axis that still exists.</para>
    /// <para><b>An expense that already has a tag keeps it.</b> One tag per expense is the model, and the tag was
    /// chosen for that row while the sub-category was mostly the shape of the picker. Those rows still re-file to
    /// the parent, so no money moves — they just lose the sub-category distinction. The count comes back in
    /// <see cref="CategoryFlattenResult.ExpensesTagSlotTaken"/> so a caller can say so out loud.</para>
    /// </remarks>
    /// <summary>
    /// What the last <see cref="FlattenCategoryTree"/> on this loaded aggregate did — <see cref="CategoryFlattenResult.Nothing"/>
    /// for every account whose tree was already flat. Load-time state, not stored data: it exists so the surface that
    /// loaded the account can tell the user their sub-categories became tags, once, instead of the conversion being
    /// something they notice later as "my budgets changed".
    /// </summary>
    public CategoryFlattenResult LastCategoryFlatten { get; private set; } = CategoryFlattenResult.Nothing;

    public CategoryFlattenResult FlattenCategoryTree()
    {
        var children = _categories.Where(c => !c.IsRoot).ToList();
        if (children.Count == 0) return LastCategoryFlatten = CategoryFlattenResult.Nothing;

        int converted = 0, refiled = 0, slotTaken = 0, budgetsMerged = 0, recurringMoved = 0;

        foreach (var child in children)
        {
            var parent = child.ParentId is { } pid ? FindCategory(pid) : null;
            // Two levels deep can't happen (AddCategory refused it), but a parent that is itself a child in some
            // hand-edited snapshot would otherwise convert into a tag bound to a category about to disappear.
            if (parent is null || !parent.IsRoot)
            {
                child.ClearParent();
                continue;
            }

            // A tag the user already has under this name wins over minting a second one: two tags called
            // "Groceries" would split every future breakdown and break the account's own name-uniqueness rule.
            // Otherwise the tag takes over the sub-category's id — see the Tag(name, id) constructor for why.
            var tag = _tags.FirstOrDefault(t => NameEquals(t.Name, child.Name));
            if (tag is null)
            {
                tag = new Tag(child.Name, child.Id);
                tag.SetIcon(child.Icon);
                _tags.Add(tag);
            }
            tag.SetArchived(false);   // it is about to carry history; a hidden tag would vanish from the Breakdown
            if (tag.CategoryId is null) tag.SetCategory(parent.Id);

            foreach (var period in _periods)
            {
                foreach (var expense in period.Expenses.Where(e => e.CategoryId == child.Id))
                {
                    expense.MoveToCategory(parent.Id);
                    if (expense.TagId is null) { expense.SetTag(tag.Id); refiled++; }
                    else slotTaken++;
                }

                if (period.FindBudget(child.Id) is { } childBudget)
                {
                    var parentBudget = period.FindBudget(parent.Id);
                    // The parent's own alert settings survive when it had a budget; a parent that had none adopts
                    // the child's, so a threshold someone set deliberately isn't replaced by the 80% default.
                    period.SetBudget(parent.Id,
                        parentBudget is null ? childBudget.Allocated : parentBudget.Allocated + childBudget.Allocated,
                        parentBudget?.AlertThreshold ?? childBudget.AlertThreshold,
                        parentBudget?.NotifyOnEveryExpense ?? childBudget.NotifyOnEveryExpense);
                    period.RemoveBudgetIfAny(child.Id);
                    budgetsMerged++;
                }
            }

            // Everything else that files by category id. A bill pointing at a category that no longer exists would
            // post its next expense into nothing, so these move even though they hold no history themselves.
            foreach (var item in _recurring.Where(r => r.CategoryId == child.Id))
            {
                item.MoveToCategory(parent.Id);
                recurringMoved++;
            }
            foreach (var trip in _trips.Where(t => t.CategoryId == child.Id))
                trip.SetCategory(parent.Id);
            foreach (var other in _tags.Where(t => t.CategoryId == child.Id))
                other.SetCategory(parent.Id);

            _categories.Remove(child);
            converted++;
        }

        return LastCategoryFlatten = new CategoryFlattenResult(converted, refiled, slotTaken, budgetsMerged, recurringMoved);
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
    /// <param name="isTripTag">Mark it as one of the trip labels (see <see cref="Tag.IsTripTag"/>). The everyday
    /// picker hides those and the trip form shows only those, so a tag created <i>while filing against a trip</i>
    /// has to be born on the trip axis — otherwise it is made, selected, and instantly invisible in the row that
    /// made it.</param>
    public Tag AddTag(string name, string? icon = null, bool isTripTag = false)
    {
        if (_tags.Any(t => NameEquals(t.Name, name)))
            throw new InvalidOperationException($"A tag named “{name.Trim()}” already exists.");
        var tag = new Tag(name);
        tag.SetIcon(icon);
        tag.SetTripTag(isTripTag);
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

    /// <summary>
    /// F2, learned: the first time a tag is used on a new expense, it takes that expense's category as its binding.
    /// <para>
    /// Both clients already <i>apply</i> a binding at entry — the gap was that nothing ever <i>made</i> one except a
    /// deliberate trip to the manage-tags sheet, and a tag typed into the add-expense box is born with none. So the
    /// feature existed and, for anyone who tags as they go, never once fired. Using the two together is the teaching.
    /// </para>
    /// <para>
    /// ⚠️ Only when the tag has NO binding — the same "fill in the blank, never overwrite" rule the sub-category
    /// flatten and the trip-tag seed already use. One odd filing must not silently re-point a tag the user relies on,
    /// and a binding they set by hand outranks anything inferred from a tap.
    /// </para>
    /// <para>
    /// ⚠️ Silent and never throws: this is a side effect of adding an expense, and a bad category id or a missing tag
    /// is the caller's problem to have already rejected — failing the expense over a filing hint would be absurd.
    /// </para>
    /// <para>
    /// ⚠️ Known consequence, deliberately not chased: clearing a binding by hand and then tagging one more expense
    /// re-teaches it, because "never taught" and "deliberately taught nothing" are the same null. Separating them
    /// costs a new field on the tag, and both existing auto-binds above have the same blind spot — worth fixing all
    /// three together, or not at all.
    /// </para>
    /// </summary>
    public void LearnTagCategory(Guid tagId, Guid categoryId)
    {
        if (FindTag(tagId) is not { CategoryId: null } tag) return;
        if (categoryId == Guid.Empty || FindCategory(categoryId) is null) return;
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

    // --- Trips: a named journey expenses point at, and the tag set its cost split is drawn on ------------------

    /// <summary>Every trip, in the order they were added. Body data — travels in the snapshot.</summary>
    public IReadOnlyList<Trip> Trips => _trips;

    /// <summary>Trips newest departure first — the order the trips list reads in.</summary>
    public IEnumerable<Trip> TripsByDeparture => _trips.OrderByDescending(t => t.From);

    public Trip? FindTrip(Guid tripId) => _trips.FirstOrDefault(t => t.Id == tripId);

    /// <summary>The trip labels (Stay, Travel, Food &amp; drink…), in the order they were seeded.</summary>
    public IEnumerable<Tag> TripTags => _tags.Where(t => t.IsTripTag);

    /// <summary>
    /// The trip that "now" falls inside, or null when we're not travelling. This — not a stored flag — is what trip
    /// mode means.
    /// <para>
    /// <b>★ Derived on purpose.</b> A mode you switch on is a mode you forget to switch off, and the failure is
    /// silent and expensive: you come home and keep filing groceries to Rome for three weeks. Deriving it from the
    /// dates means the app cannot be wrong about whether you're away for longer than it takes to correct the dates.
    /// </para>
    /// Overlapping trips are a user mistake rather than a modelled case; the one that started most recently wins,
    /// so the answer is stable and the newer plan is the one in force.
    /// </summary>
    public Trip? ActiveTrip(DateOnly today) =>
        _trips.Where(t => t.IsActiveOn(today)).OrderByDescending(t => t.From).FirstOrDefault();

    /// <summary>Trips that haven't started yet, soonest first — the ones a countdown or a "book it to this trip"
    /// picker should offer.</summary>
    public IEnumerable<Trip> UpcomingTrips(DateOnly today) =>
        _trips.Where(t => t.IsUpcomingOn(today)).OrderBy(t => t.From);

    /// <summary>Add a trip. Rejects a duplicate name (case-insensitive) so two "Rome"s can't be told apart only by
    /// their dates in a picker.</summary>
    public Trip AddTrip(string name, DateOnly from, DateOnly to, string? destination = null, string? icon = null)
    {
        if (_trips.Any(t => NameEquals(t.Name, name)))
            throw new InvalidOperationException($"A trip named “{name.Trim()}” already exists.");
        var trip = new Trip(name, from, to);
        trip.SetDestination(destination);
        trip.SetIcon(icon);
        _trips.Add(trip);
        return trip;
    }

    /// <summary>Rename/re-date/re-describe a trip. Moving the dates changes when trip mode is active and nothing
    /// else — expenses stay attached, because membership was never date-based.</summary>
    public void UpdateTrip(Guid tripId, string name, DateOnly from, DateOnly to, string? destination = null, string? icon = null)
    {
        var trip = FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        if (_trips.Any(t => t.Id != tripId && NameEquals(t.Name, name)))
            throw new InvalidOperationException($"A trip named “{name.Trim()}” already exists.");
        trip.Update(name, from, to, destination, icon);
    }

    /// <summary>Link a trip to the savings bucket funding it, or clear with null. The bucket must exist here — a
    /// link pointing at nothing would quietly drop the "funded from" line and read as the feature being broken.</summary>
    public void SetTripSavingCategory(Guid tripId, Guid? savingCategoryId)
    {
        var trip = FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        if (savingCategoryId is { } id && id != Guid.Empty && FindSavingCategory(id) is null)
            throw new InvalidOperationException("Savings bucket does not exist in this account.");
        trip.SetSavingCategory(savingCategoryId);
    }

    /// <summary>Point a trip at the single category its expenses file into, or clear with null. The category must
    /// exist here — a link to nothing would silently fall back to per-label filing, which looks exactly like the
    /// setting being ignored.</summary>
    public void SetTripCategory(Guid tripId, Guid? categoryId)
    {
        var trip = FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        if (categoryId is { } id && id != Guid.Empty && FindCategory(id) is null)
            throw new InvalidOperationException("Category does not exist in this account.");
        trip.SetCategory(categoryId);
    }

    /// <summary>Confirm a trip has begun — see <see cref="Trip.StartedOn"/> for why this is a tap and not a date
    /// comparison. Refuses outside the trip's own window: "we've left" is meaningless the week before, and a trip
    /// started early would sit in a state nothing in the UI describes.</summary>
    public void StartTrip(Guid tripId, DateOnly today)
    {
        var trip = FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        if (today < trip.From || today > trip.To)
            throw new InvalidOperationException("This trip isn't due to start today.");
        trip.Start(today);
    }

    /// <summary>Take back a start ("we haven't left yet").</summary>
    public void UnstartTrip(Guid tripId) =>
        (FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.")).Unstart();

    /// <summary>Trips whose dates have arrived but that nobody has confirmed leaving on — what the "Start the trip?"
    /// prompt is built from, soonest departure first.</summary>
    public IEnumerable<Trip> TripsAwaitingStart(DateOnly today) =>
        _trips.Where(t => t.IsAwaitingStart(today)).OrderBy(t => t.From);

    /// <summary>End a trip as of <paramref name="today"/> — see <see cref="Trip.Finish"/>. Idempotent: finishing an
    /// already-finished trip just restates the same day.</summary>
    public void FinishTrip(Guid tripId, DateOnly today) =>
        (FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.")).Finish(today);

    /// <summary>
    /// Record money coming back on an expense, and put it in the wallet that actually received it.
    /// <paramref name="totalRefunded"/> is the running total (see <see cref="Period.SetRefund"/>); zero undoes.
    ///
    /// <para><b>★ Why a wallet argument at all.</b> Shrinking the expense is already a credit to the wallet it was
    /// paid from — a non-synced expense is part of that fund's spending position, so removing €20 of it puts €20
    /// back. That covers the ordinary case and needs no movement. What it cannot express is <b>paid by card,
    /// handed back in cash</b>: there the expense's wallet must not keep the money and another one must gain it.
    /// So when <paramref name="toFundId"/> names a different wallet, this also records an intra-account transfer
    /// for the refunded amount. It is total-preserving, which is exactly right — the money re-entered the account
    /// when the expense shrank; this only says where it sits.</para>
    ///
    /// <para>⚠️ A <b>synced</b> source is not debited by that transfer (<see cref="FundTransfer.SetSyncedSides"/>) —
    /// the real bank balance already accounts for that side, the same rule the bank money-in confirm follows.</para>
    ///
    /// <para>⚠️ Only the <i>added</i> amount moves. Restating a running total that has already been part-moved
    /// would transfer the same euros twice, so the transfer is for the delta against what had come back before.</para>
    /// </summary>
    /// <returns>The rebuilt expense — its id is new, the ledger being append-only.</returns>
    public Expense RefundExpense(Guid expenseId, Money totalRefunded, Guid? toFundId = null)
    {
        var period = CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var before = period.Expenses.FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this period.");
        var sourceFundId = before.FundId;
        var added = totalRefunded.Amount - before.RefundedAmount;

        var refunded = period.SetRefund(expenseId, totalRefunded);

        if (toFundId is { } destination && destination != sourceFundId && added > 0m)
        {
            var from = FindFund(sourceFundId) ?? throw new InvalidOperationException("The expense's wallet doesn't exist in this account.");
            var to = FindFund(destination) ?? throw new InvalidOperationException("That wallet doesn't exist in this account.");
            var transfer = period.TransferFunds(sourceFundId, destination, new Money(added, Currency), refunded.Date,
                $"Refund · {before.Note}".TrimEnd(' ', '·'));
            transfer.SetSyncedSides(from.IsSynced, to.IsSynced);
        }
        return refunded;
    }

    /// <summary>Put a finished trip back on the road.</summary>
    public void ReopenTrip(Guid tripId) =>
        (FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.")).Reopen();

    /// <summary>
    /// Record that <paramref name="amount"/> of the trip's linked savings bucket has been released into its budget.
    /// The money movement itself belongs to the period (<c>Period.ConvertSavingToBudget</c>); this is the trip's own
    /// record of it, so the recap can say "€1,500 of this was money you'd saved" without re-deriving it from
    /// allocations that carry no trip.
    /// <para>Requires the trip to have both a bucket and a category — without the first there is nothing to release,
    /// and without the second there is no budget to release it into.</para>
    /// </summary>
    public void ApplyTripSavings(Guid tripId, decimal amount)
    {
        var trip = FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        if (trip.SavingCategoryId is null)
            throw new InvalidOperationException("This trip isn't linked to a savings bucket.");
        if (trip.CategoryId is null)
            throw new InvalidOperationException("This trip has no category to release the money into.");
        if (amount <= 0m)
            throw new ArgumentException("Amount must be positive.", nameof(amount));
        trip.AddSavingsApplied(amount);
    }

    /// <summary>Set (or clear) what the trip is expected to cost.</summary>
    public void SetTripBudget(Guid tripId, decimal? budget) =>
        (FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.")).SetBudget(budget);

    /// <summary>Set (or clear) the trip's fixed currency conversion. See <see cref="Trip.Rate"/> for why a single
    /// user-set rate rather than a live feed, and why it converts at entry time only.</summary>
    public void SetTripRate(Guid tripId, string? spendCurrency, decimal? rate) =>
        (FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.")).SetRate(spendCurrency, rate);

    /// <summary>
    /// Remove a trip, detaching every expense that pointed at it. The expenses themselves are never touched
    /// otherwise — the money was still spent, it just stops being counted as part of a journey.
    /// <para>
    /// Detaching rather than leaving the ids dangling: an expense carrying a trip id that resolves to nothing would
    /// look attached in the data and appear in no trip, which is the kind of state that surfaces years later as
    /// "why doesn't this total match".
    /// </para>
    /// </summary>
    public void RemoveTrip(Guid tripId)
    {
        var trip = FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        // ⚠️ OwnTripExpenses, not every row carrying this id: a row linked to ANOTHER account's trip that happens
        // to share nothing but a Guid is not this trip's, and detaching it here would unlink someone else's
        // attachment. Expenses in other accounts pointing at this trip cannot be reached at all — deliberately;
        // see Expense.TripAccountId for why a dangling pointer there is the safe outcome.
        foreach (var expense in OwnTripExpenses(tripId))
            expense.SetTrip(null);
        _trips.Remove(trip);
    }

    /// <summary>Every expense attached to a trip, across all periods, newest first — and within a day, newest on the
    /// clock first (an untimed row reports <see cref="Expense.SortTime"/> = midnight, so it settles at the bottom of
    /// its own day rather than the top). A trip's ledger is the one list where a single day holds a dozen entries,
    /// so the clock is what makes it read as a day rather than a heap.
    /// <para>The recap's whole input — note it spans periods by nature, because the booking and the holiday rarely
    /// sit in the same month.</para></summary>
    public IEnumerable<Expense> TripExpenses(Guid tripId) => OwnTripExpenses(tripId);

    /// <summary>This account's own expenses attached to <paramref name="tripId"/>, in ledger order.
    /// <para>⚠️ <c>TripAccountId is null</c> is the load-bearing half. An expense in THIS account attached to
    /// ANOTHER account's trip carries that trip's id, and without the guard it would surface in this account's
    /// own trip lists the moment two accounts minted trips whose ids collided — vanishingly unlikely, but the
    /// guard makes the safety local rather than a coincidence.</para></summary>
    private IEnumerable<Expense> OwnTripExpenses(Guid tripId) =>
        _periods.SelectMany(p => p.Expenses).Where(e => e.TripId == tripId && e.TripAccountId is null)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.SortTime);

    /// <summary>This account's expenses attached to a trip that lives in <b>another</b> account — the rows the
    /// other account's recap will gather. Used by the server to build the fan-out; nothing here counts them, since
    /// this account's own totals already do (this account paid).</summary>
    public IEnumerable<Expense> ExpensesOnForeignTrip(Guid tripId, Guid tripAccountId) =>
        _periods.SelectMany(p => p.Expenses)
            .Where(e => e.TripId == tripId && e.TripAccountId == tripAccountId)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.SortTime);

    /// <summary>
    /// Create the trip label set once, the first time it's needed. Each seed carries the category it files into, so
    /// picking "Stay" on the expense form also files the expense — tagging on a trip <i>replaces</i> categorising
    /// instead of being extra work on top of it, which is the only version people actually do while standing in a
    /// hotel lobby.
    /// <para>
    /// <b>Seeded once, ever</b> — if any trip tag already exists this is a no-op, so a second trip (or a client
    /// posting a different language's labels) can't mint a parallel set and split every future breakdown in two.
    /// A seed whose name matches a tag the user already has adopts that tag rather than colliding with it.
    /// </para>
    /// </summary>
    public IReadOnlyList<Tag> EnsureTripTags(IEnumerable<(string Name, string? Icon, Guid? CategoryId)> seeds)
    {
        if (_tags.Any(t => t.IsTripTag)) return TripTags.ToList();

        var created = new List<Tag>();
        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.Name)) continue;
            var tag = _tags.FirstOrDefault(t => NameEquals(t.Name, seed.Name)) ?? AddTag(seed.Name);
            tag.SetTripTag(true);
            tag.SetArchived(false);   // it's about to be offered on every trip expense form
            if (tag.Icon is null) tag.SetIcon(seed.Icon);
            if (tag.CategoryId is null && seed.CategoryId is { } cid && FindCategory(cid) is not null)
                tag.SetCategory(cid);
            created.Add(tag);
        }
        return created;
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
        // No "it has sub-categories" branch any more: the tree is flattened on load, so a category with children
        // cannot reach this method. A blocker naming a concept the app no longer has would be unreadable advice.
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
    /// instead of refusing (which is all <see cref="RemoveCategory"/> can do).
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
        _ = FindCategory(targetId)
            ?? throw new InvalidOperationException("The category to move the expenses to doesn't exist.");

        foreach (var period in _periods)
        {
            foreach (var expense in period.Expenses.Where(e => e.CategoryId == categoryId))
                expense.MoveToCategory(targetId);
            period.RemoveBudgetIfAny(categoryId);
        }
        foreach (var tag in _tags.Where(t => t.CategoryId == categoryId))
            tag.SetCategory(null);
        _categories.Remove(category);
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

    /// <summary>Re-price a debt's monthly installment without touching its balance — the "keep the end date" half of
    /// a lump-sum payment. See <see cref="Savings.SavingCategory.SetDebtInstallment"/>.</summary>
    public void SetSavingDebtInstallment(Guid savingCategoryId, decimal installment) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetDebtInstallment(installment);

    /// <summary>Set a debt bucket's residual/balloon — the sum a lease's schedule amortises down to rather than
    /// through. 0 clears it (an ordinary loan).</summary>
    public void SetSavingDebtResidual(Guid savingCategoryId, decimal residual) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.")).SetDebtResidual(residual);

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
    /// <remarks>This is the <b>extra</b> path: money deployed onto a loan over and above its installment, which is
    /// the only kind of repayment that can put the loan ahead of schedule. Hence <c>isExtraRepayment: true</c> — see
    /// <see cref="Savings.SavingCategory.DebtExtraPrincipalRepaid"/>.</remarks>
    public void RecordSavingDebtPayment(Guid savingCategoryId, decimal amount, DateOnly? asOf = null) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .RecordDebtPayment(amount, asOf, isExtraRepayment: true);

    /// <summary>
    /// Principal deployed at <paramref name="savingCategoryId"/> <b>after</b> <paramref name="asOf"/>, over and
    /// above its installments — the dated half of the story <see cref="Savings.SavingCategory"/> cannot tell.
    /// <para>
    /// ★ The bucket keeps only <c>DebtExtraPrincipalRepaid</c>, an <b>undated running total</b>, which is enough to
    /// say "you are ahead" and useless for saying "you were ahead by this much in June". The dated facts are the
    /// disbursement allocations, and they live on the periods — so the reconstruction has to happen here.
    /// </para>
    /// <para>⚠️ Disbursements only. A logged installment also lowers the balance, but the schedule walk already
    /// accounts for it, and adding it back here would count that month's payment twice.</para>
    /// </summary>
    public decimal ExtraRepaidAfter(Guid savingCategoryId, DateOnly asOf) =>
        Periods.SelectMany(p => p.SavingAllocations)
            .Where(a => a.IsDisbursement && a.SavingCategoryId == savingCategoryId && a.Date > asOf)
            .Sum(a => Math.Abs(a.Amount.Amount));

    /// <summary>
    /// Total owed across every live debt bucket on <paramref name="asOf"/>, reconstructed for past dates — the
    /// series behind "Debt owed" in Trends over time.
    /// <para>
    /// ★ This is the whole fix for a chart that used to read <i>"No change over this window"</i> on an account that
    /// had just paid a loan down: it walks each bucket's schedule backward AND restores the prepayments made since,
    /// which are the two halves that were both missing. Either alone is wrong — the schedule alone draws a curve
    /// that never shows the payment, and the prepayments alone ignore the interest that was accruing.
    /// </para>
    /// </summary>
    public decimal DebtOwedOn(DateOnly asOf) =>
        SavingCategories
            .Where(s => s.IsDebt && s.DebtOriginalBalance > 0m)
            .Sum(s => Math.Max(0m, s.DebtOwedOn(asOf, ExtraRepaidAfter(s.Id, asOf))));

    /// <summary>
    /// Make <paramref name="savingCategoryId"/> the emergency fund, clearing the flag from whichever bucket held it —
    /// there is one answer to "how long could I last", so two funds claiming it would both be measuring the same
    /// expenses. Pass <c>false</c> to simply clear it.
    /// </summary>
    public void SetEmergencyFund(Guid savingCategoryId, bool isEmergency)
    {
        var target = FindSavingCategory(savingCategoryId)
            ?? throw new InvalidOperationException("Saving category not found.");
        if (isEmergency)
            foreach (var other in _savingCategories.Where(s => s.Id != savingCategoryId))
                other.SetEmergencyFund(false);
        target.SetEmergencyFund(isEmergency);
    }

    /// <summary>The bucket currently marked as the emergency fund, if any.</summary>
    public Savings.SavingCategory? EmergencyFund => _savingCategories.FirstOrDefault(s => s.IsEmergencyFund);

    /// <summary>
    /// What the emergency fund should hold: three months of essential spending, rounded up to the nearest 500.
    /// </summary>
    /// <remarks>
    /// <b>The monthly figure is an average over completed periods, not this period's running total.</b> Read
    /// literally, "the sum of essential expenses" mid-month would put the target near zero on the 2nd and trip it
    /// upward all month — a goal that grows as you spend is the opposite of a goal. Up to
    /// <paramref name="lookbackPeriods"/> completed periods are averaged, which is also what makes the figure
    /// resilient to one unusual month. With no completed period yet, the open one is used: a rough number beats no
    /// number while the account is new.
    /// <para>
    /// The rounding is doing real work beyond tidiness: it damps the target so an ordinary week's shopping can't
    /// move it, which is what lets the figure be derived live without the goal twitching under the user.
    /// </para>
    /// Returns null when nothing is marked essential — the app has nothing to base a claim on, and inventing a
    /// target from total spending would quietly redefine what "essential" means.
    /// </remarks>
    public decimal? EmergencyFundTarget(int months = 3, int lookbackPeriods = 6) =>
        EssentialSpendPerPeriod(lookbackPeriods) is { } perPeriod
            ? Math.Ceiling(perPeriod * months / 500m) * 500m
            : null;

    /// <summary>
    /// What the essential categories actually cost in a typical period — the figure <see cref="EmergencyFundTarget"/>
    /// is built from, exposed so the UI can state the basis rather than an unexplained number.
    /// <para>
    /// <b>Read the basis from here, never as <c>target / months</c>.</b> The target is rounded UP to the nearest 500,
    /// so dividing it back yields a plausible-looking monthly figure the user never actually spent — a fabricated
    /// number presented as a fact about their own spending.
    /// </para>
    /// </summary>
    public decimal? EssentialSpendPerPeriod(int lookbackPeriods = 6)
    {
        // Flat: the inherited case (a sub-category counting because its parent was essential) went away with the
        // tree — that spend now sits on the essential category itself, so a plain filter sees all of it.
        var essential = _categories.Where(c => c.IsEssential).Select(c => c.Id).ToHashSet();
        if (essential.Count == 0) return null;

        var closed = _periods.Where(p => p.Status == PeriodStatus.Closed).TakeLast(lookbackPeriods).ToList();
        var basis = closed.Count > 0 ? closed : _periods.TakeLast(1).ToList();
        if (basis.Count == 0) return null;

        var perPeriod = basis.Sum(p => p.Expenses.Where(e => essential.Contains(e.CategoryId))
                                        .Sum(e => e.Amount.Amount)) / basis.Count;
        return perPeriod > 0m ? decimal.Round(perPeriod, 2) : null;
    }

    /// <summary>One period that cost noticeably more than this account's usual, and the category that drove it.</summary>
    /// <param name="Excess">How much above the typical period this one ran.</param>
    /// <param name="DriverExcess">How much of <paramref name="Excess"/> that one category accounts for.</param>
    public readonly record struct CostHeavyPeriod(
        DateOnly From, DateOnly To, decimal Total, decimal Excess, Guid? DriverCategoryId, decimal DriverExcess);

    /// <summary>
    /// Closed periods that ran materially above this account's typical spend, worst first — the factual half of
    /// "which months cost you most", with the category that drove each one.
    /// </summary>
    /// <remarks>
    /// <b>This deliberately observes and does not predict.</b> It reports what happened; it does not claim a month
    /// will be expensive again, because with a year of history there is exactly one observation per month and a
    /// seasonal pattern cannot be told apart from a one-off. A second year earns that claim; this does not make it.
    /// <para>
    /// "Typical" is the <b>median</b> period, not the mean: one blow-out month drags a mean up far enough to hide
    /// itself, which is precisely the month this is looking for. Needs <paramref name="minPeriods"/> closed periods
    /// before it says anything at all — below that, "typical" describes nothing.
    /// </para>
    /// <para>
    /// The driver is the category whose own spend in that period most exceeds <i>its</i> median across periods, so
    /// a category that is simply always large (rent) never gets blamed for a month it didn't change in.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CostHeavyPeriod> CostHeavyPeriods(int minPeriods = 4, decimal threshold = 1.15m)
    {
        var closed = _periods.Where(p => p.Status == PeriodStatus.Closed).ToList();
        if (closed.Count < minPeriods) return [];

        var totals = closed.ToDictionary(p => p.Id, p => p.Expenses.Sum(e => e.Amount.Amount));
        var typical = Median(totals.Values);
        if (typical <= 0m) return [];

        // Each category's own median across the same periods, so "unusual" is judged per category.
        var categoryMedians = _categories.ToDictionary(
            c => c.Id,
            c => Median(closed.Select(p => p.Expenses.Where(e => e.CategoryId == c.Id).Sum(e => e.Amount.Amount))));

        var heavy = new List<CostHeavyPeriod>();
        foreach (var p in closed)
        {
            var total = totals[p.Id];
            if (total <= typical * threshold) continue;

            var driver = p.Expenses
                .GroupBy(e => e.CategoryId)
                .Select(g => (CategoryId: g.Key, Excess: g.Sum(e => e.Amount.Amount) - categoryMedians.GetValueOrDefault(g.Key)))
                .Where(x => x.Excess > 0m)
                .OrderByDescending(x => x.Excess)
                .FirstOrDefault();

            heavy.Add(new CostHeavyPeriod(p.From, p.To, total, decimal.Round(total - typical, 2),
                driver.CategoryId == Guid.Empty ? null : driver.CategoryId, decimal.Round(driver.Excess, 2)));
        }
        return heavy.OrderByDescending(h => h.Excess).ToList();
    }

    /// <summary>The typical (median) spend of a closed period — the baseline <see cref="CostHeavyPeriods"/> compares
    /// against. Null until there is enough history for "typical" to mean anything.</summary>
    public decimal? TypicalPeriodSpend(int minPeriods = 4)
    {
        var closed = _periods.Where(p => p.Status == PeriodStatus.Closed).ToList();
        if (closed.Count < minPeriods) return null;
        var median = Median(closed.Select(p => p.Expenses.Sum(e => e.Amount.Amount)));
        return median > 0m ? decimal.Round(median, 2) : null;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0m;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

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

    /// <summary>Set (or clear) the foreign currency a fund holds and the rate it was bought at — see
    /// <see cref="Fund.Currency"/> for why the rate belongs to the money rather than to the trip. Forward-only in
    /// effect: amounts are converted at entry time and stored in the account currency, so changing this can never
    /// rewrite what past expenses cost.</summary>
    public void SetFundCurrency(Guid fundId, string? currency, decimal? rate) =>
        (FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.")).SetCurrency(currency, rate);

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
