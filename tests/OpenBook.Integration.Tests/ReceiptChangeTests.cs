using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Banking;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted sales receipt in place: its entry reversed and a new one posted, same id.</summary>
public sealed class ReceiptChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _services = null!;
    private Account _training = null!;
    private Item _consulting = null!;
    private Item _course = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
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

    private Task<SalesReceipt> Receipt(DateOnly date, Guid depositTo, params SalesLineDraft[] lines) =>
        In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(null, date, depositTo, lines, Reference: "Cash")));

    private Task<SalesReceipt> Change(Guid id, DateOnly date, SalesLineDraft[] lines, Guid? customerId = null, Guid? depositTo = null,
        string? number = null, string? closingPassword = null) =>
        In<SalesReceiptService, SalesReceipt>(s => s.ChangeAsync(id, new ChangeSalesReceiptCommand(date, lines, "Corrected", customerId, depositTo, number)),
            closingPassword);

    private SalesLineDraft Hours(decimal hours) => new(_consulting.Id, "Consulting", hours, 100m);

    private SalesLineDraft Course(decimal count = 1m) => new(_course.Id, "Course", count, 50m);

    private Task<decimal> Balance(Guid accountId) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_reverses_the_old_entry_and_posts_the_new_one()
    {
        var original = await Receipt(Mar1, _undeposited.Id, Hours(2m));
        var changed = await Change(original.Id, Mar5, [Hours(1m), Course()], _acme.Id, _checking.Id, "R-7");

        Assert.Equal((original.Id, "R-7", 150m, Mar5, _acme.Id, _checking.Id, "Cash"),
            (changed.Id, changed.Number, changed.Total, changed.Date, changed.CustomerId, changed.DepositAccountId, changed.Reference));
        Assert.Equal((150m, 0m, -100m, -50m), (await Balance(_checking.Id), await Balance(_undeposited.Id), await Balance(_services.Id), await Balance(_training.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: SalesReceipt.Source, SourceId: original.Id)));
        Assert.Equal(3, entries.Count);
        Assert.Equal(Mar1, entries.Single(e => e.Kind == JournalEntryKind.Reversal).Date);
        Assert.Single(await In<SalesReceiptService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(original.Id)));

        // Back to a walk-in sale; another receipt's number is refused.
        var other = await Receipt(Mar1, _checking.Id, Course());
        Assert.Equal("receipt.number_taken", await Refused(() => Change(original.Id, Mar5, [Course()], number: other.Number.ToLowerInvariant())));
        Assert.Null((await Change(original.Id, Mar5, [Hours(1m), Course()])).CustomerId);
    }

    [Fact]
    public async Task A_deposited_receipt_keeps_its_total_and_deposit_account()
    {
        var receipt = await Receipt(Mar1, _undeposited.Id, Hours(1m), Course());
        await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar5, [(DepositedKind.SalesReceipt, receipt.Id)])));

        Assert.Equal("receipt.deposited", await Refused(() => Change(receipt.Id, Mar1, [Hours(1m)])));
        Assert.Equal("receipt.deposited", await Refused(() => Change(receipt.Id, Mar1, [Hours(1m), Course()], depositTo: _checking.Id)));

        // Same money, other income: fine.
        await Change(receipt.Id, Mar1, [Course(3m)]);
        Assert.Equal((150m, 0m, 0m, -150m), (await Balance(_checking.Id), await Balance(_undeposited.Id), await Balance(_services.Id), await Balance(_training.Id)));
    }

    [Fact]
    public async Task A_receipt_matched_to_the_bank_feed_keeps_its_total_and_the_match_follows_it()
    {
        var receipt = await Receipt(Mar1, _checking.Id, Hours(1m), Course());
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar1, 150m, "Deposit", null, null, "F1")]));
        var line = (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending))).Single().Item;
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Id, receipt.JournalEntryId));

        Assert.Equal("receipt.feed_matched", await Refused(() => Change(receipt.Id, Mar1, [Hours(1m)])));
        var changed = await Change(receipt.Id, Mar1, [Course(3m)]);
        Assert.Equal(changed.JournalEntryId, (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Matched)))
            .Single().Item.MatchedJournalEntryId);
    }

    [Fact]
    public async Task Closed_periods_need_the_password_and_voided_receipts_cant_change()
    {
        var receipt = await Receipt(Jan5, _checking.Id, Hours(1m));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));
        Assert.Equal("period.password_required", await Refused(() => Change(receipt.Id, Mar1, [Hours(2m)])));
        Assert.Equal(200m, (await Change(receipt.Id, Mar1, [Hours(2m)], closingPassword: "secret1")).Total);

        await In<SalesReceiptService, SalesReceipt>(s => s.VoidAsync(receipt.Id, "Wrong"));
        Assert.Equal("receipt.voided", await Refused(() => Change(receipt.Id, Mar1, [Hours(1m)])));
    }

    [Fact]
    public async Task Stock_is_put_back_and_taken_again()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 10m, PurchaseCost: 4m)));
        await In<InventoryAdjustmentService, InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(Mar1,
            [new InventoryAdjustmentLineDraft(widget.Id, 5m, 4m)])));
        Task<decimal> OnHand() => In<InventoryService, decimal>(async s => (await s.StockForAsync(widget.Id))!.Quantity);
        SalesLineDraft Widgets(decimal n) => new(widget.Id, "Widget", n, 10m);

        var receipt = await Receipt(Mar5, _checking.Id, Widgets(2m));
        Assert.Equal(3m, await OnHand());
        await Change(receipt.Id, Mar5, [Widgets(5m)]);
        Assert.Equal(0m, await OnHand());
        Assert.Equal("inventory.insufficient", await Refused(() => Change(receipt.Id, Mar5, [Widgets(6m)])));
        Assert.Equal(0m, await OnHand());
    }
}
