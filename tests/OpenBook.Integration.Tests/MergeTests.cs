using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Application.Lists;
using OpenBook.Application.Notes;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Merging duplicate customers, vendors and items: everything moves to the one kept, the ledger stays as posted.</summary>
public sealed class MergeTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _undeposited = null!;
    private Account _supplies = null!;
    private Account _sales = null!;
    private Item _service = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _sales.Id, 100m)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Customer> Customer(string name) => In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name)));

    private Task<Invoice> Invoice(Customer customer, Item? item = null) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft((item ?? _service).Id, "Work", 1m, 100m)])));

    [Fact]
    public async Task Merging_customers_moves_documents_notes_and_balances_to_the_one_kept()
    {
        var dup = await Customer("Acme Inc");
        var keep = await Customer("Acme");
        var invoice = await Invoice(dup);
        await Invoice(keep);
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(dup.Id, Mar1, _undeposited.Id, [new ApplyToInvoice(invoice.Id, 40m)])));
        await In<NoteService, OpenBook.Domain.Notes.Note>(s => s.AddAsync("customer", dup.Id, "Prefers email"));

        var result = await In<MergeService, MergeResult>(s => s.MergeCustomersAsync(dup.Id, keep.Id));
        Assert.True(result.Moved >= 3);

        Assert.Equal(keep.Id, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.CustomerId);
        Assert.Single(await In<NoteService, IReadOnlyList<OpenBook.Domain.Notes.Note>>(s => s.ListAsync("customer", keep.Id)));
        var merged = (await In<CustomerService, Customer?>(s => s.GetAsync(dup.Id)))!;
        Assert.Equal(("Acme Inc (merged)", false), (merged.DisplayName, merged.IsActive));

        var aging = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(Mar1.AddDays(1)));
        Assert.Equal(160m, Assert.Single(aging.Customers).Buckets.Total);
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);
        Assert.Contains(await _db.InDb(db => db.AuditRecords.Where(a => a.EntityId == dup.Id).ToListAsync()), a => a.After!.Contains("MergedInto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_merge_that_would_break_a_one_per_record_rule_changes_nothing()
    {
        var dup = await Customer("Beta Ltd");
        var keep = await Customer("Beta");
        await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(), CustomerId: dup.Id)));
        await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(), CustomerId: keep.Id)));
        var invoice = await Invoice(dup);

        Assert.Equal("merge.conflict", (await Assert.ThrowsAsync<DomainException>(() => In<MergeService, MergeResult>(s => s.MergeCustomersAsync(dup.Id, keep.Id)))).Code);
        Assert.Equal(dup.Id, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.CustomerId);
        Assert.True((await In<CustomerService, Customer?>(s => s.GetAsync(dup.Id)))!.IsActive);

        Assert.Equal("merge.same", (await Assert.ThrowsAsync<DomainException>(() => In<MergeService, MergeResult>(s => s.MergeCustomersAsync(keep.Id, keep.Id)))).Code);
    }

    [Fact]
    public async Task Merging_vendors_moves_bills_and_preferred_vendors()
    {
        var dup = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Co.")));
        var keep = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Co")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(dup.Id, Mar1, [new BillLineDraft(_supplies.Id, 50m, "Paper")])));
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _sales.Id, 10m, PreferredVendorId: dup.Id)));

        await In<MergeService, MergeResult>(s => s.MergeVendorsAsync(dup.Id, keep.Id));
        Assert.Equal(keep.Id, (await In<BillService, Bill?>(s => s.GetAsync(bill.Id)))!.VendorId);
        Assert.Equal(keep.Id, (await In<ItemService, Item?>(s => s.GetAsync(widget.Id)))!.PreferredVendorId);
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);
    }

    [Fact]
    public async Task Merging_items_moves_sales_lines_but_not_stock()
    {
        var dup = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consult", ItemType.Service, _sales.Id, 90m)));
        var invoice = await Invoice(await Customer("Gamma"), dup);
        await In<MergeService, MergeResult>(s => s.MergeItemsAsync(dup.Id, _service.Id));
        Assert.Equal(_service.Id, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Lines.Single().ItemId);
        Assert.False((await In<ItemService, Item?>(s => s.GetAsync(dup.Id)))!.IsActive);

        var stocked = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _sales.Id, 10m)));
        Assert.Equal("merge.item_stock", (await Assert.ThrowsAsync<DomainException>(() => In<MergeService, MergeResult>(s => s.MergeItemsAsync(stocked.Id, _service.Id)))).Code);
    }
}
