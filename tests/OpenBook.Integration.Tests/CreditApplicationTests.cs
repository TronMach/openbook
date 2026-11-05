using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Applying credit respects the closing date, and credit memos and vendor credits can be taken back.</summary>
public sealed class CreditApplicationTests : IAsyncLifetime
{
    private const string Password = "closing-1";
    private static readonly DateOnly Dec20 = new(2025, 12, 20);
    private static readonly DateOnly Dec31 = new(2025, 12, 31);
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan15 = new(2026, 1, 15);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _supplies = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;
    private Vendor _staples = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6300");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp")));
        _staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? password = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = password;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private SalesLineDraft[] Hours(decimal hours) => [new(_consulting.Id, null, hours, 100m)];

    private Task<Invoice> Invoice(DateOnly date, decimal hours = 5m) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, Hours(hours))));

    private Task<CreditMemo> Memo(DateOnly date, decimal hours = 2m) =>
        In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, date, Hours(hours))));

    private Task<CustomerPayment> Prepayment(DateOnly date, decimal amount) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, date, _checking.Id, [], Amount: amount)));

    private Task<Bill> Bill(DateOnly date, decimal amount = 500m) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_staples.Id, date, [new BillLineDraft(_supplies.Id, amount)])));

    private Task<VendorCredit> VendorCredit(DateOnly date, decimal amount = 200m) =>
        In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, date, [new BillLineDraft(_supplies.Id, amount)])));

    private Task<bool> Apply(DateOnly date, ApplyCredit credit, string? password = null) =>
        In<CreditService, bool>(async s => { await s.ApplyAsync(_acme.Id, date, [credit]); return true; }, password);

    private Task<bool> ApplyVendor(DateOnly date, ApplyVendorCredit credit, string? password = null) =>
        In<VendorCreditService, bool>(async s => { await s.ApplyAsync(_staples.Id, date, [credit]); return true; }, password);

    private Task<bool> Unapply(Guid memo, Guid invoice, string? password = null) =>
        In<CreditService, bool>(async s => { await s.UnapplyAsync(memo, invoice); return true; }, password);

    private Task<bool> UnapplyVendor(Guid credit, Guid bill, string? password = null) =>
        In<VendorCreditService, bool>(async s => { await s.UnapplyAsync(credit, bill); return true; }, password);

    private Task<ClosingStatus> CloseWithPassword(DateOnly through) =>
        _db.InScope(sp => sp.GetRequiredService<CloseBooksService>().SetAsync(through, Password, false));

    private Task<bool> ReportOnCash() =>
        _db.InDb(async db =>
        {
            (await db.Companies.SingleAsync()).SetDefaultReportBasis(ReportBasis.Cash);
            await db.SaveChangesAsync();
            return true;
        });

    private Task<int> Overrides() => _db.InDb(db => db.AuditRecords.CountAsync(a => a.Action == AuditAction.ClosedPeriodOverride));

    private async Task<decimal> CashIncome(int year) =>
        (await _db.InScope(sp => sp.GetRequiredService<FinancialReports>().ProfitAndLossAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ReportBasis.Cash))).NetIncome;

    [Fact]
    public async Task A_credit_memo_comes_back_off_an_invoice()
    {
        var invoice = await Invoice(Jan10);
        var memo = await Memo(Jan10);
        await Apply(Jan15, new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, 200m));
        var entries = await _db.InDb(db => db.JournalEntries.CountAsync());
        Assert.Equal("creditmemo.applied", await Refused(() => In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(memo.Id, "wrong"))));

        await Unapply(memo.Id, invoice.Id);

        var after = await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id));
        var credit = await In<CreditService, CreditMemo?>(s => s.GetCreditMemoAsync(memo.Id));
        Assert.Equal((500m, 200m, 0), (after!.Balance, credit!.Balance, credit.Applications.Count));
        Assert.Equal(entries, await _db.InDb(db => db.JournalEntries.CountAsync()));

        await In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(memo.Id, "Applied to the wrong invoice"));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong too"));
    }

    [Fact]
    public async Task Removing_a_credit_is_audited_on_both_documents()
    {
        var invoice = await Invoice(Jan10);
        var memo = await Memo(Jan10);
        await Apply(Jan15, new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, 200m));
        var before = await _db.InDb(db => db.AuditRecords.CountAsync(a => a.EntityId == memo.Id || a.EntityId == invoice.Id));

        await Unapply(memo.Id, invoice.Id);

        Assert.Equal(before + 2, await _db.InDb(db => db.AuditRecords.CountAsync(a => a.EntityId == memo.Id || a.EntityId == invoice.Id)));
    }

    [Fact]
    public async Task A_credit_that_is_not_applied_there_cannot_be_removed()
    {
        var invoice = await Invoice(Jan10);
        var memo = await Memo(Jan10);

        Assert.Equal("credit.not_applied", await Refused(() => Unapply(memo.Id, invoice.Id)));
    }

    [Fact]
    public async Task A_vendor_credit_comes_back_off_a_bill()
    {
        var bill = await Bill(Jan10);
        var credit = await VendorCredit(Jan10);
        await ApplyVendor(Jan15, new ApplyVendorCredit(credit.Id, bill.Id, 200m));
        Assert.Equal("vendorcredit.applied", await Refused(() => In<VendorCreditService, VendorCredit>(s => s.VoidAsync(credit.Id, "wrong"))));

        await UnapplyVendor(credit.Id, bill.Id);

        Assert.Equal(500m, (await In<BillService, Bill?>(s => s.GetAsync(bill.Id)))!.Balance);
        Assert.Equal(200m, (await In<VendorCreditService, VendorCredit?>(s => s.GetAsync(credit.Id)))!.Balance);
        await In<VendorCreditService, VendorCredit>(s => s.VoidAsync(credit.Id, "Applied to the wrong bill"));
    }

    [Fact]
    public async Task Taking_a_credit_back_from_a_closed_date_needs_the_password()
    {
        var invoice = await Invoice(Dec20);
        var memo = await Memo(Dec20);
        await Apply(Dec20.AddDays(1), new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, 200m));
        await CloseWithPassword(Dec31);

        Assert.Equal("period.password_required", await Refused(() => Unapply(memo.Id, invoice.Id)));
        await Unapply(memo.Id, invoice.Id, Password);

        Assert.Equal(1, await Overrides());
    }

    [Fact]
    public async Task Credit_cannot_be_applied_on_a_closed_date()
    {
        var invoice = await Invoice(Dec20);
        var memo = await Memo(Dec20);
        var bill = await Bill(Dec20);
        var credit = await VendorCredit(Dec20);
        await CloseWithPassword(Dec31);
        var application = new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, 200m);
        var vendorApplication = new ApplyVendorCredit(credit.Id, bill.Id, 200m);

        Assert.Equal("period.password_required", await Refused(() => Apply(Dec31, application)));
        Assert.Equal("period.password_required", await Refused(() => ApplyVendor(Dec31, vendorApplication)));
        Assert.Equal("period.wrong_password", await Refused(() => Apply(Dec31, application, "not it")));
        Assert.Equal(0, await Overrides());

        await Apply(Dec31, application, Password);
        await ApplyVendor(Dec31, vendorApplication, Password);
        Assert.Equal(2, await Overrides());
    }

    [Fact]
    public async Task On_the_accrual_basis_last_years_prepayment_can_still_be_used()
    {
        var payment = await Prepayment(Dec20, 300m);
        await CloseWithPassword(Dec31);
        var invoice = await Invoice(Jan10);

        await Apply(Jan15, new ApplyCredit(CreditKind.Payment, payment.Id, invoice.Id, 300m));

        Assert.Equal(200m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Balance);
    }

    [Fact]
    public async Task On_the_cash_basis_applying_a_prepayment_needs_its_period_open()
    {
        await ReportOnCash();
        var payment = await Prepayment(Dec20, 300m);
        await CloseWithPassword(Dec31);
        var invoice = await Invoice(Jan10);
        var closed = await CashIncome(2025);
        var application = new ApplyCredit(CreditKind.Payment, payment.Id, invoice.Id, 300m);

        Assert.Equal("period.password_required", await Refused(() => Apply(Jan15, application)));
        Assert.Equal(closed, await CashIncome(2025));

        await Apply(Jan15, application, Password);

        Assert.Equal(closed + 300m, await CashIncome(2025));
        Assert.Equal(1, await Overrides());
    }

    [Fact]
    public async Task On_the_cash_basis_a_credit_memo_applied_in_an_open_period_needs_no_password()
    {
        await ReportOnCash();
        var memo = await Memo(Dec20);
        await CloseWithPassword(Dec31);
        var invoice = await Invoice(Jan10);

        await Apply(Jan15, new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, 200m));

        Assert.Equal(300m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Balance);
    }
}
