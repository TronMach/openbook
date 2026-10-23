using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

public sealed class PurchasesFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _visa = null!;
    private Account _rent = null!;
    private Account _supplies = null!;
    private Vendor _landlord = null!;
    private Vendor _staples = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _visa = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _supplies = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6300");
        _landlord = await Vendors(s => s.CreateAsync(new VendorDetails("Landlord LLC", AccountNumber: "UNIT-4B", PaymentTermsDays: 0, DefaultExpenseAccountId: _rent.Id)));
        _staples = await Vendors(s => s.CreateAsync(new VendorDetails("Staples", PaymentTermsDays: 30)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Vendors<T>(Func<VendorService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<VendorService>()));
    private Task<T> Bills<T>(Func<BillService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<BillService>()));
    private Task<T> Payments<T>(Func<BillPaymentService, Task<T>> f) => _db.InScope(sp => f(sp.GetRequiredService<BillPaymentService>()));

    private Task<Bill> Bill(Vendor vendor, decimal amount, Account? category = null, string? reference = null) =>
        Bills(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Mar1, [new BillLineDraft((category ?? _rent).Id, amount)], Reference: reference)));

    private Task<IReadOnlyList<BillPayment>> Pay(Account from, string? firstCheck, params (Bill Bill, decimal Amount)[] apps) =>
        Payments(s => s.PayAsync(new PayBillsCommand(Mar31, from.Id, apps.Select(a => new ApplyToBill(a.Bill.Id, a.Amount)).ToList(), firstCheck)));

    private async Task<decimal> Balance(Guid accountId)
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), ReportBasis.Accrual));
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private async Task<Account> Payables() =>
        (await _db.InScope(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).All.Single(a => a.Type == AccountType.AccountsPayable);

    [Fact]
    public async Task Bill_debits_the_category_and_credits_payables_created_on_first_use()
    {
        var bill = await Bill(_staples, 240m, _supplies, "INV-88");

        Assert.Equal(240m, await Balance(_supplies.Id));
        Assert.Equal(-240m, await Balance((await Payables()).Id));
        Assert.Equal(Mar1.AddDays(30), bill.DueDate);
        var entry = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().GetAsync(bill.JournalEntryId));
        Assert.Equal(("purchases.bill", "Bill INV-88 · Staples"), (entry!.Source, entry.Memo));
    }

    [Fact]
    public async Task Bill_numbers_are_unique_per_vendor_only()
    {
        await Bill(_staples, 10m, reference: "1001");
        await Bill(_landlord, 10m, reference: "1001");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Bill(_staples, 10m, reference: "1001"));
        Assert.Equal("bill.reference_taken", ex.Code);
    }

    [Fact]
    public async Task Pay_bills_writes_one_numbered_check_per_vendor_with_partial_payments()
    {
        var rent = await Bill(_landlord, 1500m);
        var paper = await Bill(_staples, 200m, _supplies);
        var toner = await Bill(_staples, 300m, _supplies);

        var payments = await Pay(_checking, "1042", (rent, 1500m), (paper, 200m), (toner, 100m));

        Assert.Equal(
            [("Landlord LLC", 1500m, "1042", "UNIT-4B"), ("Staples", 300m, "1043", null)],
            payments.Select(p => (p.VendorId == _landlord.Id ? "Landlord LLC" : "Staples", p.Amount, p.Reference, p.Memo)));
        Assert.Equal(-1800m, await Balance(_checking.Id));
        Assert.Equal(-200m, await Balance((await Payables()).Id));
        Assert.Equal(200m, (await Bills(s => s.GetAsync(toner.Id)))!.Balance);
        Assert.Equal("1044", await _db.InScope(sp => sp.GetRequiredService<BankingService>().SuggestCheckNumberAsync(_checking.Id)));

        var summary = (await Vendors(s => s.ListAsync(Mar31))).Single(v => v.Vendor.Id == _staples.Id);
        Assert.Equal((200m, 1), (summary.Balance, summary.OpenBills));
    }

    [Fact]
    public async Task Bills_can_be_paid_by_credit_card_but_not_from_an_expense_account()
    {
        var bill = await Bill(_staples, 80m, _supplies);
        await Pay(_visa, null, (bill, 80m));
        Assert.Equal(-80m, await Balance(_visa.Id));

        var other = await Bill(_staples, 10m, _supplies);
        var ex = await Assert.ThrowsAsync<DomainException>(() => Pay(_rent, null, (other, 10m)));
        Assert.Equal("purchases.payment_account_type", ex.Code);
    }

    [Fact]
    public async Task Overpaying_a_bill_is_refused_and_nothing_posts()
    {
        var bill = await Bill(_staples, 50m, _supplies);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Pay(_checking, "1", (bill, 50.01m)));

        Assert.Equal("payment.exceeds_balance", ex.Code);
        Assert.Equal(0m, await Balance(_checking.Id));
    }

    [Fact]
    public async Task Voiding_a_payment_reopens_its_bills_which_can_then_be_voided()
    {
        var bill = await Bill(_landlord, 1500m);
        var payment = (await Pay(_checking, "1042", (bill, 1500m))).Single();

        Assert.Equal("bill.has_payments", (await Assert.ThrowsAsync<DomainException>(() => Bills(s => s.VoidAsync(bill.Id, "duplicate")))).Code);

        await Payments(s => s.VoidAsync(payment.Id, "check lost"));
        Assert.Equal(1500m, (await Bills(s => s.GetAsync(bill.Id)))!.Balance);

        await Bills(s => s.VoidAsync(bill.Id, "duplicate"));
        Assert.Equal(0m, await Balance(_checking.Id));
        Assert.Equal(0m, await Balance(_rent.Id));
        Assert.Equal(0m, await Balance((await Payables()).Id));
    }

    [Fact]
    public async Task A_bill_payment_reconciled_at_the_bank_cannot_be_voided()
    {
        var bill = await Bill(_landlord, 100m);
        var payment = (await Pay(_checking, "1042", (bill, 100m))).Single();
        var rec = await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().StartAsync(_checking.Id, Mar31, -100m));
        var sheet = await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().GetWorksheetAsync(rec.Id));
        await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().SetClearedAsync(rec.Id, [sheet.Items.Single().Line.JournalLineId], true));
        await _db.InScope(sp => sp.GetRequiredService<ReconciliationService>().CompleteAsync(rec.Id));

        Assert.Equal("Bill payment to Landlord LLC · 1042", sheet.Items.Single().Description);
        Assert.Equal("journal.reconciled", (await Assert.ThrowsAsync<DomainException>(() => Payments(s => s.VoidAsync(payment.Id, "x")))).Code);
    }

    [Fact]
    public async Task Expenses_can_name_a_vendor_whose_name_becomes_the_payee()
    {
        var expense = await _db.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _visa.Id, Mar1, [new BankSplit(_supplies.Id, 42.5m)], VendorId: _staples.Id)));

        Assert.Equal("Staples", expense.Payee);
        var listed = await _db.InScope(sp => sp.GetRequiredService<IPurchasesStore>().ListExpensesAsync(_staples.Id, default));
        Assert.Equal(expense.Id, Assert.Single(listed).Id);

        await Vendors(s => s.SetActiveAsync(_landlord.Id, false));
        var ex = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<BankingService>().RecordAsync(
            new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 10m)], VendorId: _landlord.Id))));
        Assert.Equal("vendor.inactive", ex.Code);
    }

    [Fact]
    public async Task Vendors_with_unpaid_bills_cannot_be_made_inactive_and_bad_categories_are_refused()
    {
        await Bill(_staples, 10m, _supplies);
        Assert.Equal("vendor.has_balance", (await Assert.ThrowsAsync<DomainException>(() => Vendors(s => s.SetActiveAsync(_staples.Id, false)))).Code);

        var ar = await _db.CreateAccount("Receivables", AccountType.AccountsReceivable);
        Assert.Equal("bill.category_type", (await Assert.ThrowsAsync<DomainException>(() => Bill(_staples, 10m, ar))).Code);
    }

    [Fact]
    public async Task Vendor_import_reads_addresses_terms_and_default_categories()
    {
        const string csv = """
            Vendor,Email,Street,City,State,Zip,Terms,Default Category,Account No
            City Power,billing@power.test,PO Box 1,Portland,OR,97201,Net 15,6300,ACCT-1
            Staples,,,,,,,,
            Nowhere Inc,,,,,,,Mystery Account,
            """;

        var preview = await _db.InScope(sp => sp.GetRequiredService<VendorImportService>().PreviewAsync(csv));

        Assert.Equal((1, 1, 1), (preview.NewCount, preview.ExistingCount, preview.ErrorCount));
        var power = preview.Rows[0].Details!;
        Assert.Equal(("PO Box 1\nPortland, OR 97201", 15, _supplies.Id, "ACCT-1"), (power.Address, power.PaymentTermsDays, power.DefaultExpenseAccountId!.Value, power.AccountNumber));
        Assert.Contains("no account 'Mystery Account'", preview.Rows[2].Message, StringComparison.Ordinal);
    }
}
