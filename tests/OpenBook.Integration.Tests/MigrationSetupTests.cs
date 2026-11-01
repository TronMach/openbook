using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Import.Migration;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

/// <summary>Setting up a company from an export: details read from it, imported with it or nothing kept.</summary>
public sealed class MigrationSetupTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new(setUpCompany: false);

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private static byte[] Qbxml(decimal credit = 900m) => Encoding.UTF8.GetBytes($"""
        <QBXML><QBXMLMsgsRs>
          <CompanyQueryRs><CompanyRet><CompanyName>Rock Castle</CompanyName><FirstMonthFiscalYear>July</FirstMonthFiscalYear><TaxForm>Form1120S</TaxForm></CompanyRet></CompanyQueryRs>
          <AccountQueryRs>
            <AccountRet><FullName>Checking</FullName><AccountType>Bank</AccountType></AccountRet>
            <AccountRet><FullName>Sales</FullName><AccountType>Income</AccountType></AccountRet>
          </AccountQueryRs>
          <JournalEntryQueryRs><JournalEntryRet><TxnDate>2026-07-01</TxnDate>
            <JournalDebitLine><AccountRef><FullName>Checking</FullName></AccountRef><Amount>900.00</Amount></JournalDebitLine>
            <JournalCreditLine><AccountRef><FullName>Sales</FullName></AccountRef><Amount>{credit}</Amount></JournalCreditLine>
          </JournalEntryRet></JournalEntryQueryRs>
        </QBXMLMsgsRs></QBXML>
        """);

    private Task<MigrationSetupResult> SetUpAsync(byte[] file) => _db.InScope(sp => sp.GetRequiredService<MigrationSetupService>().SetUpAsync(
        new SetUpCompanyCommand("Rock Castle", BusinessType.SCorporation, "USD", 7, "rocky", "correct horse battery"), nameof(MigrationSource.QuickBooksQbxml), file));

    [Fact]
    public async Task The_company_is_set_up_and_the_file_imported_together()
    {
        Assert.Equal(new SourceCompany("Rock Castle", FiscalYearStartMonth: 7, BusinessType: BusinessType.SCorporation),
            MigrationSetupService.CompanyIn(nameof(MigrationSource.QuickBooksQbxml), Qbxml()));

        Assert.NotNull((await SetUpAsync(Qbxml())).Owner);
        Assert.Equal(1, await _db.InDb(db => db.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task A_file_with_problems_sets_up_nothing()
    {
        var result = await SetUpAsync(Qbxml(credit: 800m));

        Assert.Null(result.Owner);
        Assert.Equal(1, result.Result.ErrorCount);
        Assert.False(await _db.InScope(sp => sp.GetRequiredService<CompanySetupService>().IsSetUpAsync()));
    }

    [Theory]
    [InlineData(MigrationSource.QuickBooksOnline, """{"QueryResponse": {"CompanyInfo": [{"CompanyName": "Craig's", "FiscalYearStartMonth": "April"}]}, "Preferences": {"CurrencyPrefs": {"HomeCurrency": {"value": "CAD"}}}}""", "Craig's", 4, "CAD")]
    [InlineData(MigrationSource.GnuCash, """
        <gnc-v2 xmlns:gnc="g" xmlns:book="b" xmlns:act="a" xmlns:cmdty="c" xmlns:slot="s"><gnc:book>
          <book:slots><slot><slot:key>options</slot:key><slot:value><slot><slot:key>Business</slot:key><slot:value>
            <slot><slot:key>Company Name</slot:key><slot:value>Atelier Rive</slot:value></slot></slot:value></slot></slot:value></slot></book:slots>
          <gnc:account><act:commodity><cmdty:space>CURRENCY</cmdty:space><cmdty:id>EUR</cmdty:id></act:commodity></gnc:account>
        </gnc:book></gnc-v2>
        """, "Atelier Rive", null, "EUR")]
    [InlineData(MigrationSource.QuickBooksIif, "!ACCNT\tNAME\tACCNTTYPE\nACCNT\tChecking\tBANK\n", null, null, null)]
    public void Exports_give_the_company_details_they_have(MigrationSource source, string file, string? name, int? month, string? currency)
    {
        var company = MigrationReaders.ReadCompany(source, Encoding.UTF8.GetBytes(file));
        Assert.Equal((name, month, currency), (company?.Name, company?.FiscalYearStartMonth, company?.Currency));
    }
}
