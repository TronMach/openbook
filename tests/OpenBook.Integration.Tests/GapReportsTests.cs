using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Integration.Tests;

/// <summary>The roll-forward, profit by product, budget by program, budgets as CSV, and reporting to funders.</summary>
public sealed class GapReportsTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _income = null!;
    private Account _rent = null!;
    private Account _ap = null!;
    private Item _widget = null!;
    private Vendor _vendor = null!;
    private Domain.Sales.Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        _ap = await _db.CreateAccount("Equity", AccountType.Equity, "3000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _income.Id, 25m, Taxable: false, PurchaseCost: 10m)));
        _vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Supplier")));
        _acme = await In<CustomerService, Domain.Sales.Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<ReportTable> Report(string key, string query) =>
        In<ReportCatalog, ReportTable>(s => s.RunAsync(key, ReportParameters.Parse(query)));

    private static decimal? Amount(ReportTable table, string label, int column) =>
        table.Rows.First(r => string.Equals(r.Cells[0].Value as string, label, StringComparison.Ordinal)).Cells[column].Value as decimal?;

    private async Task StockAndSell()
    {
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_vendor.Id, Jan5, [new BillLineDraft(Guid.Empty, 100m, null, _widget.Id, 10m)])));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan5.AddDays(1), [new SalesLineDraft(_widget.Id, null, 4m, 25m)])));
    }

    [Fact]
    public async Task The_roll_forward_runs_from_opening_to_closing_and_ties_to_valuation()
    {
        await StockAndSell();
        var table = await Report("inventory-rollforward", "preset=Custom&from=2026-01-01&to=2026-03-31");

        Assert.Equal(0m, Amount(table, "Inventory at Jan 1, 2026", 1));
        Assert.Equal(100m, Amount(table, "Purchases", 1));
        Assert.Equal(-40m, Amount(table, "Cost of goods sold", 1));
        Assert.Equal(60m, Amount(table, "Inventory at Mar 31, 2026", 1));
        Assert.Equal(60m, Amount(table, "Inventory asset accounts in the books", 1));
        Assert.Equal(40m, Amount(table, "Cost of goods sold accounts in the period", 1));

        var next = await Report("inventory-rollforward", "preset=Custom&from=2026-04-01&to=2026-04-30");
        Assert.Equal(60m, Amount(next, "Inventory at Apr 1, 2026", 1));
    }

    [Fact]
    public async Task Profit_by_product_sets_sales_against_cost()
    {
        await StockAndSell();
        var table = await Report("profit-by-product", "preset=Custom&from=2026-01-01&to=2026-03-31");

        var row = table.Rows.First(r => (r.Cells[0].Value as string) == "Widget");
        Assert.Equal(100m, row.Cells[2].Value as decimal?);
        Assert.Equal(40m, row.Cells[3].Value as decimal?);
        Assert.Equal(60m, row.Cells[4].Value as decimal?);
    }

    private async Task<(Segment Program, Budget Budget)> ProgramBudget(string name, decimal monthly)
    {
        var program = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, name));
        var budget = await In<BudgetService, Budget>(s => s.CreateAsync(new CreateBudgetCommand(name, 2026, ClassId: program.Id)));
        var amounts = Enumerable.Range(1, 12).Select(m => new BudgetAmount(_rent.Id, m, monthly)).ToList();
        await In<BudgetService, Budget>(s => s.UpdateAsync(budget.Id, name, amounts));
        return (program, budget);
    }

    [Fact]
    public async Task Budget_by_program_shows_each_class_with_the_year_and_whats_left()
    {
        var (food, _) = await ProgramBudget("Food bank", 100m);
        await ProgramBudget("Shelter", 50m);
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(_rent.Id, 250m, null, food.Id), JournalLineDraft.Cr(_checking.Id, 250m)])));

        var table = await Report("budget-by-class", "asOf=2026-03-31");
        var labels = table.Columns.Select(c => c.Header).ToList();
        Assert.Contains("Food bank: actual", labels);
        Assert.Contains("Shelter: budget", labels);
        Assert.Contains("Left of the year", labels);

        var rent = table.Rows.First(r => (r.Cells[0].Value as string)?.Contains("Rent", StringComparison.Ordinal) == true);
        Assert.Equal(250m, rent.Cells[labels.IndexOf("Food bank: actual")].Value as decimal?);
        Assert.Equal(300m, rent.Cells[labels.IndexOf("Food bank: budget")].Value as decimal?);
        Assert.Equal(1800m, rent.Cells[labels.IndexOf("Budget for the year")].Value as decimal?);
        Assert.Equal(1550m, rent.Cells[labels.IndexOf("Left of the year")].Value as decimal?);
    }

    [Fact]
    public async Task A_budget_goes_out_as_csv_and_comes_back()
    {
        var (_, budget) = await ProgramBudget("Food bank", 100m);
        var csv = await In<BudgetService, string>(s => s.ToCsvAsync(budget.Id));
        Assert.Contains("\"Rent\",100.00", csv, StringComparison.Ordinal);

        var changed = csv.Replace("\"Rent\",100.00,100.00", "\"Rent\",7.00,8.00", StringComparison.Ordinal);
        var saved = await In<BudgetService, Budget>(s => s.ImportCsvAsync(budget.Id, changed));

        Assert.Equal(7m, saved.Lines.Single(l => l.AccountId == _rent.Id && l.Period == 1).Amount);
        Assert.Equal(8m, saved.Lines.Single(l => l.AccountId == _rent.Id && l.Period == 2).Amount);
        Assert.Equal(100m, saved.Lines.Single(l => l.AccountId == _rent.Id && l.Period == 3).Amount);

        var refused = await Assert.ThrowsAsync<DomainException>(() => In<BudgetService, Budget>(s => s.ImportCsvAsync(budget.Id, "Account,Jan\nNo such account,5\n")));
        Assert.Equal("budget.csv_account", refused.Code);
    }

    private async Task<Grant> GrantWithSpending()
    {
        var grant = await In<GrantService, Grant>(s => s.CreateAsync(new GrantCommand("Library", _acme.Id, 1000m, Jan5, Jan5, new DateOnly(2026, 12, 31), Conditional: true)));
        await In<GrantService, Grant>(s => s.SetBudgetAsync(grant.Id, [(_rent.Id, 600m)]));
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 2, 1),
            [JournalLineDraft.Dr(_rent.Id, 200m, null, grant.ClassId), JournalLineDraft.Cr(_checking.Id, 200m)])));
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 3, 1),
            [JournalLineDraft.Dr(_rent.Id, 50m, null, grant.ClassId), JournalLineDraft.Cr(_checking.Id, 50m)])));
        return grant;
    }

    [Fact]
    public async Task A_grant_is_reduced_when_the_funder_wont_pay_and_an_advance_is_paid_back()
    {
        async Task NoProblems(string when)
        {
            var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
            Assert.True(report.Problems == 0, $"{when}: " + string.Join("; ", report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings.Select(f => f.Text))));
        }

        // Unconditional: 1,000 awarded, 600 paid, the rest written off.
        var plain = await In<GrantService, Grant>(s => s.CreateAsync(new GrantCommand("Roof", _acme.Id, 1_000m, Jan5, Jan5, new DateOnly(2026, 12, 31))));
        await In<GrantService, Grant>(s => s.ReceiveAsync(plain.Id, Jan5.AddDays(10), 600m, _checking.Id));
        Assert.Equal("grant.reduce_amount", (await Assert.ThrowsAsync<DomainException>(() => In<GrantService, Grant>(s => s.ReduceAsync(plain.Id, Mar31, 401m, "Too much")))).Code);
        var reduced = await In<GrantService, Grant>(s => s.ReduceAsync(plain.Id, Mar31, 400m, "Funder closed the program"));
        Assert.Equal((600m, 600m, 0m), (reduced.NetAward, reduced.Recognized, reduced.Receivable));
        Assert.Equal("grant.overpaid", (await Assert.ThrowsAsync<DomainException>(() => In<GrantService, Grant>(s => s.ReceiveAsync(plain.Id, Mar31, 1m, _checking.Id)))).Code);
        await NoProblems("after the reduction");
        var restored = await In<GrantService, Grant>(s => s.VoidAdjustmentAsync(plain.Id, 1, "Entered by mistake"));
        Assert.Equal(400m, restored.Receivable);

        // Conditional: paid 500 ahead, 250 earned, 250 paid back, and the award brought down to what's earned.
        var grant = await GrantWithSpending();
        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, Jan5.AddDays(1), 500m, _checking.Id));
        await In<GrantService, Grant>(s => s.RecognizeAsync(grant.Id, Mar31));
        var returned = await In<GrantService, Grant>(s => s.ReturnAdvanceAsync(grant.Id, Mar31, 250m, _checking.Id, "Unspent"));
        Assert.Equal((0m, 250m), (returned.Advance, returned.Returned));
        var capped = await In<GrantService, Grant>(s => s.ReduceAsync(grant.Id, Mar31, 750m, "Project ended early"));
        Assert.Equal((250m, 0m), (capped.NetAward, capped.Reducible));
        Assert.Null(capped.Adjustments[^1].EntryId);
        await NoProblems("after the advance went back");
    }

    [Fact]
    public async Task Grant_reports_due_endings_and_spending_to_recognize_are_reminders()
    {
        var grant = await GrantWithSpending();
        var today = new DateOnly(2026, 12, 10);
        await In<GrantService, Grant>(s => s.AddReportDueAsync(grant.Id, new DateOnly(2026, 12, 1), "Interim report"));
        await In<GrantService, Grant>(s => s.AddReportDueAsync(grant.Id, new DateOnly(2026, 12, 20), "Financial report"));
        await In<GrantService, Grant>(s => s.AddReportDueAsync(grant.Id, new DateOnly(2027, 2, 1), "Final report"));
        Assert.Equal("grant.report_title_required", (await Assert.ThrowsAsync<DomainException>(() => In<GrantService, Grant>(s => s.AddReportDueAsync(grant.Id, today, " ")))).Code);

        var reminders = await In<GrantService, GrantReminders>(s => s.RemindersAsync(today));
        Assert.Equal(["Interim report", "Financial report"], reminders.Reports.Select(r => r.Report.Title));
        Assert.True(reminders.AnyOverdue);
        Assert.Equal(grant.Id, Assert.Single(reminders.Ending).Id);
        Assert.Equal(grant.Id, Assert.Single(reminders.ToRecognize).Id);

        await In<GrantService, Grant>(s => s.MarkReportSentAsync(grant.Id, 1, today));
        await In<GrantService, Grant>(s => s.RemoveReportDueAsync(grant.Id, 2));
        reminders = await In<GrantService, GrantReminders>(s => s.RemindersAsync(today));
        Assert.Empty(reminders.Reports);
        Assert.Equal(new DateOnly?(today), (await In<GrantService, Grant?>(s => s.GetAsync(grant.Id)))!.ReportsDue.Single(r => r.LineNumber == 1).SentOn);
    }

    [Fact]
    public async Task Investments_reconcile_and_board_designated_net_assets_show_apart()
    {
        var investments = await _db.CreateAccount("Investments", AccountType.OtherAsset, "1600");
        var board = await _db.CreateAccount("Board-Designated Reserve", AccountType.Equity, "3050");
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(investments.Id, 1_000m), JournalLineDraft.Cr(_income.Id, 1_000m)], "Bought shares")));
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(_ap.Id, 400m), JournalLineDraft.Cr(board.Id, 400m)], "Board reserve")));

        // A brokerage statement reconciles like a bank's.
        var reconciliation = await In<OpenBook.Application.Banking.ReconciliationService, Domain.Banking.Reconciliation>(s => s.StartAsync(investments.Id, Mar31, 1_000m));
        Assert.Equal(investments.Id, reconciliation.AccountId);
        Assert.Equal("bank.not_reconcilable", (await Assert.ThrowsAsync<DomainException>(() =>
            In<OpenBook.Application.Banking.ReconciliationService, Domain.Banking.Reconciliation>(s => s.StartAsync(_rent.Id, Mar31, 0m)))).Code);

        var position = await Report("statement-of-financial-position", "asOf=2026-03-31");
        Assert.Equal(400m, Amount(position, "Without donor restrictions: board-designated", 1));
        Assert.NotNull(Amount(position, "Without donor restrictions: undesignated", 1));
        Assert.True(OpenBook.Application.Nonprofit.NonprofitReports.IsBoardDesignated("Net Assets:Board designated fund"));
    }

    [Fact]
    public async Task A_grant_is_amended_upward_and_its_details_change_on_its_page()
    {
        var grant = await In<GrantService, Grant>(s => s.CreateAsync(new GrantCommand("Library", _acme.Id, 1_000m, Jan5, Jan5, new DateOnly(2026, 12, 31))));
        var renamed = await In<GrantService, Grant>(s => s.UpdateAsync(grant.Id, new GrantDetailsCommand("Library and archive", "AW-7", Jan5, new DateOnly(2027, 6, 30), "Extended")));
        Assert.Equal(("Library and archive", new DateOnly(2027, 6, 30)), (renamed.Name, renamed.EndsOn));

        var amended = await In<GrantService, Grant>(s => s.AmendAsync(grant.Id, Mar31, 500m, "Amendment 1"));
        Assert.Equal((1_500m, 1_500m, 1_500m), (amended.NetAward, amended.Recognized, amended.Receivable));
        await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, Mar31, 1_200m, _checking.Id));
        // The funder has paid more than the original award, so the amendment can't be taken back now.
        Assert.Equal("grant.increase_used", (await Assert.ThrowsAsync<DomainException>(() => In<GrantService, Grant>(s => s.VoidAdjustmentAsync(grant.Id, 1, "Mistake")))).Code);
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.Equal(0, report.Problems);
    }

    [Fact]
    public async Task The_net_asset_roll_forward_adds_up_and_in_kind_gifts_list_what_was_given()
    {
        var restricted = await _db.CreateAccount("Restricted Gifts", AccountType.Income, "4020");
        await In<OpenBook.Application.Accounts.AccountService, Account>(s => s.UpdateAsync(new OpenBook.Application.Accounts.UpdateAccountCommand(
            restricted.Id, restricted.Name, restricted.Type, restricted.Number, null, null, DonorRestricted: true)));
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(_checking.Id, 900m), JournalLineDraft.Cr(_income.Id, 600m), JournalLineDraft.Cr(restricted.Id, 300m)], "Gifts")));
        await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(_acme.Id, Mar31, 250m, _rent.Id, GiftKind.InKind, Description: "Office chairs")));

        var roll = await Report("net-asset-rollforward", "preset=Custom&from=2026-01-01&to=2026-12-31");
        decimal Cell(int row, int col) => (decimal)roll.Rows[row].Cells[col].Value!;
        Assert.Equal((0m, 0m), (Cell(0, 1), Cell(0, 2)));
        Assert.Equal(Cell(0, 3) + Cell(1, 3) + Cell(2, 3), Cell(3, 3));
        Assert.Equal(300m, Cell(3, 2));

        var inKind = await Report("in-kind-gifts", "preset=Custom&from=2026-01-01&to=2026-12-31");
        Assert.Equal("Office chairs", inKind.Rows[0].Cells[2].Value);
        Assert.Equal(250m, inKind.Rows[^1].Cells[3].Value);
    }

    [Fact]
    public async Task Grant_spending_on_a_subaccount_counts_against_its_budgeted_parent()
    {
        var grant = await GrantWithSpending();
        var hall = await In<OpenBook.Application.Accounts.AccountService, Account>(s => s.CreateAsync(new OpenBook.Application.Accounts.CreateAccountCommand("Hall", AccountType.Expense, "6010", _rent.Id)));
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Mar31,
            [JournalLineDraft.Dr(hall.Id, 40m, null, grant.ClassId), JournalLineDraft.Cr(_checking.Id, 40m)])));
        await In<GrantService, Grant>(s => s.SetBudgetAsync(grant.Id, [(_rent.Id, 500m)]));
        var position = (await In<GrantService, GrantPosition?>(s => s.PositionAsync(grant.Id, Mar31)))!;
        var row = Assert.Single(position.Rows);
        Assert.Equal((_rent.Id, 500m, 290m), (row.AccountId, row.Budget, row.Spent));
    }

    [Fact]
    public async Task Stock_is_written_down_in_value_without_losing_units()
    {
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Jan5, [new InventoryAdjustmentLineDraft(_widget.Id, 10m, 10m)])));
        Assert.Equal("inventory.write_down_too_much", (await Assert.ThrowsAsync<DomainException>(() => In<InventoryAdjustmentService, InventoryAdjustment>(s =>
            s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar31, [new InventoryAdjustmentLineDraft(_widget.Id, 0m, ValueChange: -101m)]))))).Code);
        var down = await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar31,
            [new InventoryAdjustmentLineDraft(_widget.Id, 0m, ValueChange: -40m)], Reference: "Obsolete")));
        var stock = (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))!;
        Assert.Equal((10m, 60m), (stock.Quantity, stock.Value));

        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.VoidAsync(down.Id, "Written down by mistake"));
        stock = (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))!;
        Assert.Equal((10m, 100m), (stock.Quantity, stock.Value));
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);

        // Once some of it has gone, the write-down stays.
        var again = await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar31,
            [new InventoryAdjustmentLineDraft(_widget.Id, 0m, ValueChange: -40m)])));
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar31, [new InventoryAdjustmentLineDraft(_widget.Id, -2m)])));
        Assert.Equal("inventory.write_down_moved", (await Assert.ThrowsAsync<DomainException>(() =>
            In<InventoryAdjustmentService, InventoryAdjustment>(s => s.VoidAsync(again.Id, "Too late")))).Code);
    }

    [Fact]
    public async Task The_funders_report_shows_the_period_to_date_and_whats_left()
    {
        var grant = await GrantWithSpending();
        var table = await Report("grant-budget-vs-actual", $"preset=Custom&from=2026-03-01&to=2026-03-31&grantId={grant.Id}");

        var total = table.Rows[^1];
        Assert.Equal(600m, total.Cells[1].Value as decimal?);
        Assert.Equal(50m, total.Cells[2].Value as decimal?);
        Assert.Equal(250m, total.Cells[3].Value as decimal?);
        Assert.Equal(350m, total.Cells[4].Value as decimal?);
    }

    [Fact]
    public async Task A_reimbursement_request_asks_for_whats_spent_and_a_payment_settles_it()
    {
        var grant = await GrantWithSpending();
        var requested = await In<GrantService, Grant>(s => s.RequestAsync(grant.Id, Mar31, Jan5, Mar31));
        var request = Assert.Single(requested.Requests);
        Assert.Equal(250m, request.Amount);
        Assert.NotEmpty((await In<GrantService, Application.Forms.RenderedForm>(s => s.RequestLetterAsync(grant.Id, 1))).Pdf);

        var paid = await In<GrantService, Grant>(s => s.ReceiveAsync(grant.Id, Mar31, 250m, _checking.Id, requestLine: 1));
        Assert.Equal(1, paid.Requests[0].ReceiptLine);
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<GrantService, Grant>(s => s.VoidRequestAsync(grant.Id, 1)));
        Assert.Equal("grant.request_paid", refused.Code);

        var voided = await In<GrantService, Grant>(s => s.VoidReceiptAsync(grant.Id, 1, "Bounced"));
        Assert.Null(voided.Requests[0].ReceiptLine);
    }

    [Fact]
    public async Task Nonprofit_statements_compare_with_an_earlier_period()
    {
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(new DateOnly(2025, 2, 1),
            [JournalLineDraft.Dr(_checking.Id, 70m), JournalLineDraft.Cr(_income.Id, 70m)])));
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 2, 1),
            [JournalLineDraft.Dr(_checking.Id, 90m), JournalLineDraft.Cr(_income.Id, 90m)])));

        var activities = await Report("statement-of-activities", "preset=Custom&from=2026-01-01&to=2026-12-31&compare=PreviousYear");
        Assert.Equal(5, activities.Columns.Count);
        var end = activities.Rows[^1];
        Assert.Equal(160m, end.Cells[3].Value as decimal?);
        Assert.Equal(70m, end.Cells[4].Value as decimal?);

        var position = await Report("statement-of-financial-position", "asOf=2026-12-31&compare=PreviousYear");
        Assert.Equal(3, position.Columns.Count);

        var expenses = await Report("functional-expenses", "preset=Custom&from=2026-01-01&to=2026-12-31&compare=PreviousYear");
        Assert.Contains(expenses.Columns, c => c.Header.Contains("2025", StringComparison.Ordinal));
    }
}
