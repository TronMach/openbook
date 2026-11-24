using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;
using static OpenBook.Integration.Tests.Browser.BrowserTests;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>
/// The bank feed as a person works it: a statement imported, then each line added, split, matched, paid to a bill,
/// excluded and brought back, with the books checked after each; and other interactive pages no test had opened (applying
/// credit, adding a customer from a picker, the drop-down menu).
/// </summary>
[Collection(BrowserGroup.Name)]
public sealed class BankFeedFlowTests(BrowserApp app)
{
    private static readonly float Slow = 20_000;

    private static DateOnly Day => CompanyTime.Today.AddDays(-4);

    [BrowserFact]
    public async Task A_statement_is_imported_and_its_lines_wait_for_review()
    {
        var tag = Tag();
        var bank = await NewAccount($"Import {tag}", AccountType.Bank);
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/banking/{bank.Id}/import");
        var send = p.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Send 2 to review") });
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        // The file is read over the circuit, which may not be up for the first try.
        while (true)
        {
            await p.Locator("#file").SetInputFilesAsync(new FilePayload
            {
                Name = "statement.csv",
                MimeType = "text/csv",
                Buffer = Encoding.UTF8.GetBytes($"Date,Description,Amount\n{Day:yyyy-MM-dd},Coffee {tag},-4.50\n{Day:yyyy-MM-dd},Deposit {tag},120.00\n"),
            });
            try
            {
                await Assertions.Expect(send).ToBeEnabledAsync(new() { Timeout = 2000 });
                break;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
            }
        }

        await send.ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("2 lines are waiting for review", new() { Timeout = Slow });
        Assert.Equal([-4.50m, 120m], (await Feed(bank.Id)).Select(f => f.Amount).Order());
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task Lines_are_added_split_excluded_and_brought_back()
    {
        var tag = Tag();
        var bank = await NewAccount($"Feed {tag}", AccountType.Bank);
        var coffee = await NewAccount($"Coffee {tag}", AccountType.Expense);
        var fuel = await NewAccount($"Fuel {tag}", AccountType.Expense);
        await Queue(bank.Id, (-4.50m, $"Cafe {tag}"), (-60m, $"Garage {tag}"), (-9m, $"Mystery {tag}"));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/banking/{bank.Id}/feed");
        ILocator Row(string payee) => p.Locator("table.feed tbody tr").Filter(new() { HasText = payee });

        // Added to one category. What's been chosen on another line stays.
        await PickAsync(p, Row($"Garage {tag}").GetByLabel("Category").First, $"fuel {tag}", $"Fuel {tag}");
        await PickAsync(p, Row($"Cafe {tag}").GetByLabel("Category").First, $"coffee {tag}", $"Coffee {tag}");
        await Row($"Cafe {tag}").GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Added 4.50", new() { Timeout = Slow });
        await Assertions.Expect(Row($"Cafe {tag}")).ToHaveCountAsync(0, new() { Timeout = Slow });
        await Assertions.Expect(Row($"Garage {tag}").GetByLabel("Category").First).ToHaveValueAsync($"Fuel {tag}");

        // Split between two categories; it can't be added until the whole amount is assigned.
        var garage = Row($"Garage {tag}");
        await garage.GetByRole(AriaRole.Button, new() { Name = "Split" }).ClickAsync();
        var splits = garage.Locator(".feed-splits");
        var add = garage.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true });
        await Assertions.Expect(splits.GetByLabel("Amount")).ToHaveCountAsync(2, new() { Timeout = Slow });
        await splits.GetByLabel("Amount").Nth(0).FillAsync("50");
        await splits.GetByLabel("Amount").Nth(0).PressAsync("Tab");
        await PickAsync(p, splits.GetByLabel("Category").Nth(1), $"coffee {tag}", $"Coffee {tag}");
        await Assertions.Expect(splits).ToContainTextAsync("Left to assign: 10.00", new() { Timeout = Slow });
        await Assertions.Expect(add).ToBeDisabledAsync();
        await splits.GetByLabel("Amount").Nth(1).FillAsync("10");
        await splits.GetByLabel("Amount").Nth(1).PressAsync("Tab");
        await Assertions.Expect(add).ToBeEnabledAsync(new() { Timeout = Slow });
        await add.ClickAsync();
        await Assertions.Expect(Row($"Garage {tag}")).ToHaveCountAsync(0, new() { Timeout = Slow });

        // Excluded, then brought back.
        await Row($"Mystery {tag}").GetByRole(AriaRole.Button, new() { Name = "Exclude" }).ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Excluded", new() { Timeout = Slow });
        await p.GetByRole(AriaRole.Navigation, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { Name = "Excluded" }).ClickAsync();
        var excluded = p.Locator("table.data tbody tr").Filter(new() { HasText = $"Mystery {tag}" });
        await excluded.GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync(new() { Timeout = Slow });
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Back in the review queue", new() { Timeout = Slow });

        var spent = await Db(db => db.BankTransactions.AsNoTracking().Where(t => t.AccountId == bank.Id).ToListAsync());
        Assert.Equal([4.50m, 60m], spent.Select(t => t.Amount).Order());
        var entryId = spent.Single(t => t.Amount == 60m).JournalEntryId;
        var split = await Db(db => db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.Id == entryId));
        Assert.Equal([(coffee.Id, 10m), (fuel.Id, 50m)], split.Lines.Where(l => l.Debit > 0m).Select(l => (l.AccountId, l.Debit)).OrderBy(l => l.Debit));
        Assert.Equal(BankFeedStatus.Pending, (await Feed(bank.Id)).Single(f => f.Amount == -9m).Status);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_line_is_matched_or_pays_an_open_bill_and_adding_the_ready_ones_leaves_both_alone()
    {
        var tag = Tag();
        var bank = await NewAccount($"Match {tag}", AccountType.Bank);
        var expense = await NewAccount($"Repairs {tag}", AccountType.Expense);
        var income = await NewAccount($"Interest {tag}", AccountType.Income);
        var recorded = await app.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Receive, bank.Id, Day, [new BankSplit(income.Id, 15m)])));
        var vendor = await app.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails($"Fix It {tag}")));
        var bill = await app.InScope(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(vendor.Id, Day.AddDays(-10),
            [new BillLineDraft(expense.Id, 81.37m)], Reference: $"R-{tag}")));
        // A rule gives every line its category, so all three would count as ready if matches and bills weren't looked at.
        await app.InScope(sp => sp.GetRequiredService<BankRuleService>().CreateAsync(new BankRuleDetails($"All {tag}", tag, expense.Id, AccountId: bank.Id)));
        await Queue(bank.Id, (15m, $"Interest {tag}"), (-81.37m, $"Fix It {tag}"), (-3m, $"Parking {tag}"));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/banking/{bank.Id}/feed");
        ILocator Row(string payee) => p.Locator("table.feed tbody tr").Filter(new() { HasText = payee });
        var ready = p.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Add the 1 with a category") });
        await Assertions.Expect(ready).ToBeVisibleAsync(new() { Timeout = Slow });
        // The first click may come before the circuit is up.
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (await ready.IsVisibleAsync() && DateTime.UtcNow < deadline)
        {
            await ready.ClickAsync();
            await p.WaitForTimeoutAsync(500);
        }

        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Added 1 transaction", new() { Timeout = Slow });
        Assert.Equal(81.37m, (await Db(db => db.Bills.AsNoTracking().SingleAsync(b => b.Id == bill.Id))).Balance);

        await Row($"Interest {tag}").GetByRole(AriaRole.Button, new() { Name = "Match", Exact = true }).ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Matched", new() { Timeout = Slow });
        await Row($"Fix It {tag}").GetByRole(AriaRole.Button, new() { Name = "Pay bill and match" }).ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Bill paid and matched", new() { Timeout = Slow });

        Assert.Equal(0m, (await Db(db => db.Bills.AsNoTracking().SingleAsync(b => b.Id == bill.Id))).Balance);
        var feed = await Feed(bank.Id);
        Assert.Equal(recorded.JournalEntryId, feed.Single(f => f.Amount == 15m).MatchedJournalEntryId);
        Assert.All(feed, f => Assert.NotEqual(BankFeedStatus.Pending, f.Status));
        // The interest once, the parking once, and the bill's payment isn't a second expense.
        Assert.Equal([3m, 15m], (await Db(db => db.BankTransactions.AsNoTracking().Where(t => t.AccountId == bank.Id).ToListAsync())).Select(t => t.Amount).Order());
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_customers_credit_is_applied_to_their_invoice()
    {
        var tag = Tag();
        var income = await NewAccount($"Sales {tag}", AccountType.Income);
        var bank = await NewAccount($"Cash {tag}", AccountType.Bank);
        var customer = await app.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails($"Payer {tag}")));
        var item = await app.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails($"Work {tag}", ItemType.Service, income.Id, 90m)));
        await app.InScope(sp => sp.GetRequiredService<PaymentService>().ReceiveAsync(new ReceivePaymentCommand(customer.Id, Day, bank.Id, [], Amount: 40m)));
        var invoice = await app.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(customer.Id, Day,
            [new SalesLineDraft(item.Id, null, 1m, 90m)])));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/sales/credits/apply?customer={customer.Id}");
        var apply = p.GetByRole(AriaRole.Button, new() { Name = "Apply credit" });
        var oldest = p.GetByRole(AriaRole.Button, new() { Name = "Apply oldest first" });
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (!await apply.IsEnabledAsync() && DateTime.UtcNow < deadline)
        {
            await oldest.ClickAsync();
            await p.WaitForTimeoutAsync(400);
        }

        await apply.ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Credit applied", new() { Timeout = Slow });
        Assert.Equal(50m, (await Db(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id))).Balance);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_customer_is_added_from_the_invoices_picker_without_leaving_it()
    {
        var tag = Tag();
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/sales/invoices/new");
        var picker = p.Locator("#customer");
        await OpenPickerAsync(p, picker);
        await picker.PressSequentiallyAsync($"Brand New {tag}");
        var list = p.Locator($"#{await picker.GetAttributeAsync("aria-controls")}");
        await list.Locator(".search-add").ClickAsync(new() { Timeout = Slow });
        var panel = p.GetByRole(AriaRole.Dialog, new() { Name = "Add a customer" });
        await Assertions.Expect(panel.GetByLabel("Name")).ToHaveValueAsync($"Brand New {tag}", new() { Timeout = Slow });
        await panel.GetByLabel("Email").FillAsync($"new@{tag}.test");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await Assertions.Expect(picker).ToHaveValueAsync($"Brand New {tag}", new() { Timeout = Slow });
        Assert.Equal($"new@{tag}.test", (await Db(db => db.Customers.AsNoTracking().SingleAsync(c => c.DisplayName == $"Brand New {tag}"))).Email);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task The_drop_down_menu_opens_and_goes_places()
    {
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await Menu(MenuStyle.DropDowns);
        try
        {
            await p.GotoAsync("/");
            var sales = p.Locator("nav[aria-label='Main menu'] details").Filter(new() { HasText = "Sales" }).First;
            await sales.Locator("summary").ClickAsync();
            await sales.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("[Ii]nvoice") }).First.ClickAsync();
            await p.WaitForURLAsync(new Regex("/sales"), new() { Timeout = Slow });
            await page.AssertCleanAsync();
        }
        finally
        {
            await Menu(MenuStyle.Bar);
        }
    }

    private Task<int> Menu(MenuStyle style) => Db(async db =>
    {
        var preference = await db.ViewPreferences.SingleOrDefaultAsync(v => v.Username == BrowserApp.Username);
        if (preference is null)
            db.Add(preference = ViewPreference.Create(BrowserApp.Username));
        preference.Set(style);
        return await db.SaveChangesAsync();
    });

    private Task<FeedImportResult> Queue(Guid account, params (decimal Amount, string Payee)[] lines) =>
        app.InScope(sp => sp.GetRequiredService<BankFeedService>().QueueAsync(account, [.. lines.Select((l, i) => new FeedLine(Day, l.Amount, l.Payee, null, null, $"{l.Payee}-{i}"))]));

    private Task<List<BankFeedItem>> Feed(Guid account) => Db(db => db.BankFeedItems.AsNoTracking().Where(f => f.AccountId == account).ToListAsync());

    private Task<Account> NewAccount(string name, AccountType type) =>
        app.InScope(sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand(name, type)));

    private Task<T> Db<T>(Func<OpenBookDbContext, Task<T>> read) => app.InScope(sp => read(sp.GetRequiredService<OpenBookDbContext>()));

    private static string Tag() => Guid.NewGuid().ToString("N")[..6];
}
