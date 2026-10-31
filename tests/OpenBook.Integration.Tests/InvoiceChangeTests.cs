using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.CustomFields;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted invoice in place: its entry reversed and a new one posted, same id and number.</summary>
public sealed class InvoiceChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _services = null!;
    private Account _training = null!;
    private Item _consulting = null!;
    private Item _course = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _services = await _db.CreateAccount("Services", AccountType.Income, "4000");
        _training = await _db.CreateAccount("Training", AccountType.Income, "4100");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _services.Id, 100m)));
        _course = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Course", ItemType.Service, _training.Id, 50m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", PaymentTermsDays: 30)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(DateOnly date, params SalesLineDraft[] lines) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, lines)));

    private Task<Invoice> Change(Guid id, DateOnly date, SalesLineDraft[] lines, string? closingPassword = null, IReadOnlyList<int?>? from = null) =>
        In<InvoiceService, Invoice>(s => s.ChangeAsync(id, new ChangeInvoiceCommand(date, lines, "Corrected", FromLines: from)), closingPassword);

    private SalesLineDraft Hours(decimal hours) => new(_consulting.Id, "Consulting", hours, 100m);

    private SalesLineDraft Course(decimal count = 1m) => new(_course.Id, "Course", count, 50m);

    private Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual, DateOnly? from = null, DateOnly? to = null) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(basis, from, to ?? new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_reverses_the_old_entry_and_posts_the_new_one_keeping_the_id_and_number()
    {
        var original = await Invoice(Mar1, Hours(2m));
        var changed = await Change(original.Id, Mar5, [Hours(3m), Course()]);

        Assert.Equal((original.Id, original.Number, 350m, Mar5), (changed.Id, changed.Number, changed.Total, changed.Date));
        Assert.Equal(Mar5.AddDays(30), changed.DueDate);
        Assert.Equal(300m, -await Balance(_services.Id));
        Assert.Equal(50m, -await Balance(_training.Id));

        // The original, its reversal on the original date, and the new entry on the new date, all on record.
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: Domain.Sales.Invoice.Source, SourceId: original.Id)));
        Assert.Equal(3, entries.Count);
        Assert.Equal(JournalEntryStatus.Voided, entries.Single(e => e.Id == original.JournalEntryId).Status);
        Assert.Equal(Mar1, entries.Single(e => e.Kind == JournalEntryKind.Reversal).Date);
        Assert.Equal((Mar5, JournalEntryStatus.Posted), entries.Where(e => e.Id == changed.JournalEntryId).Select(e => (e.Date, e.Status)).Single());

        var audit = await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == "invoice" && r.EntityId == original.Id && r.Action == AuditAction.Updated).ToListAsync());
        Assert.Contains("Corrected", audit.Single().After!, StringComparison.Ordinal);

        // Changing it again works on the new entry; voiding still works.
        await Change(original.Id, Mar5, [Hours(1m)]);
        await In<InvoiceService, Invoice>(s => s.VoidAsync(original.Id, "Cancelled"));
        Assert.Equal(0m, await Balance(_services.Id));
        Assert.Equal(0m, await Balance(_training.Id));
    }

    [Fact]
    public async Task A_paid_invoice_can_change_while_its_total_covers_what_was_settled()
    {
        var invoice = await Invoice(Mar1, Hours(2m));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar5, _checking.Id, [new ApplyToInvoice(invoice.Id, 150m)])));

        Assert.Equal("invoice.total_below_paid", await Refused(() => Change(invoice.Id, Mar1, [Hours(1m)])));
        var changed = await Change(invoice.Id, Mar1, [Hours(2m), Course()]);
        Assert.Equal((250m, 150m, 100m), (changed.Total, changed.AmountPaid, changed.Balance));
        Assert.Single(await In<InvoiceService, IReadOnlyList<CustomerPayment>>(s => s.ListPaymentsAsync(invoice.Id)));
    }

    [Fact]
    public async Task Closed_periods_guard_the_old_entry_and_payments_counted_on_cash_basis()
    {
        // A payment in January left as credit, applied to a March invoice: cash basis counts it in January.
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan5, _checking.Id, [], Amount: 200m)));
        var invoice = await Invoice(Mar1, Hours(2m));
        await In<CreditService, bool>(async s => { await s.ApplyAsync(_acme.Id, Mar1, [new ApplyCredit(CreditKind.Payment, payment.Id, invoice.Id, 200m)]); return true; });
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));
        Assert.Equal(200m, -await Balance(_services.Id, ReportBasis.Cash, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // The same income moved to other lines is fine; other income would change January's cash-basis figures.
        await Change(invoice.Id, Mar1, [Hours(1m), Hours(1m)]);
        Assert.Equal("period.password_required", await Refused(() => Change(invoice.Id, Mar1, [Hours(2m), Course()])));
        await Change(invoice.Id, Mar1, [Hours(2m), Course()], closingPassword: "secret1");
        Assert.Equal(50m * 200m / 250m, -await Balance(_training.Id, ReportBasis.Cash, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // An invoice dated in the closed period can't be changed without the password either.
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 3, 31), null, false), closingPassword: "secret1");
        Assert.Equal("period.password_required", await Refused(() => Change(invoice.Id, Mar1, [Hours(3m)])));
    }

    [Fact]
    public async Task Stock_is_put_back_and_taken_again()
    {
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _services.Id, 10m, PurchaseCost: 5m)));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Supplier")));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Jan5, [new BillLineDraft(Guid.Empty, 50m, null, widget.Id, 10m)])));
        Task<decimal> OnHand() => In<InventoryService, decimal>(async s => (await s.StockForAsync(widget.Id))!.Quantity);
        SalesLineDraft Widgets(decimal n) => new(widget.Id, "Widget", n, 10m);

        var invoice = await Invoice(Mar1, Widgets(3m));
        await Change(invoice.Id, Mar1, [Widgets(5m)]);
        Assert.Equal(5m, await OnHand());
        await Change(invoice.Id, Mar5, [Widgets(2m)]);
        Assert.Equal(8m, await OnHand());
        Assert.Equal("inventory.insufficient", await Refused(() => Change(invoice.Id, Mar5, [Widgets(11m)])));
        Assert.Equal(8m, await OnHand());

        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Cancelled"));
        Assert.Equal(10m, await OnHand());
    }

    [Fact]
    public async Task Lines_billed_from_a_sales_order_stay_as_they_are()
    {
        var order = await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_acme.Id, Mar1, [Hours(5m)])));
        var invoice = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1)));

        Assert.Equal("invoice.line_locked", await Refused(() => Change(invoice.Id, Mar1, [Hours(4m)])));
        var drafts = SalesLine.Drafts(invoice.Lines);
        var changed = await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Mar5, drafts, "Date was wrong", Memo: "Thanks")));
        Assert.Equal((Mar5, "Thanks", 500m), (changed.Date, changed.Memo, changed.Total));
        Assert.Equal("invoice.change_reason_required", await Refused(() =>
            In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Mar5, drafts, " ")))));
    }

    [Fact]
    public async Task Custom_field_values_follow_their_lines()
    {
        var packSize = (await In<CustomFieldService, IReadOnlyList<Domain.CustomFields.CustomField>>(s => s.AddPresetsAsync(["item.pack_size"]))).Single();
        await In<CustomFieldService, IReadOnlyList<CustomFieldEntry>>(s => s.SaveAsync("item", _course.Id, new Dictionary<Guid, string?> { [packSize.Id] = "Box of 5" }));
        var invoice = await Invoice(Mar1, Hours(1m), Course());
        Task<IReadOnlyDictionary<int, IReadOnlyList<CustomFieldEntry>>> LineValues() => In<CustomFieldService, IReadOnlyDictionary<int, IReadOnlyList<CustomFieldEntry>>>(s => s.LineEntriesAsync(invoice.Id));
        Assert.Equal("Box of 5", (await LineValues())[2].Single().Value);

        // The consulting line removed: the course is now line 1 and its value moves with it.
        await Change(invoice.Id, Mar1, [Course(2m)], from: [2]);
        var lines = await LineValues();
        Assert.Equal("Box of 5", lines[1].Single().Value);
        Assert.False(lines.ContainsKey(2));
    }
}
