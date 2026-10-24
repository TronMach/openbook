using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

public sealed class ReportingAndSettingsTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _cash = null!;
    private Account _payable = null!;
    private Account _expense = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _cash = await _db.CreateAccount("Checking", AccountType.Bank);
        _payable = await _db.CreateAccount("Accrued Liabilities", AccountType.OtherCurrentLiability);
        _expense = await _db.CreateAccount("Utilities", AccountType.Expense);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<JournalEntry> Post(DateOnly date, decimal amount, Account debit, Account credit, EntryBasis basis = EntryBasis.Both) =>
        _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .PostAsync(new PostJournalEntryCommand(date, [Dr(debit.Id, amount), Cr(credit.Id, amount)], Basis: basis)));

    private Task<TrialBalance> TrialBalance(ReportBasis? basis = null) =>
        _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));

    private static decimal Debit(TrialBalance tb, Account a) => tb.Lines.SingleOrDefault(l => l.AccountId == a.Id)?.Debit.Amount ?? 0m;

    private static UpdateCompanySettingsCommand Settings(bool useNumbers = false, ReportBasis basis = ReportBasis.Cash, int month = 4) =>
        new("Test Co", BusinessType.SingleMemberLlc, null, null, null, null, month, useNumbers, basis);

    [Fact]
    public async Task Entry_basis_controls_which_report_basis_includes_it()
    {
        await Post(new DateOnly(2026, 3, 1), 100m, _expense, _cash);
        await Post(new DateOnly(2026, 3, 31), 40m, _expense, _payable, EntryBasis.AccrualOnly);
        await Post(new DateOnly(2026, 3, 31), 5m, _expense, _cash, EntryBasis.CashOnly);

        var accrual = await TrialBalance(ReportBasis.Accrual);
        var cash = await TrialBalance(ReportBasis.Cash);

        Assert.Equal(140m, Debit(accrual, _expense));
        Assert.Equal(105m, Debit(cash, _expense));
        Assert.DoesNotContain(cash.Lines, l => l.AccountId == _payable.Id);
        Assert.True(accrual.IsBalanced);
        Assert.True(cash.IsBalanced);
    }

    [Fact]
    public async Task Reports_default_to_the_company_basis()
    {
        await Post(new DateOnly(2026, 3, 31), 40m, _expense, _payable, EntryBasis.AccrualOnly);
        Assert.Equal(ReportBasis.Accrual, (await TrialBalance()).Basis);

        await _db.InScope(sp => sp.GetRequiredService<CompanySettingsService>().UpdateAsync(Settings(basis: ReportBasis.Cash)));

        var tb = await TrialBalance();
        Assert.Equal(ReportBasis.Cash, tb.Basis);
        Assert.Empty(tb.Lines);
    }

    [Fact]
    public async Task Voiding_reverses_on_the_same_basis()
    {
        var entry = await Post(new DateOnly(2026, 3, 31), 40m, _expense, _payable, EntryBasis.AccrualOnly);

        var reversal = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(entry.Id, "wrong period"));

        Assert.Equal(EntryBasis.AccrualOnly, reversal.Basis);
        Assert.Empty((await TrialBalance(ReportBasis.Accrual)).Lines);
    }

    [Fact]
    public async Task Basis_of_a_posted_entry_cannot_be_changed_in_the_database()
    {
        await Post(new DateOnly(2026, 3, 31), 40m, _expense, _payable, EntryBasis.AccrualOnly);

        var ex = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() =>
            _db.InDb(db => db.Database.ExecuteSqlRawAsync("""UPDATE "JournalEntries" SET "Basis" = 'Both';""")));
        Assert.Contains("immutable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_lists_newest_first_with_date_filter_and_paging()
    {
        for (var day = 1; day <= 5; day++)
            await Post(new DateOnly(2026, 3, day), day, _expense, _cash);

        var (page1, total) = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .ListAsync(new JournalEntryFilter(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 5), Skip: 0, Take: 3)));
        var (page2, _) = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .ListAsync(new JournalEntryFilter(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 5), Skip: 3, Take: 3)));

        Assert.Equal(4, total);
        Assert.Equal([5, 4, 3], page1.Select(e => e.Date.Day));
        Assert.Equal([2], page2.Select(e => e.Date.Day));
        Assert.All(page1, e => Assert.Equal(2, e.Lines.Count));
    }

    [Fact]
    public async Task Settings_update_is_audited_only_when_something_changed()
    {
        Task<Company> Update(UpdateCompanySettingsCommand c) =>
            _db.InScope(sp => sp.GetRequiredService<CompanySettingsService>().UpdateAsync(c));
        Task<int> Audits() => _db.InDb(db => db.AuditRecords.CountAsync(r => r.EntityType == "company" && r.Action == AuditAction.Updated));

        var company = await Update(Settings(useNumbers: false, basis: ReportBasis.Cash, month: 4));
        await Update(Settings(useNumbers: false, basis: ReportBasis.Cash, month: 4));

        Assert.Equal(4, company.FiscalYearStartMonth);
        Assert.False(company.UseAccountNumbers);
        Assert.Equal(1, await Audits());
    }
}
