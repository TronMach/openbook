using System.Diagnostics;
using System.Security.Claims;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure;
using OpenBook.Web.Security;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web make-demo --ConnectionStrings:OpenBook="Data Source=demo.db" [--years 3] [--invoices 150] [--password …]</c>:
/// fills a new company file with a few years of everyday work (customers, items, invoices, payments, deposits,
/// bills, bill payments, expenses, credit memos, stock and the odd void), entered through the usual services so every
/// rule and audit record applies. For trying OpenBook and for timing pages on books that have some history.
/// Refuses a database that already has a company. The data is made up and the same for the same options.
/// </summary>
public static class DemoCompanyCommand
{
    public const string Name = "make-demo";

    private const string Owner = "owner";

    private static readonly ClaimsPrincipal OwnerPrincipal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, Owner)], authenticationType: "console"));

    private static readonly string[] CustomerWords =
        ["Acme", "Birch", "Cobalt", "Delta", "Ember", "Falcon", "Granite", "Harbor", "Iris", "Juniper", "Keystone", "Lumen", "Maple", "Nimbus", "Orchid", "Pioneer"];

    private static readonly string[] CustomerKinds = ["Bakery", "Dental", "Design", "Farms", "Garage", "Hotel", "Labs", "Law", "Media", "Studio", "Supply", "Works"];

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var years = Math.Clamp(Option(args, "--years", 3), 1, 10);
        var perMonth = Math.Clamp(Option(args, "--invoices", 150), 1, 2000);
        var password = OptionText(args, "--password") ?? $"demo-{Guid.NewGuid():N}"[..17];

        await services.MigrateOpenBookDatabaseAsync();
        if (await InScope(services, sp => sp.GetRequiredService<CompanySetupService>().IsSetUpAsync()))
        {
            Console.Error.WriteLine("That database already has a company. make-demo only fills a new one: pass --ConnectionStrings:OpenBook=\"Data Source=<new file>\".");
            return 1;
        }

        var watch = Stopwatch.StartNew();
        await InScope(services, sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(new SetUpCompanyCommand(
            "Demo Trading Co", BusinessType.SoleProprietorship, "USD", 1, Owner, password, Email: "books@demo.test")));
        var counts = await FillAsync(services, years, perMonth, Console.Out);

        Console.WriteLine($"Made a demo company with {years} year(s) of activity in {watch.Elapsed:mm\\:ss}: {counts}.");
        Console.WriteLine($"Sign in as '{Owner}' with password: {password}");
        return 0;
    }

    /// <summary>
    /// Fills the company just set up (its owner named <c>owner</c>) with <paramref name="years"/> of everyday work, as
    /// <c>make-demo</c> does; the accessibility checks use it so lists and record pages have something to draw. Returns
    /// what it made, and writes each month's counts to <paramref name="progress"/> when given.
    /// </summary>
    public static async Task<string> FillAsync(IServiceProvider services, int years, int perMonth, TextWriter? progress = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        await InScope(services, async sp =>
        {
            CompanyTime.Use(await sp.GetRequiredService<ICompanyStore>().GetCompanyAsync(default));
            return await sp.GetRequiredService<ChartTemplates>().ApplyAsync("sole-proprietor");
        });

        var generator = new Generator(services, new Random(20261102), years, perMonth, progress);
        await generator.RunAsync();
        return generator.Counts;
    }

    private static int Option(string[] args, string name, int fallback) =>
        int.TryParse(OptionText(args, name), out var value) ? value : fallback;

    private static string? OptionText(string[] args, string name)
    {
        var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static async Task<T> InScope<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = OwnerPrincipal;
        return await work(scope.ServiceProvider);
    }

    private sealed class Generator(IServiceProvider services, Random random, int years, int perMonth, TextWriter? progress)
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly List<Guid> _customers = [];
        private readonly List<Guid> _vendors = [];
        private readonly List<Guid> _services = [];
        private readonly List<Guid> _products = [];
        private ChartOfAccounts _chart = null!;

        public string Counts => string.Join(", ", _counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Value:N0} {c.Key}"));

        private Guid Account(string name) => _chart.FindByFullName(name)?.Id ?? throw new InvalidOperationException($"No account '{name}'.");

        private T Pick<T>(IReadOnlyList<T> list) => list[random.Next(list.Count)];

        private void Count(string what) => _counts[what] = _counts.GetValueOrDefault(what) + 1;

        private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
            InScope(services, sp => work(sp.GetRequiredService<TService>()));

        /// <summary>Runs one piece of work; a refusal (a rule saying no) is counted and skipped, like a person would.</summary>
        private async Task Try(string what, Func<Task> work)
        {
            try
            {
                await work();
                Count(what);
            }
            catch (DomainException)
            {
                Count("refused");
            }
        }

        public async Task RunAsync()
        {
            _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
            var checking = Account("Checking");
            var undeposited = Account("Undeposited Funds");
            var card = Account("Business Credit Card");
            string[] expenseAccounts = ["Advertising and Marketing", "Bank Fees", "Meals", "Office Supplies", "Software and Subscriptions", "Travel", "Utilities:Electric", "Utilities:Internet and Phone"];

            await MakeListsAsync();
            var today = CompanyTime.Today;
            var start = new DateOnly(today.Year - years, today.Month, 1);
            await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(start,
                [JournalLineDraft.Dr(checking, 50000m), JournalLineDraft.Cr(Account("Owner's Equity:Owner Contributions"), 50000m)], "Owner's starting money")));

            for (var month = start; month <= today; month = month.AddMonths(1))
            {
                var days = month.Year == today.Year && month.Month == today.Month ? today.Day : DateTime.DaysInMonth(month.Year, month.Month);
                DateOnly Day() => month.AddDays(random.Next(days));

                // Stock comes in first so most product sales find it.
                for (var i = 0; i < Math.Max(2, perMonth / 15); i++)
                {
                    var product = Pick(_products);
                    var quantity = random.Next(20, 80);
                    await Try("bills", () => In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_vendors[0], month,
                        [new BillLineDraft(Account("Cost of Goods Sold"), quantity * 12m, ItemId: product, Quantity: quantity)], Reference: $"P-{month:yyMM}-{i}"))));
                }

                for (var i = 0; i < perMonth; i++)
                {
                    var customer = Pick(_customers);
                    IReadOnlyList<SalesLineDraft> lines = random.Next(3) == 0
                        ? [new SalesLineDraft(Pick(_products), null, random.Next(1, 6), 30m)]
                        : [new SalesLineDraft(Pick(_services), null, random.Next(1, 9), 0m, AutoPrice: true)];
                    await Try("invoices", () => In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer, Day(), lines))));
                }

                for (var i = 0; i < perMonth / 10; i++)
                    await Try("sales receipts", () => In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(
                        null, Day(), undeposited, [new SalesLineDraft(Pick(_services), null, 1m, 0m, AutoPrice: true)]))));

                for (var i = 0; i < perMonth / 2; i++)
                {
                    var vendor = Pick(_vendors);
                    await Try("bills", () => In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor, Day(),
                        [new BillLineDraft(Account(Pick(expenseAccounts)), random.Next(40, 900))], Reference: $"{month:yyMM}-{i}"))));
                }

                for (var i = 0; i < perMonth / 2; i++)
                    await Try("expenses", () => In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
                        BankTransactionKind.Spend, random.Next(3) == 0 ? card : checking, Day(),
                        [new BankSplit(Account(Pick(expenseAccounts)), random.Next(8, 300))], VendorId: Pick(_vendors)))));

                await PayAndDepositAsync(month, days, checking, undeposited);

                if (random.Next(2) == 0)
                    await Try("credit memos", () => In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(
                        Pick(_customers), Day(), [new SalesLineDraft(Pick(_services), null, 1m, 0m, AutoPrice: true)]))));

                progress?.WriteLine($"{month:MMM yyyy}: {Counts}");
            }
        }

        private async Task MakeListsAsync()
        {
            var service = Account("Service Revenue");
            var product = Account("Product Sales");
            foreach (var (name, rate) in new[] { ("Consulting", 120m), ("Design", 95m), ("Support plan", 60m), ("Training", 150m), ("Installation", 80m) })
                _services.Add((await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails(name, ItemType.Service, service, rate)))).Id);
            foreach (var name in new[] { "Cable kit", "Sensor", "Mount", "Controller" })
                _products.Add((await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails(name, ItemType.Inventory, product, 30m, PurchaseCost: 12m, ReorderPoint: 20m)))).Id);

            var count = Math.Clamp(perMonth * 2, 20, 400);
            for (var i = 0; i < count; i++)
            {
                var name = $"{CustomerWords[i % CustomerWords.Length]} {CustomerKinds[i / CustomerWords.Length % CustomerKinds.Length]}" +
                    (i >= CustomerWords.Length * CustomerKinds.Length ? $" {i / (CustomerWords.Length * CustomerKinds.Length) + 1}" : "");
                _customers.Add((await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name, Email: $"ap{i}@customer.test",
                    PaymentTermsDays: random.Next(2) == 0 ? 30 : 15)))).Id);
            }

            foreach (var name in new[] { "Northwind Parts", "City Power", "Fiber Net", "Office Depot Plus", "Ad Agency", "Cloud Tools", "Cafe Corner", "Airline Travel" })
                _vendors.Add((await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails(name, PaymentTermsDays: 30)))).Id);
        }

        /// <summary>Most invoices due by now get paid (some late, some in part), payments go in the bank each week, and bills get paid.</summary>
        private async Task PayAndDepositAsync(DateOnly month, int days, Guid checking, Guid undeposited)
        {
            var end = month.AddDays(days - 1);
            var open = (await In<ISalesStore, IReadOnlyList<Invoice>>(s => s.ListInvoicesAsync(null, default)))
                .Where(i => i.Status == DocumentStatus.Posted && i.Balance > 0m && i.DueDate <= end.AddDays(10)).ToList();
            foreach (var invoice in open.Where(_ => random.Next(10) < 8))
            {
                var date = invoice.DueDate.AddDays(random.Next(-10, 20));
                date = date < invoice.Date ? invoice.Date : date > end ? end : date;
                var amount = random.Next(8) == 0 ? decimal.Round(invoice.Balance / 2m, 2) : invoice.Balance;
                await Try("payments", () => In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(invoice.CustomerId, date,
                    random.Next(4) == 0 ? checking : undeposited, [new ApplyToInvoice(invoice.Id, amount)], Reference: $"{random.Next(1000, 9999)}"))));
            }

            for (var week = month.AddDays(6); week <= end.AddDays(6); week = week.AddDays(7))
            {
                var on = week > end ? end : week;
                var waiting = (await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync())).Where(w => w.Date <= on).ToList();
                if (waiting.Count > 0)
                    await Try("deposits", () => In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(checking, on,
                        waiting.Select(w => (w.Kind, w.Id)).ToList()))));
            }

            var bills = (await In<IPurchasesStore, IReadOnlyList<Bill>>(s => s.ListBillsAsync(null, default)))
                .Where(b => b.Status == DocumentStatus.Posted && b.Balance > 0m && b.CanBePaid && b.DueDate <= end).ToList();
            if (bills.Count > 0)
                await Try("bill payment runs", () => In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(end, checking,
                    bills.Select(b => new ApplyToBill(b.Id, b.Balance)).ToList()))));

            // The odd mistake, put right the usual way.
            var payments = (await In<ISalesStore, IReadOnlyList<CustomerPayment>>(s => s.ListPaymentsAsync(null, default)))
                .Where(p => p.Status == DocumentStatus.Posted && p.DepositId is null && p.Date >= month).ToList();
            if (payments.Count > 0 && random.Next(3) == 0)
                await Try("voids", () => In<PaymentService, CustomerPayment>(s => s.VoidAsync(Pick(payments).Id, "Entered twice")));
        }
    }
}
