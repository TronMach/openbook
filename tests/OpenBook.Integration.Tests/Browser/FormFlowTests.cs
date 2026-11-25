using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;
using static OpenBook.Integration.Tests.Browser.BrowserTests;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>
/// The everyday interactive forms driven as a person would, from the first click to the saved record: a journal entry,
/// a bill, a customer payment, paying bills, a bank expense, a deposit, a reconciliation and a customer import. Each
/// checks what was saved in the books, and fails on any script error or failed request. The company is shared with the
/// other browser tests, so each test makes its own records, named with a tag.
/// </summary>
[Collection(BrowserGroup.Name)]
public sealed class FormFlowTests(BrowserApp app)
{
    private static readonly float Slow = 20_000;

    [BrowserFact]
    public async Task A_journal_entry_is_typed_balanced_and_posted()
    {
        var tag = Tag();
        await NewAccount($"Rent {tag}", AccountType.Expense);
        await NewAccount($"Cash box {tag}", AccountType.Bank);

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/journal/new");
        var accounts = p.Locator("input[aria-label='Account']");
        // The memo first: a Tab from it lands in the first line's account picker, whose list opens over the buttons.
        await OpenPickerAsync(p, accounts.Nth(0));
        await p.Keyboard.PressAsync("Escape");
        await p.Locator("#memo").FillAsync($"November rent {tag}");
        await PickAsync(p, accounts.Nth(0), $"rent {tag}", $"Rent {tag}");
        await p.Locator("input[aria-label='Debit']").Nth(0).FillAsync("250");
        await PickAsync(p, accounts.Nth(1), $"cash box {tag}", $"Cash box {tag}");
        var credit = p.Locator("input[aria-label='Credit']").Nth(1);
        await credit.FillAsync("250");
        await credit.PressAsync("Tab");

        var post = p.GetByRole(AriaRole.Button, new() { Name = "Post entry" });
        await Assertions.Expect(post).ToBeEnabledAsync(new() { Timeout = Slow });
        await post.ClickAsync();
        await p.WaitForURLAsync(new Regex("/journal/[0-9a-f-]{36}$"), new() { Timeout = Slow });

        var id = Guid.Parse(p.Url[^36..]);
        var entry = await Db(db => db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.Id == id));
        Assert.Equal(250m, entry.Lines.Sum(l => l.Debit));
        Assert.Contains(tag, entry.Memo, StringComparison.Ordinal);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_bill_is_entered_with_a_category_line()
    {
        var tag = Tag();
        await NewAccount($"Postage {tag}", AccountType.Expense);
        await app.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails($"Mail Co {tag}")));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/purchases/bills/new");
        await PickAsync(p, p.Locator("#vendor"), $"mail co {tag}", $"Mail Co {tag}");
        await p.Locator("#reference").FillAsync($"M-{tag}");
        await PickAsync(p, p.Locator("input[aria-label='Category']").First, $"postage {tag}", $"Postage {tag}");
        var amount = p.Locator("input[aria-label='Amount']").First;
        await amount.FillAsync("120");
        await amount.PressAsync("Tab");

        await p.GetByRole(AriaRole.Button, new() { Name = "Save bill" }).ClickAsync();
        await p.WaitForURLAsync(new Regex("/purchases/bills/[0-9a-f-]{36}$"), new() { Timeout = Slow });

        var bill = await Db(db => db.Bills.AsNoTracking().SingleAsync(b => b.Reference == $"M-{tag}"));
        Assert.Equal((120m, DocumentStatus.Posted), (bill.Total, bill.Status));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_customer_payment_pays_an_invoice_in_full()
    {
        var tag = Tag();
        var (customer, invoice) = await InvoiceAsync(tag, 340m);
        await NewAccount($"Bank {tag}", AccountType.Bank); // somewhere to put it, when this runs before the tests that make one

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/sales/payments/new?customer={customer}");
        var payInFull = p.GetByRole(AriaRole.Button, new() { Name = "Pay in full" });
        await Assertions.Expect(payInFull).ToHaveCountAsync(1, new() { Timeout = Slow });
        await ClickUntilAsync(payInFull, p.Locator("input[aria-label^='Payment for invoice']"), new Regex(@"^340(\.00)?$"));
        await p.Locator("#reference").FillAsync($"CHK-{tag}");
        await p.Locator("#reference").PressAsync("Tab");

        await p.GetByRole(AriaRole.Button, new() { Name = "Save payment" }).ClickAsync();
        await p.WaitForURLAsync(new Regex("/sales/payments/[0-9a-f-]{36}$"), new() { Timeout = Slow });

        Assert.Equal(0m, (await Db(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice))).Balance);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task Ticked_bills_are_paid_from_the_chosen_account()
    {
        var tag = Tag();
        var bank = await NewAccount($"Operating {tag}", AccountType.Bank);
        var expense = await NewAccount($"Repairs {tag}", AccountType.Expense);
        var vendor = await app.InScope(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails($"Fix It {tag}")));
        var bill = await app.InScope(sp => sp.GetRequiredService<BillService>().CreateAsync(new CreateBillCommand(vendor.Id,
            OpenBook.Application.CompanyTime.Today.AddDays(-5), [new BillLineDraft(expense.Id, 80m)], Reference: $"R-{tag}")));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/purchases/pay-bills");
        var tick = p.GetByLabel($"Pay bill R-{tag}");
        // The button says what will be paid: "Pay 0.00" until a bill is ticked.
        var pay = p.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^Pay [0-9]") });
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        // A tick before the circuit is up shows in the page but never reaches the server: untick and tick again.
        while (await pay.TextContentAsync() != "Pay 80.00" && DateTime.UtcNow < deadline)
        {
            await p.Locator("#account").SelectOptionAsync(bank.Id.ToString());
            await tick.SetCheckedAsync(false);
            await tick.SetCheckedAsync(true);
            await p.WaitForTimeoutAsync(400);
        }

        await pay.ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Paid 1 vendor(s)", new() { Timeout = Slow });
        Assert.Equal(0m, (await Db(db => db.Bills.AsNoTracking().SingleAsync(b => b.Id == bill.Id))).Balance);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task An_expense_is_recorded_from_a_bank_account()
    {
        var tag = Tag();
        var bank = await NewAccount($"Petty {tag}", AccountType.Bank);
        await NewAccount($"Coffee {tag}", AccountType.Expense);

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/banking/{bank.Id}/record/spend");
        await PickAsync(p, p.Locator("input[aria-label='Category']").First, $"coffee {tag}", $"Coffee {tag}");
        await p.Locator("#payee").FillAsync($"Corner Cafe {tag}");
        var amount = p.Locator("input[aria-label='Amount']").First;
        await amount.FillAsync("18.40");
        await amount.PressAsync("Tab");

        await p.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await p.WaitForURLAsync(new Regex("/banking/transactions/[0-9a-f-]{36}$"), new() { Timeout = Slow });

        var id = Guid.Parse(p.Url[^36..]);
        var spent = await Db(db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == id));
        Assert.Equal((BankTransactionKind.Spend, 18.40m, bank.Id), (spent.Kind, spent.Amount, spent.AccountId));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_payment_waiting_in_undeposited_funds_is_deposited()
    {
        var tag = Tag();
        var bank = await NewAccount($"Deposits {tag}", AccountType.Bank);
        var (customer, invoice) = await InvoiceAsync(tag, 75m);
        var undeposited = await app.InScope(sp => sp.GetRequiredService<SalesAccounts>().GetOrCreateUndepositedFundsAsync(default));
        await app.InScope(sp => sp.GetRequiredService<PaymentService>().ReceiveAsync(new ReceivePaymentCommand(customer,
            OpenBook.Application.CompanyTime.Today, undeposited.Id, [new ApplyToInvoice(invoice, 75m)], Reference: $"D-{tag}")));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/banking/deposits/new");
        await p.Locator("#bank").SelectOptionAsync(bank.Id.ToString());
        var tick = p.GetByLabel(new Regex($"^Deposit .*{tag}"));
        var save = p.GetByRole(AriaRole.Button, new() { Name = "Save deposit" });
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (!await save.IsEnabledAsync() && DateTime.UtcNow < deadline)
        {
            await tick.SetCheckedAsync(true);
            await p.WaitForTimeoutAsync(300);
        }

        await save.ClickAsync();
        await p.WaitForURLAsync(new Regex("/banking/deposits/[0-9a-f-]{36}$"), new() { Timeout = Slow });

        var id = Guid.Parse(p.Url[^36..]);
        var deposit = await Db(db => db.BankDeposits.AsNoTracking().SingleAsync(d => d.Id == id));
        Assert.Equal((bank.Id, 75m), (deposit.BankAccountId, deposit.Total));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_bank_account_is_reconciled_by_ticking_what_cleared()
    {
        var tag = Tag();
        var bank = await NewAccount($"Statement {tag}", AccountType.Bank);
        var expense = await NewAccount($"Fees {tag}", AccountType.Expense);
        var income = await NewAccount($"Interest {tag}", AccountType.Income);
        var day = OpenBook.Application.CompanyTime.Today.AddDays(-3);
        await app.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Receive, bank.Id, day, [new BankSplit(income.Id, 500m)])));
        await app.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, bank.Id, day, [new BankSplit(expense.Id, 12.50m)])));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/banking/{bank.Id}/reconcile");
        var start = p.GetByRole(AriaRole.Button, new() { Name = "Start reconciling" });
        await FillUntilAsync(p.Locator("#statement-balance"), "487.50", start);
        await start.ClickAsync();

        var finish = p.GetByRole(AriaRole.Button, new() { Name = "Finish reconciliation" });
        await Assertions.Expect(finish).ToBeDisabledAsync(new() { Timeout = Slow });
        // A tick made while the page is still saving the one before is ignored (the handler is busy): tick again until
        // everything that cleared is ticked and the difference is nil.
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (!await finish.IsEnabledAsync() && DateTime.UtcNow < deadline)
        {
            foreach (var all in await p.Locator("input[aria-label^='Tick all']").AllAsync())
                if (await all.IsEnabledAsync() && !await all.IsCheckedAsync())
                    await all.CheckAsync();
            await p.WaitForTimeoutAsync(400);
        }

        await Assertions.Expect(finish).ToBeEnabledAsync(new() { Timeout = Slow });
        await finish.ClickAsync();
        await p.WaitForURLAsync(new Regex("/banking/reconciliations/[0-9a-f-]{36}$"), new() { Timeout = Slow });

        Assert.True(await Db(db => db.Reconciliations.AsNoTracking().AnyAsync(r => r.AccountId == bank.Id && r.CompletedAt != null)));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task Customers_are_imported_from_a_csv_file_after_a_preview()
    {
        var tag = Tag();
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/customers/import");
        var import = p.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Import 2 customer") });
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        // The file is read over the circuit, which may not be up for the first try.
        do
        {
            await p.Locator("#file").SetInputFilesAsync(new FilePayload
            {
                Name = "customers.csv",
                MimeType = "text/csv",
                Buffer = Encoding.UTF8.GetBytes($"Name,Email\nOak Cafe {tag},oak@{tag}.test\nPine Bakery {tag},pine@{tag}.test\n"),
            });
            try
            {
                await Assertions.Expect(import).ToBeEnabledAsync(new() { Timeout = 2000 });
                break;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
            }
        }
        while (true);

        await import.ClickAsync();
        await Assertions.Expect(p.Locator("body")).ToContainTextAsync("2", new() { Timeout = Slow });
        var deadline2 = DateTime.UtcNow.AddMilliseconds(Slow);
        while (await Db(db => db.Customers.CountAsync(c => c.DisplayName.EndsWith(tag))) < 2 && DateTime.UtcNow < deadline2)
            await p.WaitForTimeoutAsync(300);
        Assert.Equal(2, await Db(db => db.Customers.CountAsync(c => c.DisplayName.EndsWith(tag))));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_half_typed_journal_entry_comes_back_after_the_page_reloads()
    {
        var tag = Tag();
        await NewAccount($"Supplies {tag}", AccountType.Expense);

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/journal/new");
        await PickAsync(p, p.Locator("input[aria-label='Account']").First, $"supplies {tag}", $"Supplies {tag}");
        var debit = p.Locator("input[aria-label='Debit']").First;
        await debit.FillAsync("42");
        await debit.PressAsync("Tab");
        // Kept every two seconds while the page is connected.
        await p.WaitForTimeoutAsync(3000);

        // A dropped connection reloads the page empty; the tab still has the typing.
        await p.ReloadAsync();
        var bringBack = p.GetByRole(AriaRole.Button, new() { Name = "Bring it back" });
        await Assertions.Expect(bringBack).ToBeVisibleAsync(new() { Timeout = Slow });
        await ClickUntilAsync(bringBack, p.Locator("input[aria-label='Debit']").First, new Regex("^42(\\.00)?$"));
        await Assertions.Expect(p.Locator("input[aria-label='Account']").First).ToHaveValueAsync($"Supplies {tag}");
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task An_invoice_left_at_its_suggested_number_takes_the_next_one_when_its_gone_and_a_typed_one_is_refused()
    {
        var tag = Tag();
        var (customer, _) = await InvoiceAsync(tag, 60m);
        var item = (await Db(db => db.Items.AsNoTracking().SingleAsync(i => i.Name == $"Work {tag}"))).Id;

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/sales/invoices/new?customer={customer}&item={item}");
        var number = p.Locator("#number");
        var save = p.GetByRole(AriaRole.Button, new() { Name = "Save invoice" });
        await Assertions.Expect(save).ToBeEnabledAsync(new() { Timeout = Slow });
        var memo = p.Locator("#memo");
        await memo.FillAsync($"Left as suggested {tag}");
        await memo.PressAsync("Tab");
        var suggested = await number.InputValueAsync();

        // Someone else saves with that number first.
        var theirs = await app.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(customer,
            OpenBook.Application.CompanyTime.Today, [new SalesLineDraft(item, null, 1m, 60m)], Number: suggested)));
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (!Regex.IsMatch(p.Url, "/sales/invoices/[0-9a-f-]{36}$") && DateTime.UtcNow < deadline)
        {
            // The first click may come before the circuit is up.
            try
            {
                await save.ClickAsync(new() { Timeout = 1000 });
            }
            catch (TimeoutException)
            {
                // Saving, or gone with the page.
            }

            await p.WaitForTimeoutAsync(1000);
        }

        await p.WaitForURLAsync(new Regex("/sales/invoices/[0-9a-f-]{36}$"), new() { Timeout = Slow });
        var id = Guid.Parse(p.Url[^36..]);
        var saved = await Db(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id));
        Assert.Equal(SalesAccounts.NextNumber([theirs.Number]), saved.Number);

        // A number typed in is the person's own choice.
        await p.GotoAsync($"/sales/invoices/new?customer={customer}&item={item}");
        await Assertions.Expect(save).ToBeEnabledAsync(new() { Timeout = Slow });
        // The server greys the button out without a number: then the typing reaches it.
        deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (await save.IsEnabledAsync() && DateTime.UtcNow < deadline)
        {
            await number.FillAsync(theirs.Number);
            await number.FillAsync(string.Empty);
            await number.PressAsync("Tab");
            await p.WaitForTimeoutAsync(500);
        }

        await FillUntilAsync(number, theirs.Number, save);
        await save.ClickAsync();
        await Assertions.Expect(p.Locator(".alert.error")).ToContainTextAsync($"Invoice number {theirs.Number} is already used.", new() { Timeout = Slow });
        Assert.Equal(3, await Db(db => db.Invoices.CountAsync(i => i.CustomerId == customer)));
        await page.AssertCleanAsync();
    }

    /// <summary>Types into a box until the button it unlocks is enabled: the first typing may come before the circuit is up.</summary>
    private static async Task FillUntilAsync(ILocator input, string value, ILocator unlocks)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (true)
        {
            await input.FillAsync(value);
            await input.PressAsync("Tab");
            try
            {
                await Assertions.Expect(unlocks).ToBeEnabledAsync(new() { Timeout = 1000 });
                return;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
            }
        }
    }

    /// <summary>Clicks until a box shows what the click should have put there (the circuit may not be up for the first).</summary>
    private static async Task ClickUntilAsync(ILocator button, ILocator box, Regex expected)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Slow);
        while (true)
        {
            await button.ClickAsync();
            try
            {
                await Assertions.Expect(box).ToHaveValueAsync(expected, new() { Timeout = 1000 });
                return;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
            }
        }
    }

    private async Task<(Guid Customer, Guid Invoice)> InvoiceAsync(string tag, decimal amount)
    {
        var income = await NewAccount($"Sales {tag}", AccountType.Income);
        var customer = await app.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails($"Payer {tag}")));
        var item = await app.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails($"Work {tag}", ItemType.Service, income.Id, amount)));
        var invoice = await app.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(customer.Id,
            OpenBook.Application.CompanyTime.Today.AddDays(-10), [new SalesLineDraft(item.Id, null, 1m, amount)])));
        return (customer.Id, invoice.Id);
    }

    private Task<Account> NewAccount(string name, AccountType type) =>
        app.InScope(sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand(name, type)));

    private Task<T> Db<T>(Func<OpenBookDbContext, Task<T>> read) => app.InScope(sp => read(sp.GetRequiredService<OpenBookDbContext>()));

    private static string Tag() => Guid.NewGuid().ToString("N")[..6];
}
