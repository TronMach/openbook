using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>
/// OpenBook in a real browser: the pages' own scripts (shortcuts.js, searchselect.js, the theme), the interactive
/// forms over their circuit, and the static forms' guards. Every test also fails on any script error, console error
/// or failed request on the pages it opened. The tests share one company, so each makes its own records.
/// </summary>
[Collection(BrowserGroup.Name)]
public sealed partial class BrowserTests(BrowserApp app)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [BrowserFact]
    public async Task Signing_in_through_the_page_reaches_home()
    {
        await using var page = await app.SignedInAsync();

        Assert.Equal(app.Address + "/", page.Page.Url);
        await Assertions.Expect(page.Page.Locator("h1").First).ToBeVisibleAsync();
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task Everyday_pages_open_without_script_errors()
    {
        await using var page = await app.SignedInAsync();
        string[] paths =
        [
            "/", "/sales", "/sales/invoices/new", "/sales/receipts/new", "/customers", "/customers/center", "/purchases",
            "/purchases/bills/new", "/banking", "/accounts", "/journal/new", "/reports", "/reports/balance-sheet",
            "/dashboard", "/accountant/check", "/settings", "/settings/forms", "/help", "/capture", "/search?q=acme",
        ];
        foreach (var path in paths)
        {
            var response = await page.Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            Assert.True(response?.Ok, $"{path} answered {response?.Status}");
            Assert.DoesNotContain("/login", page.Page.Url, StringComparison.Ordinal);
        }

        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task Keyboard_shortcuts_search_show_help_and_go_places()
    {
        await using var page = await app.SignedInAsync();
        var p = page.Page;

        await p.Keyboard.PressAsync("/");
        Assert.True(await p.EvaluateAsync<bool>("document.activeElement === document.querySelector('form.search input')"));

        await p.Locator("body").ClickAsync(new LocatorClickOptions { Position = new Position { X = 5, Y = 5 } });
        await p.Keyboard.PressAsync("Shift+Slash");
        await Assertions.Expect(p.Locator("dialog#shortcut-help")).ToBeVisibleAsync();
        await p.Keyboard.PressAsync("Escape");
        await Assertions.Expect(p.Locator("dialog#shortcut-help")).ToBeHiddenAsync();

        await p.Keyboard.PressAsync("g");
        await p.Keyboard.PressAsync("s");
        await p.WaitForURLAsync(new Regex("/sales$"));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task The_theme_chosen_is_kept_after_reloading()
    {
        await using var page = await app.SignedInAsync();
        var p = page.Page;

        // Auto, then light, then dark.
        var menu = p.Locator("details.user-menu");
        for (var i = 0; i < 2; i++)
        {
            // The account menu stays open after a click inside it, so open it only when it's closed.
            if (!await menu.EvaluateAsync<bool>("d => d.open"))
                await menu.Locator("summary").ClickAsync();
            await p.Locator("#theme-toggle").ClickAsync();
        }

        Assert.Equal("dark", await p.Locator("html").GetAttributeAsync("data-theme"));
        await p.ReloadAsync();
        Assert.Equal("dark", await p.Locator("html").GetAttributeAsync("data-theme"));
        await Assertions.Expect(p.Locator("#theme-toggle")).ToHaveTextAsync("Theme: dark");
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task An_invoice_is_made_by_typing_into_the_pickers()
    {
        var tag = Tag();
        var income = await app.InScope(sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand($"Fees {tag}", AccountType.Income)));
        await app.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails($"Harbor Lights {tag}")));
        await app.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails($"Bookkeeping {tag}", ItemType.Service, income.Id, 85m)));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/sales/invoices/new");

        await PickAsync(p, p.Locator("#customer"), $"harbor {tag}", $"Harbor Lights {tag}");
        await PickAsync(p, p.Locator("input[aria-label='Product or service']").First, $"bookk {tag}", $"Bookkeeping {tag}");
        var quantity = p.Locator("input[aria-label='Quantity']").First;
        await quantity.FillAsync("3");
        await quantity.PressAsync("Tab");
        await Assertions.Expect(p.Locator("body")).ToContainTextAsync("255.00", new LocatorAssertionsToContainTextOptions { Timeout = (float)Wait.TotalMilliseconds });

        await p.GetByRole(AriaRole.Button, new() { Name = "Save invoice" }).ClickAsync();
        await p.WaitForURLAsync(new Regex("/sales/invoices/[0-9a-f-]{36}$"), new PageWaitForURLOptions { Timeout = (float)Wait.TotalMilliseconds });

        var id = Guid.Parse(p.Url[^36..]);
        var invoice = await app.InScope(sp => sp.GetRequiredService<OpenBookDbContext>().Invoices.AsNoTracking().SingleAsync(i => i.Id == id));
        Assert.Equal(255m, invoice.Total);
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_barcode_scanned_into_the_item_picker_arrives_whole()
    {
        // A scanner types its digits with no pause between them, then Enter. The picker once wrote each keystroke's
        // text back into the box as its render came back, dropping the digits typed meanwhile.
        var tag = Tag();
        var barcode = $"0{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)}";
        var income = await app.InScope(sp => sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand($"Shop {tag}", AccountType.Income)));
        await app.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails($"Scanned Widget {tag}", ItemType.NonInventory, income.Id, 4.5m, Barcode: barcode)));

        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/sales/invoices/new");
        var item = p.Locator("input[aria-label='Product or service']").First;
        await OpenPickerAsync(p, item);

        await p.Keyboard.TypeAsync(barcode);
        await Assertions.Expect(item).ToHaveValueAsync(barcode);
        await Assertions.Expect(p.Locator($"#{await item.GetAttributeAsync("aria-controls")} .search-option.active:not(.search-add)"))
            .ToContainTextAsync($"Scanned Widget {tag}");
        await p.Keyboard.PressAsync("Enter");
        await Assertions.Expect(item).ToHaveValueAsync($"Scanned Widget {tag}");
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_double_click_on_save_makes_one_record()
    {
        var name = $"Double Click {Tag()}";
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync("/customers/new");
        await p.Locator("#name").FillAsync(name);

        await p.GetByRole(AriaRole.Button, new() { Name = "Create customer" }).DblClickAsync();
        await p.WaitForURLAsync(new Regex("/customers/[0-9a-f-]{36}"));
        await p.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Equal(1, await app.InScope(sp => sp.GetRequiredService<OpenBookDbContext>().Customers.CountAsync(c => c.DisplayName == name)));
        await page.AssertCleanAsync();
    }

    [BrowserFact]
    public async Task A_button_that_asks_first_does_nothing_until_the_question_is_accepted()
    {
        var customer = await app.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails($"Asks First {Tag()}")));
        await using var page = await app.SignedInAsync();
        var p = page.Page;
        await p.GotoAsync($"/customers/{customer.Id}");

        // An apostrophe and an ampersand (which Windows file names may have) stay text in the question.
        const string file = "O'Brien & Sons receipt (1).txt";
        await p.Locator("input[aria-label='File to attach']").SetInputFilesAsync(new FilePayload
        {
            Name = file,
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("a receipt"),
        });
        await p.Locator("#attachments").GetByRole(AriaRole.Button, new() { Name = "Attach", Exact = true }).ClickAsync();
        var remove = p.Locator("#attachments").GetByRole(AriaRole.Button, new() { Name = "Remove" });
        await Assertions.Expect(remove).ToHaveCountAsync(1);

        string? asked = null;
        void Dismiss(object? _, IDialog dialog)
        {
            asked = dialog.Message;
            _ = dialog.DismissAsync();
        }

        p.Dialog += Dismiss;
        await remove.ClickAsync();
        await p.WaitForTimeoutAsync(500);
        Assert.Equal($"Remove {file}?", asked);
        await Assertions.Expect(remove).ToHaveCountAsync(1);

        p.Dialog -= Dismiss;
        p.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        await remove.ClickAsync();
        await Assertions.Expect(remove).ToHaveCountAsync(0);
        await page.AssertCleanAsync();
    }

    /// <summary>
    /// Types into a search picker and chooses the match with Enter. An interactive page answers only once its circuit
    /// is up, so this waits until the list opens before typing, and until the highlighted choice is the one wanted
    /// (while the list catches up with the typing, "+ Add" can be the highlighted row).
    /// </summary>
    private static async Task PickAsync(IPage page, ILocator input, string text, string expected)
    {
        await OpenPickerAsync(page, input);
        await input.PressSequentiallyAsync(text);
        var listbox = page.Locator($"#{await input.GetAttributeAsync("aria-controls")}");
        await Assertions.Expect(listbox.Locator(".search-option.active:not(.search-add)")).ToContainTextAsync(expected);
        await input.PressAsync("Enter");
        await Assertions.Expect(input).ToHaveValueAsync(expected);
    }

    /// <summary>Clicks into a picker until its list opens: the first clicks may come before the page's circuit is up.</summary>
    private static async Task OpenPickerAsync(IPage page, ILocator input)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            await input.ClickAsync();
            try
            {
                await Assertions.Expect(input).ToHaveAttributeAsync("aria-expanded", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 1000 });
                break;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
                await page.Keyboard.PressAsync("Escape");
            }
        }
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..6];
}
