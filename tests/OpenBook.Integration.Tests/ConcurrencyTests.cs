using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>Two saves overlapping: the second fails as a conflict (nothing saved) instead of a raw database error or a lost update.</summary>
public sealed class ConcurrencyTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _revenue = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _revenue = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Consulting", ItemType.Service, _revenue.Id, 100m)));
        _acme = await _db.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme Corp")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private PostJournalEntryCommand Entry(decimal amount) =>
        new(Mar1, [new JournalLineDraft(_checking.Id, amount, 0m), new JournalLineDraft(_revenue.Id, 0m, amount)]);

    [Fact]
    public async Task An_entry_number_taken_in_between_is_a_conflict_and_nothing_is_saved()
    {
        await using var late = _db.Services.CreateAsyncScope();
        var lateDb = late.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        var number = await late.ServiceProvider.GetRequiredService<ILedgerStore>().NextEntryNumberAsync(default);

        // Someone else posts first and takes that number.
        var first = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(Entry(10m)));
        Assert.Equal(number, first.Number);

        lateDb.JournalEntries.Add(JournalEntry.Post(number, Mar1, first.Currency,
            [new JournalLineDraft(_checking.Id, 20m, 0m), new JournalLineDraft(_revenue.Id, 0m, 20m)], DateTimeOffset.UtcNow));
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => lateDb.SaveChangesAsync());
        Assert.Equal("save.conflict", ex.Code);
        Assert.False(late.ServiceProvider.GetRequiredService<IUnitOfWork>().HasSaved);
        Assert.Equal(1, await _db.InDb(db => db.JournalEntries.CountAsync()));

        // Run again from the start, it takes the next number.
        var again = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(Entry(20m)));
        Assert.Equal(number + 1, again.Number);
    }

    [Fact]
    public async Task A_change_to_an_invoice_someone_else_just_paid_is_refused_rather_than_overwriting_the_payment()
    {
        var invoice = await _db.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(
            new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, 2m, 100m)])));

        await using var stale = _db.Services.CreateAsyncScope();
        var staleDb = stale.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        var staleInvoice = await staleDb.Invoices.SingleAsync(i => i.Id == invoice.Id);

        await _db.InScope(sp => sp.GetRequiredService<PaymentService>().ReceiveAsync(
            new ReceivePaymentCommand(_acme.Id, Mar1, _checking.Id, [new ApplyToInvoice(invoice.Id, 200m)])));

        // The stale copy still says nothing is paid; saving it would put the amount paid back to zero.
        Assert.Equal(0m, staleInvoice.AmountPaid);
        staleDb.Entry(staleInvoice).Property(i => i.AmountPaid).CurrentValue = 0m;
        staleDb.Entry(staleInvoice).Property(i => i.Memo).CurrentValue = "Changed meanwhile";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => staleDb.SaveChangesAsync());

        var saved = await _db.InDb(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id));
        Assert.Equal(200m, saved.AmountPaid);
        Assert.Equal(1, await _db.InDb(db => db.Invoices.Where(i => i.Id == invoice.Id).Select(i => EF.Property<int>(i, "Version")).SingleAsync()));
    }

    [Fact]
    public async Task Two_sales_planned_from_the_same_stock_cant_both_take_the_last_units()
    {
        var widget = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(
            new ItemDetails("Widget", ItemType.Inventory, _revenue.Id, 10m, PurchaseCost: 5m)));
        var vendor = await _db.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Supplier")));
        await _db.InScope(sp => sp.GetRequiredService<BillService>().CreateAsync(
            new CreateBillCommand(vendor.Id, Mar1, [new BillLineDraft(Guid.Empty, 25m, null, widget.Id, 5m)])));
        Task<Invoice> SellAll() => _db.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(
            new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(widget.Id, null, 5m, 10m)])));

        // A sale planned from the stock as it was: the item loaded, then its movements read, all five taken.
        await using var stale = _db.Services.CreateAsyncScope();
        var staleDb = stale.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        var item = await staleDb.Items.SingleAsync(i => i.Id == widget.Id);
        var movements = await stale.ServiceProvider.GetRequiredService<IInventoryStore>().ListMovementsAsync(widget.Id, default);
        var takes = Costing.Take(item, movements, 5m, Mar1);

        // Meanwhile someone else sells all five.
        await SellAll();

        foreach (var take in takes)
            staleDb.InventoryMovements.Add(InventoryMovement.Issue(take, widget.Id, Mar1, "test", Guid.NewGuid(), DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => staleDb.SaveChangesAsync());
        Assert.Equal(0m, (await _db.InScope(sp => sp.GetRequiredService<InventoryService>().StockForAsync(widget.Id)))!.Quantity);

        // Run again from the start, the sale sees the stock is gone.
        Assert.Equal("inventory.insufficient", (await Assert.ThrowsAsync<DomainException>(SellAll)).Code);
    }

    [Fact]
    public async Task A_scope_knows_once_it_has_saved_so_a_conflict_after_that_isnt_retried()
    {
        await using var scope = _db.Services.CreateAsyncScope();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var conflict = new ConcurrencyConflictException(new InvalidOperationException());
        Assert.True(ConflictRetry.ShouldRetry(conflict, 1, work));

        await scope.ServiceProvider.GetRequiredService<PostingEngine>().PostAsync(Entry(5m));
        Assert.True(work.HasSaved);
        Assert.False(ConflictRetry.ShouldRetry(conflict, 1, work));

        await using var fresh = _db.Services.CreateAsyncScope();
        var freshWork = fresh.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Assert.False(ConflictRetry.ShouldRetry(conflict, ConflictRetry.Attempts, freshWork));
        Assert.False(ConflictRetry.ShouldRetry(new DomainException("x", "y"), 1, freshWork));
    }
}
