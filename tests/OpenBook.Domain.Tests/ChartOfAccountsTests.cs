using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Tests;

public class ChartOfAccountsTests
{
    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    /// <summary>Utilities (6800) > Electric (6810); Checking (1000); Rent (6500).</summary>
    private static (ChartOfAccounts Chart, Account Utilities, Account Electric, Account Checking, Account Rent) Sample()
    {
        var utilities = Account.Create("Utilities", AccountType.Expense, "6800");
        var electric = Account.Create("Electric", AccountType.Expense, "6810", utilities.Id);
        var checking = Account.Create("Checking", AccountType.Bank, "1000");
        var rent = Account.Create("Rent", AccountType.Expense, "6500");
        return (new ChartOfAccounts([electric, rent, utilities, checking]), utilities, electric, checking, rent);
    }

    [Theory]
    [InlineData("Rent: Office", "account.name_colon")]
    [InlineData("  ", "account.name_required")]
    public void Invalid_names_are_rejected(string name, string code) =>
        Assert.Equal(code, CodeOf(() => Account.Create(name, AccountType.Expense)));

    [Theory]
    [InlineData("10 00")]
    [InlineData("123456789012345678901")]
    [InlineData("10/00")]
    public void Invalid_numbers_are_rejected(string number) =>
        Assert.Equal("account.number_invalid", CodeOf(() => Account.Create("Rent", AccountType.Expense, number)));

    [Fact]
    public void Every_type_has_a_class() =>
        Assert.All(Enum.GetValues<AccountType>(), t => Assert.True(Enum.IsDefined(t.ClassOf())));

    [Fact]
    public void Full_names_and_lookup_use_the_colon_path()
    {
        var (chart, _, electric, _, _) = Sample();

        Assert.Equal("Utilities:Electric", chart.FullName(electric.Id));
        Assert.Equal(2, chart.Depth(electric.Id));
        Assert.Same(electric, chart.FindByFullName(" utilities : ELECTRIC "));
        Assert.Null(chart.FindByFullName("Electric"));
    }

    [Fact]
    public void Ordered_puts_balance_sheet_first_then_numbers_with_children_under_parents()
    {
        var (chart, _, _, _, _) = Sample();

        Assert.Equal(
            ["Checking", "Rent", "Utilities", "Electric"],
            chart.Ordered().Select(x => x.Account.Name));
        Assert.Equal([1, 1, 1, 2], chart.Ordered().Select(x => x.Depth));
    }

    [Fact]
    public void Subaccount_must_share_parent_type()
    {
        var (chart, utilities, _, _, _) = Sample();
        Assert.Equal("account.parent_type", CodeOf(() => chart.EnsureCanAdd("Water", AccountType.OtherExpense, null, utilities.Id)));
    }

    [Fact]
    public void Nesting_is_limited_to_five_levels()
    {
        var accounts = new List<Account> { Account.Create("L1", AccountType.Expense) };
        for (var level = 2; level <= ChartOfAccounts.MaxDepth; level++)
            accounts.Add(Account.Create($"L{level}", AccountType.Expense, parentId: accounts[^1].Id));
        var chart = new ChartOfAccounts(accounts);

        Assert.Equal("account.too_deep", CodeOf(() => chart.EnsureCanAdd("L6", AccountType.Expense, null, accounts[^1].Id)));
    }

    [Fact]
    public void Sibling_names_are_unique_ignoring_case_but_cousins_may_share_names()
    {
        var (chart, utilities, _, _, _) = Sample();

        Assert.Equal("account.name_taken", CodeOf(() => chart.EnsureCanAdd("ELECTRIC", AccountType.Expense, null, utilities.Id)));
        chart.EnsureCanAdd("Electric", AccountType.Expense, null, parentId: null);
    }

    [Fact]
    public void Numbers_are_unique() =>
        Assert.Equal("account.number_taken", CodeOf(() => Sample().Chart.EnsureCanAdd("Water", AccountType.Expense, "6810", null)));

    [Fact]
    public void Account_cannot_move_under_its_own_subaccount()
    {
        var (chart, utilities, electric, _, _) = Sample();
        Assert.Equal("account.parent_cycle",
            CodeOf(() => chart.EnsureCanUpdate(utilities, "Utilities", "6800", AccountType.Expense, electric.Id, hasPostings: false)));
    }

    [Fact]
    public void Moving_a_subtree_counts_its_height()
    {
        var accounts = new List<Account> { Account.Create("L1", AccountType.Expense) };
        for (var level = 2; level <= 4; level++)
            accounts.Add(Account.Create($"L{level}", AccountType.Expense, parentId: accounts[^1].Id));
        var branch = Account.Create("Branch", AccountType.Expense);
        var leaf = Account.Create("Leaf", AccountType.Expense, parentId: branch.Id);
        var chart = new ChartOfAccounts([.. accounts, branch, leaf]);

        // Branch would be level 5 and Leaf level 6.
        Assert.Equal("account.too_deep",
            CodeOf(() => chart.EnsureCanUpdate(branch, "Branch", null, AccountType.Expense, accounts[^1].Id, hasPostings: false)));
    }

    [Fact]
    public void Type_cannot_change_while_subaccounts_exist()
    {
        var (chart, utilities, _, _, _) = Sample();
        Assert.Equal("account.type_has_children",
            CodeOf(() => chart.EnsureCanUpdate(utilities, "Utilities", "6800", AccountType.OtherExpense, null, hasPostings: false)));
    }

    [Fact]
    public void With_postings_type_can_change_only_within_its_class()
    {
        var (chart, _, _, _, rent) = Sample();

        chart.EnsureCanUpdate(rent, "Rent", "6500", AccountType.OtherExpense, null, hasPostings: true);
        Assert.Equal("account.type_class_change",
            CodeOf(() => chart.EnsureCanUpdate(rent, "Rent", "6500", AccountType.Income, null, hasPostings: true)));
        chart.EnsureCanUpdate(rent, "Rent", "6500", AccountType.Income, null, hasPostings: false);
    }

    [Fact]
    public void Deactivating_requires_inactive_subaccounts_and_reactivating_an_active_parent()
    {
        var (chart, utilities, electric, _, _) = Sample();

        Assert.Equal("account.active_children", CodeOf(() => chart.EnsureCanDeactivate(utilities)));
        electric.Deactivate();
        chart.EnsureCanDeactivate(utilities);
        utilities.Deactivate();

        Assert.Equal("account.parent_inactive", CodeOf(() => chart.EnsureCanReactivate(electric)));
        Assert.Equal("account.parent_inactive", CodeOf(() => chart.EnsureCanAdd("Water", AccountType.Expense, null, utilities.Id)));
    }

    [Theory]
    [InlineData(EntryBasis.Both, ReportBasis.Accrual, true)]
    [InlineData(EntryBasis.Both, ReportBasis.Cash, true)]
    [InlineData(EntryBasis.AccrualOnly, ReportBasis.Accrual, true)]
    [InlineData(EntryBasis.AccrualOnly, ReportBasis.Cash, false)]
    [InlineData(EntryBasis.CashOnly, ReportBasis.Accrual, false)]
    [InlineData(EntryBasis.CashOnly, ReportBasis.Cash, true)]
    public void Entry_basis_decides_which_reports_include_it(EntryBasis entry, ReportBasis report, bool expected) =>
        Assert.Equal(expected, entry.AppliesTo(report));
}
