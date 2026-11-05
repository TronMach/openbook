using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Integration.Tests;

/// <summary>From a company's second fiscal year: nothing is closed, so earlier years' income belongs in retained earnings.</summary>
public sealed class PriorYearsTests : IAsyncLifetime
{
    private static readonly DateOnly YearEnd = new(2026, 12, 31);
    private readonly LedgerDatabase _db = new();
    private Account _cash = null!;
    private Account _sales = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _cash = await _db.CreateAccount("Cash", AccountType.Bank, "1000");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        await SaleAsync(new DateOnly(2025, 6, 1), 1_000m);
        await SaleAsync(new DateOnly(2026, 2, 1), 200m);
        await SaleAsync(new DateOnly(2026, 6, 1), 300m);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<JournalEntry> SaleAsync(DateOnly date, decimal amount) =>
        _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(date, [JournalLineDraft.Dr(_cash.Id, amount), JournalLineDraft.Cr(_sales.Id, amount)])));

    private Task<TrialBalance> TrialBalanceAsync(ReportBasis basis = ReportBasis.Accrual) =>
        _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(YearEnd, basis));

    [Theory]
    [InlineData(ReportBasis.Accrual)]
    [InlineData(ReportBasis.Cash)]
    public async Task The_trial_balance_shows_this_year_and_a_retained_earnings_row(ReportBasis basis)
    {
        var tb = await TrialBalanceAsync(basis);

        Assert.True(tb.IsBalanced);
        Assert.Equal(1_500m, tb.Lines.Single(l => l.AccountId == _cash.Id).Debit.Amount);
        Assert.Equal(500m, tb.Lines.Single(l => l.AccountId == _sales.Id).Credit.Amount);
        var retained = tb.Lines.Single(l => l.AccountId == Guid.Empty);
        Assert.Equal(("Retained earnings", 1_000m), (retained.FullName, retained.Credit.Amount));
    }

    [Fact]
    public async Task A_retained_earnings_account_takes_prior_years_income()
    {
        var account = await _db.CreateAccount("Retained Earnings", AccountType.Equity, "3900");

        var tb = await TrialBalanceAsync();

        Assert.DoesNotContain(tb.Lines, l => l.AccountId == Guid.Empty);
        Assert.Equal(1_000m, tb.Lines.Single(l => l.AccountId == account.Id).Credit.Amount);
    }

    [Fact]
    public async Task In_the_first_year_there_is_nothing_to_retain()
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2025, 12, 31), ReportBasis.Accrual));

        Assert.DoesNotContain(tb.Lines, l => l.AccountId == Guid.Empty);
        Assert.Equal(1_000m, tb.Lines.Single(l => l.AccountId == _sales.Id).Credit.Amount);
    }

    [Fact]
    public async Task The_trial_balance_agrees_with_the_statements()
    {
        var (tb, profit, sheet) = await _db.InScope(async sp =>
        {
            var reports = sp.GetRequiredService<FinancialReports>();
            return (await sp.GetRequiredService<TrialBalanceQuery>().RunAsync(YearEnd, ReportBasis.Accrual),
                await reports.ProfitAndLossAsync(new DateOnly(2026, 1, 1), YearEnd, ReportBasis.Accrual),
                await reports.BalanceSheetAsync(YearEnd, ReportBasis.Accrual));
        });

        Assert.Equal(profit.NetIncome, tb.Lines.Single(l => l.AccountId == _sales.Id).Credit.Amount);
        Assert.Equal(sheet.Equity.Total - profit.NetIncome, tb.Lines.Single(l => l.AccountId == Guid.Empty).Credit.Amount);
    }

    [Fact]
    public async Task The_worksheet_folds_prior_years_the_same_way()
    {
        var lines = await _db.InScope(sp => sp.GetRequiredService<AdjustingEntryService>().WorksheetAsync(YearEnd, ReportBasis.Accrual));

        Assert.Equal(-500m, lines.Single(l => l.AccountId == _sales.Id).Unadjusted);
        Assert.Equal(-1_000m, lines.Single(l => l.AccountId == Guid.Empty).Unadjusted);
        Assert.Equal(0m, lines.Sum(l => l.Unadjusted));
    }

    [Fact]
    public async Task The_general_ledger_opens_income_at_the_fiscal_year_and_the_bank_at_its_whole_balance()
    {
        var ledger = await _db.InScope(sp => sp.GetRequiredService<FinancialReports>().GeneralLedgerAsync(new DateOnly(2026, 3, 1), YearEnd, ReportBasis.Accrual));

        Assert.Equal(200m, ledger.Accounts.Single(a => a.Account.Id == _sales.Id).OpeningBalance);
        Assert.Equal(1_200m, ledger.Accounts.Single(a => a.Account.Id == _cash.Id).OpeningBalance);
        Assert.Equal(500m, ledger.Accounts.Single(a => a.Account.Id == _sales.Id).Lines[^1].Balance);
    }

    [Fact]
    public async Task A_general_ledger_from_the_year_start_opens_income_at_zero()
    {
        var ledger = await _db.InScope(sp => sp.GetRequiredService<FinancialReports>().GeneralLedgerAsync(new DateOnly(2026, 1, 1), YearEnd, ReportBasis.Accrual));

        Assert.Equal(0m, ledger.Accounts.Single(a => a.Account.Id == _sales.Id).OpeningBalance);
    }
}
