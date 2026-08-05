using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using Xunit;

namespace FinApp.Domain.Tests;

public class ContributionsTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Contribution_categories_reject_dupes_and_block_removal_when_referenced()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "A").UserId;
        var salary = account.AddContributionCategory("Salary");
        Assert.Throws<InvalidOperationException>(() => account.AddContributionCategory(" salary "));

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member, M(500), salary.Id, account.FundId("Bank"), new DateOnly(2026, 1, 5));

        Assert.Equal("deposits reference it", account.ContributionCategoryRemovalBlocker(salary.Id));
        Assert.Throws<InvalidOperationException>(() => account.RemoveContributionCategory(salary.Id));
    }

    [Fact]
    public void Deposit_attributed_to_a_fund_raises_that_fund_balance()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "A").UserId;
        var salary = account.AddContributionCategory("Salary");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        period.Deposit(member, M(800), salary.Id, account.FundId("Bank"), new DateOnly(2026, 1, 3));

        Assert.Equal(M(800), period.FundBalance(account.FundId("Bank")));
        Assert.Equal(M(0), period.FundBalance(account.FundId("Cash")));
        Assert.Equal(M(800), period.ContributionsPaidTotal);
    }

    [Fact]
    public void Every_deposit_is_its_own_row_even_for_the_same_member_category_and_fund()
    {
        // Two salary payments in a month are two ledger events. Merging them (the old behaviour) showed one row
        // holding the total under the date of the FIRST payment, so the ledger stopped saying when the money
        // arrived and an edit or a delete acted on the merged sum instead of the entry the user picked.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "A").UserId;
        var bank = account.FundId("Bank");
        var salary = account.AddContributionCategory("Salary");
        var vouchers = account.AddContributionCategory("Vouchers");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        period.Deposit(member, M(100), salary.Id, bank, new DateOnly(2026, 1, 5));
        period.Deposit(member, M(50), salary.Id, bank, new DateOnly(2026, 1, 20));
        period.Deposit(member, M(30), vouchers.Id, bank, new DateOnly(2026, 1, 20));

        Assert.Equal(3, period.Contributions.Count);
        var salaryRows = period.Contributions.Where(c => c.CategoryId == salary.Id).OrderBy(c => c.Date).ToList();
        Assert.Equal([M(100), M(50)], salaryRows.Select(c => c.Paid));
        // Each keeps its own date — the whole point of not merging.
        Assert.Equal([new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 20)], salaryRows.Select(c => c.Date));
        Assert.Equal(M(180), period.ContributionsPaidTotal);
    }

    [Fact]
    public void Deposit_rows_can_be_edited_and_removed_independently()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "A").UserId;
        var bank = account.FundId("Bank");
        var salary = account.AddContributionCategory("Salary");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var first = period.Deposit(member, M(100), salary.Id, bank, new DateOnly(2026, 1, 5));
        var second = period.Deposit(member, M(50), salary.Id, bank, new DateOnly(2026, 1, 20));

        period.RemoveContribution(first.Id);

        var remaining = Assert.Single(period.Contributions);
        Assert.Equal(second.Id, remaining.Id);
        Assert.Equal(M(50), period.ContributionsPaidTotal);
    }
}
