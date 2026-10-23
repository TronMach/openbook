using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Sales;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>
/// A year of books: opening cash, an invoice across two income accounts paid in part, a cash sale, a bill paid in
/// full, an expense to the same (1099) vendor, a bank fee, equipment bought for cash and a loan received.
/// Accrual 2026: income 1,200, expenses 1,215. Cash 2026: income 700, expenses 1,215.
/// </summary>
public sealed class ReportsAndBudgetsTests : IAsyncLifetime
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _equipment = null!;
    private Account _loan = null!;
    private Account _design = null!;
    private Account _printing = null!;
    private Account _rent = null!;
    private Account _fees = null!;
    private Account _supplies = null!;
    private Account _utilities = null!;
    private Account _electric = null!;
    private Customer _acme = null!;
    private Vendor _landlord = null!;
    private Invoice _invoice = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _equipment = await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        _loan = await _db.CreateAccount("Bank Loan", AccountType.LongTermLiability, "2700");
        var equity = await _db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
        _design = await _db.CreateAccount("Design Revenue", AccountType.Income, "4000");
        _printing = await _db.CreateAccount("Printing Revenue", AccountType.Income, "4100");
        _fees = await _db.CreateAccount("Bank Fees", AccountType.Expense, "6100");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6400");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _utilities = await _db.CreateAccount("Utilities", AccountType.Expense, "6800");
        _electric = await _db.InScope(sp => sp.GetRequiredService<AccountService>()
            .CreateAsync(new CreateAccountCommand("Electric", AccountType.Expense, "6810", _utilities.Id)));

        await Post(new DateOnly(2025, 12, 31), 5000m, _checking, equity, "Opening");

        var designItem = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Design", ItemType.Service, _design.Id, 100m)));
        var printItem = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Printing", ItemType.NonInventory, _printing.Id, 50m)));
        _acme = await _db.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        _invoice = await _db.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(
            _acme.Id, new DateOnly(2026, 3, 1), [new SalesLineDraft(designItem.Id, null, 6m, 100m), new SalesLineDraft(printItem.Id, null, 8m, 50m)])));
        await _db.InScope(sp => sp.GetRequiredService<PaymentService>().ReceiveAsync(new ReceivePaymentCommand(
            _acme.Id, new DateOnly(2026, 4, 15), _checking.Id, [new ApplyToInvoice(_invoice.Id, 500m)])));

        _landlord = await _db.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Landlord", Track1099: true)));
        var bill = await _db.InScope(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(
            _landlord.Id, new DateOnly(2026, 3, 1), [new BillLineDraft(_rent.Id, 900m), new BillLineDraft(_electric.Id, 100m)])));
        await _db.InScope(sp => sp.GetRequiredService<BillPaymentService>().PayAsync(new PayBillsCommand(
            new DateOnly(2026, 5, 1), _checking.Id, [new ApplyToBill(bill.Id, 1000m)])));
        await _db.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, new DateOnly(2026, 5, 10), [new BankSplit(_supplies.Id, 200m)], VendorId: _landlord.Id)));

        await _db.InScope(sp => sp.GetRequiredService<SalesReceiptService>().CreateAsync(new CreateSalesReceiptCommand(
            null, new DateOnly(2026, 6, 1), _checking.Id, [new SalesLineDraft(designItem.Id, null, 2m, 100m)])));
        await Post(new DateOnly(2026, 6, 30), 15m, _fees, _checking, "Monthly fee");
        await Post(new DateOnly(2026, 7, 1), 1500m, _equipment, _checking, "Printer");
        await Post(new DateOnly(2026, 8, 1), 2000m, _checking, _loan, "Loan");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<JournalEntry> Post(DateOnly date, decimal amount, Account debit, Account credit, string memo) =>
        _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(date, [Dr(debit.Id, amount), Cr(credit.Id, amount)], memo)));

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<ReportTable> Catalog(string key, string query) =>
        Run(sp => sp.GetRequiredService<ReportCatalog>().RunAsync(key, ReportParameters.Parse(query)));

    private static IReadOnlyList<ReportCell> RowFor(ReportTable table, string label) =>
        table.Rows.Single(r => Equals(r.Cells[0].Value, label)).Cells;

    [Theory]
    [InlineData(ReportBasis.Accrual, -15)]
    [InlineData(ReportBasis.Cash, -515)]
    public async Task Single_column_profit_and_loss_matches_the_statement(ReportBasis basis, int netIncome)
    {
        var statement = await Run(sp => sp.GetRequiredService<FinancialReports>().ProfitAndLossAsync(Jan1, Dec31, basis));
        var table = await Run(sp => sp.GetRequiredService<StatementReports>().ProfitAndLossAsync(Jan1, Dec31, basis));

        Assert.Equal(statement.NetIncome, table.Find("Net income"));
        Assert.Equal((decimal)netIncome, table.Find("Net income"));
        Assert.Equal(2, table.Columns.Count);
        var design = table.Rows.Single(r => Equals(r.Cells[0].Value, "Design Revenue"));
        Assert.Equal(new ReportLink("account", _design.Id) { From = Jan1, To = Dec31 }, design.Cells[1].Link);
    }

    [Fact]
    public async Task Profit_and_loss_by_month_has_a_column_per_month_and_a_total()
    {
        var table = await Catalog("profit-and-loss", "preset=Custom&from=2026-01-01&to=2026-12-31&basis=Accrual&compare=Months");

        Assert.Equal(14, table.Columns.Count);
        Assert.Equal("Mar 2026", table.Columns[3].Header);
        Assert.Equal("Total", table.Columns[13].Header);
        var net = RowFor(table, "Net income");
        Assert.Equal(-15m, net[13].Value);
        Assert.Equal(0m, net[3].Value); // March: invoice 1,000 against the bill's 1,000
        Assert.Equal(-200m, net[5].Value); // May: supplies
        Assert.Equal(-15m, Enumerable.Range(1, 12).Sum(i => (decimal)net[i].Value!));

        // Each month's account cells drill into that month's general ledger.
        var rent = RowFor(table, "Rent");
        Assert.Equal(new DateOnly(2026, 3, 31), rent[3].Link!.To);
    }

    [Fact]
    public async Task Profit_and_loss_against_last_year_shows_the_change()
    {
        var table = await Catalog("profit-and-loss", "preset=Custom&from=2026-01-01&to=2026-12-31&basis=Accrual&compare=PreviousYear");

        Assert.Equal(["", "Jan – Dec 2026", "Jan – Dec 2025", "$ change", "% change"], table.Columns.Select(c => c.Header));
        var income = RowFor(table, "Total income");
        Assert.Equal(1200m, income[1].Value);
        Assert.Equal(0m, income[2].Value);
        Assert.Equal(1200m, income[3].Value);
        Assert.Null(income[4].Value);
    }

    [Theory]
    [InlineData(ReportBasis.Accrual)]
    [InlineData(ReportBasis.Cash)]
    public async Task Comparative_balance_sheet_balances_in_every_column(ReportBasis basis)
    {
        var table = await Run(sp => sp.GetRequiredService<StatementReports>().BalanceSheetAsync(new DateOnly(2026, 6, 30), basis, Comparison.PreviousPeriod));

        Assert.Equal("May 31, 2026", table.Columns[2].Header);
        var assets = RowFor(table, "Total assets");
        var both = RowFor(table, "Total liabilities and equity");
        Assert.Equal(assets[1].Value, both[1].Value);
        Assert.Equal(assets[2].Value, both[2].Value);
        var single = await Run(sp => sp.GetRequiredService<FinancialReports>().BalanceSheetAsync(new DateOnly(2026, 5, 31), basis));
        Assert.Equal(single.TotalAssets, assets[2].Value);
    }

    [Theory]
    [InlineData(ReportBasis.Accrual)]
    [InlineData(ReportBasis.Cash)]
    public async Task Cash_flow_explains_the_change_in_bank_balances(ReportBasis basis)
    {
        var cf = await Run(sp => sp.GetRequiredService<StatementReports>().CashFlowAsync(Jan1, Dec31, basis));

        Assert.Equal(5000m, cf.CashAtStart);
        Assert.Equal(4985m, cf.CashAtEnd);
        Assert.Equal(cf.CashAtEnd - cf.CashAtStart, cf.NetChange);
        Assert.Equal(-1500m, Assert.Single(cf.Investing).Amount);
        Assert.Equal(2000m, Assert.Single(cf.Financing).Amount);
        if (basis == ReportBasis.Accrual)
            Assert.Equal(-500m, cf.Operating.Single(l => l.Label == "Accounts Receivable").Amount);
        else
            Assert.Empty(cf.Operating);
    }

    [Theory]
    [InlineData(ReportBasis.Accrual, 1000, 1200)]
    [InlineData(ReportBasis.Cash, 500, 700)]
    public async Task Sales_by_customer_ties_to_income(ReportBasis basis, int acme, int total)
    {
        var table = await Run(sp => sp.GetRequiredService<ActivityReports>().SalesByCustomerAsync(Jan1, Dec31, basis));

        Assert.Equal((decimal)acme, table.Find("Acme"));
        Assert.Equal(200m, table.Find("Sales receipts with no customer"));
        Assert.Equal((decimal)total, table.Find("Total"));
        var pl = await Run(sp => sp.GetRequiredService<FinancialReports>().ProfitAndLossAsync(Jan1, Dec31, basis));
        Assert.Equal(pl.Income.Total, table.Find("Total"));
    }

    [Theory]
    [InlineData(ReportBasis.Accrual)]
    [InlineData(ReportBasis.Cash)]
    public async Task Expenses_by_vendor_ties_to_expenses(ReportBasis basis)
    {
        var table = await Run(sp => sp.GetRequiredService<ActivityReports>().ExpensesByVendorAsync(Jan1, Dec31, basis));

        Assert.Equal(1200m, table.Find("Landlord"));
        Assert.Equal(15m, table.Find("No vendor"));
        var pl = await Run(sp => sp.GetRequiredService<FinancialReports>().ProfitAndLossAsync(Jan1, Dec31, basis));
        Assert.Equal(pl.CostOfGoodsSold.Total + pl.Expenses.Total + pl.OtherExpenses.Total, table.Find("Total"));
    }

    [Fact]
    public async Task Sales_by_item_totals_quantity_and_amount()
    {
        var table = await Run(sp => sp.GetRequiredService<ActivityReports>().SalesByItemAsync(Jan1, Dec31));

        var design = RowFor(table, "Design");
        Assert.Equal(8m, design[1].Value);
        Assert.Equal(800m, design[2].Value);
        Assert.Equal(100m, design[4].Value);
        Assert.Equal(1200m, RowFor(table, "Total")[2].Value);
    }

    [Fact]
    public async Task Open_items_list_what_is_still_owed()
    {
        var invoices = await Run(sp => sp.GetRequiredService<ActivityReports>().OpenInvoicesAsync(Dec31));
        Assert.Equal(500m, RowFor(invoices, "Total")[5].Value);
        Assert.Equal(new ReportLink("invoice", _invoice.Id), RowFor(invoices, $"Invoice {_invoice.Number}")[0].Link);

        var bills = await Run(sp => sp.GetRequiredService<ActivityReports>().UnpaidBillsAsync(Dec31));
        Assert.Equal(0m, RowFor(bills, "Total")[5].Value);
        var march = await Run(sp => sp.GetRequiredService<ActivityReports>().UnpaidBillsAsync(new DateOnly(2026, 3, 31)));
        Assert.Equal(1000m, RowFor(march, "Total")[5].Value);
    }

    [Fact]
    public async Task Detail_1099_lists_each_payment_behind_the_summary()
    {
        var table = await Run(sp => sp.GetRequiredService<ActivityReports>().Detail1099Async(2026));
        Assert.Equal(1200m, RowFor(table, "Total Landlord")[4].Value);
        Assert.Equal(2, table.Rows.Count(r => r.Depth == 1));

        var summary = await Run(sp => sp.GetRequiredService<PayablesService>().Report1099Async(2026));
        Assert.Equal(summary.Vendors.Single().Total, RowFor(table, "Total")[4].Value);
    }

    [Fact]
    public async Task Budget_vs_actual_compares_monthly_amounts()
    {
        var budget = await Run(sp => sp.GetRequiredService<BudgetService>().CreateAsync(new CreateBudgetCommand("2026 plan", 2026)));
        var amounts = Enumerable.Range(1, 12).SelectMany(m => new[] { new BudgetAmount(_design.Id, m, 100m), new BudgetAmount(_rent.Id, m, 75m) }).ToList();
        await Run(sp => sp.GetRequiredService<BudgetService>().UpdateAsync(budget.Id, "2026 plan", amounts));

        var table = await Run(sp => sp.GetRequiredService<StatementReports>().BudgetVsActualAsync(budget.Id, 1, 12, ReportBasis.Accrual));
        Assert.Equal(["", "Actual", "Budget", "Over budget", "% of budget"], table.Columns.Select(c => c.Header));
        var design = RowFor(table, "Design Revenue");
        Assert.Equal(800m, design[1].Value);
        Assert.Equal(1200m, design[2].Value);
        Assert.Equal(-400m, design[3].Value);
        Assert.Equal(66.7m, design[4].Value);
        Assert.Equal(0m, RowFor(table, "Printing Revenue")[2].Value);
        Assert.Equal(0m, RowFor(table, "Rent")[3].Value); // 900 actual against 75 a month

        // The first quarter only.
        var q1 = await Run(sp => sp.GetRequiredService<StatementReports>().BudgetVsActualAsync(budget.Id, 1, 3, ReportBasis.Accrual));
        Assert.Equal(300m, RowFor(q1, "Design Revenue")[2].Value);
        Assert.Equal(600m, RowFor(q1, "Design Revenue")[1].Value);
    }

    [Fact]
    public async Task Budgets_start_from_last_years_actuals_or_another_budget()
    {
        var seeded = await Run(sp => sp.GetRequiredService<BudgetService>().CreateAsync(new CreateBudgetCommand("2027 plan", 2027, BudgetSeed.PriorYearActuals)));
        Assert.Equal(600m, seeded.AmountFor(_design.Id, 3));
        Assert.Equal(200m, seeded.AmountFor(_design.Id, 6));
        Assert.Equal(900m, seeded.AmountFor(_rent.Id, 3));
        Assert.DoesNotContain(seeded.Lines, l => l.AccountId == _checking.Id);

        var copy = await Run(sp => sp.GetRequiredService<BudgetService>().CreateAsync(new CreateBudgetCommand("2028 plan", 2028, BudgetSeed.CopyBudget, seeded.Id)));
        Assert.Equal(seeded.Lines.Sum(l => l.Amount), copy.Lines.Sum(l => l.Amount));
        Assert.Equal(new DateOnly(2028, 1, 1), copy.FiscalYearStart);

        var taken = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<BudgetService>().CreateAsync(new CreateBudgetCommand("2027 PLAN", 2027))));
        Assert.Equal("budget.name_taken", taken.Code);
        var bank = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<BudgetService>()
            .UpdateAsync(copy.Id, "2028 plan", [new BudgetAmount(_checking.Id, 1, 10m)])));
        Assert.Equal("budget.account_invalid", bank.Code);
    }

    [Fact]
    public async Task Deleting_a_budget_is_audited()
    {
        var budget = await Run(sp => sp.GetRequiredService<BudgetService>().CreateAsync(new CreateBudgetCommand("Scratch", 2026)));
        await Run(async sp => { await sp.GetRequiredService<BudgetService>().DeleteAsync(budget.Id); return 0; });

        Assert.Empty(await Run(sp => sp.GetRequiredService<BudgetService>().ListAsync()));
        var audit = await Run(sp => sp.GetRequiredService<ILedgerStore>().ListAuditAsync("budget", budget.Id, null, default));
        Assert.Equal([AuditAction.Deleted, AuditAction.Created], audit.Select(a => a.Action));
    }

    [Fact]
    public async Task Saved_reports_keep_settings_and_update_by_name()
    {
        var service = (Func<IServiceProvider, SavedReportService>)(sp => sp.GetRequiredService<SavedReportService>());
        var saved = await Run(sp => service(sp).SaveAsync("Monthly P&L", "profit-and-loss", "preset=LastMonth"));
        var again = await Run(sp => service(sp).SaveAsync("monthly p&l", "profit-and-loss", "preset=LastMonth&compare=PreviousYear"));
        Assert.Equal(saved.Id, again.Id);
        Assert.Equal("preset=LastMonth&compare=PreviousYear", Assert.Single(await Run(sp => service(sp).ListAsync())).Query);

        var conflict = await Assert.ThrowsAsync<DomainException>(() => Run(sp => service(sp).SaveAsync("Monthly P&L", "balance-sheet", null)));
        Assert.Equal("report.name_taken", conflict.Code);
        var unknown = await Assert.ThrowsAsync<DomainException>(() => Run(sp => service(sp).SaveAsync("X", "nope", null)));
        Assert.Equal("report.unknown", unknown.Code);

        await Run(async sp => { await service(sp).DeleteAsync(saved.Id); return 0; });
        Assert.Empty(await Run(sp => service(sp).ListAsync()));
    }

    [Theory]
    [InlineData(ReportBasis.Accrual, 1200)]
    [InlineData(ReportBasis.Cash, 700)]
    public async Task Tax_summary_totals_accounts_by_tax_line(ReportBasis basis, int receipts)
    {
        var changed = await Run(sp => sp.GetRequiredService<AccountService>().MapTaxLinesAsync(new Dictionary<Guid, string?>
        {
            [_design.Id] = "c.1",
            [_printing.Id] = "c.1",
            [_rent.Id] = "c.20b",
            [_utilities.Id] = "c.25",
            [_fees.Id] = null,
        }));
        Assert.Equal(4, changed);

        var summary = await Run(sp => sp.GetRequiredService<StatementReports>().TaxSummaryAsync(Jan1, Dec31, basis));
        Assert.Equal(TaxForm.ScheduleC, summary.Form);
        Assert.Equal((decimal)receipts, summary.Lines.Single(l => l.Line!.Key == "c.1").Total);
        Assert.Equal(900m, summary.Lines.Single(l => l.Line!.Key == "c.20b").Total);
        // Electric has no line of its own, so it goes on its parent's.
        Assert.Equal(_electric.Id, summary.Lines.Single(l => l.Line!.Key == "c.25").Accounts.Single().AccountId);
        Assert.Equal(1000m, summary.Deductions);
        Assert.Equal([_fees.Id, _supplies.Id], summary.Unassigned.Accounts.Select(a => a.AccountId));
        Assert.Equal(summary.Income, (decimal)receipts);

        var audit = await Run(sp => sp.GetRequiredService<ILedgerStore>().ListAuditAsync("account", _rent.Id, null, default));
        Assert.Contains("c.20b", audit[0].After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_report_runs_and_exports_as_csv_excel_and_pdf()
    {
        var budget = await Run(sp => sp.GetRequiredService<BudgetService>().CreateAsync(new CreateBudgetCommand("2026 plan", 2026)));
        var exporter = _db.Services.GetRequiredService<IReportExporter>();
        foreach (var definition in ReportCatalog.All)
        {
            var table = await Catalog(definition.Key,
                $"preset=Custom&from=2026-01-01&to=2026-12-31&asOf=2026-12-31&year=2026&detail=true&budgetId={budget.Id}&compare=PreviousYear");
            Assert.NotEmpty(table.Rows);
            Assert.All(table.Rows, r => Assert.True(r.Cells.Count <= table.Columns.Count, $"{definition.Key}: row wider than the columns"));

            var csv = Encoding.UTF8.GetString(ReportCsv.Write(table));
            Assert.Contains(table.Title, csv, StringComparison.Ordinal);
            Assert.Equal("PK"u8.ToArray(), exporter.ToXlsx(table)[..2]);
            Assert.Equal("%PDF"u8.ToArray(), exporter.ToPdf(table)[..4]);
        }
    }

    [Fact]
    public async Task Unknown_reports_and_comparisons_are_refused_or_ignored()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Catalog("nope", ""));
        Assert.Equal("report.unknown", ex.Code);

        // Cash flow has no comparisons, so the parameter is ignored.
        var table = await Catalog("cash-flow", "preset=Custom&from=2026-01-01&to=2026-12-31&compare=Months");
        Assert.Equal(2, table.Columns.Count);
    }

    [Fact]
    public void Csv_quotes_fields_and_neutralizes_formulas()
    {
        var table = new ReportTable("T", [], [new("Name"), new("Amount", ColumnFormat.Money)],
            [new ReportTableRow(["=SUM(A1)", -1.5m]), new ReportTableRow(["Smith, Jane", 2m], 1)]);

        var csv = Encoding.UTF8.GetString(ReportCsv.Write(table)).TrimStart('﻿');
        Assert.Contains("'=SUM(A1),-1.50", csv, StringComparison.Ordinal);
        Assert.Contains("\"  Smith, Jane\",2.00", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_columns_follow_the_period()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), Comparisons.Columns(new(2026, 2, 1), new(2026, 2, 28), Comparison.PreviousPeriod)[1].From);
        var days = Comparisons.Columns(new(2026, 3, 11), new(2026, 3, 20), Comparison.PreviousPeriod)[1];
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10)), (days.From, days.To));
        Assert.Equal(new DateOnly(2027, 2, 28), Comparisons.Columns(new(2028, 2, 1), new(2028, 2, 29), Comparison.PreviousYear)[1].To);

        var quarters = Comparisons.Columns(new(2026, 2, 15), new(2026, 9, 30), Comparison.Quarters);
        Assert.Equal(["Feb 15, 2026 – Mar 31, 2026", "Q2 2026", "Q3 2026"], quarters.Select(q => q.Label));
        var tooMany = Assert.Throws<DomainException>(() => Comparisons.Columns(new(2020, 1, 1), new(2026, 12, 31), Comparison.Months));
        Assert.Equal("report.too_many_columns", tooMany.Code);
    }
}
