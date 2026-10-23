using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Auditing;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>V1.3: estimates, discounts and terms, credits and refunds, deposits, recurring invoices, aging, statements, forms and email.</summary>
public sealed class ReceivablesFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _services = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;
    private Customer _globex = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _services.Id, 150m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test", PaymentTermsDays: 30,
            EarlyPaymentDiscountPercent: 2m, EarlyPaymentDiscountDays: 10)));
        _globex = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Globex", PaymentTermsDays: 15)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private SalesLineDraft Consulting(decimal hours) => new(_consulting.Id, null, hours, 150m);

    private Task<Invoice> Invoice(Customer customer, decimal hours, DateOnly? date = null, params SalesLineDraft[] extra) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date ?? Mar1, [Consulting(hours), .. extra])));

    private Task<CustomerPayment> Receive(Customer customer, DateOnly date, decimal? amount, Guid? depositTo = null, params ApplyToInvoice[] applications) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, date, depositTo ?? _checking.Id, applications, Amount: amount)));

    private async Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual)
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private Task<Account> AccountNamed(string name) =>
        _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Name == name));

    private Task<Account> Receivables() => _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Type == AccountType.AccountsReceivable));

    private Task<Invoice?> Reload(Invoice invoice) => In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id));

    private Task<int> EntryCount() => _db.InDb(db => db.JournalEntries.CountAsync());

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    // ---- Estimates ----

    [Fact]
    public async Task Estimate_becomes_an_invoice_and_reopens_if_the_invoice_is_voided()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(
            _acme.Id, Mar1, [Consulting(8m), new SalesLineDraft(null, null, 1m, 5m, SalesLineKind.DiscountPercent)], Mar1.AddDays(30), Memo: "Phase 1")));
        Assert.Equal(0, await EntryCount());
        Assert.Equal("1001", estimate.Number);

        var invoice = await In<EstimateService, Invoice>(s => s.ConvertToInvoiceAsync(estimate.Id, Mar1.AddDays(3)));

        Assert.Equal(1140m, invoice.Total);
        Assert.Equal(estimate.Id, invoice.EstimateId);
        Assert.Equal("Phase 1", invoice.Memo);
        var closed = await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id));
        Assert.Equal((EstimateStatus.Closed, invoice.Id), (closed!.Status, closed.InvoiceId));
        Assert.Equal("estimate.closed", await Refused(() => In<EstimateService, Invoice>(s => s.ConvertToInvoiceAsync(estimate.Id, Mar1))));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "wrong customer"));
        var reopened = await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id));
        Assert.Equal(EstimateStatus.Accepted, reopened!.Status);
    }

    [Fact]
    public async Task Declined_estimates_cannot_be_invoiced()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [Consulting(1m)])));
        await In<EstimateService, Estimate>(s => s.SetStatusAsync(estimate.Id, EstimateStatus.Declined));

        Assert.Equal("estimate.declined", await Refused(() => In<EstimateService, Invoice>(s => s.ConvertToInvoiceAsync(estimate.Id, Mar1))));
        Assert.Equal(0, await EntryCount());
    }

    // ---- Discounts and terms ----

    [Fact]
    public async Task Discount_lines_post_to_discounts_given()
    {
        var invoice = await Invoice(_globex, 10m, null, new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent));

        var discounts = await AccountNamed(SalesAccounts.DiscountsAccountName);
        Assert.Equal(AccountType.Income, discounts.Type);
        Assert.Equal(1350m, invoice.Total);
        Assert.Equal(150m, await Balance(discounts.Id));
        Assert.Equal(-1500m, await Balance(_services.Id));
    }

    [Fact]
    public async Task Early_payment_discount_settles_the_invoice_and_counts_on_cash_basis()
    {
        var invoice = await Invoice(_acme, 10m);
        Assert.Equal(Mar1.AddDays(10), invoice.EarlyPaymentDiscountDate);
        Assert.Equal(Mar1.AddDays(30), invoice.DueDate);

        await Receive(_acme, Mar1.AddDays(8), 1470m, null, new ApplyToInvoice(invoice.Id, 1470m, 30m));

        Assert.Equal(0m, (await Reload(invoice))!.Balance);
        var discounts = await AccountNamed(SalesAccounts.DiscountsAccountName);
        Assert.Equal(0m, await Balance((await Receivables()).Id));
        Assert.Equal(1470m, await Balance(_checking.Id));
        Assert.Equal(30m, await Balance(discounts.Id));
        // Cash basis: the full invoice income on the payment date, less the discount.
        Assert.Equal(-1500m, await Balance(_services.Id, ReportBasis.Cash));
        Assert.Equal(30m, await Balance(discounts.Id, ReportBasis.Cash));
    }

    // ---- Unapplied payments, credit memos, refunds ----

    [Fact]
    public async Task Overpayment_becomes_credit_that_can_be_applied_and_refunded()
    {
        var first = await Invoice(_globex, 2m);
        var payment = await Receive(_globex, Mar1, 500m, null, new ApplyToInvoice(first.Id, 300m));
        var receivables = await Receivables();
        Assert.Equal(200m, payment.Unapplied);
        Assert.Equal(-200m, await Balance(receivables.Id));

        var second = await Invoice(_globex, 1m, Mar1.AddDays(5));
        var entries = await EntryCount();
        await In<CreditService, bool>(async s =>
        {
            await s.ApplyAsync(_globex.Id, Mar1.AddDays(6), [new ApplyCredit(CreditKind.Payment, payment.Id, second.Id, 150m)]);
            return true;
        });
        Assert.Equal(entries, await EntryCount());
        Assert.Equal(0m, (await Reload(second))!.Balance);

        var refund = await In<CreditService, CustomerRefund>(s => s.RefundAsync(new RefundCreditCommand(CreditKind.Payment, payment.Id, Mar1.AddDays(7), _checking.Id, 50m, "CHK 9")));
        Assert.Equal(0m, (await In<PaymentService, CustomerPayment?>(s => s.GetAsync(payment.Id)))!.Unapplied);
        Assert.Equal(0m, await Balance(receivables.Id));
        Assert.Equal(450m, await Balance(_checking.Id));
        Assert.Equal(0m, await Balance(receivables.Id, ReportBasis.Cash));
        Assert.Equal(-450m, await Balance(_services.Id, ReportBasis.Cash));

        Assert.Equal("payment.refunded", await Refused(() => In<PaymentService, CustomerPayment>(s => s.VoidAsync(payment.Id, "x"))));
        await In<CreditService, CustomerRefund>(s => s.VoidRefundAsync(refund.Id, "check lost"));
        Assert.Equal(50m, (await In<PaymentService, CustomerPayment?>(s => s.GetAsync(payment.Id)))!.Unapplied);
    }

    [Fact]
    public async Task Credit_memo_applies_to_invoices_and_its_refund_reverses_income_on_cash_basis()
    {
        var invoice = await Invoice(_acme, 4m);
        var credit = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Mar1.AddDays(2), [Consulting(2m)])));
        Assert.Equal("1002", credit.Number);
        Assert.Equal(300m, await Balance((await Receivables()).Id));

        var available = await In<CreditService, IReadOnlyList<AvailableCredit>>(s => s.AvailableAsync(_acme.Id));
        Assert.Equal(300m, Assert.Single(available).Available);

        await In<CreditService, bool>(async s =>
        {
            await s.ApplyAsync(_acme.Id, Mar1.AddDays(3), [new ApplyCredit(CreditKind.CreditMemo, credit.Id, invoice.Id, 200m)]);
            return true;
        });
        Assert.Equal(400m, (await Reload(invoice))!.Balance);
        Assert.Equal("creditmemo.applied", await Refused(() => In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(credit.Id, "x"))));
        Assert.Equal("credit.exceeds_available", await Refused(() => _db.InScope(sp =>
            sp.GetRequiredService<CreditService>().ApplyAsync(_acme.Id, Mar1.AddDays(3), [new ApplyCredit(CreditKind.CreditMemo, credit.Id, invoice.Id, 101m)]))));

        await In<CreditService, CustomerRefund>(s => s.RefundAsync(new RefundCreditCommand(CreditKind.CreditMemo, credit.Id, Mar1.AddDays(4), _checking.Id, 100m)));

        // Accrual: 600 invoiced less a 300 credit. Cash: nothing was paid, but the 100 refund reverses income.
        Assert.Equal(-300m, await Balance(_services.Id));
        Assert.Equal(100m, await Balance(_services.Id, ReportBasis.Cash));
        Assert.Equal(-100m, await Balance(_checking.Id, ReportBasis.Cash));
        Assert.Equal(400m, await Balance((await Receivables()).Id));
    }

    [Fact]
    public async Task Invoices_with_credit_applied_cannot_be_voided()
    {
        var invoice = await Invoice(_acme, 1m);
        var credit = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Mar1, [Consulting(1m)])));
        await _db.InScope(sp => sp.GetRequiredService<CreditService>().ApplyAsync(_acme.Id, Mar1, [new ApplyCredit(CreditKind.CreditMemo, credit.Id, invoice.Id, 150m)]));

        Assert.Equal("invoice.has_payments", await Refused(() => In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "x"))));
    }

    // ---- Undeposited funds ----

    [Fact]
    public async Task Undeposited_payments_are_grouped_into_one_bank_deposit()
    {
        var a = await Invoice(_acme, 1m);
        var b = await Invoice(_globex, 2m);
        var p1 = await Receive(_acme, Mar1, null, _undeposited.Id, new ApplyToInvoice(a.Id, 150m));
        var p2 = await Receive(_globex, Mar1, null, _undeposited.Id, new ApplyToInvoice(b.Id, 300m));

        var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
        Assert.Equal(2, waiting.Count);

        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar1.AddDays(1),
            [(DepositedKind.Payment, p1.Id), (DepositedKind.Payment, p2.Id)])));

        Assert.Equal(450m, deposit.Total);
        Assert.Equal(450m, await Balance(_checking.Id));
        Assert.Equal(0m, await Balance(_undeposited.Id));
        Assert.Empty(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()));
        Assert.Equal("payment.deposited", await Refused(() => In<PaymentService, CustomerPayment>(s => s.VoidAsync(p1.Id, "x"))));
        Assert.Equal("deposit.not_undeposited", await Refused(() => In<DepositService, BankDeposit>(s => s.DepositAsync(
            new MakeDepositCommand(_checking.Id, Mar1.AddDays(1), [(DepositedKind.Payment, p1.Id)])))));

        await In<DepositService, BankDeposit>(s => s.VoidAsync(deposit.Id, "split it"));
        Assert.Equal(2, (await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync())).Count);
        Assert.Equal(450m, await Balance(_undeposited.Id));
        await In<PaymentService, CustomerPayment>(s => s.VoidAsync(p1.Id, "bounced"));
    }

    [Fact]
    public async Task Sales_receipts_in_undeposited_funds_can_be_deposited()
    {
        var receipt = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(null, Mar1, _undeposited.Id, [Consulting(1m)])));

        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar1, [(DepositedKind.SalesReceipt, receipt.Id)])));

        Assert.Equal(150m, deposit.Total);
        Assert.Equal("receipt.deposited", await Refused(() => In<SalesReceiptService, SalesReceipt>(s => s.VoidAsync(receipt.Id, "x"))));
        Assert.Equal("deposit.before_payment", await Refused(async () =>
        {
            var another = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(null, Mar1.AddDays(5), _undeposited.Id, [Consulting(1m)])));
            await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar1, [(DepositedKind.SalesReceipt, another.Id)])));
        }));
    }

    // ---- Aging and statements ----

    [Fact]
    public async Task Aging_buckets_follow_due_dates_and_match_receivables()
    {
        // Globex is Net 15.
        await Invoice(_globex, 1m, new DateOnly(2026, 1, 1));   // due Jan 16: 74 days late on Mar 31
        await Invoice(_globex, 2m, new DateOnly(2026, 3, 1));   // due Mar 16: 15 days late
        await Invoice(_globex, 3m, new DateOnly(2026, 3, 30));  // due Apr 14: current
        var overpaid = await Receive(_globex, new DateOnly(2026, 3, 31), 100m);
        Assert.Equal(100m, overpaid.Unapplied);

        var report = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(new DateOnly(2026, 3, 31)));

        var globex = Assert.Single(report.Customers);
        Assert.Equal(new AgingBuckets(350m, 300m, 0m, 150m, 0m), globex.Buckets);
        Assert.Equal(await Balance((await Receivables()).Id), report.Totals.Total);

        var earlier = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(new DateOnly(2026, 2, 1)));
        Assert.Equal(new AgingBuckets(0m, 150m, 0m, 0m, 0m), Assert.Single(earlier.Customers).Buckets);
    }

    [Fact]
    public async Task Aging_counts_payments_only_from_their_date()
    {
        var invoice = await Invoice(_globex, 2m, new DateOnly(2026, 3, 1));
        await Receive(_globex, new DateOnly(2026, 3, 20), null, null, new ApplyToInvoice(invoice.Id, 300m));

        var before = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(new DateOnly(2026, 3, 19)));
        var after = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(new DateOnly(2026, 3, 20)));

        Assert.Equal(300m, before.Totals.Days1To30);
        Assert.Empty(after.Customers);
    }

    [Fact]
    public async Task Statement_runs_a_balance_from_the_balance_forward()
    {
        var old = await Invoice(_globex, 1m, new DateOnly(2026, 2, 10));
        var march = await Invoice(_globex, 2m, new DateOnly(2026, 3, 5));
        await Receive(_globex, new DateOnly(2026, 3, 10), null, null, new ApplyToInvoice(old.Id, 150m));
        await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_globex.Id, new DateOnly(2026, 3, 12), [Consulting(0.5m)])));

        var statement = await In<ReceivablesService, CustomerStatement>(s => s.StatementAsync(_globex.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));

        Assert.Equal(150m, statement.OpeningBalance);
        Assert.Equal([StatementLineKind.Invoice, StatementLineKind.Payment, StatementLineKind.CreditMemo], statement.Lines.Select(l => l.Kind));
        Assert.Equal([450m, 300m, 225m], statement.Lines.Select(l => l.Balance));
        Assert.Equal(225m, statement.ClosingBalance);
        Assert.Equal(statement.ClosingBalance, statement.Aging.Total);
        Assert.Equal(march.Id, statement.Lines[0].DocumentId);
    }

    // ---- Recurring invoices ----

    [Fact]
    public async Task Recurring_invoices_catch_up_and_email_when_set_to()
    {
        await _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));
        await In<RecurringInvoiceService, RecurringInvoice>(s => s.CreateAsync(new RecurringInvoiceDetails(
            "Retainer", _acme.Id, [Consulting(10m)], RecurrenceFrequency.Monthly, new DateOnly(2026, 1, 31), AutoCreate: true, AutoEmail: true)));
        await In<RecurringInvoiceService, RecurringInvoice>(s => s.CreateAsync(new RecurringInvoiceDetails(
            "Manual", _globex.Id, [Consulting(1m)], RecurrenceFrequency.Monthly, new DateOnly(2026, 3, 1))));

        var results = await In<RecurringInvoiceService, IReadOnlyList<RecurringInvoiceResult>>(s => s.CreateDueAsync(new DateOnly(2026, 3, 31), automaticOnly: true));

        var result = Assert.Single(results);
        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31)], result.Created.Select(i => i.Date));
        Assert.Equal(3, result.Emailed);
        Assert.Equal(3, _db.Mail.Sent.Count);
        Assert.All(_db.Mail.Sent, m =>
        {
            Assert.Equal("ap@acme.test", m.Message.To);
            Assert.Equal("s3cret-pass", m.Password);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(m.Message.Attachments.Single().Content, 0, 4), StringComparison.Ordinal);
        });
        Assert.All(result.Created, i => Assert.NotNull(i.RecurringInvoiceId));
    }

    [Fact]
    public async Task A_failed_email_leaves_the_recurring_invoice_created()
    {
        await _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, null, null, false, "books@studio.test", null, null)));
        await In<RecurringInvoiceService, RecurringInvoice>(s => s.CreateAsync(new RecurringInvoiceDetails(
            "Retainer", _acme.Id, [Consulting(1m)], RecurrenceFrequency.Monthly, new DateOnly(2026, 3, 1), AutoCreate: true, AutoEmail: true)));
        _db.Mail.Failure = "connection refused";

        var result = Assert.Single(await In<RecurringInvoiceService, IReadOnlyList<RecurringInvoiceResult>>(s => s.CreateDueAsync(new DateOnly(2026, 3, 1), automaticOnly: true)));

        Assert.Single(result.Created);
        Assert.Contains("connection refused", result.EmailError, StringComparison.Ordinal);
        Assert.Equal(1, await _db.InDb(db => db.Invoices.CountAsync()));
    }

    // ---- Forms and email ----

    [Theory]
    [InlineData(FormTemplate.Classic)]
    [InlineData(FormTemplate.Modern)]
    [InlineData(FormTemplate.Compact)]
    public async Task Forms_render_as_pdf_in_every_template(FormTemplate template)
    {
        await In<CompanySettingsService, Company>(s => s.UpdateFormStyleAsync(template, "#335577", "Pay by bank transfer"));
        var invoice = await Invoice(_acme, 2m, null, new SalesLineDraft(null, "Thanks!", 0m, 0m));
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [Consulting(1m)])));

        foreach (var (kind, id) in new[] { (FormKind.Invoice, invoice.Id), (FormKind.Estimate, estimate.Id) })
        {
            var form = await In<FormService, RenderedForm>(s => s.RenderAsync(kind, id));
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(form.Pdf, 0, 4), StringComparison.Ordinal);
            Assert.EndsWith(".pdf", form.FileName, StringComparison.Ordinal);
        }

        var statement = await In<FormService, RenderedForm>(s => s.RenderStatementAsync(_acme.Id, Mar1, Mar1.AddDays(30)));
        Assert.True(statement.Pdf.Length > 1000);
    }

    [Fact]
    public async Task Emailing_an_invoice_needs_settings_then_records_the_send()
    {
        var invoice = await Invoice(_acme, 2m);
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(FormKind.Invoice, invoice.Id));
        Assert.Equal("ap@acme.test", draft.To);
        Assert.Contains(invoice.Number, draft.Subject, StringComparison.Ordinal);

        Assert.Equal("email.not_configured", await Refused(() => _db.InScope(sp => sp.GetRequiredService<EmailService>().SendFormAsync(FormKind.Invoice, invoice.Id, draft))));

        var settings = await In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.StartTls, "books", "pw-123", false, "books@studio.test", null, "copy@studio.test")));
        Assert.Equal("protected:pw-123", settings.ProtectedPassword);

        await _db.InScope(sp => sp.GetRequiredService<EmailService>().SendFormAsync(FormKind.Invoice, invoice.Id, draft with { Subject = "Your invoice" }));

        var sent = Assert.Single(_db.Mail.Sent);
        Assert.Equal("Your invoice", sent.Message.Subject);
        Assert.NotNull((await Reload(invoice))!.LastEmailedAt);
        var audit = await In<AuditLogQuery, AuditPage>(q => q.QueryAsync(new AuditFilter(EntityId: invoice.Id)));
        Assert.Equal(AuditAction.Emailed, audit.Entries[0].Record.Action);

        // Saving again without a password keeps it; clearing removes it.
        settings = await In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.StartTls, "books", null, false, "books@studio.test", null, null)));
        Assert.Equal("protected:pw-123", settings.ProtectedPassword);
        settings = await In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.StartTls, "books", null, true, "books@studio.test", null, null)));
        Assert.Null(settings.ProtectedPassword);
    }

    [Fact]
    public async Task Send_failures_become_readable_errors()
    {
        await _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Folder, null, 587, SmtpSecurity.Auto, null, null, false, "books@studio.test", null, null)));
        _db.Mail.Failure = "disk full";

        var ex = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<EmailService>().SendTestAsync("me@studio.test")));

        Assert.Equal("email.send_failed", ex.Code);
        Assert.Contains("disk full", ex.Message, StringComparison.Ordinal);
    }
}
