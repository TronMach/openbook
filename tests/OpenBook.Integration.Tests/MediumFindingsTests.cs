using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Assets;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Application.Segments;
using OpenBook.Application.Webhooks;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Email;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>The Medium findings of the November audit marked "do first".</summary>
public sealed class MediumFindingsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _office = null!;
    private Account _liability = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;
    private Vendor _staples = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _office = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6300");
        _liability = await _db.CreateAccount("Bank Loan", AccountType.LongTermLiability, "2500");
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", Email: "ap@acme.test")));
        _staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(decimal hours = 2m) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private Task<Bill> Bill(decimal amount, string reference) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_staples.Id, Mar1, [new BillLineDraft(_office.Id, amount)], Reference: reference)));

    private Task<PurchasingSettings> HoldBillsFrom(decimal threshold) =>
        In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(threshold, 0m, false, false)));

    private Task<EmailSettings> SetUpEmail() =>
        In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "pw", false, "books@studio.test", "Studio", null, null)));

    private async Task<BankFeedItem> FeedLine(decimal amount)
    {
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, amount, "Staples", null, null, $"fit-{Guid.NewGuid():N}")]));
        return (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending)))[^1].Item;
    }

    private async Task<BankFeedStatus> StatusOf(Guid item) => await _db.InDb(db => db.BankFeedItems.Where(i => i.Id == item).Select(i => i.Status).SingleAsync());

    [Theory]
    [InlineData("2% 10 Net 30", 30, 2, 10)]
    [InlineData("1.5% 15 Net 45", 45, 1.5, 15)]
    [InlineData("Net 60", 60, 0, 0)]
    [InlineData("Due on receipt", 0, 0, 0)]
    [InlineData("Consignment", 30, 0, 0)]
    public async Task Customer_terms_import_as_QuickBooks_writes_them(string terms, int due, double percent, int days)
    {
        await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.ImportAsync($"Name,Terms\nNew Co,{terms}\n"));

        var customer = await _db.InDb(db => db.Customers.AsNoTracking().SingleAsync(c => c.DisplayName == "New Co"));
        Assert.Equal(new PaymentTerms(due, (decimal)percent, days), customer.Terms);
    }

    [Fact]
    public async Task A_statement_with_details_before_description_keeps_the_real_payee()
    {
        const string chase = "Details,Posting Date,Description,Amount\nDEBIT,03/05/2026,STAPLES 1234,-20.00\n";

        var row = (await In<BankFeedService, BankImportPreview>(s => s.PreviewAsync(_checking.Id, chase, new BankImportOptions()))).Rows.Single();

        Assert.Equal("STAPLES 1234", row.Payee);
        Assert.Equal("DEBIT", row.Memo);
    }

    [Fact]
    public async Task A_two_digit_year_is_refused()
    {
        var code = await Refused(() => In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(
            new DateOnly(26, 3, 1), [JournalLineDraft.Dr(_office.Id, 5m), JournalLineDraft.Cr(_checking.Id, 5m)]))));

        Assert.Equal("journal.date_too_early", code);
    }

    [Fact]
    public void A_combined_code_of_zero_percent_rates_charges_nothing()
    {
        TaxComponent Zero(string name) => new(Guid.NewGuid(), name, 0m, Guid.NewGuid());

        Assert.Equal([0m, 0m], TaxCalculator.Split(100m, [Zero("State"), Zero("County")], exempt: false));
    }

    [Fact]
    public async Task Two_people_applying_one_credit_at_once_conflict()
    {
        var memo = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, 1m, 100m)])));
        var first = await Invoice();
        var second = await Invoice();

        await _db.InDb(async slow =>
        {
            var stale = await slow.CreditMemos.SingleAsync(c => c.Id == memo.Id);
            await _db.InDb(async quick =>
            {
                (await quick.CreditMemos.SingleAsync(c => c.Id == memo.Id)).Apply(first.Id, 100m, Mar5);
                return await quick.SaveChangesAsync();
            });

            stale.Apply(second.Id, 100m, Mar5);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => slow.SaveChangesAsync());
            return true;
        });
    }

    [Fact]
    public async Task A_held_bills_payment_can_be_corrected_but_not_raised()
    {
        var bill = await Bill(100m, "A");
        var payment = (await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Mar5, _checking.Id, [new ApplyToBill(bill.Id, 10m)])))).Single();
        await HoldBillsFrom(50m);
        await In<BillService, Bill>(s => s.ChangeAsync(bill.Id, new ChangeBillCommand(Mar1, [new BillLineDraft(_office.Id, 5_000m)], "Wrong amount", Reference: "A")));

        var code = await Refused(() => In<BillPaymentService, BillPayment>(s => s.ChangeAsync(payment.Id,
            new ChangeBillPaymentCommand(Mar5, [new ApplyToBill(bill.Id, 5_000m)], "Pay it all"))));
        Assert.Equal("bill.not_approved", code);

        await In<BillPaymentService, BillPayment>(s => s.ChangeAsync(payment.Id, new ChangeBillPaymentCommand(Mar5, [new ApplyToBill(bill.Id, 8m)], "Paid less")));
    }

    [Fact]
    public async Task A_vendor_credit_cant_settle_a_held_bill()
    {
        await HoldBillsFrom(50m);
        var bill = await Bill(100m, "A");
        var credit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_staples.Id, Mar1, [new BillLineDraft(_office.Id, 10m)])));

        var code = await Refused(() => _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<VendorCreditService>().ApplyAsync(_staples.Id, Mar5, [new ApplyVendorCredit(credit.Id, bill.Id, 10m)]);
            return true;
        }));

        Assert.Equal("bill.not_approved", code);
    }

    [Fact]
    public async Task Voiding_what_a_feed_line_stands_for_puts_the_line_back()
    {
        var added = await FeedLine(-20m);
        var transaction = await In<BankFeedService, BankTransaction>(s => s.AddAsync(added.Id, new AddFeedItemCommand(_office.Id)));
        await In<BankingService, BankTransaction>(s => s.VoidAsync(transaction.Id, "Wrong account"));
        Assert.Equal(BankFeedStatus.Pending, await StatusOf(added.Id));

        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar5, [new BankSplit(_office.Id, 30m)], "Staples")));
        var matched = await FeedLine(-30m);
        await _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<BankFeedService>().MatchAsync(matched.Id, spend.JournalEntryId);
            return true;
        });
        Assert.Equal(BankFeedStatus.Matched, await StatusOf(matched.Id));

        await In<BankingService, BankTransaction>(s => s.VoidAsync(spend.Id, "Duplicate"));
        Assert.Equal(BankFeedStatus.Pending, await StatusOf(matched.Id));
    }

    [Fact]
    public async Task Undoing_an_added_feed_line_still_works()
    {
        var line = await FeedLine(-20m);
        var transaction = await In<BankFeedService, BankTransaction>(s => s.AddAsync(line.Id, new AddFeedItemCommand(_office.Id)));

        await _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<BankFeedService>().ReopenAsync(line.Id);
            return true;
        });

        Assert.Equal(BankFeedStatus.Pending, await StatusOf(line.Id));
        Assert.Equal(BankTransactionStatus.Voided, (await In<BankingService, BankTransaction?>(s => s.GetAsync(transaction.Id)))!.Status);
    }

    [Fact]
    public async Task Positive_pay_leaves_out_bank_lines_and_the_next_check_ignores_them()
    {
        var line = await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(Mar5, -20m, "Debit", "4471902", null, "fit-1")]));
        Assert.Equal(1, line.Queued);
        var item = (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending))).Single().Item;
        await In<BankFeedService, BankTransaction>(s => s.AddAsync(item.Id, new AddFeedItemCommand(_office.Id)));

        Assert.Empty(await In<PositivePayService, IReadOnlyList<PositivePayCheck>>(s => s.ListAsync(_checking.Id, Mar1, Mar5)));
        Assert.Null(await In<BankingService, string?>(s => s.SuggestCheckNumberAsync(_checking.Id)));
    }

    [Fact]
    public async Task The_first_positive_pay_file_leaves_out_long_cleared_checks()
    {
        var today = CompanyTime.Today;
        Task<BankTransaction> Check(DateOnly date, string number) => In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, date, [new BankSplit(_office.Id, 30m)], "Staples", number)));
        await Check(today.AddDays(-400), "101");
        await Check(today.AddDays(-5), "102");

        var unsent = await In<PositivePayService, IReadOnlyList<PositivePayCheck>>(s => s.UnsentAsync(_checking.Id));

        Assert.Equal("102", Assert.Single(unsent).Number);
    }

    [Fact]
    public void Only_currencies_with_cents_can_go_through_a_card_processor()
    {
        Assert.True(new Currency("USD").HasCents);
        Assert.False(new Currency("JPY").HasCents);
        Assert.False(new Currency("KWD").HasCents);
    }

    [Fact]
    public async Task Sending_an_email_doesnt_conflict_with_someone_changing_the_document()
    {
        await SetUpEmail();
        var invoice = await Invoice();
        var source = new EmailSource(EmailKind.Invoice, invoice.Id);

        await _db.InDb(async editing =>
        {
            var open = await editing.Invoices.SingleAsync(i => i.Id == invoice.Id);
            var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(source));
            await In<EmailService, SentEmail>(s => s.SendAsync(source, draft));

            open.SetLocation(null);
            open.MarkEmailed(DateTimeOffset.UnixEpoch);
            return await editing.SaveChangesAsync();
        });

        Assert.Single(_db.Mail.Sent);
    }

    [Fact]
    public async Task The_same_email_cant_be_scheduled_twice_for_one_time()
    {
        var invoice = await Invoice();
        var when = DateTimeOffset.UtcNow.AddDays(1);
        var message = new EmailDraft("ap@acme.test", "Invoice", "Here it is");
        await In<ScheduledEmailService, ScheduledEmail>(s => s.ScheduleAsync(invoice.Id, message, when));

        Assert.Equal("scheduled_email.duplicate", await Refused(() => In<ScheduledEmailService, ScheduledEmail>(s => s.ScheduleAsync(invoice.Id, message, when))));
    }

    [Fact]
    public async Task A_late_fee_is_unique_per_invoice_and_day()
    {
        var unique = await _db.InDb(db => Task.FromResult(db.Model.FindEntityType(typeof(Invoice))!.GetIndexes()
            .Any(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["LateFeeForInvoiceId", "LateFeeOn"]))));

        Assert.True(unique);
    }

    [Fact]
    public async Task A_loan_entered_by_mistake_can_be_voided_and_renamed_after_payments()
    {
        var loan = await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Van loan", _liability.Id, Guid.Empty, 10_000m, 6m, 12, new DateOnly(2026, 2, 1)), _checking.Id, Mar1)));
        await In<LoanService, LoanPayment>(s => s.PayAsync(loan.Id, new LoanPaymentCommand(Mar5, _checking.Id)));

        Assert.Equal("Truck loan", (await In<LoanService, Loan>(s => s.DescribeAsync(loan.Id, "Truck loan", "Fixed the name"))).Name);
        Assert.Equal("loan.has_payments", await Refused(() => In<LoanService, Loan>(s => s.VoidAsync(loan.Id, "Mistake"))));

        var payment = (await In<LoanService, Loan?>(s => s.GetAsync(loan.Id)))!.PaymentsMade.Single();
        await _db.InScope(sp => sp.GetRequiredService<LoanService>().VoidPaymentAsync(loan.Id, payment.Id, "Mistake"));
        var voided = await In<LoanService, Loan>(s => s.VoidAsync(loan.Id, "Mistake"));

        Assert.True(voided.IsVoided);
        Assert.Equal(0m, voided.Balance);
        Assert.Empty(await In<LoanService, IReadOnlyList<Loan>>(s => s.ListAsync()));
        Assert.Equal(JournalEntryStatus.Voided, await _db.InDb(db => db.JournalEntries.Where(e => e.Id == voided.ProceedsEntryId).Select(e => e.Status).SingleAsync()));
    }

    [Fact]
    public async Task Voided_documents_say_so_and_cant_be_emailed()
    {
        await SetUpEmail();
        var invoice = await Invoice();
        var memo = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, 1m, 100m)])));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong customer"));
        await In<CreditService, CreditMemo>(s => s.VoidCreditMemoAsync(memo.Id, "Wrong customer"));

        var source = new EmailSource(EmailKind.Invoice, invoice.Id);
        var code = await Refused(() => In<EmailService, SentEmail>(s => s.SendAsync(source, new EmailDraft("ap@acme.test", "Invoice", "Here it is"))));
        Assert.Equal("document.voided", code);

        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.CreditMemo, memo.Id));
        Assert.Equal("Credit memo (void)", form.Title);
        Assert.DoesNotContain(form.Totals, t => t.Label is "Used" or "Credit remaining");
    }

    [Fact]
    public void A_crafted_line_in_an_uploaded_file_is_read_in_moments()
    {
        var text = "invoice " + new string('1', 1_000_000) + "_\n";
        var watch = Stopwatch.StartNew();

        DocumentParser.Parse(text, DocumentKind.VendorInvoice, []);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Wrong_passwords_shut_out_the_address_not_the_person()
    {
        Task<SignInResult> SignIn(string password, string address) => In<AuthService, SignInResult>(s => s.SignInAsync(LedgerDatabase.OwnerUsername, password, address));
        for (var i = 0; i < User.MaxFailedLogins; i++)
            await SignIn("wrong password", "203.0.113.9");

        Assert.Equal(SignInStatus.LockedOut, (await SignIn(LedgerDatabase.OwnerPassword, "203.0.113.9")).Status);
        Assert.Equal(SignInStatus.Succeeded, (await SignIn(LedgerDatabase.OwnerPassword, "198.51.100.1")).Status);
    }

    [Fact]
    public async Task An_invitations_link_stays_out_of_the_history()
    {
        await SetUpEmail();
        var sent = await In<InviteService, SentInvite>(s => s.InviteAsync(new InviteCommand("pat@studio.test", UserRole.Standard, "Pat", null, SendEmail: true), "https://books.studio.test"));

        Assert.Contains(sent.Link, Assert.Single(_db.Mail.Sent).Message.Body, StringComparison.Ordinal);
        var kept = await _db.InDb(db => db.SentEmails.AsNoTracking().SingleAsync(e => e.Kind == EmailKind.Invitation));
        Assert.DoesNotContain("obi_", kept.Body, StringComparison.Ordinal);
        Assert.Contains("(link removed)", kept.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Webhooks_wait_for_a_gap_in_the_audit_ids_until_its_old()
    {
        var now = DateTimeOffset.UtcNow;
        AuditRecord Record(long id, DateTimeOffset at)
        {
            var record = AuditRecord.Create(at, "test", AuditAction.Created, "invoice", Guid.NewGuid(), null, "{}");
            typeof(AuditRecord).GetProperty(nameof(AuditRecord.Id))!.SetValue(record, id);
            return record;
        }

        long[] Ids(params AuditRecord[] records) => [.. WebhookDispatcher.Settled(10, records, now).Select(r => r.Id)];

        Assert.Equal([11L, 12L], Ids(Record(11, now), Record(12, now), Record(14, now)));
        Assert.Equal([11L, 14L], Ids(Record(11, now), Record(14, now - WebhookDispatcher.GapWait)));
    }

    [Fact]
    public async Task A_rate_that_has_charged_tax_stays_with_its_agency()
    {
        var state = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("State")));
        var city = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("City")));
        var rate = await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("Sales tax", state.Id, 5m), createCode: false));
        var code = await In<SalesTaxService, TaxCode>(s => s.CreateCodeAsync(new TaxCodeDetails("Sales tax", [rate.Id], null)));

        await In<SalesTaxService, TaxRate>(s => s.UpdateRateAsync(rate.Id, new TaxRateDetails("Sales tax", city.Id, 5m)));
        await In<SalesTaxService, TaxRate>(s => s.UpdateRateAsync(rate.Id, new TaxRateDetails("Sales tax", state.Id, 5m)));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, 1m, 100m, Taxable: true)], TaxCodeId: code.Id)));

        Assert.Equal("tax.rate_charged", await Refused(() => In<SalesTaxService, TaxRate>(s => s.UpdateRateAsync(rate.Id, new TaxRateDetails("Sales tax", city.Id, 5m)))));
        await In<SalesTaxService, TaxRate>(s => s.UpdateRateAsync(rate.Id, new TaxRateDetails("State sales tax", state.Id, 5m)));
    }

    [Fact]
    public async Task A_grant_turns_class_tracking_on()
    {
        var funder = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Foundation")));
        Assert.False((await In<SegmentService, SegmentTracking>(s => s.GetTrackingAsync())).Classes);

        await In<GrantService, Grant>(s => s.CreateAsync(new GrantCommand("Literacy", funder.Id, 1000m, Mar1, Mar1, Mar5.AddYears(1))));

        Assert.True((await In<SegmentService, SegmentTracking>(s => s.GetTrackingAsync())).Classes);
    }

    [Fact]
    public async Task A_discount_waits_for_an_invoice_with_items_above_it()
    {
        var order = await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_acme.Id, Mar1,
        [
            new SalesLineDraft(_consulting.Id, null, 1m, 100m),
            new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent),
            new SalesLineDraft(_consulting.Id, null, 2m, 100m),
            new SalesLineDraft(null, "Rebate", 1m, 20m, SalesLineKind.DiscountAmount),
            new SalesLineDraft(_consulting.Id, null, 3m, 100m),
        ])));

        // The first item: both discounts have it above them, and the flat one goes whole.
        var first = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1, [new InvoiceSourceLine(1, 1m)])));
        Assert.Equal(70m, first.Total);

        // The last item alone has no discount above it on this invoice.
        var second = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1, [new InvoiceSourceLine(5, 3m)])));
        Assert.Equal(300m, second.Total);

        var rest = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1)));
        Assert.Equal(200m, rest.Total); // Adds up to the order: 570
    }

    [Fact]
    public async Task A_flat_discount_above_the_first_item_billed_waits()
    {
        var order = await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_acme.Id, Mar1,
        [
            new SalesLineDraft(_consulting.Id, null, 1m, 100m),
            new SalesLineDraft(null, "Rebate", 1m, 20m, SalesLineKind.DiscountAmount),
            new SalesLineDraft(_consulting.Id, null, 2m, 100m),
        ])));

        var first = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1, [new InvoiceSourceLine(3, 2m)])));
        Assert.Equal(200m, first.Total);

        var rest = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Mar1)));
        Assert.Equal(80m, rest.Total);
        Assert.Equal(SalesOrderStatus.Closed, (await In<SalesOrderService, SalesOrder?>(s => s.GetAsync(order.Id)))!.Status);
    }

    [Fact]
    public void A_gnucash_file_whose_accounts_are_each_others_parents_doesnt_hang()
    {
        var book = Gnucash(
            """
            <gnc:account version="2.0.0"><act:name>A</act:name><act:id type="guid">a</act:id><act:type>BANK</act:type><act:parent type="guid">b</act:parent></gnc:account>
            <gnc:account version="2.0.0"><act:name>B</act:name><act:id type="guid">b</act:id><act:type>BANK</act:type><act:parent type="guid">a</act:parent></gnc:account>
            """, "");

        var data = OpenBook.Application.Import.Migration.MigrationReaders.Read(OpenBook.Application.Import.Migration.MigrationSource.GnuCash, book, "USD");

        Assert.Equal(2, data.Accounts.Count);
    }

    [Theory]
    [InlineData(-1000, true)]
    [InlineData(-900, false)]
    public void Gnucash_trading_lines_come_out_when_they_cancel(int tradingUsd, bool clean)
    {
        var book = Gnucash(
            """
            <gnc:account version="2.0.0"><act:name>Checking</act:name><act:id type="guid">c</act:id><act:type>BANK</act:type></gnc:account>
            <gnc:account version="2.0.0"><act:name>Shares</act:name><act:id type="guid">s</act:id><act:type>ASSET</act:type></gnc:account>
            <gnc:account version="2.0.0"><act:name>Trading</act:name><act:id type="guid">t</act:id><act:type>TRADING</act:type></gnc:account>
            """,
            $"""
            <gnc:transaction version="2.0.0">
              <trn:id type="guid">t1</trn:id>
              <trn:currency><cmdty:space>CURRENCY</cmdty:space><cmdty:id>USD</cmdty:id></trn:currency>
              <trn:date-posted><ts:date>2026-03-10 10:59:00 +0000</ts:date></trn:date-posted>
              <trn:description>Shares</trn:description>
              <trn:splits>
                <trn:split><split:value>-1000/1</split:value><split:account type="guid">c</split:account></trn:split>
                <trn:split><split:value>1000/1</split:value><split:account type="guid">s</split:account></trn:split>
                <trn:split><split:value>{tradingUsd}/1</split:value><split:account type="guid">t</split:account></trn:split>
                <trn:split><split:value>1000/1</split:value><split:account type="guid">t</split:account></trn:split>
              </trn:splits>
            </gnc:transaction>
            """);

        var data = OpenBook.Application.Import.Migration.MigrationReaders.Read(OpenBook.Application.Import.Migration.MigrationSource.GnuCash, book, "USD");

        var transaction = Assert.Single(data.Transactions);
        Assert.Equal(2, transaction.Lines.Count);
        Assert.Equal(clean, (transaction.Errors ?? []).Count == 0);
    }

    private static byte[] Gnucash(string accounts, string transactions) => System.Text.Encoding.UTF8.GetBytes($"""
        <gnc-v2 xmlns:gnc="http://www.gnucash.org/XML/gnc" xmlns:act="http://www.gnucash.org/XML/act" xmlns:book="http://www.gnucash.org/XML/book"
                xmlns:cmdty="http://www.gnucash.org/XML/cmdty" xmlns:trn="http://www.gnucash.org/XML/trn" xmlns:split="http://www.gnucash.org/XML/split"
                xmlns:ts="http://www.gnucash.org/XML/ts">
        <gnc:book version="2.0.0">
          <gnc:account version="2.0.0"><act:name>Root Account</act:name><act:id type="guid">root</act:id><act:type>ROOT</act:type></gnc:account>
          {accounts}
          {transactions}
        </gnc:book>
        </gnc-v2>
        """);

    [Fact]
    public async Task Billable_lines_arent_savings()
    {
        var billed = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1,
            [new SalesLineDraft(_consulting.Id, null, 10m, 80m, PriceSource: SalesLine.BillableTimePrice)])));
        var priced = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1,
            [new SalesLineDraft(_consulting.Id, null, 1m, 80m, PriceSource: "Sale")])));

        Assert.Equal(0m, FormService.Savings(billed.Lines));
        Assert.Equal(20m, FormService.Savings(priced.Lines));
    }

    [Fact]
    public async Task A_gifts_payment_method_reaches_the_deposit()
    {
        var holding = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var donor = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Donor")));

        await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(donor.Id, Mar1, 50m, holding.Id, Reference: "1001", Method: PaymentMethod.Check)));

        var item = Assert.Single(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()));
        Assert.Equal(PaymentMethod.Check, item.Method);
    }

    [Fact]
    public async Task Memos_and_void_reasons_past_their_limits_are_refused_on_either_database()
    {
        var long1 = new string('x', 1001);
        var cash = JournalLineDraft.Dr(_checking.Id, 10m);
        var loan = JournalLineDraft.Cr(_liability.Id, 10m);

        Assert.Equal("journal.memo_too_long", await Refused(() => In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Mar1, [cash, loan], long1)))));

        var entry = await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Mar1, [cash, loan], new string('m', 1000), ReverseOn: Mar5)));
        Assert.Equal("journal.void_reason_too_long", await Refused(() => In<PostingEngine, JournalEntry>(s => s.VoidAsync(entry.Id, new string('r', 501)))));
    }

    private sealed class StripeAnswers(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer());
    }

    [Fact]
    public async Task A_failing_stripe_is_a_refusal_every_caller_understands()
    {
        Func<HttpResponseMessage>[] failures =
        [
            () => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = new StringContent("<html>Bad gateway</html>") },
            () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") },
            () => throw new HttpRequestException("No route to host"),
            () => throw new TaskCanceledException("Timed out"),
        ];

        foreach (var failure in failures)
        {
            using var gateway = new OpenBook.Infrastructure.Payments.StripeGateway(new StripeAnswers(failure));
            Assert.Equal("online.provider_unreachable", await Refused(() => gateway.GetCheckoutAsync("sk_test", "cs_1", default)));
        }
    }

    [Fact]
    public void Files_from_windows_keep_their_characters()
    {
        byte[] windows = [0x43, 0x61, 0x66, 0xE9, 0x92, 0x73];
        Assert.Equal("Café’s", OpenBook.Application.Import.TextFiles.Decode(windows));
        Assert.Equal("Café", OpenBook.Application.Import.TextFiles.Decode(System.Text.Encoding.UTF8.GetBytes("Café")));
        Assert.Equal("Café", OpenBook.Application.Import.TextFiles.Decode(System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("Café")).ToArray()));
    }

    [Fact]
    public void A_report_in_another_currency_is_refused()
    {
        static string Report(params string[] lines) => string.Join(Environment.NewLine, lines);
        OpenBook.Application.Channels.ChannelCurrency.Check(Report("Date,Currency,Amount", "2026-03-01,USD,10"), "USD");
        OpenBook.Application.Channels.ChannelCurrency.Check(Report("Date,Amount", "2026-03-01,10"), "USD");

        var refused = Assert.Throws<DomainException>(() => OpenBook.Application.Channels.ChannelCurrency.Check(Report("Some report", "", "Date,Transaction currency,Amount", "2026-03-01,CAD,10"), "USD"));

        Assert.Equal("channel.other_currency", refused.Code);
    }
}
