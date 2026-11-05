using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Assets;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Integration.Tests;

/// <summary>L5 slice 2: fixed assets, depreciation runs, disposal and loans.</summary>
public sealed class FixedAssetsTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _equipment = null!;
    private Account _bankLoan = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _equipment = await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        _bankLoan = await _db.CreateAccount("Bank Loan", AccountType.LongTermLiability, "2500");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    // The year things are dated in: an income or expense account's balance on a trial balance is that fiscal year's.
    private static readonly DateOnly YearEnd = new(2026, 12, 31);

    private async Task<decimal> Balance(string accountName, DateOnly? asOf = null)
    {
        var account = await _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Name == accountName));
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(asOf ?? new DateOnly(2030, 12, 31), ReportBasis.Accrual));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == account.Id);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private Task<FixedAsset> Van(decimal prior = 0m, DateOnly? priorThrough = null) => In<FixedAssetService, FixedAsset>(s => s.CreateAsync(new FixedAssetDetails(
        "Delivery van", _equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 15), 3_600m, DepreciationMethod.StraightLine, 36,
        PriorDepreciation: prior, PriorThrough: priorThrough)));

    [Fact]
    public async Task Depreciation_runs_post_what_is_due_and_only_the_latest_can_be_voided()
    {
        var van = await Van();
        Assert.Equal(0m, await Balance(AssetAccounts.AccumulatedName));

        var preview = await In<DepreciationService, IReadOnlyList<DepreciationDue>>(s => s.PreviewAsync(new DateOnly(2026, 3, 31)));
        Assert.Equal(300m, Assert.Single(preview).Amount);
        var march = await In<DepreciationService, DepreciationRun>(s => s.RunAsync(new DateOnly(2026, 3, 31)));
        Assert.Equal(-300m, await Balance(AssetAccounts.AccumulatedName));
        Assert.Equal(300m, await Balance(AssetAccounts.ExpenseName, YearEnd));
        Assert.Equal("depreciation.nothing_due", await Refused(() => In<DepreciationService, DepreciationRun>(s => s.RunAsync(new DateOnly(2026, 3, 31)))));

        var june = await In<DepreciationService, DepreciationRun>(s => s.RunAsync(new DateOnly(2026, 6, 30)));
        Assert.Equal(300m, june.Total);
        Assert.Equal("depreciation.not_latest", await Refused(() => In<DepreciationService, DepreciationRun>(s => s.VoidAsync(march.Id, "Wrong"))));
        await In<DepreciationService, DepreciationRun>(s => s.VoidAsync(june.Id, "Wrong"));
        Assert.Equal(-300m, await Balance(AssetAccounts.AccumulatedName));

        Assert.Equal("asset.depreciated", await Refused(() => In<FixedAssetService, FixedAsset>(s => s.UpdateAsync(van.Id, new FixedAssetDetails(
            "Delivery van", _equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 15), 4_000m, DepreciationMethod.StraightLine, 36)))));
        var renamed = await In<FixedAssetService, FixedAsset>(s => s.UpdateAsync(van.Id, new FixedAssetDetails(
            "White van", _equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 15), 3_600m, DepreciationMethod.StraightLine, 36, Number: "FA-1")));
        Assert.Equal(("White van", "FA-1"), (renamed.Name, renamed.Number));

        var register = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("fixed-assets", ReportParameters.Parse("asOf=2026-12-31")));
        Assert.Equal(3_300m, register.Find("Total", 5));
        var schedule = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("depreciation-schedule", ReportParameters.Parse("year=2026")));
        Assert.Equal(1_200m, schedule.Find("Total", 6));
    }

    [Fact]
    public async Task Disposal_posts_depreciation_to_date_and_the_gain_or_loss()
    {
        var van = await Van();
        await In<DepreciationService, DepreciationRun>(s => s.RunAsync(new DateOnly(2026, 3, 31)));

        var disposed = await In<FixedAssetService, FixedAsset>(s => s.DisposeAsync(van.Id, new DisposeAssetCommand(new DateOnly(2026, 5, 10), 3_000m, _checking.Id)));
        Assert.True(disposed.IsDisposed);
        Assert.Equal(100m, disposed.DisposalDepreciation); // April
        Assert.Equal(0m, await Balance(AssetAccounts.AccumulatedName));
        Assert.Equal(-3_600m, await Balance("Equipment"));
        Assert.Equal(3_000m, await Balance("Checking"));
        Assert.Equal(200m, await Balance(AssetAccounts.GainLossName, YearEnd)); // book value 3,200 sold for 3,000
        Assert.Equal(400m, await Balance(AssetAccounts.ExpenseName, YearEnd));
        Assert.Empty(await In<DepreciationService, IReadOnlyList<DepreciationDue>>(s => s.PreviewAsync(new DateOnly(2026, 12, 31))));
        Assert.Equal("asset.disposed", await Refused(() => In<FixedAssetService, FixedAsset>(s => s.DisposeAsync(van.Id, new DisposeAssetCommand(new DateOnly(2026, 6, 1))))));

        var runs = await In<DepreciationService, IReadOnlyList<DepreciationRun>>(s => s.ListRunsAsync());
        Assert.Equal("depreciation.asset_disposed", await Refused(() => In<DepreciationService, DepreciationRun>(s => s.VoidAsync(runs[0].Id, "x"))));

        await In<FixedAssetService, FixedAsset>(s => s.UndoDisposalAsync(van.Id, "Sale fell through"));
        Assert.Equal(-300m, await Balance(AssetAccounts.AccumulatedName));
        Assert.Equal(0m, await Balance(AssetAccounts.GainLossName, YearEnd));
        Assert.Equal(100m, Assert.Single(await In<DepreciationService, IReadOnlyList<DepreciationDue>>(s => s.PreviewAsync(new DateOnly(2026, 4, 30)))).Amount);
    }

    [Fact]
    public async Task Depreciation_from_before_OpenBook_is_not_posted_again()
    {
        await Van(prior: 500m, priorThrough: new DateOnly(2026, 4, 30));
        Assert.Empty(await In<DepreciationService, IReadOnlyList<DepreciationDue>>(s => s.PreviewAsync(new DateOnly(2026, 4, 30))));
        Assert.Equal(200m, Assert.Single(await In<DepreciationService, IReadOnlyList<DepreciationDue>>(s => s.PreviewAsync(new DateOnly(2026, 6, 30)))).Amount);
    }

    [Fact]
    public async Task A_loan_uses_the_starter_charts_interest_account()
    {
        var interest = await _db.CreateAccount("Interest Expense", AccountType.OtherExpense, "8000");

        var loan = await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Van loan", _bankLoan.Id, Guid.Empty, 10_000m, 6m, 12, new DateOnly(2026, 2, 1)), null, null)));

        Assert.Equal(interest.Id, loan.InterestAccountId);
    }

    [Fact]
    public async Task Loans_record_the_money_received_and_split_payments()
    {
        var loan = await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Van loan", _bankLoan.Id, Guid.Empty, 10_000m, 6m, 12, new DateOnly(2026, 2, 1)), _checking.Id, new DateOnly(2026, 1, 5))));
        Assert.Equal(860.67m, loan.PaymentAmount);
        Assert.Equal(10_000m, await Balance("Checking"));
        Assert.Equal(-10_000m, await Balance("Bank Loan"));

        var first = await In<LoanService, LoanPayment>(s => s.PayAsync(loan.Id, new LoanPaymentCommand(new DateOnly(2026, 2, 1), _checking.Id)));
        Assert.Equal((860.67m, 50m, 810.67m), (first.Amount, first.Interest, first.Principal));
        var extra = await In<LoanService, LoanPayment>(s => s.PayAsync(loan.Id, new LoanPaymentCommand(new DateOnly(2026, 3, 1), _checking.Id, 2_000m)));
        Assert.Equal((45.95m, 1_954.05m), (extra.Interest, extra.Principal)); // 9,189.33 × 0.5%
        Assert.Equal(-7_235.28m, await Balance("Bank Loan"));
        Assert.Equal(95.95m, await Balance(AssetAccounts.InterestName, YearEnd));

        Assert.Equal("loan.payment_order", await Refused(() => In<LoanService, LoanPayment>(s => s.PayAsync(loan.Id, new LoanPaymentCommand(new DateOnly(2026, 2, 15), _checking.Id)))));
        Assert.Equal("loan.payment_not_latest", await Refused(() => In<LoanService, Loan>(s => s.VoidPaymentAsync(loan.Id, first.Id, "x"))));
        Assert.Equal("loan.has_payments", await Refused(() => In<LoanService, Loan>(s =>
            s.UpdateAsync(loan.Id, new LoanDetails("Van loan", _bankLoan.Id, Guid.Empty, 10_000m, 5m, 12, new DateOnly(2026, 2, 1))))));

        var reloaded = await In<LoanService, Loan>(s => s.VoidPaymentAsync(loan.Id, extra.Id, "Bounced"));
        Assert.Equal(9_189.33m, reloaded.Balance);
        var remaining = LoanService.RemainingSchedule(reloaded);
        Assert.Equal(11, remaining.Count);
        Assert.Equal(2, remaining[0].Number);

        var report = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("loans", ReportParameters.Parse("")));
        Assert.Equal(9_189.33m, report.Find("Van loan", 6));
    }

    [Fact]
    public async Task An_asset_cant_be_disposed_of_before_depreciation_already_posted()
    {
        var van = await Van();
        await In<DepreciationService, DepreciationRun>(s => s.RunAsync(new DateOnly(2026, 12, 31)));

        Assert.Equal("asset.depreciated_after", await Refused(() => In<FixedAssetService, FixedAsset>(s => s.DisposeAsync(van.Id, new DisposeAssetCommand(new DateOnly(2026, 6, 30), 0m, null)))));

        await In<FixedAssetService, FixedAsset>(s => s.DisposeAsync(van.Id, new DisposeAssetCommand(new DateOnly(2027, 1, 31), 0m, null)));
    }

    [Fact]
    public async Task Depreciation_brought_in_that_differs_from_the_schedule_still_ends_at_the_base()
    {
        var van = await Van(prior: 200m, priorThrough: new DateOnly(2026, 6, 30));

        Assert.Equal(200m + 100m, Depreciation.ScheduledThrough(van, new DateOnly(2026, 7, 31)));
        Assert.Equal(3_600m, Depreciation.ScheduledThrough(van, new DateOnly(2028, 12, 31)));
    }
}
