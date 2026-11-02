using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The books check: it passes on books entered through the services, notes entries made straight to an account, and
/// finds figures that were changed behind the services' backs. Seeded random runs of everyday documents (create, pay,
/// change, void, deposit, credit, write off) check that no sequence leaves the books disagreeing with themselves.
/// </summary>
public sealed class BooksCheckTests : IAsyncLifetime
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _supplies = null!;
    private Item _consulting = null!;
    private Item _widget = null!;
    private readonly List<Customer> _customers = [];
    private readonly List<Vendor> _vendors = [];

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        var products = await _db.CreateAccount("Product Sales", AccountType.Income, "4100");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, products.Id, 25m, PurchaseCost: 10m)));
        foreach (var name in new[] { "Acme", "Globex", "Initech" })
            _customers.Add(await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name, PaymentTermsDays: 15))));
        foreach (var name in new[] { "Paper Co", "Parts Inc" })
            _vendors.Add(await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails(name, DefaultExpenseAccountId: _supplies.Id))));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<BooksCheckReport> Check() => In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());

    private static string Describe(BooksCheckReport report) => string.Join(Environment.NewLine,
        report.Results.Where(r => r.Outcome == CheckOutcome.Problem)
            .SelectMany(r => r.Findings.Select(f => $"{r.Title}: {f.Text}").DefaultIfEmpty($"{r.Title}: {r.Summary}")));

    private static void AssertAgrees(BooksCheckReport report, string when)
    {
        Assert.True(report.Problems == 0, $"{when}:{Environment.NewLine}{Describe(report)}");
        Assert.Equal(10, report.Results.Count);
    }

    private Task<Invoice> Invoice(Customer customer, decimal hours, DateOnly date) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    [Fact]
    public async Task Books_entered_through_the_services_agree()
    {
        var invoice = await Invoice(_customers[0], 3m, Start);
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_customers[0].Id, Start.AddDays(3), _undeposited.Id,
            [new ApplyToInvoice(invoice.Id, 100m)], Amount: 150m)));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_vendors[1].Id, Start, [new BillLineDraft(_supplies.Id, 100m, ItemId: _widget.Id, Quantity: 10m)])));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_customers[1].Id, Start.AddDays(5), [new SalesLineDraft(_widget.Id, null, 4m, 25m)])));

        var report = await Check();

        AssertAgrees(report, "after a few documents");
        Assert.All(report.Results, r => Assert.Equal(CheckOutcome.Ok, r.Outcome));
        // 300 invoiced less 100 applied and 50 unapplied credit, plus the widget invoice's 100.
        Assert.Equal(250m, (await In<ReceivablesService, AgingReport>(s => s.AgingAsync(DateOnly.MaxValue))).Totals.Total);
    }

    [Fact]
    public async Task A_journal_entry_to_receivables_is_a_note_not_a_problem()
    {
        await Invoice(_customers[0], 2m, Start);
        var receivable = await _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Type == AccountType.AccountsReceivable));
        var equity = await _db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
        await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Start,
            [JournalLineDraft.Dr(receivable.Id, 75m), JournalLineDraft.Cr(equity.Id, 75m)], "Old balance")));

        var report = await Check();

        AssertAgrees(report, "with a journal entry to A/R");
        var receivables = report.Results.Single(r => r.Key == "receivables");
        Assert.Equal(CheckOutcome.Note, receivables.Outcome);
        Assert.Contains(receivables.Findings, f => f.EntityType == "journal_entry");
    }

    [Fact]
    public async Task Figures_changed_behind_the_services_are_found()
    {
        var invoice = await Invoice(_customers[0], 2m, Start);
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_customers[0].Id, Start, _undeposited.Id,
            [new ApplyToInvoice(invoice.Id, 200m)])));

        await _db.InDb(db => db.Invoices.Where(i => i.Id == invoice.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.AmountPaid, 120m)));
        await _db.InDb(db => db.CustomerPayments.Where(p => p.Id == payment.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, DocumentStatus.Voided)));

        var report = await Check();

        Assert.Equal(CheckOutcome.Problem, report.Outcome);
        var paid = report.Results.Single(r => r.Key == "invoices-paid");
        Assert.Equal(CheckOutcome.Problem, paid.Outcome);
        Assert.Contains(paid.Findings, f => f.EntityId == invoice.Id);
        var documents = report.Results.Single(r => r.Key == "documents-entries");
        Assert.Contains(documents.Findings, f => f.EntityId == payment.Id);
        Assert.Equal(CheckOutcome.Problem, report.Results.Single(r => r.Key == "undeposited").Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Random_runs_of_everyday_documents_leave_the_books_agreeing(int seed)
    {
        var random = new Random(seed);
        var done = new List<string>();
        for (var step = 1; step <= 80; step++)
        {
            var date = Start.AddDays(random.Next(0, 28));
            var operation = random.Next(0, 19);
            try
            {
                done.Add(await StepAsync(operation, date, random));
            }
            catch (DomainException ex)
            {
                // Refused: the point is that a refusal leaves nothing half done.
                done.Add($"refused {operation}: {ex.Code}");
            }

            if (step % 10 == 0)
                AssertAgrees(await Check(), $"seed {seed}, step {step} ({string.Join("; ", done.TakeLast(10))})");
        }
    }

    private async Task<string> StepAsync(int operation, DateOnly date, Random random)
    {
        T Pick<T>(IReadOnlyList<T> list) => list[random.Next(list.Count)];
        var customer = Pick(_customers);
        var vendor = Pick(_vendors);
        var invoices = await In<ISalesStore, IReadOnlyList<Invoice>>(s => s.ListInvoicesAsync(null, default));
        var open = invoices.Where(i => i.Status == DocumentStatus.Posted && i.Balance > 0m).ToList();
        var bills = await In<IPurchasesStore, IReadOnlyList<Bill>>(s => s.ListBillsAsync(null, default));
        var openBills = bills.Where(b => b.Status == DocumentStatus.Posted && b.Balance > 0m && b.CanBePaid).ToList();

        switch (operation)
        {
            case 0:
                IReadOnlyList<SalesLineDraft> lines = random.Next(2) == 0
                    ? [new SalesLineDraft(_consulting.Id, null, random.Next(1, 6), 100m)]
                    : [new SalesLineDraft(_widget.Id, null, random.Next(1, 4), 25m), new SalesLineDraft(_consulting.Id, null, 1m, 80m)];
                await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date, lines)));
                return "invoice";
            case 1:
                IReadOnlyList<BillLineDraft> billLines = random.Next(2) == 0
                    ? [new BillLineDraft(_supplies.Id, random.Next(20, 200))]
                    : [new BillLineDraft(_supplies.Id, random.Next(5, 15) * 10m, ItemId: _widget.Id, Quantity: random.Next(5, 15))];
                await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, date, billLines)));
                return "bill";
            case 2 when open.Count > 0:
            {
                var invoice = Pick(open);
                var amount = random.Next(2) == 0 ? invoice.Balance : decimal.Round(invoice.Balance / 2m, 2);
                decimal? extra = random.Next(4) == 0 ? amount + 50m : null;
                await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(invoice.CustomerId, date,
                    random.Next(2) == 0 ? _undeposited.Id : _checking.Id, [new ApplyToInvoice(invoice.Id, amount)], Amount: extra)));
                return "payment";
            }
            case 3:
            {
                var payments = (await In<ISalesStore, IReadOnlyList<CustomerPayment>>(s => s.ListPaymentsAsync(null, default))).Where(p => p.Status == DocumentStatus.Posted).ToList();
                if (payments.Count == 0)
                    return "no payment to void";
                await In<PaymentService, CustomerPayment>(s => s.VoidAsync(Pick(payments).Id, "Test"));
                return "void payment";
            }
            case 4:
            {
                var posted = invoices.Where(i => i.Status == DocumentStatus.Posted).ToList();
                if (posted.Count == 0)
                    return "no invoice to void";
                await In<InvoiceService, Invoice>(s => s.VoidAsync(Pick(posted).Id, "Test"));
                return "void invoice";
            }
            case 5:
            {
                var posted = invoices.Where(i => i.Status == DocumentStatus.Posted).ToList();
                if (posted.Count == 0)
                    return "no invoice to change";
                var invoice = Pick(posted);
                await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(invoice.Date,
                    [new SalesLineDraft(_consulting.Id, null, random.Next(1, 8), 100m)], "Test")));
                return "change invoice";
            }
            case 6:
            {
                var memo = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(customer.Id, date,
                    [new SalesLineDraft(_consulting.Id, null, 1m, 50m)])));
                var target = open.FirstOrDefault(i => i.CustomerId == customer.Id);
                if (target is not null)
                    await In<CreditService, bool>(async s =>
                    {
                        await s.ApplyAsync(customer.Id, date, [new ApplyCredit(CreditKind.CreditMemo, memo.Id, target.Id, Math.Min(50m, target.Balance))]);
                        return true;
                    });
                return "credit memo";
            }
            case 7 when open.Count > 0:
                await In<WriteOffService, IReadOnlyList<BadDebtWriteOff>>(s => s.WriteOffAsync(new WriteOffCommand(date, [new WriteOffLine(Pick(open).Id)])));
                return "write off";
            case 8:
            {
                var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
                if (waiting.Count == 0)
                    return "nothing to deposit";
                var last = waiting.Max(w => w.Date);
                await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, date < last ? last : date,
                    waiting.Select(w => (w.Kind, w.Id)).ToList())));
                return "deposit";
            }
            case 9:
            {
                var deposits = (await In<ISalesStore, IReadOnlyList<BankDeposit>>(s => s.ListDepositsAsync(default))).Where(d => d.Status == DocumentStatus.Posted).ToList();
                if (deposits.Count == 0)
                    return "no deposit to void";
                await In<DepositService, BankDeposit>(s => s.VoidAsync(Pick(deposits).Id, "Test"));
                return "void deposit";
            }
            case 10 when openBills.Count > 0:
            {
                var bill = Pick(openBills);
                var amount = random.Next(2) == 0 ? bill.Balance : decimal.Round(bill.Balance / 2m, 2);
                await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date < bill.Date ? bill.Date : date, _checking.Id,
                    [new ApplyToBill(bill.Id, amount)])));
                return "pay bill";
            }
            case 11:
            {
                var payments = (await In<IPurchasesStore, IReadOnlyList<BillPayment>>(s => s.ListPaymentsAsync(null, default))).Where(p => p.Status == DocumentStatus.Posted).ToList();
                if (payments.Count == 0)
                    return "no bill payment to void";
                await In<BillPaymentService, BillPayment>(s => s.VoidAsync(Pick(payments).Id, "Test"));
                return "void bill payment";
            }
            case 12:
            {
                var posted = bills.Where(b => b.Status == DocumentStatus.Posted).ToList();
                if (posted.Count == 0)
                    return "no bill to void";
                await In<BillService, Bill>(s => s.VoidAsync(Pick(posted).Id, "Test"));
                return "void bill";
            }
            case 13:
            {
                var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(vendor.Id, date, [new BillLineDraft(_supplies.Id, 20m)])));
                var target = openBills.FirstOrDefault(b => b.VendorId == vendor.Id);
                if (target is not null)
                    await In<VendorCreditService, bool>(async s =>
                    {
                        await s.ApplyAsync(vendor.Id, date < target.Date ? target.Date : date, [new ApplyVendorCredit(credit.Id, target.Id, Math.Min(20m, target.Balance))]);
                        return true;
                    });
                return "vendor credit";
            }
            case 14:
                await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, date,
                    [new BankSplit(_supplies.Id, random.Next(10, 90))], VendorId: vendor.Id)));
                return "expense";
            case 15:
            {
                var spends = (await In<IBankingStore, IReadOnlyList<BankTransaction>>(s => s.ListAllTransactionsAsync(default)))
                    .Where(t => t.Status == BankTransactionStatus.Posted).ToList();
                if (spends.Count == 0)
                    return "no expense to void";
                await In<BankingService, BankTransaction>(s => s.VoidAsync(Pick(spends).Id, "Test"));
                return "void expense";
            }
            case 16:
            {
                var payments = (await In<ISalesStore, IReadOnlyList<CustomerPayment>>(s => s.ListPaymentsAsync(null, default))).Where(p => p.Status == DocumentStatus.Posted).ToList();
                if (payments.Count == 0)
                    return "no payment to change";
                var payment = Pick(payments);
                await In<PaymentService, CustomerPayment>(s => s.ChangeAsync(payment.Id, new ChangePaymentCommand(payment.Date,
                    payment.Applications.Select(a => new ApplyToInvoice(a.InvoiceId, a.Amount, a.Discount)).ToList(), "Test", Amount: payment.Amount + 10m)));
                return "change payment";
            }
            case 17:
            {
                // Change a deposit: take its last item out, or put a waiting one in.
                var deposits = (await In<ISalesStore, IReadOnlyList<BankDeposit>>(s => s.ListDepositsAsync(default))).Where(d => d.Status == DocumentStatus.Posted).ToList();
                if (deposits.Count == 0)
                    return "no deposit to change";
                var deposit = Pick(deposits);
                var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
                var items = deposit.Lines.Select(l => (l.Kind, l.SourceId)).ToList();
                if (items.Count > 1 && random.Next(2) == 0)
                    items.RemoveAt(items.Count - 1);
                else if (waiting.Count > 0)
                    items.Add((waiting[0].Kind, waiting[0].Id));
                var on = waiting.Count > 0 && waiting[0].Date > deposit.Date ? waiting[0].Date : deposit.Date;
                await In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id, new MakeDepositCommand(_checking.Id, on, items), "Test"));
                return "change deposit";
            }
            case 18:
            {
                // A hand-entered entry, sometimes changed straight away.
                var entry = await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(date,
                    [JournalLineDraft.Dr(_supplies.Id, 25m), JournalLineDraft.Cr(_checking.Id, 25m)], "Petty cash")));
                if (random.Next(2) == 0)
                    await In<PostingEngine, JournalEntry>(s => s.ChangeAsync(entry.Id, new PostJournalEntryCommand(date,
                        [JournalLineDraft.Dr(_supplies.Id, 30m), JournalLineDraft.Cr(_checking.Id, 30m)], "Petty cash"), "Test"));
                return "journal entry";
            }
            default:
                return "nothing to do";
        }
    }
}
