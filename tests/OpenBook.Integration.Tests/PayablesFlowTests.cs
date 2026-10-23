using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Attachments;
using OpenBook.Application.Auditing;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>V1.4: vendor credits and refunds, recurring bills, A/P aging, 1099s, attachments.</summary>
public sealed class PayablesFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _card = null!;
    private Account _supplies = null!;
    private Account _contractors = null!;
    private Vendor _staples = null!;
    private Vendor _jo = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6300");
        _contractors = await _db.CreateAccount("Contract Labor", AccountType.Expense, "6150");
        _staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples", PaymentTermsDays: 30)));
        _jo = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Jo Freelance", PaymentTermsDays: 15,
            Track1099: true, TaxId: "123-45-6789")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Bill> Bill(Vendor vendor, decimal amount, DateOnly? date = null, Guid? account = null) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, date ?? Mar1, [new BillLineDraft(account ?? _supplies.Id, amount)])));

    private Task<IReadOnlyList<BillPayment>> Pay(DateOnly date, Guid from, params (Bill Bill, decimal Amount)[] bills) =>
        In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date, from, bills.Select(b => new ApplyToBill(b.Bill.Id, b.Amount)).ToList())));

    private async Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual)
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private Task<Account> Payables() => _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Type == AccountType.AccountsPayable));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    // ---- Vendor credits ----

    [Fact]
    public async Task Vendor_credit_reduces_payables_and_applies_to_bills_without_posting()
    {
        var bill = await Bill(_staples, 500m);
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(
            _staples.Id, Mar1.AddDays(2), [new BillLineDraft(_supplies.Id, 120m, "Returned toner")], "CM-44")));

        Assert.Equal(-380m, await Balance((await Payables()).Id));
        Assert.Equal(380m, await Balance(_supplies.Id));

        var entries = await _db.InDb(db => db.JournalEntries.CountAsync());
        await _db.InScope(sp => sp.GetRequiredService<VendorCreditService>().ApplyAsync(_staples.Id, Mar1.AddDays(3),
            [new ApplyVendorCredit(credit.Id, bill.Id, 120m)]));

        Assert.Equal(entries, await _db.InDb(db => db.JournalEntries.CountAsync()));
        Assert.Equal(380m, (await In<BillService, Bill?>(s => s.GetAsync(bill.Id)))!.Balance);
        Assert.Equal("vendorcredit.applied", await Refused(() => In<VendorCreditService, VendorCredit>(s => s.VoidAsync(credit.Id, "x"))));
        Assert.Equal("bill.has_payments", await Refused(() => In<BillService, Bill>(s => s.VoidAsync(bill.Id, "x"))));
        Assert.Single(await In<BillService, IReadOnlyList<VendorCredit>>(s => s.ListCreditsAsync(bill.Id)));
    }

    [Fact]
    public async Task Vendor_credits_only_apply_to_that_vendors_bills_within_what_is_left()
    {
        var joBill = await Bill(_jo, 300m);
        var staplesBill = await Bill(_staples, 50m);
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, Mar1, [new BillLineDraft(_supplies.Id, 100m)])));
        var service = (IServiceProvider sp) => sp.GetRequiredService<VendorCreditService>();

        Assert.Equal("credit.wrong_vendor", await Refused(() => _db.InScope(sp => service(sp).ApplyAsync(_staples.Id, Mar1, [new ApplyVendorCredit(credit.Id, joBill.Id, 10m)]))));
        Assert.Equal("payment.exceeds_balance", await Refused(() => _db.InScope(sp => service(sp).ApplyAsync(_staples.Id, Mar1, [new ApplyVendorCredit(credit.Id, staplesBill.Id, 60m)]))));
        Assert.Equal("credit.applied_before_payment", await Refused(() => _db.InScope(sp => service(sp).ApplyAsync(_staples.Id, Mar1.AddDays(-1), [new ApplyVendorCredit(credit.Id, staplesBill.Id, 10m)]))));
    }

    [Fact]
    public async Task Vendor_refund_brings_money_back_and_reduces_expense_on_cash_basis()
    {
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, Mar1, [new BillLineDraft(_supplies.Id, 75m)])));

        var refund = await In<VendorCreditService, VendorRefund>(s => s.RecordRefundAsync(new RecordVendorRefundCommand(credit.Id, Mar1.AddDays(5), _checking.Id, 75m, "Check 881")));

        Assert.Equal(75m, await Balance(_checking.Id));
        Assert.Equal(0m, await Balance((await Payables()).Id));
        Assert.Equal(-75m, await Balance(_supplies.Id));
        // Cash basis: the credit itself moves nothing; the refund cuts supplies expense when the money arrives.
        Assert.Equal(-75m, await Balance(_supplies.Id, ReportBasis.Cash));
        Assert.Equal(0m, await Balance((await Payables()).Id, ReportBasis.Cash));

        Assert.Equal("vendorcredit.refunded", await Refused(() => In<VendorCreditService, VendorCredit>(s => s.VoidAsync(credit.Id, "x"))));
        await In<VendorCreditService, VendorRefund>(s => s.VoidRefundAsync(refund.Id, "bounced"));
        Assert.Equal(75m, (await In<VendorCreditService, VendorCredit?>(s => s.GetAsync(credit.Id)))!.Balance);
        await In<VendorCreditService, VendorCredit>(s => s.VoidAsync(credit.Id, "entered twice"));
    }

    // ---- Recurring bills ----

    [Fact]
    public async Task Recurring_bills_catch_up_and_link_back_to_their_template()
    {
        var rent = await In<RecurringBillService, RecurringBill>(s => s.CreateAsync(new RecurringBillDetails(
            "Office rent", _staples.Id, [new BillLineDraft(_supplies.Id, 1500m)], RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 31), AutoCreate: true)));
        await In<RecurringBillService, RecurringBill>(s => s.CreateAsync(new RecurringBillDetails(
            "Manual", _jo.Id, [new BillLineDraft(_contractors.Id, 100m)], RecurrenceFrequency.Monthly, new DateOnly(2026, 3, 1))));

        var result = Assert.Single(await In<RecurringBillService, IReadOnlyList<RecurringBillResult>>(s => s.CreateDueAsync(new DateOnly(2026, 3, 31), automaticOnly: true)));

        Assert.Null(result.Error);
        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31)], result.Created.Select(b => b.Date));
        Assert.All(result.Created, b => Assert.Equal(rent.Id, b.RecurringBillId));
        Assert.Equal("Office rent", result.Created[0].Memo);
        Assert.Equal(-4500m, await Balance((await Payables()).Id));
        Assert.Empty(await In<RecurringBillService, IReadOnlyList<RecurringBillResult>>(s => s.CreateDueAsync(new DateOnly(2026, 3, 31), automaticOnly: true)));
    }

    // ---- A/P aging ----

    [Fact]
    public async Task Ap_aging_buckets_follow_due_dates_and_match_payables()
    {
        // Staples is Net 30.
        await Bill(_staples, 100m, new DateOnly(2026, 1, 1));   // due Jan 31: 59 days late on Mar 31
        var feb = await Bill(_staples, 200m, new DateOnly(2026, 2, 15));   // due Mar 17: 14 days late
        await Bill(_staples, 300m, new DateOnly(2026, 3, 20));  // due Apr 19: current
        await Pay(new DateOnly(2026, 3, 25), _checking.Id, (feb, 50m));
        await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, new DateOnly(2026, 3, 30), [new BillLineDraft(_supplies.Id, 40m)])));

        var report = await In<PayablesService, PayablesAgingReport>(s => s.AgingAsync(new DateOnly(2026, 3, 31)));

        var staples = Assert.Single(report.Vendors);
        Assert.Equal(new OpenBook.Application.Sales.AgingBuckets(260m, 150m, 100m, 0m, 0m), staples.Buckets);
        Assert.Equal(-await Balance((await Payables()).Id), report.Totals.Total);

        var before = await In<PayablesService, PayablesAgingReport>(s => s.AgingAsync(new DateOnly(2026, 3, 24)));
        Assert.Equal(200m, before.Totals.Days1To30);
    }

    // ---- 1099 ----

    [Fact]
    public async Task Report_1099_counts_bank_payments_and_leaves_out_card_payments()
    {
        var b1 = await Bill(_jo, 1500m, new DateOnly(2026, 2, 1), _contractors.Id);
        var b2 = await Bill(_jo, 700m, new DateOnly(2026, 3, 1), _contractors.Id);
        await Pay(new DateOnly(2026, 2, 10), _checking.Id, (b1, 1500m));
        await Pay(new DateOnly(2026, 3, 10), _card.Id, (b2, 700m));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, new DateOnly(2026, 4, 1), [new BankSplit(_contractors.Id, 600m)], "Jo Freelance", "1007", null, _jo.Id)));

        var report = await In<PayablesService, Report1099>(s => s.Report1099Async(2026));

        Assert.Equal(2000m, report.Threshold);
        var jo = Assert.Single(report.Vendors);
        Assert.Equal((2100m, 700m, true), (jo.Total, jo.CardPayments, jo.MeetsThreshold));
        Assert.DoesNotContain(report.Vendors, v => v.Vendor.Id == _staples.Id);

        var strict = await In<PayablesService, Report1099>(s => s.Report1099Async(2026, threshold: 5000m));
        Assert.False(Assert.Single(strict.Vendors).MeetsThreshold);
        Assert.Equal(600m, PayablesService.DefaultThreshold(2025));
    }

    // ---- Attachments ----

    [Fact]
    public async Task Files_attach_to_records_are_audited_and_can_be_removed()
    {
        var bill = await Bill(_staples, 10m);
        var bytes = "%PDF-1.4 fake receipt"u8.ToArray();

        var attachment = await In<AttachmentService, OpenBook.Domain.Attachments.Attachment>(s => s.AddAsync("bill", bill.Id, "receipt.pdf", new MemoryStream(bytes)));

        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(bytes, await In<AttachmentService, byte[]?>(s => s.GetContentAsync(attachment.Id)));
        Assert.Single(await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bill", bill.Id)));

        await _db.InScope(sp => sp.GetRequiredService<AttachmentService>().RemoveAsync(attachment.Id));

        Assert.Empty(await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bill", bill.Id)));
        Assert.Equal(0, await _db.InDb(db => db.AttachmentContents.CountAsync()));
        var history = await In<AuditLogQuery, AuditPage>(q => q.QueryAsync(new AuditFilter(EntityId: bill.Id)));
        Assert.Equal([AuditAction.Detached, AuditAction.Attached, AuditAction.Created], history.Entries.Select(e => e.Record.Action));
    }

    [Fact]
    public async Task Attachments_need_a_real_record_and_an_allowed_file()
    {
        var bill = await Bill(_staples, 10m);
        var add = (string type, Guid id, string name, int size) => _db.InScope(sp =>
            sp.GetRequiredService<AttachmentService>().AddAsync(type, id, name, new MemoryStream(new byte[size])));

        Assert.Equal("attachment.entity_invalid", await Refused(() => add("account", bill.Id, "a.pdf", 10)));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => add("bill", Guid.NewGuid(), "a.pdf", 10));
        Assert.Equal("attachment.type_not_allowed", await Refused(() => add("bill", bill.Id, "evil.html", 10)));
        Assert.Equal("attachment.too_large", await Refused(() => add("bill", bill.Id, "huge.pdf", (int)OpenBook.Domain.Attachments.Attachment.MaxBytes + 1)));
        Assert.Equal(0, await _db.InDb(db => db.Attachments.CountAsync()));
    }

    [Fact]
    public async Task Vendor_tax_ids_are_never_returned_in_full_by_lists()
    {
        var vendors = await In<VendorService, IReadOnlyList<VendorSummary>>(s => s.ListAsync(Mar1));

        Assert.Equal("•••••6789", vendors.Single(v => v.Vendor.Id == _jo.Id).Vendor.MaskedTaxId);
    }
}
