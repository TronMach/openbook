using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>The audit's Medium findings on imports, banking and stock.</summary>
public sealed class ImportBankingStockFindingsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _office = null!;
    private Account _income = null!;
    private Item _widget = null!;
    private Vendor _staples = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _office = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6300");
        _income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _income.Id, 20m)));
        _staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<MigrationResult> Preview(MigrationSource source, string content) =>
        _db.InScope(sp => sp.GetRequiredService<MigrationImportService>().PreviewAsync(source, Encoding.UTF8.GetBytes(content)));

    private Task<BankImportPreview> Statement(string text) =>
        In<BankFeedService, BankImportPreview>(s => s.PreviewAsync(_checking.Id, text, new BankImportOptions()));

    private Task<Bill> Stock(decimal quantity, decimal cost, DateOnly date, string reference) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_staples.Id, date, [new BillLineDraft(Guid.Empty, quantity * cost, null, _widget.Id, quantity)], Reference: reference)));

    private Task<Invoice> Sell(decimal quantity, DateOnly date) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(
            _db.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails($"Buyer {Guid.NewGuid():N}"))).GetAwaiter().GetResult().Id,
            date, [new SalesLineDraft(_widget.Id, null, quantity, 20m)])));

    [Fact]
    public void A_file_with_two_accounts_is_refused()
    {
        const string text = """
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>1111</ACCTID></BANKACCTFROM><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260304</DTPOSTED><TRNAMT>-5.00</TRNAMT><FITID>A</FITID><NAME>ONE</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>2222</ACCTID></BANKACCTFROM><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260304</DTPOSTED><TRNAMT>-6.00</TRNAMT><FITID>B</FITID><NAME>TWO</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;

        Assert.Equal("ofx.several_accounts", Assert.Throws<DomainException>(() => Ofx.Parse(text)).Code);
    }

    [Fact]
    public async Task The_second_of_two_identical_lines_comes_in_on_its_own()
    {
        const string csv = "Date,Description,Amount\n03/05/2026,COFFEE,-4.00\n";
        const string both = "Date,Description,Amount\n03/05/2026,COFFEE,-4.00\n03/05/2026,COFFEE,-4.00\n";
        async Task Queue(BankImportPreview preview) => await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, preview.Rows.Where(r => !r.AlreadyImported)
            .Select(r => new FeedLine(r.Date!.Value, r.Amount!.Value, r.Payee, r.Reference, r.Memo, r.FitId, r.Occurrence)).ToList()));

        await Queue(await Statement(csv));
        var again = await Statement(both);
        Assert.Equal([true, false], again.Rows.Select(r => r.AlreadyImported));
        await Queue(again);

        Assert.Equal(2, (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id))).Count);
    }

    [Fact]
    public async Task A_journal_row_with_no_amount_is_left_out()
    {
        const string csv = "Entry,Date,Account,Debit,Credit\n1,03/05/2026,Office Supplies,10.00,\n1,03/05/2026,Checking,,10.00\n1,03/05/2026,Sales,,\n";

        var preview = await In<JournalImportService, JournalImportResult>(s => s.PreviewEntriesAsync(csv));

        Assert.True(Assert.Single(preview.Entries).IsValid);
        Assert.Equal(2, preview.Entries[0].Lines.Count);
    }

    [Fact]
    public async Task A_transaction_of_nothing_but_zeros_is_skipped()
    {
        const string iif = "!ACCNT\tNAME\tACCNTTYPE\nACCNT\tCash\tBANK\nACCNT\tRent\tEXP\n"
            + "!TRNS\tTRNSTYPE\tDATE\tACCNT\tAMOUNT\n!SPL\tTRNSTYPE\tDATE\tACCNT\tAMOUNT\n!ENDTRNS\n"
            + "TRNS\tCHECK\t3/5/2026\tCash\t0\nSPL\tCHECK\t3/5/2026\tRent\t0\nENDTRNS\n";

        var preview = await Preview(MigrationSource.QuickBooksIif, iif);

        Assert.Equal(MigrationRowStatus.Skipped, preview.Rows.Single(r => r.Kind == MigrationKind.Transaction).Status);
        Assert.DoesNotContain(preview.Rows, r => r.Status == MigrationRowStatus.Error);
    }

    [Fact]
    public async Task An_account_numbered_like_the_chart_comes_in_without_its_number()
    {
        var preview = await Preview(MigrationSource.QuickBooksIif, "!ACCNT\tNAME\tACCNTTYPE\tACCNUM\nACCNT\tPetty Cash\tBANK\t1000\n");

        var row = preview.Rows.Single(r => r.Kind == MigrationKind.Account);
        Assert.Equal(MigrationRowStatus.New, row.Status);
        Assert.Contains("without a number", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transaction_on_an_inactive_account_shows_in_the_preview()
    {
        await In<AccountService, Account>(s => s.DeactivateAsync(_office.Id));
        const string iif = "!TRNS\tTRNSTYPE\tDATE\tACCNT\tAMOUNT\n!SPL\tTRNSTYPE\tDATE\tACCNT\tAMOUNT\n!ENDTRNS\n"
            + "TRNS\tCHECK\t3/5/2026\tChecking\t-5\nSPL\tCHECK\t3/5/2026\tOffice Supplies\t5\nENDTRNS\n";

        var row = (await Preview(MigrationSource.QuickBooksIif, iif)).Rows.Single(r => r.Kind == MigrationKind.Transaction);

        Assert.Equal(MigrationRowStatus.Error, row.Status);
        Assert.Contains("inactive", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_cant_be_categorized_to_accounts_payable()
    {
        var payable = await _db.CreateAccount("Accounts Payable", AccountType.AccountsPayable, "2000");

        Assert.Equal("bank.category_receivable", await Refused(() => In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar5, [new BankSplit(payable.Id, 30m)], "Staples")))));
    }

    [Fact]
    public async Task A_line_a_rule_cant_post_waits_for_review_and_the_rest_come_in()
    {
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Coffee", "COFFEE", _office.Id, AutoAdd: true)));
        await In<AccountService, Account>(s => s.DeactivateAsync(_office.Id));

        var result = await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, -4m, "COFFEE", null, null, "fit-1"), new FeedLine(Mar5, -9m, "LUNCH", null, null, "fit-2")]));

        Assert.Equal((2, 0), (result.Queued, result.AutoAdded));
        Assert.Contains("Coffee", result.NotAdded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_never_categorizes_a_line_to_its_own_account()
    {
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Sweep", "SWEEP", _checking.Id, AutoAdd: true)));

        var result = await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, -4m, "SWEEP", null, null, "fit-1")]));

        Assert.Equal((1, 0, null), (result.Queued, result.AutoAdded, result.NotAdded));
    }

    [Fact]
    public async Task Found_stock_with_no_purchase_cost_comes_in_at_the_latest_receipt()
    {
        await Stock(10m, 5m, Mar1, "S-1");

        var adjustment = await In<InventoryAdjustmentService, InventoryAdjustment?>(s => s.CountAsync(new InventoryCountCommand(Mar5, [new CountedItem(_widget.Id, 12m)])));

        Assert.Equal(10m, adjustment!.Lines.Single().ValueChange);
    }

    [Fact]
    public async Task A_backdated_sale_is_told_the_stock_went_to_later_sales()
    {
        await Stock(10m, 5m, new DateOnly(2026, 1, 10), "S-1");
        await Stock(10m, 5m, new DateOnly(2026, 1, 20), "S-2");
        await Sell(15m, new DateOnly(2026, 2, 1));

        var refused = await Assert.ThrowsAsync<DomainException>(() => Sell(1m, new DateOnly(2026, 1, 15)));

        Assert.Equal("inventory.insufficient", refused.Code);
        Assert.Contains("later sales", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_warehouse_or_bin_with_stock_stays_active()
    {
        var east = await In<WarehouseService, Warehouse>(s => s.CreateAsync("East", null));
        var bin = await In<WarehouseService, Bin>(s => s.AddBinAsync(east.Id, "A-1"));
        await Stock(5m, 5m, Mar1, "S-1");
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar5,
            [new InventoryAdjustmentLineDraft(_widget.Id, 3m, 5m, new StockPlace(east.Id, bin.Id))], Guid.Empty)));

        Assert.Equal("warehouse.has_stock", await Refused(() => In<WarehouseService, Warehouse>(s => s.UpdateAsync(east.Id, "East", null, active: false))));
        Assert.Equal("bin.has_stock", await Refused(() => In<WarehouseService, Bin>(s => s.SetBinActiveAsync(bin.Id, false))));
    }

    [Fact]
    public async Task An_account_with_postings_cant_change_to_or_from_accounts_receivable()
    {
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _income.Id, 100m)));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft(service.Id, null, 1m, 100m)])));

        var code = await Refused(() => In<AccountService, Account>(s => s.UpdateAsync(new UpdateAccountCommand(invoice.ReceivableAccountId, "Accounts Receivable", AccountType.OtherCurrentAsset, null, null, null))));

        Assert.Equal("account.type_receivable", code);
    }
}
