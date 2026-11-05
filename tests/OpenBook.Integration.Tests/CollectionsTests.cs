using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L3 slice 1: sub-customers, bad debt write-offs, late fees and payment reminders.</summary>
public sealed class CollectionsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _acme = await Customer("Acme Corp", email: "ap@acme.test");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<bool> In<TService>(Func<TService, Task> work) where TService : notnull =>
        _db.InScope(async sp =>
        {
            await work(sp.GetRequiredService<TService>());
            return true;
        });

    private Task<Customer> Customer(string name, Guid? parent = null, bool billWithParent = false, string? email = null,
        bool noReminders = false, bool noLateFees = false) =>
        In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name, Email: email, PaymentTermsDays: 30, ParentId: parent,
            BillWithParent: billWithParent, NoReminders: noReminders, NoLateFees: noLateFees)));

    private Task<Customer> Update(Customer c, Guid? parent) =>
        In<CustomerService, Customer>(s => s.UpdateAsync(c.Id, new CustomerDetails(c.DisplayName, Email: c.Email, ParentId: parent, BillWithParent: c.BillWithParent)));

    private Task<Invoice> Invoice(Customer customer, decimal hours, DateOnly? date = null) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date ?? Mar1, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private Task<CustomerPayment> Receive(Customer from, DateOnly date, params ApplyToInvoice[] applications) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(from.Id, date, _checking.Id, applications)));

    private Task<Invoice?> Reload(Invoice invoice) => In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id));

    private async Task<decimal> Balance(string accountName, ReportBasis basis = ReportBasis.Accrual)
    {
        var account = await _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Name == accountName));
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == account.Id);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<EmailSettings> UseEmail() => _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
        EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));

    // ---- Sub-customers ----

    [Fact]
    public async Task Sub_customers_nest_in_tree_order_without_cycles_or_too_many_levels()
    {
        var job = await Customer("Kitchen remodel", _acme.Id);
        var phase = await Customer("Phase 1", job.Id);
        var beta = await Customer("Beta Co");

        var list = await In<CustomerService, IReadOnlyList<CustomerSummary>>(s => s.ListAsync(Mar1));
        Assert.Equal(["Acme Corp", "Acme Corp:Kitchen remodel", "Acme Corp:Kitchen remodel:Phase 1", "Beta Co"], list.Select(c => c.FullName));
        Assert.Equal([0, 1, 2, 0], list.Select(c => c.Level));

        Assert.Equal("customer.parent_cycle", await Refused(() => Update(_acme, phase.Id)));
        Assert.Equal("customer.parent_cycle", await Refused(() => Update(job, job.Id)));

        var parent = phase;
        for (var level = 4; level <= 5; level++)
            parent = await Customer($"Level {level}", parent.Id);
        Assert.Equal("customer.too_deep", await Refused(() => Customer("Level 6", parent.Id)));
        // Moving a branch under another customer counts the levels below it too.
        var other = await Customer("Other", beta.Id);
        Assert.Equal("customer.too_deep", await Refused(() => Update(job, other.Id)));

        Assert.Equal("customer.has_active_children", await Refused(() => In<CustomerService, Customer>(s => s.SetActiveAsync(_acme.Id, false))));
        await In<CustomerService, Customer>(s => s.SetActiveAsync(other.Id, false));
        var orphan = await Customer("Orphan", _acme.Id);
        await In<CustomerService, Customer>(s => s.SetActiveAsync(orphan.Id, false));
        Assert.Equal("customer.parent_inactive", await Refused(() => Update(orphan, other.Id)));
    }

    [Fact]
    public async Task A_parent_pays_and_gets_statements_for_sub_customers_billed_with_it()
    {
        var billed = await Customer("Store 12", _acme.Id, billWithParent: true);
        var separate = await Customer("Store 14", _acme.Id);
        var billedInvoice = await Invoice(billed, 3m);
        var separateInvoice = await Invoice(separate, 2m);
        var own = await Invoice(_acme, 1m);

        var open = await In<PaymentService, IReadOnlyList<Invoice>>(s => s.OpenInvoicesAsync(_acme.Id));
        Assert.Equal(new[] { billedInvoice.Id, own.Id }.Order(), open.Select(i => i.Id).Order());
        Assert.Equal("payment.wrong_customer", await Refused(() => Receive(_acme, Mar1.AddDays(5), new ApplyToInvoice(separateInvoice.Id, 200m))));

        await Receive(_acme, Mar1.AddDays(5), new ApplyToInvoice(billedInvoice.Id, 300m));
        Assert.Equal(0m, (await Reload(billedInvoice))!.Balance);

        // The sub-customer's statement is the parent's, and includes the sub-customer's invoice.
        var statement = await In<ReceivablesService, CustomerStatement>(s => s.StatementAsync(billed.Id, Mar1, Mar31));
        Assert.Equal(_acme.Id, statement.Customer.Id);
        Assert.Contains(statement.Lines, l => l.DocumentId == billedInvoice.Id && l.Description.EndsWith("· Acme Corp:Store 12", StringComparison.Ordinal));
        Assert.DoesNotContain(statement.Lines, l => l.DocumentId == separateInvoice.Id);
        Assert.Equal(100m, statement.ClosingBalance);
        Assert.Equal(100m, statement.Aging.Total);

        // A/R aging lists each customer under its parent, with the parent's payment settling the sub-customer's invoice.
        var aging = await In<ReceivablesService, AgingReport>(s => s.AgingAsync(Mar31));
        Assert.Equal(["Acme Corp", "Store 14"], aging.Customers.Select(c => c.Customer.DisplayName));
    }

    // ---- Bad debt ----

    [Fact]
    public async Task Writing_off_an_invoice_expenses_its_balance_and_voiding_puts_it_back()
    {
        var invoice = await Invoice(_acme, 15m);
        await Receive(_acme, Mar1.AddDays(10), new ApplyToInvoice(invoice.Id, 500m));
        var writeOffDate = new DateOnly(2026, 6, 30);

        var candidates = await In<WriteOffService, IReadOnlyList<WriteOffCandidate>>(s => s.CandidatesAsync(writeOffDate, 90));
        Assert.Equal(91, Assert.Single(candidates).DaysPastDue);

        var writeOff = Assert.Single(await In<WriteOffService, IReadOnlyList<BadDebtWriteOff>>(s =>
            s.WriteOffAsync(new WriteOffCommand(writeOffDate, [new WriteOffLine(invoice.Id)], Memo: "Customer closed"))));
        Assert.Equal(1000m, writeOff.Amount);
        Assert.Equal(0m, (await Reload(invoice))!.Balance);
        Assert.Equal(1000m, await Balance(WriteOffService.BadDebtsAccountName));
        Assert.Equal(0m, await Balance("Accounts Receivable"));
        // On cash basis the invoice was never income, so writing it off isn't an expense either.
        Assert.Equal(0m, await Balance(WriteOffService.BadDebtsAccountName, ReportBasis.Cash));

        var before = await In<ReceivablesService, CustomerAging>(s => s.CustomerAgingAsync(_acme.Id, writeOffDate.AddDays(-1)));
        Assert.Equal(1000m, before.Buckets.Total);
        var after = await In<ReceivablesService, CustomerAging>(s => s.CustomerAgingAsync(_acme.Id, writeOffDate));
        Assert.Empty(after.Invoices);
        var statement = await In<ReceivablesService, CustomerStatement>(s => s.StatementAsync(_acme.Id, Mar1, writeOffDate));
        Assert.Equal(StatementLineKind.WriteOff, statement.Lines[^1].Kind);
        Assert.Equal(0m, statement.ClosingBalance);

        Assert.Equal("invoice.has_payments", await Refused(() => In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "x"))));
        Assert.Equal("writeoff.invoice_not_open", await Refused(() =>
            In<WriteOffService, IReadOnlyList<BadDebtWriteOff>>(s => s.WriteOffAsync(new WriteOffCommand(writeOffDate, [new WriteOffLine(invoice.Id)])))));

        await In<WriteOffService, BadDebtWriteOff>(s => s.VoidAsync(writeOff.Id, "They paid after all"));
        Assert.Equal(1000m, (await Reload(invoice))!.Balance);
        Assert.Equal(0m, await Balance(WriteOffService.BadDebtsAccountName));
    }

    [Fact]
    public async Task Write_offs_check_amounts_dates_and_accounts_before_posting()
    {
        var invoice = await Invoice(_acme, 2m);
        var revenue = await _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Name == "Service Revenue"));
        var allowance = await _db.CreateAccount("Allowance Expense", AccountType.Expense, "6100");
        Task<IReadOnlyList<BadDebtWriteOff>> WriteOff(decimal? amount, DateOnly date, Guid? account = null) =>
            In<WriteOffService, IReadOnlyList<BadDebtWriteOff>>(s => s.WriteOffAsync(new WriteOffCommand(date, [new WriteOffLine(invoice.Id, amount)], account)));

        Assert.Equal("writeoff.exceeds_balance", await Refused(() => WriteOff(200.01m, Mar31)));
        Assert.Equal("writeoff.before_invoice", await Refused(() => WriteOff(null, Mar1.AddDays(-1))));
        Assert.Equal("writeoff.account_type", await Refused(() => WriteOff(null, Mar31, revenue.Id)));
        Assert.Equal(0, await _db.InDb(db => db.JournalEntries.CountAsync(e => e.Source == BadDebtWriteOff.Source)));

        await WriteOff(50m, Mar31, allowance.Id);
        Assert.Equal(150m, (await Reload(invoice))!.Balance);
        Assert.Equal(50m, await Balance("Allowance Expense"));
    }

    // ---- Late fees ----

    [Fact]
    public async Task A_waived_late_fee_is_not_charged_again()
    {
        var invoice = await Invoice(_acme, 1000m, Mar1);
        await In<CollectionService, CollectionSettings>(s => s.SaveLateFeesAsync(new LateFeeSettingsCommand(true, true, 0m, 25m, 0, 0)));
        var today = invoice.DueDate.AddDays(1);
        var fee = Assert.Single(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(today)));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(fee.Id, "Waived as a courtesy"));

        Assert.Empty(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(today)));
        Assert.Empty(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(today.AddDays(90))));
    }

    [Fact]
    public async Task After_a_waived_fee_the_next_repeat_comes_a_full_period_later()
    {
        var invoice = await Invoice(_acme, 1000m, Mar1);
        await In<CollectionService, CollectionSettings>(s => s.SaveLateFeesAsync(new LateFeeSettingsCommand(true, true, 0m, 25m, 0, 30)));
        var today = invoice.DueDate.AddDays(1);
        var fee = Assert.Single(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(today)));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(fee.Id, "Waived as a courtesy"));

        Assert.Empty(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(today.AddDays(29))));
        Assert.Single(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(today.AddDays(30))));
    }

    [Fact]
    public async Task Late_fees_wait_out_the_grace_period_and_repeat_while_unpaid()
    {
        var exempt = await Customer("Friends & Family", noLateFees: true);
        var invoice = await Invoice(_acme, 10m);
        await Invoice(exempt, 10m);
        Assert.Empty(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(new DateOnly(2026, 6, 1))));

        await In<CollectionService, CollectionSettings>(s => s.SaveLateFeesAsync(new LateFeeSettingsCommand(true, false, 1.5m, 10m, 5, 30)));
        Assert.Empty(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(Mar31.AddDays(5))));

        var april6 = Mar31.AddDays(6);
        var due = Assert.Single(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(april6)));
        Assert.Equal((invoice.Id, 25m, 6), (due.Invoice.Id, due.Fee, due.DaysPastDue));

        var fee = Assert.Single(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(april6)));
        Assert.Equal((25m, april6, invoice.Id), (fee.Total, fee.DueDate, fee.LateFeeForInvoiceId));
        Assert.Equal(-25m, await Balance(LateFeeService.IncomeAccountName));
        Assert.Empty(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(april6)));
        Assert.Empty(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(april6.AddDays(29))));

        // Thirty days on, still unpaid: another fee, and the fee invoice itself never gets one.
        var second = Assert.Single(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(april6.AddDays(30))));
        Assert.Equal(invoice.Id, second.LateFeeForInvoiceId);
        Assert.Equal(1, await _db.InDb(db => db.Items.CountAsync(i => i.Name == LateFeeService.ItemName)));

        // Paid: no more fees.
        await Receive(_acme, april6.AddDays(31), new ApplyToInvoice(invoice.Id, 1000m));
        Assert.Empty(await In<LateFeeService, IReadOnlyList<LateFeeDue>>(s => s.DueAsync(april6.AddDays(90))));
    }

    [Fact]
    public async Task Late_fee_and_reminder_settings_are_checked()
    {
        Assert.Equal("latefee.nothing_to_charge", await Refused(() =>
            In<CollectionService, CollectionSettings>(s => s.SaveLateFeesAsync(new LateFeeSettingsCommand(true, true, 0m, 0m, 0, 0)))));
        Assert.Equal("latefee.percent_invalid", await Refused(() =>
            In<CollectionService, CollectionSettings>(s => s.SaveLateFeesAsync(new LateFeeSettingsCommand(true, true, 1.23456m, 0m, 0, 0)))));
        Assert.Equal("reminder.schedule_invalid", await Refused(() =>
            In<CollectionService, CollectionSettings>(s => s.SaveRemindersAsync(new ReminderSettingsCommand(true, "-3, soon", null, null)))));
        Assert.Equal("reminder.schedule_required", await Refused(() =>
            In<CollectionService, CollectionSettings>(s => s.SaveRemindersAsync(new ReminderSettingsCommand(true, "", null, null)))));

        var saved = await In<CollectionService, CollectionSettings>(s => s.SaveRemindersAsync(new ReminderSettingsCommand(true, "30; -3 7 7", " ", null)));
        Assert.Equal("-3,7,30", saved.ReminderSchedule);
        Assert.Equal(CollectionSettings.DefaultSubject, saved.ReminderSubject);
        Assert.Equal(1, await _db.InDb(db => db.AuditRecords.CountAsync(a => a.EntityType == CollectionService.EntityType)));
    }

    // ---- Scheduled emails ----

    [Fact]
    public async Task Scheduled_invoice_emails_go_when_due_and_failures_can_be_tried_again()
    {
        await UseEmail();
        var invoice = await Invoice(_acme, 2m);
        var voided = await Invoice(_acme, 1m);
        var now = _db.Clock.UtcNow;
        Task<ScheduledEmail> Schedule(Invoice i, DateTimeOffset at) => In<ScheduledEmailService, ScheduledEmail>(s =>
            s.ScheduleAsync(i.Id, new EmailDraft("ap@acme.test", $"Invoice {i.Number}", "Attached."), at));
        Task<IReadOnlyList<ScheduledEmailResult>> SendDue() => In<ScheduledEmailService, IReadOnlyList<ScheduledEmailResult>>(s => s.SendDueAsync());
        Task<IReadOnlyList<ScheduledEmail>> List(Invoice? i = null) => In<ScheduledEmailService, IReadOnlyList<ScheduledEmail>>(s => s.ListAsync(i?.Id));

        Assert.Equal("scheduled_email.in_past", await Refused(() => Schedule(invoice, now.AddMinutes(-1))));
        Assert.Equal("scheduled_email.too_far", await Refused(() => Schedule(invoice, now.AddYears(2))));
        Assert.Equal("email.address_invalid", await Refused(() => In<ScheduledEmailService, ScheduledEmail>(s =>
            s.ScheduleAsync(invoice.Id, new EmailDraft("not an address", "", ""), now.AddHours(1)))));

        var later = await Schedule(invoice, now.AddHours(2));
        var cancelled = await Schedule(invoice, now.AddHours(1));
        var forVoided = await Schedule(voided, now.AddHours(1));
        await In<ScheduledEmailService>(s => s.CancelAsync(cancelled.Id));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(voided.Id, "Entered twice"));

        // Nothing is due yet.
        Assert.Empty(await SendDue());
        Assert.Empty(_db.Mail.Sent);

        // An hour on: the voided invoice's email is cancelled, not sent; the cancelled one stays cancelled.
        _db.Clock.UtcNow = now.AddHours(1);
        var result = Assert.Single(await SendDue());
        Assert.Equal((forVoided.Id, "The invoice was voided."), (result.Email.Id, result.Error));
        Assert.Empty(_db.Mail.Sent);

        // Two hours on, with the mail server down: kept as failed with the reason.
        _db.Clock.UtcNow = now.AddHours(2);
        _db.Mail.Failure = "Connection refused";
        Assert.Contains("Connection refused", Assert.Single(await SendDue()).Error, StringComparison.Ordinal);
        var failed = Assert.Single(await List(invoice), e => e.Id == later.Id);
        Assert.Equal(ScheduledEmailStatus.Failed, failed.Status);
        Assert.Empty(await SendDue());

        // Tried again once the server is back: it goes with the invoice PDF and the invoice is marked emailed.
        _db.Mail.Failure = null;
        await In<ScheduledEmailService>(s => s.RetryAsync(later.Id));
        Assert.Null(Assert.Single(await SendDue()).Error);
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal(("Invoice 1001", "Attached."), (message.Subject, message.Body));
        Assert.Single(message.Attachments);
        Assert.NotNull((await Reload(invoice))!.LastEmailedAt);
        Assert.Empty(await SendDue());

        var states = (await List(invoice)).ToDictionary(e => e.Id, e => e.Status);
        Assert.Equal(ScheduledEmailStatus.Sent, states[later.Id]);
        Assert.Equal(ScheduledEmailStatus.Cancelled, states[cancelled.Id]);
        Assert.Equal(3, (await List()).Count);
        Assert.Equal("scheduled_email.not_pending", await Refused(() => In<ScheduledEmailService>(s => s.CancelAsync(later.Id))));
        Assert.Equal("scheduled_email.invoice_voided", await Refused(() => Schedule(voided, now.AddHours(5))));
        Assert.True(await _db.InDb(db => db.AuditRecords.CountAsync(a => a.EntityType == ScheduledEmailService.EntityType)) >= 8);
    }

    // ---- Reminders ----

    [Fact]
    public async Task Reminders_send_each_step_once_and_skip_steps_already_passed()
    {
        await UseEmail();
        await In<CollectionService, CollectionSettings>(s => s.SaveRemindersAsync(new ReminderSettingsCommand(true, "-3,7,30",
            "Invoice {invoice} reminder", "Dear {customer}, invoice {invoice} for {balance} {when}. {company}")));
        var store = await Customer("Store 12", _acme.Id, billWithParent: true);
        var quiet = await Customer("Quiet Ltd", email: "q@quiet.test", noReminders: true);
        var noEmail = await Customer("No Email Inc");
        var invoice = await Invoice(_acme, 2m);
        var storeInvoice = await Invoice(store, 1m);
        await Invoice(quiet, 1m);
        await Invoice(noEmail, 1m);

        var due = await In<ReminderService, IReadOnlyList<ReminderDue>>(s => s.DueAsync(Mar31.AddDays(-3)));
        Assert.Equal([(invoice.Id, -3, "ap@acme.test"), (storeInvoice.Id, -3, "ap@acme.test")], due.Select(d => (d.Invoice.Id, d.Step, d.To)));

        var sent = await In<ReminderService, IReadOnlyList<ReminderResult>>(s => s.SendAsync(Mar31.AddDays(-3), [invoice.Id]));
        Assert.Null(Assert.Single(sent).Error);
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal("Invoice 1001 reminder", message.Subject);
        Assert.StartsWith("Dear Acme Corp, invoice 1001 for 200.00 is due on", message.Body, StringComparison.Ordinal);
        Assert.Single(message.Attachments);
        Assert.Empty(await In<ReminderService, IReadOnlyList<ReminderResult>>(s => s.SendAsync(Mar31.AddDays(-3), [invoice.Id])));

        // The store's invoice missed its -3 and 7 day reminders; on day 30 only that one goes.
        _db.Mail.Sent.Clear();
        var day30 = await In<ReminderService, IReadOnlyList<ReminderResult>>(s => s.SendAsync(Mar31.AddDays(30)));
        Assert.Equal([(invoice.Id, 30), (storeInvoice.Id, 30)], day30.Select(r => (r.Invoice.Id, r.Step)));
        Assert.Contains("was due on", _db.Mail.Sent[0].Message.Body, StringComparison.Ordinal);
        Assert.Empty(await In<ReminderService, IReadOnlyList<ReminderDue>>(s => s.DueAsync(Mar31.AddDays(60))));
        Assert.Equal([-3, 30], (await In<ReminderService, IReadOnlyList<InvoiceReminder>>(s => s.ForInvoiceAsync(invoice.Id))).Select(r => r.Step));
    }

    [Fact]
    public async Task Reminders_stop_once_paid_and_report_send_failures()
    {
        await UseEmail();
        await In<CollectionService, CollectionSettings>(s => s.SaveRemindersAsync(new ReminderSettingsCommand(true, "7", null, null)));
        var paid = await Invoice(_acme, 1m);
        var unpaid = await Invoice(_acme, 2m);
        await Receive(_acme, Mar31, new ApplyToInvoice(paid.Id, 100m));

        _db.Mail.Failure = "Connection refused";
        var failed = Assert.Single(await In<ReminderService, IReadOnlyList<ReminderResult>>(s => s.SendAsync(Mar31.AddDays(7))));
        Assert.Equal(unpaid.Id, failed.Invoice.Id);
        Assert.Contains("Connection refused", failed.Error, StringComparison.Ordinal);

        _db.Mail.Failure = null;
        Assert.Null(Assert.Single(await In<ReminderService, IReadOnlyList<ReminderResult>>(s => s.SendAsync(Mar31.AddDays(8)))).Error);
    }

    [Fact]
    public async Task An_invoice_with_a_late_fee_on_it_cant_be_voided_until_the_fee_is()
    {
        var invoice = await Invoice(_acme, 1000m, Mar1);
        await In<CollectionService, CollectionSettings>(s => s.SaveLateFeesAsync(new LateFeeSettingsCommand(true, true, 0m, 25m, 0, 0)));
        var fee = Assert.Single(await In<LateFeeService, IReadOnlyList<Invoice>>(s => s.ChargeAsync(invoice.DueDate.AddDays(1))));

        Assert.Equal("invoice.has_late_fees", (await Assert.ThrowsAsync<DomainException>(() => In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong")))).Code);

        await In<InvoiceService, Invoice>(s => s.VoidAsync(fee.Id, "Waived"));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong"));
    }
}
