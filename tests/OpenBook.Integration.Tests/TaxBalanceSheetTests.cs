using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>Schedule L and Form 990 Part X from the books (C347, C074), on the 2025 forms' lines.</summary>
public sealed class TaxBalanceSheetTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private async Task BusinessType(BusinessType type)
    {
        var company = await In<CompanySettingsService, Company>(s => s.GetAsync());
        await In<CompanySettingsService, Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, type, null, null, null, null, 1, false, company.DefaultReportBasis)));
    }

    private Task<JournalEntry> Post(DateOnly date, params JournalLineDraft[] lines) =>
        In<PostingEngine, JournalEntry>(e => e.PostAsync(new PostJournalEntryCommand(date, lines)));

    private Task<ReportTable> Report() =>
        _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("tax-balance-sheet", ReportParameters.Parse("preset=Custom&from=2026-01-01&to=2026-12-31")));

    [Fact]
    public async Task An_s_corporations_schedule_l_puts_each_account_on_its_line_and_balances()
    {
        await BusinessType(Domain.Companies.BusinessType.SCorporation);
        var checking = await _db.CreateAccount("Checking", AccountType.Bank);
        var van = await _db.CreateAccount("Van", AccountType.FixedAsset);
        var depreciation = await _db.CreateAccount("Accumulated Depreciation", AccountType.FixedAsset);
        var loan = await _db.CreateAccount("Loan from shareholder", AccountType.LongTermLiability);
        var stock = await _db.CreateAccount("Common Stock", AccountType.Equity);
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var expense = await _db.CreateAccount("Depreciation Expense", AccountType.Expense);

        await Post(new DateOnly(2025, 12, 1), Dr(checking.Id, 1000m), Cr(stock.Id, 1000m));
        await Post(new DateOnly(2026, 2, 1), Dr(van.Id, 20000m), Cr(loan.Id, 20000m));
        await Post(new DateOnly(2026, 6, 1), Dr(checking.Id, 5000m), Cr(sales.Id, 5000m));
        await Post(new DateOnly(2026, 12, 31), Dr(expense.Id, 4000m), Cr(depreciation.Id, 4000m));

        var report = await Report();
        Assert.Equal("Form 1120-S Schedule L: balance sheets per books", report.Title);
        Assert.Equal((1000m, 6000m), ((decimal)report.Find("1", 2)!, (decimal)report.Find("1", 3)!));
        Assert.Equal(20000m, report.Find("10a", 3));
        Assert.Equal(-4000m, report.Find("10b", 3));
        Assert.Equal(20000m, report.Find("19", 3));
        Assert.Equal(1000m, report.Find("22", 3));
        Assert.Equal(1000m, report.Find("24", 3)); // the year's profit: 5,000 less 4,000 of depreciation
        var totals = report.Rows.Where(r => r.Style == ReportRowStyle.Total).Select(r => r.Cells[3].Value).ToList();
        Assert.Equal([22000m, 22000m], totals);
        Assert.DoesNotContain(report.Rows, r => r.Cells[1].Value is string s && s.StartsWith("Doesn't balance", StringComparison.Ordinal));

        // A mapped account goes on its line; lines must suit the account's side of the books.
        await In<AccountService, int>(s => s.MapTaxLinesAsync(new Dictionary<Guid, string?> { [loan.Id] = "s.L20" }));
        Assert.Equal(20000m, (await Report()).Find("20", 3));
        Assert.Equal("account.tax_line_balance_sheet", (await Assert.ThrowsAsync<DomainException>(() =>
            In<AccountService, int>(s => s.MapTaxLinesAsync(new Dictionary<Guid, string?> { [sales.Id] = "s.L1" })))).Code);
        Assert.Equal("account.tax_line_balance_sheet", (await Assert.ThrowsAsync<DomainException>(() =>
            In<AccountService, int>(s => s.MapTaxLinesAsync(new Dictionary<Guid, string?> { [checking.Id] = "s.1a" })))).Code);
    }

    [Fact]
    public async Task A_nonprofits_part_x_splits_net_assets_by_donor_restriction()
    {
        await BusinessType(Domain.Companies.BusinessType.Nonprofit);
        var checking = await _db.CreateAccount("Checking", AccountType.Bank);
        var savings = await _db.CreateAccount("Savings", AccountType.Bank);
        var restricted = await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Restricted Gifts", AccountType.Income, DonorRestricted: true)));
        var gifts = await _db.CreateAccount("Gifts", AccountType.Income);

        await Post(new DateOnly(2026, 3, 1), Dr(checking.Id, 700m), Cr(gifts.Id, 700m));
        await Post(new DateOnly(2026, 3, 2), Dr(savings.Id, 300m), Cr(restricted.Id, 300m));

        var report = await Report();
        Assert.Equal("Form 990 Part X: balance sheet", report.Title);
        Assert.Equal((700m, 300m), ((decimal)report.Find("1", 3)!, (decimal)report.Find("2", 3)!));
        Assert.Equal((700m, 300m), ((decimal)report.Find("27", 3)!, (decimal)report.Find("28", 3)!));
    }

    [Fact]
    public async Task Schedule_c_has_no_balance_sheet_and_the_lines_follow_the_2025_forms()
    {
        Assert.Contains("Schedule C has no balance sheet", (await Report()).Rows[0].Cells[0].Value as string, StringComparison.Ordinal);
        Assert.Equal("21", TaxLines.Find("p.L21")!.Line);
        Assert.Equal("Partners' capital accounts", TaxLines.Find("p.L21")!.Name);
        Assert.Equal("Loans from shareholders", TaxLines.Find("s.L19")!.Name);
        Assert.Equal("Retained earnings", TaxLines.Find("s.L24")!.Name);
        Assert.Equal("Retained earnings, unappropriated", TaxLines.Find("k.L25")!.Name);
        Assert.Equal("Less cost of treasury stock", TaxLines.Find("k.L27")!.Name);
        Assert.Equal("Other investments", TaxLines.Find("s.L9")!.Name);
        Assert.Equal("Other investments", TaxLines.Find("p.L8")!.Name);
        Assert.Equal("Net assets with donor restrictions", TaxLines.Find("n.10.28")!.Name);
        Assert.All(TaxLines.BalanceSheetFor(TaxForm.Form990), l => Assert.True(l.BalanceSheet));
        Assert.DoesNotContain(TaxLines.For(TaxForm.Form1120S), l => l.BalanceSheet);
    }
}
