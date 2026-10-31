using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted expense, check or deposit in place: its entry reversed and a new one posted, same id.</summary>
public sealed class BankTransactionChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _savings = null!;
    private Account _rent = null!;
    private Account _supplies = null!;
    private Account _sales = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _savings = await _db.CreateAccount("Savings", AccountType.Bank, "1010");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6600");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<BankTransaction> Spend(DateOnly date, string? reference, params BankSplit[] splits) =>
        In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, date, splits, "Landlord", reference)));

    private Task<BankTransaction> Change(Guid id, DateOnly date, BankSplit[] splits, string? payee = "Landlord", string? reference = null,
        Guid? vendorId = null, string? closingPassword = null) =>
        In<BankingService, BankTransaction>(s => s.ChangeAsync(id, new ChangeBankTransactionCommand(date, splits, "Corrected", payee, reference, VendorId: vendorId)),
            closingPassword);

    private Task<decimal> Balance(Guid accountId) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    private async Task<BankFeedItem> FeedLine(DateOnly date, decimal amount, string fitId)
    {
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(date, amount, "Landlord", null, null, fitId)]));
        return (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending))).Single(r => r.Item.FitId == fitId).Item;
    }

    [Fact]
    public async Task A_change_reverses_the_old_entry_and_posts_the_new_one()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord LLC")));
        var original = await Spend(Mar1, "1001", new BankSplit(_rent.Id, 1000m));
        var changed = await Change(original.Id, Mar5, [new BankSplit(_rent.Id, 900m), new BankSplit(_supplies.Id, 50m, "Keys")], payee: null, reference: "1001", vendorId: vendor.Id);

        Assert.Equal((original.Id, 950m, Mar5, "Landlord LLC", vendor.Id, "1001"), (changed.Id, changed.Amount, changed.Date, changed.Payee, changed.VendorId, changed.Reference));
        Assert.Equal((-950m, 900m, 50m), (await Balance(_checking.Id), await Balance(_rent.Id), await Balance(_supplies.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: BankTransaction.SpendSource, SourceId: original.Id)));
        Assert.Equal(3, entries.Count);
        Assert.Equal(Mar1, entries.Single(e => e.Kind == JournalEntryKind.Reversal).Date);
        Assert.Single(await In<BankingService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(original.Id)));
        Assert.Equal([new BankSplit(_rent.Id, 900m), new BankSplit(_supplies.Id, 50m, "Keys")], await In<BankingService, IReadOnlyList<BankSplit>>(s => s.SplitsAsync(changed)));

        // Deposits too; transfers and voided transactions can't change.
        var deposit = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Receive, _checking.Id, Mar1, [new BankSplit(_sales.Id, 300m)])));
        Assert.Equal(250m, (await Change(deposit.Id, Mar1, [new BankSplit(_sales.Id, 250m)], payee: "Walk-in")).Amount);
        Assert.Equal(-250m, await Balance(_sales.Id));
        Assert.Equal("bank.vendor_on_spend_only", await Refused(() => Change(deposit.Id, Mar1, [new BankSplit(_sales.Id, 250m)], vendorId: vendor.Id)));
        var transfer = await In<BankingService, BankTransaction>(s => s.TransferAsync(new TransferCommand(_checking.Id, _savings.Id, Mar1, 100m)));
        Assert.Equal("bank.transfer_change", await Refused(() => Change(transfer.Id, Mar1, [new BankSplit(_rent.Id, 100m)])));
        await In<BankingService, BankTransaction>(s => s.VoidAsync(original.Id, "Wrong"));
        Assert.Equal("bank.voided", await Refused(() => Change(original.Id, Mar5, [new BankSplit(_rent.Id, 900m)])));
    }

    [Fact]
    public async Task Closed_periods_need_the_password_and_reconciled_transactions_cant_change()
    {
        var spend = await Spend(Jan5, null, new BankSplit(_rent.Id, 1000m));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));
        Assert.Equal("period.password_required", await Refused(() => Change(spend.Id, Mar1, [new BankSplit(_supplies.Id, 1000m)])));
        await Change(spend.Id, Mar1, [new BankSplit(_supplies.Id, 1000m)], closingPassword: "secret1");

        var reconciliations = (Func<IServiceProvider, ReconciliationService>)(sp => sp.GetRequiredService<ReconciliationService>());
        var rec = await _db.InScope(sp => reconciliations(sp).StartAsync(_checking.Id, new DateOnly(2026, 3, 31), -1000m));
        var sheet = await _db.InScope(sp => reconciliations(sp).GetWorksheetAsync(rec.Id));
        await _db.InScope(sp => reconciliations(sp).SetClearedAsync(rec.Id, [sheet.Items.Single().Line.JournalLineId], true));
        Assert.Equal("journal.reconciled", await Refused(() => Change(spend.Id, Mar1, [new BankSplit(_rent.Id, 1000m)])));
    }

    [Fact]
    public async Task A_transaction_from_the_bank_feed_keeps_the_banks_amount_and_matches_follow_it()
    {
        var added = await FeedLine(Mar1, -80m, "F1");
        var fromFeed = await In<BankFeedService, BankTransaction>(s => s.AddAsync(added.Id, new AddFeedItemCommand(_supplies.Id)));
        Assert.Equal("bank.feed_amount", await Refused(() => Change(fromFeed.Id, Mar1, [new BankSplit(_supplies.Id, 70m)])));
        await Change(fromFeed.Id, Mar1, [new BankSplit(_supplies.Id, 30m), new BankSplit(_rent.Id, 50m)]);

        var check = await Spend(Mar1, "1001", new BankSplit(_rent.Id, 1200m));
        var matched = await FeedLine(Mar1, -1200m, "F2");
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(matched.Id, check.JournalEntryId));
        var changed = await Change(check.Id, Mar1, [new BankSplit(_rent.Id, 1100m), new BankSplit(_supplies.Id, 100m)], reference: "1001");
        Assert.Equal(changed.JournalEntryId, (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Matched)))
            .Single(r => r.Item.Id == matched.Id).Item.MatchedJournalEntryId);
        Assert.Equal("bank.feed_amount", await Refused(() => Change(check.Id, Mar1, [new BankSplit(_rent.Id, 1000m)], reference: "1001")));
    }

    [Fact]
    public async Task A_printed_check_keeps_its_amount_payee_and_number()
    {
        var check = await Spend(Mar1, "1001", new BankSplit(_rent.Id, 1000m));
        await In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.Expense, check.Id)]));

        Assert.Equal("bank.check_issued", await Refused(() => Change(check.Id, Mar1, [new BankSplit(_rent.Id, 900m)], reference: "1001")));
        Assert.Equal("bank.check_issued", await Refused(() => Change(check.Id, Mar1, [new BankSplit(_rent.Id, 1000m)], payee: "Someone else", reference: "1001")));
        Assert.Equal("bank.check_issued", await Refused(() => Change(check.Id, Mar1, [new BankSplit(_rent.Id, 1000m)], reference: "1002")));
        await Change(check.Id, Mar5, [new BankSplit(_rent.Id, 600m), new BankSplit(_supplies.Id, 400m)], reference: "1001");
        Assert.Equal(400m, await Balance(_supplies.Id));
    }

    [Fact]
    public async Task Lines_marked_billable_stay_in_their_place()
    {
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var spend = await Spend(Mar1, null, new BankSplit(_rent.Id, 1000m), new BankSplit(_supplies.Id, 80m, "Paper"));
        await In<BillableExpenseService, BillableExpense>(s => s.MarkAsync(new MarkBillableCommand(BillableSource.Expense, spend.Id, 3, customer.Id)));

        Assert.Equal("bank.line_locked", await Refused(() => Change(spend.Id, Mar1, [new BankSplit(_supplies.Id, 80m, "Paper")])));
        Assert.Equal("bank.line_locked", await Refused(() => Change(spend.Id, Mar1, [new BankSplit(_rent.Id, 1000m), new BankSplit(_supplies.Id, 90m, "Paper")])));
        Assert.Equal(1050m, (await Change(spend.Id, Mar5, [new BankSplit(_rent.Id, 970m), new BankSplit(_supplies.Id, 80m, "Paper")])).Amount);
    }
}
