using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The search box narrows documents and journal entries in the database (2026-11-02): it still finds them by number,
/// reference, memo, the customer's or vendor's name, an amount (total or balance) and an entry number, ignoring case,
/// newest first, and lists only hand-entered journal entries as entries.
/// </summary>
public sealed class SearchTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _supplies = null!;
    private Customer _acme = null!;
    private Vendor _paper = null!;
    private Item _work = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _work = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Work", ItemType.Service, income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp")));
        _paper = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Supply Co")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<SearchResults> Search(string q) => In<SearchService, SearchResults>(s => s.SearchAsync(q));

    private Task<Invoice> Invoice(decimal hours, DateOnly date, string? memo = null) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, [new SalesLineDraft(_work.Id, null, hours, 100m)], Memo: memo)));

    [Fact]
    public async Task Documents_are_found_by_text_name_and_amount_newest_first()
    {
        var older = await Invoice(2m, new DateOnly(2026, 1, 3), "Spring cleanup");
        var newer = await Invoice(3m, new DateOnly(2026, 1, 9));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, new DateOnly(2026, 1, 10), _checking.Id,
            [new ApplyToInvoice(newer.Id, 120m)], Reference: "CHK-7781")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_paper.Id, new DateOnly(2026, 1, 4), [new BillLineDraft(_supplies.Id, 64.25m)], Reference: "PS-2201")));

        Assert.Equal([newer.Id, older.Id], (await Search("ACME")).Hits.Where(h => h.Kind == SearchKind.Invoice).Select(h => h.Id));
        Assert.Equal(older.Id, Assert.Single((await Search("spring CLEAN")).Hits, h => h.Kind == SearchKind.Invoice).Id);
        // An invoice's total, and its balance once part is paid (300 less 120).
        Assert.Contains((await Search("200.00")).Hits, h => h.Kind == SearchKind.Invoice && h.Id == older.Id);
        Assert.Contains((await Search("$180")).Hits, h => h.Kind == SearchKind.Invoice && h.Id == newer.Id);
        Assert.Contains((await Search("chk-7781")).Hits, h => h.Kind == SearchKind.CustomerPayment);
        Assert.Contains((await Search("paper supply")).Hits, h => h.Kind == SearchKind.Bill && h.Id == bill.Id);
        Assert.Contains((await Search("64.25")).Hits, h => h.Kind == SearchKind.Bill && h.Id == bill.Id);
        Assert.Empty((await Search("nothing like this")).Hits);
    }

    [Fact]
    public async Task Bank_transactions_and_hand_entered_journal_entries_are_found()
    {
        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id,
            new DateOnly(2026, 1, 5), [new BankSplit(_supplies.Id, 42.10m)], Payee: "Corner Hardware")));
        var equity = await _db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
        var entry = await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 1, 6),
            [JournalLineDraft.Dr(_supplies.Id, 77.70m, "Toner from home"), JournalLineDraft.Cr(equity.Id, 77.70m)], "Owner paid")));
        var invoice = await Invoice(1m, new DateOnly(2026, 1, 7));

        Assert.Equal(spend.Id, Assert.Single((await Search("hardware")).Hits, h => h.Kind == SearchKind.BankTransaction).Id);
        Assert.Contains((await Search("42.10")).Hits, h => h.Kind == SearchKind.BankTransaction && h.Id == spend.Id);
        Assert.Contains((await Search("toner")).Hits, h => h.Kind == SearchKind.JournalEntry && h.Id == entry.Id);
        Assert.Contains((await Search($"#{entry.Number}")).Hits, h => h.Kind == SearchKind.JournalEntry && h.Id == entry.Id);
        var byAmount = Assert.Single((await Search("77.70")).Hits, h => h.Kind == SearchKind.JournalEntry);
        Assert.Equal(77.70m, byAmount.Amount);
        // The invoice's own entry is found through the invoice, not as a journal entry.
        Assert.DoesNotContain((await Search($"#{(await In<PostingEngine, JournalEntry?>(s => s.GetAsync(invoice.JournalEntryId)))!.Number}")).Hits,
            h => h.Kind == SearchKind.JournalEntry && h.Id == invoice.JournalEntryId);
    }
}
