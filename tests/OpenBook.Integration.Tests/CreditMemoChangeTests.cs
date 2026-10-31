using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted credit memo in place: its entry reversed and a new one posted, same id and number.</summary>
public sealed class CreditMemoChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
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
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<CreditMemo> Credit(DateOnly date, params SalesLineDraft[] lines) =>
        In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, date, lines)));

    private Task<CreditMemo> Change(Guid id, DateOnly date, SalesLineDraft[] lines, string? number = null, string? closingPassword = null) =>
        In<CreditService, CreditMemo>(s => s.ChangeCreditMemoAsync(id, new ChangeCreditMemoCommand(date, lines, "Corrected", number)), closingPassword);

    private SalesLineDraft Hours(decimal hours) => new(_consulting.Id, "Consulting", hours, 100m);

    private SalesLineDraft Course(decimal count = 1m) => new(_course.Id, "Course", count, 50m);

    private Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(basis, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_reverses_the_old_entry_and_posts_the_new_one()
    {
        var original = await Credit(Mar1, Hours(2m));
        var changed = await Change(original.Id, Mar5, [Hours(1m), Course()]);

        Assert.Equal((original.Id, original.Number, 150m, Mar5), (changed.Id, changed.Number, changed.Total, changed.Date));
        Assert.Equal((100m, 50m), (await Balance(_services.Id), await Balance(_training.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: CreditMemo.Source, SourceId: original.Id)));
        Assert.Equal(3, entries.Count);
        Assert.Equal(Mar1, entries.Single(e => e.Kind == JournalEntryKind.Reversal).Date);
        Assert.Single(await In<CreditService, IReadOnlyList<DocumentChange>>(s => s.CreditMemoChangesAsync(original.Id)));

        var other = await Credit(Mar1, Course());
        Assert.Equal("creditmemo.number_taken", await Refused(() => Change(original.Id, Mar5, [Course()], number: other.Number)));
    }

    [Fact]
    public async Task Applied_credit_keeps_the_total_and_the_date_no_later_than_the_first_application()
    {
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [Hours(5m)])));
        var credit = await Credit(Mar1, Hours(2m));
        await In<CreditService, bool>(async s =>
        {
            await s.ApplyAsync(_acme.Id, Mar5, [new ApplyCredit(CreditKind.CreditMemo, credit.Id, invoice.Id, 150m)]);
            return true;
        });

        Assert.Equal("creditmemo.total_below_used", await Refused(() => Change(credit.Id, Mar1, [Hours(1m)])));
        Assert.Equal("credit.applied_before_payment", await Refused(() => Change(credit.Id, new DateOnly(2026, 3, 6), [Hours(2m)])));
        var changed = await Change(credit.Id, Mar5, [Hours(1m), Course()]);
        Assert.Equal((150m, 150m, 0m), (changed.Total, changed.AmountApplied, changed.Balance));
    }

    [Fact]
    public async Task Refunded_credit_in_a_closed_period_needs_the_password()
    {
        var credit = await Credit(Jan5, Hours(2m));
        await In<CreditService, CustomerRefund>(s => s.RefundAsync(new RefundCreditCommand(CreditKind.CreditMemo, credit.Id, Jan10, _checking.Id, 200m)));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));

        Assert.Equal("period.password_required", await Refused(() => Change(credit.Id, Jan5, [Hours(1m), Course(2m)])));
        await Change(credit.Id, Jan5, [Hours(1m), Course(2m)], closingPassword: "secret1");

        // Cash basis: the refund now comes out of both income accounts.
        Assert.Equal((100m, 100m), (await Balance(_services.Id, ReportBasis.Cash), await Balance(_training.Id, ReportBasis.Cash)));
    }

    [Fact]
    public async Task Returned_stock_is_taken_out_and_put_back_until_it_is_sold_again()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 10m, PurchaseCost: 4m)));
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Jan5,
            [new InventoryAdjustmentLineDraft(widget.Id, 5m, 4m)])));
        Task<decimal> OnHand() => In<InventoryService, decimal>(async s => (await s.StockForAsync(widget.Id))!.Quantity);
        SalesLineDraft Widgets(decimal n) => new(widget.Id, "Widget", n, 10m);

        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan10, [Widgets(5m)])));
        var credit = await Credit(Mar1, Widgets(2m));
        Assert.Equal(2m, await OnHand());
        await Change(credit.Id, Mar1, [Widgets(1m)]);
        Assert.Equal(1m, await OnHand());

        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar5, [Widgets(1m)])));
        Assert.Equal("inventory.layer_used", await Refused(() => Change(credit.Id, Mar1, [Widgets(2m)])));
    }
}
