using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>
/// A small year of books: opening equity, an invoice split across two income accounts paid in part,
/// a bill paid in full, a cash sale and a manual expense.
/// </summary>
public sealed class ReportingTests : IAsyncLifetime
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _equity = null!;
    private Account _design = null!;
    private Account _printing = null!;
    private Account _rent = null!;
    private Account _fees = null!;
    private Account _utilities = null!;
    private Account _electric = null!;
    private Invoice _invoice = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _equity = await _db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
        _design = await _db.CreateAccount("Design Revenue", AccountType.Income, "4000");
        _printing = await _db.CreateAccount("Printing Revenue", AccountType.Income, "4100");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _fees = await _db.CreateAccount("Bank Fees", AccountType.Expense, "6100");
        _utilities = await _db.CreateAccount("Utilities", AccountType.Expense, "6800");
        _electric = await _db.InScope(sp => sp.GetRequiredService<AccountService>()
            .CreateAsync(new CreateAccountCommand("Electric", AccountType.Expense, "6810", _utilities.Id)));

        await Post(new DateOnly(2025, 12, 31), 5000m, _checking, _equity, "Opening");

        var designItem = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Design", ItemType.Service, _design.Id, 100m)));
        var printItem = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Printing", ItemType.NonInventory, _printing.Id, 50m)));
        var customer = await _db.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        _invoice = await _db.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(
            customer.Id, new DateOnly(2026, 3, 1), [new SalesLineDraft(designItem.Id, null, 6m, 100m), new SalesLineDraft(printItem.Id, null, 8m, 50m)])));
        await _db.InScope(sp => sp.GetRequiredService<PaymentService>().ReceiveAsync(new ReceivePaymentCommand(
            customer.Id, new DateOnly(2026, 4, 15), _checking.Id, [new ApplyToInvoice(_invoice.Id, 500m)])));

        var vendor = await _db.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Landlord")));
        var bill = await _db.InScope(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            vendor.Id, new DateOnly(2026, 3, 1), [new BillLineDraft(_rent.Id, 900m), new BillLineDraft(_electric.Id, 100m)])));
        await _db.InScope(sp => sp.GetRequiredService<BillPaymentService>().PayAsync(new PayBillsCommand(
            new DateOnly(2026, 5, 1), _checking.Id, [new ApplyToBill(bill.Id, 1000m)])));

        await _db.InScope(sp => sp.GetRequiredService<SalesReceiptService>().CreateAsync(new CreateSalesReceiptCommand(
            null, new DateOnly(2026, 6, 1), _checking.Id, [new SalesLineDraft(designItem.Id, null, 2m, 100m)])));
        await Post(new DateOnly(2026, 6, 30), 15m, _fees, _checking, "Monthly fee");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<JournalEntry> Post(DateOnly date, decimal amount, Account debit, Account credit, string memo) =>
        _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(date, [Dr(debit.Id, amount), Cr(credit.Id, amount)], memo)));

    private Task<T> Reports<T>(Func<FinancialReports, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<FinancialReports>()));

    private static decimal Row(ReportSection section, Account account) => section.Rows.Single(r => r.AccountId == account.Id && r.Kind == RowKind.Account).Amount ?? 0m;

    [Fact]
    public async Task Accrual_profit_and_loss_counts_invoices_and_bills_when_issued()
    {
        var pl = await Reports(r => r.ProfitAndLossAsync(Jan1, Dec31, ReportBasis.Accrual));

        Assert.Equal((800m, 400m), (Row(pl.Income, _design), Row(pl.Income, _printing)));
        Assert.Equal(1200m, pl.Income.Total); // the invoice plus the 200 cash sale
        Assert.Equal(1015m, pl.Expenses.Total);
        Assert.Equal(185m, pl.NetIncome);
    }

    [Fact]
    public async Task Cash_profit_and_loss_counts_payments_split_by_the_documents_lines()
    {
        var pl = await Reports(r => r.ProfitAndLossAsync(Jan1, Dec31, ReportBasis.Cash));

        // 500 paid on a 1,000 invoice that was 600 design / 400 printing; plus the 200 cash sale.
        Assert.Equal((500m, 200m), (Row(pl.Income, _design), Row(pl.Income, _printing)));
        Assert.Equal(1015m, pl.Expenses.Total);
        Assert.Equal(-315m, pl.NetIncome);
    }

    [Fact]
    public async Task Cash_trial_balance_has_no_receivables_or_payables_and_still_balances()
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(Dec31, ReportBasis.Cash));

        Assert.True(tb.IsBalanced);
        Assert.DoesNotContain(tb.Lines, l => l.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable);
        Assert.Equal(4685m, tb.Lines.Single(l => l.AccountId == _checking.Id).Debit.Amount);
    }

    [Fact]
    public async Task Subaccounts_roll_up_into_parent_totals()
    {
        var pl = await Reports(r => r.ProfitAndLossAsync(Jan1, Dec31, ReportBasis.Accrual));

        var rows = pl.Expenses.Rows.Select(r => (r.Label, r.Depth, r.Amount, r.Kind)).ToList();
        Assert.Contains(("Utilities", 1, (decimal?)null, RowKind.Account), rows);
        Assert.Contains(("Electric", 2, (decimal?)100m, RowKind.Account), rows);
        Assert.Contains(("Total Utilities", 1, (decimal?)100m, RowKind.AccountTotal), rows);
    }

    [Theory]
    [InlineData(ReportBasis.Accrual)]
    [InlineData(ReportBasis.Cash)]
    public async Task Balance_sheet_balances_with_prior_years_as_retained_earnings(ReportBasis basis)
    {
        await Post(new DateOnly(2025, 6, 1), 300m, _checking, _design, "Last year's sale");

        var bs = await Reports(r => r.BalanceSheetAsync(Dec31, basis));

        Assert.True(bs.IsBalanced);
        Assert.Equal(300m, bs.Equity.Rows.Single(r => r.Label == "Retained earnings").Amount);
        Assert.Equal(basis == ReportBasis.Cash ? -315m : 185m, bs.Equity.Rows.Single(r => r.Label == "Net income").Amount);
    }

    [Fact]
    public async Task Fiscal_year_start_moves_income_between_retained_earnings_and_net_income()
    {
        await _db.InScope(sp => sp.GetRequiredService<CompanySettingsService>().UpdateAsync(new UpdateCompanySettingsCommand(
            "Test Co", BusinessType.SingleMemberLlc, null, null, null, null, 5, true, ReportBasis.Accrual)));

        var bs = await Reports(r => r.BalanceSheetAsync(Dec31, ReportBasis.Accrual));

        // Fiscal year from May 1: the March invoice and bill fall in the prior year.
        Assert.Equal(1000m - 1000m, bs.Equity.Rows.Single(r => r.Label == "Retained earnings").Amount);
        Assert.Equal(200m - 15m, bs.Equity.Rows.Single(r => r.Label == "Net income").Amount);
        Assert.True(bs.IsBalanced);
    }

    [Fact]
    public async Task General_ledger_shows_opening_and_running_balances_in_the_natural_direction()
    {
        var gl = await Reports(r => r.GeneralLedgerAsync(new DateOnly(2026, 4, 1), Dec31, ReportBasis.Accrual, _checking.Id));

        var checking = Assert.Single(gl.Accounts);
        Assert.Equal(5000m, checking.OpeningBalance);
        Assert.Equal([5500m, 4500m, 4700m, 4685m], checking.Lines.Select(l => l.Balance));
        Assert.Equal(4685m, checking.ClosingBalance);
    }

    [Fact]
    public async Task General_ledger_for_a_parent_includes_its_subaccounts()
    {
        var gl = await Reports(r => r.GeneralLedgerAsync(null, Dec31, ReportBasis.Accrual, _utilities.Id));
        Assert.Equal(["Utilities:Electric"], gl.Accounts.Select(a => a.FullName));
    }

    [Fact]
    public async Task Voided_payments_drop_out_of_cash_basis()
    {
        var payment = (await _db.InScope(sp => sp.GetRequiredService<PaymentService>().ListAsync())).Single();
        await _db.InScope(sp => sp.GetRequiredService<PaymentService>().VoidAsync(payment.Id, "bounced"));

        var pl = await Reports(r => r.ProfitAndLossAsync(Jan1, Dec31, ReportBasis.Cash));

        Assert.Equal(200m, Row(pl.Income, _design));
        Assert.DoesNotContain(pl.Income.Rows, r => r.AccountId == _printing.Id);
    }

    [Fact]
    public async Task Search_finds_by_name_number_and_amount()
    {
        Task<SearchResults> Search(string q) => _db.InScope(sp => sp.GetRequiredService<SearchService>().SearchAsync(q));

        Assert.Contains((await Search("acm")).Hits, h => h.Kind == SearchKind.Customer);
        Assert.Contains((await Search(_invoice.Number)).Hits, h => h.Kind == SearchKind.Invoice && h.Id == _invoice.Id);
        Assert.Contains((await Search("1,000.00")).Hits, h => h.Kind == SearchKind.Invoice);
        Assert.Contains((await Search("1,000.00")).Hits, h => h.Kind == SearchKind.Bill);
        Assert.Contains((await Search("monthly fee")).Hits, h => h.Kind == SearchKind.JournalEntry);
        Assert.Contains((await Search("electric")).Hits, h => h.Kind == SearchKind.Account);
        Assert.Empty((await Search("x")).Hits);
    }
}

public class AllocationAndDateRangeTests
{
    [Fact]
    public void Allocation_splits_in_proportion_and_adds_up()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        var parts = ReportLedger.Allocate(100m, [(a, 1m), (b, 1m), (c, 1m)]);

        Assert.Equal([33.33m, 33.34m, 33.33m], parts.Select(p => p.Amount));
        Assert.Equal(100m, parts.Sum(p => p.Amount));
    }

    [Fact]
    public void Allocation_handles_discount_lines_and_repeated_accounts()
    {
        Guid income = Guid.NewGuid(), discounts = Guid.NewGuid();

        var parts = ReportLedger.Allocate(45m, [(income, 60m), (income, 40m), (discounts, -10m)]).ToDictionary(p => p.AccountId, p => p.Amount);

        Assert.Equal(50m, parts[income]);
        Assert.Equal(-5m, parts[discounts]);
    }

    [Theory]
    [InlineData(DatePreset.ThisMonth, "2026-02-01", "2026-02-28")]
    [InlineData(DatePreset.LastMonth, "2026-01-01", "2026-01-31")]
    [InlineData(DatePreset.ThisQuarter, "2026-01-01", "2026-03-31")]
    [InlineData(DatePreset.LastQuarter, "2025-10-01", "2025-12-31")]
    [InlineData(DatePreset.ThisFiscalYear, "2025-07-01", "2026-06-30")]
    [InlineData(DatePreset.ThisFiscalYearToDate, "2025-07-01", "2026-02-14")]
    [InlineData(DatePreset.LastFiscalYear, "2024-07-01", "2025-06-30")]
    public void Presets_follow_the_calendar_and_the_fiscal_year(DatePreset preset, string from, string to)
    {
        var range = DateRanges.For(preset, new DateOnly(2026, 2, 14), fiscalYearStartMonth: 7);

        Assert.Equal(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), range.From);
        Assert.Equal(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), range.To);
    }

    [Fact]
    public void All_dates_has_no_start_and_custom_uses_the_given_dates()
    {
        var today = new DateOnly(2026, 2, 14);
        Assert.Null(DateRanges.For(DatePreset.AllDates, today, 1).From);
        Assert.Equal(new DateRange(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 9)),
            DateRanges.For(DatePreset.Custom, today, 1, new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 9)));
    }
}
