using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

/// <summary>Starter charts come mapped to the tax return, so a year's tax summary needs no setting up.</summary>
public sealed class ChartTemplateTaxTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private async Task<IReadOnlyList<Account>> Applied(string template, BusinessType type)
    {
        var company = await In<CompanySettingsService, Company>(s => s.GetAsync());
        await In<CompanySettingsService, Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, type, null, null, null, null, 1, false, company.DefaultReportBasis)));
        await In<ChartTemplates, ChartImportResult>(s => s.ApplyAsync(template));
        return await In<AccountService, IReadOnlyList<Account>>(s => s.ListAsync());
    }

    [Theory]
    [InlineData("sole-proprietor", BusinessType.SoleProprietorship, TaxForm.ScheduleC)]
    [InlineData("partnership", BusinessType.Partnership, TaxForm.Form1065)]
    [InlineData("corporation", BusinessType.SCorporation, TaxForm.Form1120S)]
    [InlineData("corporation", BusinessType.CCorporation, TaxForm.Form1120)]
    public async Task Every_income_and_expense_account_has_a_line_of_the_companys_form(string template, BusinessType type, TaxForm form)
    {
        var accounts = await Applied(template, type);
        var mapped = accounts.Where(a => !a.Type.IsBalanceSheet()).ToList();
        Assert.NotEmpty(mapped);
        Assert.All(mapped, a =>
        {
            var line = TaxLines.Find(a.TaxLineKey);
            Assert.True(line is not null, $"{a.Name} has no tax line");
            Assert.True(line.Form == form || line.Key == TaxLines.NonDeductibleKey, $"{a.Name} is on {line.Form}");
        });
    }

    [Fact]
    public async Task A_sole_proprietor_has_the_schedule_c_categories()
    {
        var accounts = await Applied("sole-proprietor", BusinessType.SoleProprietorship);
        foreach (var (name, key) in new[]
        {
            ("Car and Truck Expenses", "c.9"), ("Commissions and Fees", "c.10"), ("Supplies", "c.22"), ("Repairs and Maintenance", "c.21"), ("Taxes and Licenses", "c.23"),
        })
            Assert.Equal(key, accounts.Single(a => a.Name == name).TaxLineKey);
    }

    [Fact]
    public async Task Stock_keepers_get_their_asset_account_in_the_chart_with_tax_lines()
    {
        await In<ChartTemplates, ChartImportResult>(s => s.ApplyAsync("sole-proprietor", new StarterAnswers(StarterSales.Both, KeepsStock: true)));
        var accounts = await In<AccountService, IReadOnlyList<Account>>(s => s.ListAsync());
        Assert.Contains(accounts, a => a.Name == "Inventory Asset" && a.Type == AccountType.OtherCurrentAsset);
    }
}
