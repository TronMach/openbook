using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted bill in place: its entry reversed and a new one posted, the approval rules run again.</summary>
public sealed class BillChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _rent = null!;
    private Account _supplies = null!;
    private Vendor _landlord = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6600");
        _landlord = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord", PaymentTermsDays: 15)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Bill> Bill(DateOnly date, params BillLineDraft[] lines) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_landlord.Id, date, lines, Reference: "A-1")));

    private Task<Bill> Change(Guid id, DateOnly date, BillLineDraft[] lines, string? closingPassword = null, string? reference = null) =>
        In<BillService, Bill>(s => s.ChangeAsync(id, new ChangeBillCommand(date, lines, "Corrected", Reference: reference)), closingPassword);

    private BillLineDraft Rent(decimal amount) => new(_rent.Id, amount, "Rent");

    private BillLineDraft Supplies(decimal amount) => new(_supplies.Id, amount, "Supplies");

    private Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual, DateOnly? from = null, DateOnly? to = null) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(basis, from, to ?? new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_reverses_the_old_entry_and_posts_the_new_one()
    {
        var original = await Bill(Mar1, Rent(1000m));
        var changed = await Change(original.Id, Mar5, [Rent(900m), Supplies(50m)], reference: "A-1b");

        Assert.Equal((original.Id, "A-1b", 950m, Mar5, Mar5.AddDays(15)), (changed.Id, changed.Reference, changed.Total, changed.Date, changed.DueDate));
        Assert.Equal((900m, 50m), (await Balance(_rent.Id), await Balance(_supplies.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: Domain.Purchases.Bill.Source, SourceId: original.Id)));
        Assert.Equal(3, entries.Count);
        Assert.Equal(Mar1, entries.Single(e => e.Kind == JournalEntryKind.Reversal).Date);
        Assert.Single(await In<BillService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(original.Id)));

        // A blank number clears it; another bill of the vendor's number is refused.
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_landlord.Id, Mar1, [Rent(10m)], Reference: "B-2")));
        Assert.Equal("bill.reference_taken", await Refused(() => Change(original.Id, Mar5, [Rent(900m)], reference: "b-2")));
        Assert.Null((await Change(original.Id, Mar5, [Rent(900m)], reference: " ")).Reference);
    }

    [Fact]
    public async Task A_paid_bill_can_change_while_its_total_covers_what_was_paid_and_closed_periods_are_guarded()
    {
        var bill = await Bill(Jan5, Rent(1000m));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Jan5, _checking.Id, [new ApplyToBill(bill.Id, 600m)])));
        Assert.Equal("bill.total_below_paid", await Refused(() => Change(bill.Id, Jan5, [Rent(500m)])));

        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));
        Assert.Equal("period.password_required", await Refused(() => Change(bill.Id, Jan5, [Rent(1100m)])));
        var changed = await Change(bill.Id, Jan5, [Rent(900m), Supplies(200m)], closingPassword: "secret1");
        Assert.Equal((1100m, 600m, 500m), (changed.Total, changed.AmountPaid, changed.Balance));

        // Cash basis: January's payment now spreads over both lines.
        Assert.Equal(600m * 200m / 1100m, decimal.Round(await Balance(_supplies.Id, ReportBasis.Cash, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)), 2), 2);
    }

    [Fact]
    public async Task Approval_rules_run_again_after_a_change()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(1000m, 5m, true, AllowSelfApproval: true)));
        var bill = await Bill(Mar1, Rent(500m));
        Assert.Equal(BillApproval.NotRequired, bill.Approval);

        var held = await Change(bill.Id, Mar1, [Rent(1500m)]);
        Assert.Equal(BillApproval.Pending, held.Approval);
        await In<BillApprovalService, Bill>(s => s.ApproveAsync(bill.Id, "ok"));

        // Approved, then changed again: it needs approving again.
        Assert.Equal(BillApproval.Pending, (await Change(bill.Id, Mar1, [Rent(1400m)])).Approval);
        var under = await Change(bill.Id, Mar1, [Rent(400m)]);
        Assert.Equal((BillApproval.NotRequired, null), (under.Approval, under.HoldReason));
    }

    [Fact]
    public async Task Stock_it_brought_in_is_received_again_until_some_is_used()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 10m, PurchaseCost: 5m)));
        Task<decimal> OnHand() => In<InventoryService, decimal>(async s => (await s.StockForAsync(widget.Id))!.Quantity);
        BillLineDraft Widgets(decimal n, decimal amount) => new(Guid.Empty, amount, "Widgets", widget.Id, n);

        var bill = await Bill(Mar1, Widgets(10m, 50m));
        await Change(bill.Id, Mar1, [Widgets(8m, 48m)]);
        Assert.Equal(8m, await OnHand());

        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar5, [new SalesLineDraft(widget.Id, "Widget", 3m, 10m)])));
        Assert.Equal("inventory.layer_used", await Refused(() => Change(bill.Id, Mar1, [Widgets(9m, 54m)])));
        Assert.Equal(5m, await OnHand());
    }

    [Fact]
    public async Task Lines_marked_billable_stay_on_their_line()
    {
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var bill = await Bill(Mar1, Rent(1000m), Supplies(80m));
        await In<BillableExpenseService, BillableExpense>(s => s.MarkAsync(new MarkBillableCommand(BillableSource.Bill, bill.Id, 2, customer.Id)));

        Assert.Equal("bill.line_locked", await Refused(() => Change(bill.Id, Mar1, [Supplies(80m)])));
        Assert.Equal("bill.line_locked", await Refused(() => Change(bill.Id, Mar1, [Rent(1000m), Supplies(90m)])));
        Assert.Equal(1050m, (await Change(bill.Id, Mar5, [Rent(970m), Supplies(80m)])).Total);
    }

    [Fact]
    public async Task A_purchase_order_keeps_the_vendors_address_it_was_sent_to()
    {
        var supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Co", Address: "1 Old Road")));
        var other = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Ink Co", Address: "9 Ink Lane")));
        var lines = new[] { new PurchaseOrderLineDraft(null, _supplies.Id, "Paper", 1m, 40m) };
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(supplier.Id, Mar1, lines)));
        Assert.Equal("1 Old Road", order.VendorAddress);

        await In<VendorService, Vendor>(s => s.UpdateAsync(supplier.Id, new VendorDetails("Paper Co", Address: "2 New Street")));
        order = await In<PurchaseOrderService, PurchaseOrder>(s => s.UpdateAsync(order.Id, new SavePurchaseOrderCommand(supplier.Id, Mar5, lines)));
        Assert.Equal("1 Old Road", order.VendorAddress);
        order = await In<PurchaseOrderService, PurchaseOrder>(s => s.UpdateAsync(order.Id, new SavePurchaseOrderCommand(other.Id, Mar5, lines)));
        Assert.Equal("9 Ink Lane", order.VendorAddress);
    }

    [Fact]
    public async Task Bills_from_a_purchase_order_change_through_the_order()
    {
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_landlord.Id, Mar1,
            [new PurchaseOrderLineDraft(null, _supplies.Id, "Paper", 1m, 40m)])));
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1)));
        Assert.Equal("bill.from_order", await Refused(() => Change(bill.Id, Mar5, [Supplies(40m)])));
    }
}
