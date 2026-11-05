using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

public sealed class AccountServiceTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Accounts<T>(Func<AccountService, Task<T>> work) =>
        _db.InScope(sp => work(sp.GetRequiredService<AccountService>()));

    [Fact]
    public async Task Creates_subaccounts_and_lists_them_in_chart_order()
    {
        var utilities = await _db.CreateAccount("Utilities", AccountType.Expense, "6800");
        await Accounts(s => s.CreateAsync(new CreateAccountCommand("Electric", AccountType.Expense, "6810", utilities.Id, "Power")));
        await _db.CreateAccount("Checking", AccountType.Bank, "1000");

        var chart = await Accounts(s => s.GetChartAsync());

        Assert.Equal(["Checking", "Utilities", "Utilities:Electric"], chart.Ordered().Select(x => chart.FullName(x.Account.Id)));
    }

    [Fact]
    public async Task Rejects_subaccount_of_a_different_type()
    {
        var utilities = await _db.CreateAccount("Utilities", AccountType.Expense);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Accounts(s => s.CreateAsync(new CreateAccountCommand("Checking", AccountType.Bank, ParentId: utilities.Id))));
        Assert.Equal("account.parent_type", ex.Code);
    }

    [Fact]
    public async Task Update_moves_renames_and_audits_before_and_after()
    {
        var utilities = await _db.CreateAccount("Utilities", AccountType.Expense, "6800");
        var power = await _db.CreateAccount("Power", AccountType.Expense, "6900");

        await Accounts(s => s.UpdateAsync(new UpdateAccountCommand(power.Id, "Electric", AccountType.Expense, "6810", utilities.Id, null)));

        var chart = await Accounts(s => s.GetChartAsync());
        Assert.Equal("Utilities:Electric", chart.FullName(power.Id));
        Assert.Equal("6810", chart.Get(power.Id).Number);

        var audit = await _db.InDb(db => db.AuditRecords.Where(r => r.EntityId == power.Id).OrderBy(r => r.Id).ToListAsync());
        Assert.Equal([AuditAction.Created, AuditAction.Updated], audit.Select(r => r.Action));
        Assert.Contains("\"Name\":\"Power\"", audit[1].Before, StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"Electric\"", audit[1].After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_class_change_is_blocked_once_the_account_has_postings()
    {
        var cash = await _db.CreateAccount("Cash", AccountType.Bank);
        var misc = await _db.CreateAccount("Misc", AccountType.Expense);
        await _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 1, 5), [Dr(misc.Id, 10m), Cr(cash.Id, 10m)])));

        await Accounts(s => s.UpdateAsync(new UpdateAccountCommand(misc.Id, "Misc", AccountType.OtherExpense, null, null, null)));
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Accounts(s => s.UpdateAsync(new UpdateAccountCommand(misc.Id, "Misc", AccountType.Income, null, null, null))));
        Assert.Equal("account.type_class_change", ex.Code);
    }

    [Fact]
    public async Task Inactive_accounts_reject_postings_until_reactivated()
    {
        var cash = await _db.CreateAccount("Cash", AccountType.Bank);
        var rent = await _db.CreateAccount("Rent", AccountType.Expense);
        await Accounts(s => s.DeactivateAsync(rent.Id));

        Task<Domain.Ledger.JournalEntry> Post() => _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 1, 5), [Dr(rent.Id, 10m), Cr(cash.Id, 10m)])));

        Assert.Equal("journal.account_inactive", (await Assert.ThrowsAsync<DomainException>(Post)).Code);
        await Accounts(s => s.ReactivateAsync(rent.Id));
        await Post();
    }
}

public sealed class ChartImportTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<ChartImportResult> Preview(string csv) =>
        _db.InScope(sp => sp.GetRequiredService<ChartImportService>().PreviewAsync(csv));

    private Task<ChartImportResult> Import(string csv) =>
        _db.InScope(sp => sp.GetRequiredService<ChartImportService>().ImportAsync(csv));

    [Fact]
    public async Task A_quickbooks_chart_export_comes_in_as_it_is()
    {
        const string export = "Account,Type,Balance Total,Description,Accnt. #,Tax Line\n"
            + "Checking,Bank,100.00,,1000,<Unassigned>\n"
            + "Estimates,Non-Posting,,,,\n"
            + "Rent,Expense,,,6000,<Unassigned>\n";

        var preview = await Preview(export);

        Assert.True(preview.CanImport);
        Assert.Equal(ImportRowStatus.Skipped, preview.Rows.Single(r => r.FullName == "Estimates").Status);
        await Import(export);
        var chart = await Chart();
        Assert.NotNull(chart.FindByFullName("Rent"));
        Assert.Null(chart.FindByFullName("Estimates"));
    }

    private Task<ChartOfAccounts> Chart() =>
        _db.InScope(sp => sp.GetRequiredService<AccountService>().GetChartAsync());

    private static string StarterChart()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBook.slnx")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "OpenBook.Web", "wwwroot", "samples", "chart-of-accounts.csv"));
    }

    [Fact]
    public async Task The_shipped_starter_chart_imports_cleanly_and_reimporting_finds_nothing_new()
    {
        var csv = StarterChart();

        var result = await Import(csv);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(result.Rows.Count, result.NewCount);
        var chart = await Chart();
        Assert.Equal(result.NewCount, chart.All.Count);
        Assert.Equal(AccountType.AccountsReceivable, chart.FindByFullName("Accounts Receivable")!.Type);
        Assert.Equal("6810", chart.FindByFullName("Utilities:Electric")!.Number);

        var again = await Preview(csv);
        Assert.Equal(again.Rows.Count, again.ExistingCount);
        var ex = await Assert.ThrowsAsync<DomainException>(() => Import(csv));
        Assert.Equal("import.nothing_new", ex.Code);
    }

    [Fact]
    public async Task Accepts_quickbooks_headers_labels_and_children_before_parents()
    {
        const string csv = """
            "Accnt. #","Account","Type","Description"
            "6810","Utilities:Electric","Expense",""
            "1200","Customer Invoices","Accounts Receivable (A/R)","Open invoices"
            "6800","Utilities","Expenses",""
            "2700","Bank Loan","Long Term Liabilities",""
            """;

        var result = await Import(csv);

        Assert.Equal(4, result.NewCount);
        var chart = await Chart();
        Assert.Equal(AccountType.AccountsReceivable, chart.FindByFullName("Customer Invoices")!.Type);
        Assert.Equal(AccountType.LongTermLiability, chart.FindByFullName("Bank Loan")!.Type);
        Assert.NotNull(chart.FindByFullName("Utilities:Electric"));
        Assert.Equal([2, 3, 4, 5], result.Rows.Select(r => r.Line));
    }

    [Fact]
    public async Task Any_error_imports_nothing_and_each_row_says_why()
    {
        await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        const string csv = """
            Number,Name,Type
            2000,Rent,Expense
            1000,Payroll,Expense
            ,Travel:Meals,Expense
            ,Mystery,Wizardry
            ,Rent,Expense
            ,,Expense
            """;

        var preview = await Preview(csv);
        string? Message(int line) => preview.Rows.Single(r => r.Line == line).Message;

        Assert.Equal(1, preview.NewCount);
        Assert.Equal(5, preview.ErrorCount);
        Assert.Contains("already used by 'Checking'", Message(3), StringComparison.Ordinal);
        Assert.Contains("Parent account 'Travel'", Message(4), StringComparison.Ordinal);
        Assert.Contains("'Wizardry' isn't a known account type", Message(5), StringComparison.Ordinal);
        Assert.Contains("more than once", Message(6), StringComparison.Ordinal);
        Assert.Contains("name is missing", Message(7), StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Import(csv));
        Assert.Equal("import.has_errors", ex.Code);
        Assert.Single((await Chart()).All);
    }

    [Theory]
    [InlineData("Name,Type\n", "import.empty")]
    [InlineData("Number,Description\n1,x\n", "import.no_name_column")]
    [InlineData("Name,Balance\nRent,0\n", "import.no_type_column")]
    public async Task File_level_problems_are_reported(string csv, string code) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => Preview(csv))).Code);
}
