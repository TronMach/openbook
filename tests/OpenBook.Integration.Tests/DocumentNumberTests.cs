using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Components;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Documents saved at the same moment take their automatic numbers one after the other. On a real database file (or
/// PostgreSQL): the in-memory database of most tests locks by table, so saves there never overlap as in real use.
/// </summary>
public sealed class DocumentNumberTests : IAsyncLifetime
{
    private const int Many = 40;
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-numbers-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _api = null!;
    private Guid _customer;
    private Guid _vendor;
    private Guid _bank;
    private Guid _income;
    private Guid _service;
    private Guid _widget;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "numbers.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        _api = await PageSmokeTests.SetUpAsync(_app);
        _bank = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Till", AccountType.Bank)))).Id;
        _income = (await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Work done", AccountType.Income)))).Id;
        _customer = (await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")))).Id;
        _vendor = (await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Supplier")))).Id;
        _service = (await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Work", ItemType.Service, _income, 100m)))).Id;
        _widget = (await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, _income, 10m, PurchaseCost: 5m)))).Id;
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_vendor, Mar1, [new BillLineDraft(Guid.Empty, 500m, null, _widget, 100m)])));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private Task<T> Db<T>(Func<OpenBookDbContext, Task<T>> read) => In(read);

    private SalesLineDraft Work(decimal quantity = 1m) => new(_service, null, quantity, 100m);

    private async Task<(HttpStatusCode Status, bool Replayed, JsonElement Body)> PostInvoiceAsync(string? key = null, string? number = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sales/invoices")
        {
            Content = JsonContent.Create(new
            {
                customerId = _customer,
                date = Mar1,
                number,
                lines = new object[] { new { itemId = _service, quantity = 1m, rate = 100m }, new { itemId = _widget, quantity = 1m, rate = 10m } },
            }),
        };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        var response = await _api.SendAsync(request);
        return (response.StatusCode, response.Headers.Contains("Idempotency-Replayed"), await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Many_invoices_posted_at_once_without_numbers_are_all_saved_once_each_with_different_numbers()
    {
        var posted = await Task.WhenAll(Enumerable.Range(0, Many).Select(n => Task.Run(() => PostInvoiceAsync($"key-{n}"))));

        Assert.All(posted, p => Assert.True(p.Status == HttpStatusCode.Created, $"{p.Status}: {p.Body}"));
        // The next ones in turn, none skipped.
        Assert.Equal(Enumerable.Range(1001, Many).Select(n => $"{n}"), posted.Select(p => p.Body.GetProperty("number").GetString()!).Order(StringComparer.Ordinal));

        // Sent again, each key gets its own invoice back and makes nothing.
        var ids = posted.Select(p => p.Body.GetProperty("id").GetGuid()).ToList();
        for (var n = 0; n < Many; n += 10)
        {
            var again = await PostInvoiceAsync($"key-{n}");
            Assert.True(again.Replayed);
            Assert.Equal(ids[n], again.Body.GetProperty("id").GetGuid());
        }

        Assert.Equal(Many, await Db(db => db.Invoices.CountAsync()));
        var entries = await Db(db => db.JournalEntries.AsNoTracking().Where(e => e.Source == Invoice.Source).Select(e => new { e.Id, e.SourceId }).ToListAsync());
        Assert.Equal(ids.Order(), entries.Select(e => e.SourceId!.Value).Order());
        var audits = await Db(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityType == "invoice" || a.EntityType == "journal_entry")
            .Select(a => new { a.EntityId, a.Action }).ToListAsync());
        Assert.All(ids, id => Assert.Equal(AuditAction.Created, Assert.Single(audits, a => a.EntityId == id).Action));
        Assert.All(entries, e => Assert.Equal(AuditAction.Posted, Assert.Single(audits, a => a.EntityId == e.Id).Action));
        Assert.Equal(100m - Many, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget)))!.Quantity);
        await AssertBooksAsync();
    }

    [Fact]
    public async Task Every_kind_of_numbered_document_saved_at_once_gets_its_own_number_the_first_time()
    {
        const int Each = 6;
        var numbers = new ConcurrentBag<(string Sequence, string Number)>();
        var creates = new List<Func<Task>>();
        void Kind<TService, T>(string sequence, Func<TService, Task<T>> create, Func<T, object> number) where TService : notnull
        {
            for (var n = 0; n < Each; n++)
                creates.Add(async () => numbers.Add((sequence, $"{number(await In(create))}")));
        }

        // Invoices and credit memos share a sequence.
        Kind<InvoiceService, Invoice>("invoice", s => s.CreateAsync(new CreateInvoiceCommand(_customer, Mar1, [Work()])), i => i.Number);
        Kind<CreditService, CreditMemo>("invoice", s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_customer, Mar1, [Work()])), c => c.Number);
        Kind<SalesReceiptService, SalesReceipt>("receipt", s => s.CreateAsync(new CreateSalesReceiptCommand(_customer, Mar1, _bank, [Work()])), r => r.Number);
        Kind<EstimateService, Estimate>("estimate", s => s.CreateAsync(new SaveEstimateCommand(_customer, Mar1, [Work()])), e => e.Number);
        Kind<SalesOrderService, SalesOrder>("sales order", s => s.CreateAsync(new SaveSalesOrderCommand(_customer, Mar1, [Work()])), o => o.Number);
        Kind<PurchaseOrderService, PurchaseOrder>("purchase order",
            s => s.CreateAsync(new SavePurchaseOrderCommand(_vendor, Mar1, [new PurchaseOrderLineDraft(_widget, null, null, 1m, 5m)])), o => o.Number);
        Kind<GiftService, Domain.Nonprofit.Donation>("gift", s => s.RecordAsync(new RecordGiftCommand(_customer, Mar1, 20m, _bank, IncomeAccountId: _income)), d => d.Number);
        Kind<GiftService, Domain.Nonprofit.Pledge>("pledge", s => s.PledgeAsync(new PledgeCommand(_customer, Mar1, 20m, _income)), p => p.Number);
        // Other saves take entry numbers meanwhile, trying again when theirs is taken; the documents never have to.
        for (var n = 0; n < Each; n++)
            creates.Add(PostEntryAsync);

        await Task.WhenAll(creates.OrderBy(_ => Guid.NewGuid()).Select(c => Task.Run(c)));

        foreach (var sequence in numbers.GroupBy(n => n.Sequence))
            Assert.Equal(sequence.Key == "invoice" ? 2 * Each : Each, sequence.Select(n => n.Number).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8 * Each, numbers.Count);
        await AssertBooksAsync();
    }

    [Fact]
    public async Task A_number_someone_typed_is_refused_when_its_taken_however_many_type_it_at_once()
    {
        var first = await PostInvoiceAsync(number: "INV-7");
        Assert.Equal(HttpStatusCode.Created, first.Status);
        var (status, _, problem) = await PostInvoiceAsync(number: "inv-7");
        Assert.Equal((HttpStatusCode.BadRequest, "invoice.number_taken"), (status, problem.GetProperty("code").GetString()));

        var together = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => PostInvoiceAsync(number: "INV-50"))));
        Assert.Single(together, p => p.Status == HttpStatusCode.Created);
        Assert.All(together.Where(p => p.Status != HttpStatusCode.Created), p =>
            Assert.Equal((HttpStatusCode.BadRequest, "invoice.number_taken"), (p.Status, p.Body.GetProperty("code").GetString())));

        // Either kind holds the number, for the other too.
        var memo = await Assert.ThrowsAsync<DomainException>(() => In<CreditService, CreditMemo>(s =>
            s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_customer, Mar1, [Work()], Number: "INV-50"))));
        Assert.Equal(("creditmemo.number_taken", "Number INV-50 is already used by an invoice."), (memo.Code, memo.Message));
        await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_customer, Mar1, [Work()], Number: "CM-1")));
        var invoice = await Assert.ThrowsAsync<DomainException>(() => In<InvoiceService, Invoice>(s =>
            s.CreateAsync(new CreateInvoiceCommand(_customer, Mar1, [Work()], Number: "CM-1"))));
        Assert.Equal(("invoice.number_taken", "Number CM-1 is already used by a credit memo."), (invoice.Code, invoice.Message));

        Assert.Equal(98m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget)))!.Quantity);
        await AssertBooksAsync();
    }

    [Fact]
    public async Task A_refused_invoice_leaves_nothing_behind_and_the_next_one_can_be_made_in_the_same_scope()
    {
        // Receivables are made on first use, before the warehouse turns out not to exist.
        Assert.Equal(0, await Db(db => db.Accounts.CountAsync(a => a.Type == AccountType.AccountsReceivable)));
        await using var scope = _app.Services.CreateAsyncScope();
        var invoices = scope.ServiceProvider.GetRequiredService<InvoiceService>();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await Assert.ThrowsAnyAsync<DomainException>(() => invoices.CreateAsync(new CreateInvoiceCommand(_customer, Mar1, [Work()], WarehouseId: Guid.NewGuid())));

        Assert.False(work.HasSaved);
        Assert.Equal(0, await Db(db => db.Accounts.CountAsync(a => a.Type == AccountType.AccountsReceivable)));
        Assert.Equal(0, await Db(db => db.Invoices.CountAsync()));

        var made = await invoices.CreateAsync(new CreateInvoiceCommand(_customer, Mar1, [Work()]));
        Assert.Equal("1001", made.Number);
        Assert.True(work.HasSaved);
        await AssertBooksAsync();
    }

    [Fact]
    public async Task A_number_only_suggested_by_the_form_gives_way_and_a_typed_one_doesnt()
    {
        Task<Invoice> Save(string? number) => In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_customer, Mar1, [Work()], Number: number)));
        var suggested = await In<InvoiceService, string>(s => s.SuggestNumberAsync());

        // Still free: as shown, though a higher one was typed meanwhile.
        await Save("5000");
        Assert.Equal(suggested, (await SuggestedNumber.SaveAsync(suggested, suggested, Save)).Number);
        // Taken since the form opened.
        Assert.Equal("5001", (await SuggestedNumber.SaveAsync(suggested, suggested, Save)).Number);
        // Typed over the suggestion, or no suggestion.
        Assert.Equal("invoice.number_taken", (await Assert.ThrowsAsync<DomainException>(() => SuggestedNumber.SaveAsync("5000", suggested, Save))).Code);
        Assert.Equal("invoice.number_taken", (await Assert.ThrowsAsync<DomainException>(() => SuggestedNumber.SaveAsync("5000", null, Save))).Code);
        await AssertBooksAsync();
    }

    /// <summary>A journal entry, tried again as the API and the pages do when its number is taken, until it's saved.</summary>
    private async Task PostEntryAsync()
    {
        while (true)
        {
            try
            {
                await In<PostingEngine, JournalEntry>(e => e.PostAsync(new PostJournalEntryCommand(Mar1,
                    [new JournalLineDraft(_bank, 1m, 0m), new JournalLineDraft(_income, 0m, 1m)])));
                return;
            }
            catch (ConcurrencyConflictException)
            {
            }
        }
    }

    private async Task AssertBooksAsync()
    {
        var check = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(check.Problems == 0, JsonSerializer.Serialize(check));
    }
}
