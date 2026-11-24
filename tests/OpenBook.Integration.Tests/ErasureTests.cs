using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Attachments;
using OpenBook.Application.CustomFields;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Lists;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Notes;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.CustomFields;
using OpenBook.Domain.Email;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Notes;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Erasing the personal details of a customer, donor or vendor: what goes, what stays, and when it's refused.</summary>
public sealed class ErasureTests : IAsyncLifetime
{
    private const string Gone = "(erased)";
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _undeposited = null!;
    private Account _bank = null!;
    private Account _supplies = null!;
    private Item _service = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _bank = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, sales.Id, 100m)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<DomainException> Refused(Func<Task> action) => await Assert.ThrowsAsync<DomainException>(action);

    private Task<Customer> Customer(CustomerDetails details) => In<CustomerService, Customer>(s => s.CreateAsync(details));

    private Task<Invoice> Invoice(Customer customer) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft(_service.Id, "Work", 1m, 100m)])));

    private Task<CustomerPayment> Pay(Invoice invoice) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(invoice.CustomerId, Mar1, _undeposited.Id, [new ApplyToInvoice(invoice.Id, 100m)])));

    private Task<Customer> Inactive(Customer customer) => In<CustomerService, Customer>(s => s.SetActiveAsync(customer.Id, false));

    private Task<ErasureResult> Erase(Customer customer) => In<ErasureService, ErasureResult>(s => s.EraseCustomerAsync(customer.Id, customer.DisplayName));

    private Task<EmailSettings> SetUpEmail() =>
        In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "pw", false, "books@studio.test", "Studio", null, "https://books.studio.test")));

    private async Task Email(Invoice invoice)
    {
        var source = new EmailSource(EmailKind.Invoice, invoice.Id);
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(source));
        await In<EmailService, SentEmail>(s => s.SendAsync(source, draft with { Cc = "sister@example.test" }));
    }

    private async Task<IReadOnlyList<string>> ErasureAudits(Guid id) =>
        await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityId == id && a.After!.Contains("PersonalDetailsErased")).Select(a => a.After!).ToListAsync());

    [Fact]
    public async Task A_customers_details_go_and_the_documents_stay()
    {
        await SetUpEmail();
        await In<TextMessageService, TextSettings>(s => s.SaveSettingsAsync(new TextSettingsCommand(FakeTwilio.AccountSid, FakeTwilio.AuthToken, FakeTwilio.Number)));
        var pat = await Customer(new CustomerDetails("Pat Giver", "Giver Ltd", "pat@example.test", "555-0100", "1 Elm St\nAustin, TX 78701", "2 Oak St\nAustin, TX 78701",
            Notes: "Rings on Mondays"));
        await In<EmailService, Customer>(s => s.SetCustomerPreferencesAsync(pat.Id, "accounts@example.test", EmailPdf.Attach));
        await In<TextMessageService, Customer>(s => s.SetCustomerAsync(pat.Id, "555-010-2030", allowed: true));
        var job = await Customer(new CustomerDetails("Kitchen", ParentId: pat.Id, BillWithParent: true));
        var kim = await Customer(new CustomerDetails("Kim Friend", Email: "kim@example.test"));

        var invoice = await Invoice(pat);
        var jobInvoice = await Invoice(job);
        var kims = await Invoice(kim);
        await Email(invoice);
        await Email(jobInvoice);
        await Email(kims);
        await In<TextMessageService, SentText>(s => s.SendAsync(new EmailSource(EmailKind.Invoice, invoice.Id), TextMessageService.Draft(EmailKind.Invoice)));
        await In<ScheduledEmailService, ScheduledEmail>(s => s.ScheduleAsync(invoice.Id, new EmailDraft("pat@example.test", "Dear Pat", "Hello Pat"), _db.Clock.UtcNow.AddHours(1)));
        await Pay(invoice);
        await Pay(jobInvoice);
        await In<NoteService, Note>(s => s.AddAsync("customer", pat.Id, "Likes tea"));
        await In<AttachmentService, Attachment>(s => s.AddAsync("customer", pat.Id, "passport.pdf", new MemoryStream("%PDF-1.4 passport"u8.ToArray())));
        await In<AttachmentService, Attachment>(s => s.AddAsync("invoice", invoice.Id, "order.pdf", new MemoryStream("%PDF-1.4 order"u8.ToArray())));
        var field = await In<CustomFieldService, CustomField>(s => s.CreateAsync(CustomFieldTarget.Customer, new CustomFieldDetails("Birthday", CustomFieldType.Text)));
        await In<CustomFieldService, IReadOnlyList<CustomFieldEntry>>(s => s.SaveAsync("customer", pat.Id, new Dictionary<Guid, string?> { [field.Id] = "1 May" }));
        var token = (await In<CustomerPortalService, (PortalAccess, string)>(s => s.CreateAsync(pat.Id))).Item2;
        await Inactive(job);
        await Inactive(pat);
        var opened = await In<IRecordVersions, int?>(v => v.GetAsync<Customer>(pat.Id));

        var result = await Erase(pat);

        var placeholder = $"Erased customer {$"{pat.Id:N}"[^6..].ToUpperInvariant()}";
        Assert.Equal(placeholder, result.Name);
        Assert.Equal(new ErasureCounts(Notes: 1, Files: 1, FieldValues: 1, Emails: 2, Texts: 1, ScheduledEmails: 1, PortalLinks: 1, DocumentLinks: result.Erased.DocumentLinks), result.Erased);
        Assert.True(result.Erased.DocumentLinks >= 1);
        var after = (await In<CustomerService, Customer?>(s => s.GetAsync(pat.Id)))!;
        Assert.Equal((placeholder, false), (after.DisplayName, after.IsActive));
        Assert.All(new[] { after.CompanyName, after.Email, after.EmailCc, after.Phone, after.TextNumber, after.BillingAddress, after.ShippingAddress, after.Notes }, v => Assert.Null(v));
        Assert.Null(after.TextsAllowedAt);
        Assert.Equal(_db.Clock.UtcNow, after.ErasedAt);
        Assert.NotEqual(opened, await In<IRecordVersions, int?>(v => v.GetAsync<Customer>(pat.Id)));

        // What's kept around it.
        Assert.Empty(await _db.InDb(db => db.Notes.Where(n => n.EntityId == pat.Id).ToListAsync()));
        Assert.Empty(await _db.InDb(db => db.CustomFieldValues.Where(v => v.EntityId == pat.Id).ToListAsync()));
        var file = Assert.Single(await _db.InDb(db => db.Attachments.AsNoTracking().ToListAsync()));
        Assert.Equal((invoice.Id, "order.pdf"), (file.EntityId, file.FileName));
        Assert.Equal(file.Id, Assert.Single(await _db.InDb(db => db.AttachmentContents.AsNoTracking().ToListAsync())).AttachmentId);
        Assert.Null(await In<CustomerPortalService, Customer?>(s => s.CustomerForAsync(token)));
        Assert.All(await _db.InDb(db => db.DocumentLinks.AsNoTracking().Where(l => l.CustomerId == pat.Id).ToListAsync()), l => Assert.NotNull(l.RevokedAt));

        // That they were sent stays, without to whom and what; another customer's are as they were.
        var emails = await _db.InDb(db => db.SentEmails.AsNoTracking().ToListAsync());
        Assert.Equal(3, emails.Count);
        foreach (var email in emails.Where(e => e.EntityId == invoice.Id || e.EntityId == jobInvoice.Id))
        {
            Assert.Equal((Gone, Gone, Gone, Gone), (email.To, email.Cc, email.Subject, email.Body));
            Assert.Null(email.Bcc);
            Assert.NotEqual(default(DateTimeOffset), email.SentAt);
            Assert.NotEmpty(email.SentBy);
        }

        Assert.Equal("kim@example.test", emails.Single(e => e.EntityId == kims.Id).To);
        var text = Assert.Single(await _db.InDb(db => db.SentTexts.AsNoTracking().ToListAsync()));
        Assert.Equal((Gone, Gone, (string?)null), (text.To, text.Body, text.ProviderId));
        var scheduled = Assert.Single(await _db.InDb(db => db.ScheduledEmails.AsNoTracking().ToListAsync()));
        Assert.Equal((Gone, Gone, Gone, ScheduledEmailStatus.Cancelled), (scheduled.To, scheduled.Subject, scheduled.Body, scheduled.Status));

        // The document, its printed address and the books.
        var kept = (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!;
        Assert.Equal((invoice.Number, 100m, pat.Id), (kept.Number, kept.Total, kept.CustomerId));
        Assert.Equal("1 Elm St\nAustin, TX 78701", kept.BillToAddress!.ReplaceLineEndings("\n"));
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);

        // One audit record, of counts only.
        var audit = Assert.Single(await ErasureAudits(pat.Id));
        Assert.Contains("\"Emails\":2", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("Pat", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_erased_record_takes_no_changes_and_cant_be_erased_twice()
    {
        var pat = await Customer(new CustomerDetails("Pat Giver", Email: "pat@example.test"));
        await Inactive(pat);
        Assert.Equal("erase.confirm", (await Refused(() => In<ErasureService, ErasureResult>(s => s.EraseCustomerAsync(pat.Id, "Pat")))).Code);
        Assert.Equal("Pat Giver", (await In<CustomerService, Customer?>(s => s.GetAsync(pat.Id)))!.DisplayName);

        var result = await Erase(pat);

        Assert.Equal("erase.already", (await Refused(() => In<ErasureService, ErasureResult>(s => s.EraseCustomerAsync(pat.Id, result.Name)))).Code);
        Assert.Equal("customer.erased", (await Refused(() => In<CustomerService, Customer>(s => s.UpdateAsync(pat.Id, new CustomerDetails("Pat Giver"))))).Code);
        Assert.Equal("customer.erased", (await Refused(() => In<CustomerService, Customer>(s => s.SetActiveAsync(pat.Id, true)))).Code);
        Assert.Equal("customer.erased", (await Refused(() => In<EmailService, Customer>(s => s.SetCustomerPreferencesAsync(pat.Id, "pat@example.test", EmailPdf.Attach)))).Code);
        Assert.Equal("customer.erased", (await Refused(() => In<TextMessageService, Customer>(s => s.SetCustomerAsync(pat.Id, "555-010-2030", allowed: true)))).Code);
        Assert.Equal("record.erased", (await Refused(() => In<NoteService, Note>(s => s.AddAsync("customer", pat.Id, "Pat again")))).Code);
        Assert.Equal("record.erased", (await Refused(() =>
            In<AttachmentService, Attachment>(s => s.AddAsync("customer", pat.Id, "a.pdf", new MemoryStream("%PDF-1.4 a"u8.ToArray()))))).Code);
        var kim = await Customer(new CustomerDetails("Kim Friend"));
        Assert.Equal("customer.erased", (await Refused(() => In<MergeService, MergeResult>(s => s.MergeCustomersAsync(pat.Id, kim.Id)))).Code);
        Assert.Single(await ErasureAudits(pat.Id));
    }

    [Fact]
    public async Task What_isnt_settled_is_named_and_nothing_goes()
    {
        var pat = await Customer(new CustomerDetails("Pat Giver", Email: "pat@example.test"));
        var sub = await Customer(new CustomerDetails("Kitchen", ParentId: pat.Id));
        var invoice = await Invoice(pat);
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(pat.Id, Mar1, [new SalesLineDraft(_service.Id, "Work", 1m, 100m)])));
        var order = await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(pat.Id, Mar1, [new SalesLineDraft(_service.Id, "Work", 1m, 100m)])));
        await In<GiftService, Pledge>(s => s.PledgeAsync(new PledgeCommand(pat.Id, Mar1, 500m)));

        var refused = await Refused(() => Erase(pat));
        Assert.Equal("erase.not_ready", refused.Code);
        foreach (var said in new[]
                 {
                     "still active", $"Unpaid invoices: {invoice.Number}.", $"Estimates still pending: {estimate.Number}.", $"Open sales orders: {order.Number}.",
                     "Still to come on pledges: 500.00.", $"Active sub-customers: {sub.DisplayName}.",
                 })
            Assert.Contains(said, refused.Message, StringComparison.Ordinal);
        Assert.Equal(6, (await In<ErasureService, ErasureReview?>(s => s.ReviewAsync(ErasureKind.Customer, pat.Id)))!.Blockers.Count);
        Assert.Equal("pat@example.test", (await In<CustomerService, Customer?>(s => s.GetAsync(pat.Id)))!.Email);

        // Money received and not used yet is theirs.
        var kim = await Customer(new CustomerDetails("Kim Friend"));
        var kims = await Invoice(kim);
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(kim.Id, Mar1, _undeposited.Id, [new ApplyToInvoice(kims.Id, 100m)], Amount: 140m)));
        await Inactive(kim);
        Assert.Contains("Credit not used yet: 40.00.", (await Refused(() => Erase(kim))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_donor_leaves_the_household_and_the_giving_page_forgets_them()
    {
        var pat = await Customer(new CustomerDetails("Pat Giver", Email: "Pat@example.test"));
        var sam = await Customer(new CustomerDetails("Sam Reed"));
        var household = await In<HouseholdService, Household>(s => s.CreateAsync(pat.Id, "Pat and Sam", "Dear Pat and Sam"));
        await In<HouseholdService, Household>(s => s.AddMemberAsync(household.Id, sam.Id));
        var gift = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(pat.Id, Mar1, 100m, _bank.Id)));
        await _db.InDb(async db =>
        {
            var paid = DonationCheckout.Create(Guid.CreateVersion7(), PaymentProvider.Stripe, "cs_1", "https://pay.test/1", 100m, "Pat", "pat@example.test", "For Mum", _db.Clock.UtcNow);
            paid.MarkPaid();
            paid.RecordGift(gift.Id);
            var givenUp = DonationCheckout.Create(Guid.CreateVersion7(), PaymentProvider.Stripe, "cs_2", "https://pay.test/2", 50m, "Pat", "pat@example.test", null, _db.Clock.UtcNow);
            givenUp.Expire();
            var kims = DonationCheckout.Create(Guid.CreateVersion7(), PaymentProvider.Stripe, "cs_3", "https://pay.test/3", 50m, "Kim", "kim@example.test", null, _db.Clock.UtcNow);
            kims.Expire();
            db.DonationCheckouts.AddRange(paid, givenUp, kims);
            return await db.SaveChangesAsync();
        });
        await Inactive(pat);

        var result = await Erase(pat);

        Assert.Equal((2, 1), (result.Erased.OnlineGifts, result.Erased.Households));
        var left = (await In<HouseholdService, HouseholdView?>(s => s.GetAsync(household.Id)))!;
        Assert.Equal(("Sam Reed household", (string?)null, sam.Id), (left.Household.Name, left.Household.Salutation, left.Household.PrimaryDonorId));
        Assert.Equal(sam.Id, Assert.Single(left.Members).Id);
        var checkouts = (await _db.InDb(db => db.DonationCheckouts.AsNoTracking().ToListAsync())).ToDictionary(c => c.SessionId);
        Assert.All(checkouts.Values.Where(c => c.SessionId != "cs_3"), c => Assert.Equal((Gone, Gone, (string?)null), (c.Name, c.Email, c.Note)));
        Assert.Equal(("Kim", "kim@example.test"), (checkouts["cs_3"].Name, checkouts["cs_3"].Email));
        var kept = (await In<GiftService, Donation?>(s => s.GetAsync(gift.Id)))!;
        Assert.Equal((100m, pat.Id), (kept.Amount, kept.DonorId));

        // The last one out: the household goes.
        await Inactive(sam);
        Assert.Equal(1, (await Erase(sam)).Erased.Households);
        Assert.Null(await In<HouseholdService, HouseholdView?>(s => s.GetAsync(household.Id)));
    }

    [Fact]
    public async Task A_vendor_is_erased_once_paid_and_no_longer_tracked_for_a_1099()
    {
        var details = new VendorDetails("Dana Builder", "Builder Co", "dana@example.test", "555-0101", "3 Ash St\nWaco, TX 76701", "ACCT-9", Notes: "Brother of Lee", Track1099: true, TaxId: "123-45-6789");
        var dana = await In<VendorService, Vendor>(s => s.CreateAsync(details));
        await In<AchService, VendorBankAccount>(s => s.SetVendorBankAccountAsync(dana.Id, new VendorBankAccountCommand("021000021", "123456789", BankAccountKind.Checking)));
        await In<NoteService, Note>(s => s.AddAsync("vendor", dana.Id, "Nice person"));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(dana.Id, Mar1, [new BillLineDraft(_supplies.Id, 50m, "Timber")], Reference: "B-7")));

        var refused = await Refused(() => In<ErasureService, ErasureResult>(s => s.EraseVendorAsync(dana.Id, "Dana Builder")));
        Assert.Equal("erase.not_ready", refused.Code);
        Assert.Contains("1099", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Unpaid bills: B-7.", refused.Message, StringComparison.Ordinal);

        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Mar1, _bank.Id, [new ApplyToBill(bill.Id, 50m)])));
        await In<VendorService, Vendor>(s => s.SetActiveAsync(dana.Id, false));
        Assert.Equal("erase.not_ready", (await Refused(() => In<ErasureService, ErasureResult>(s => s.EraseVendorAsync(dana.Id, "Dana Builder")))).Code);
        await In<VendorService, Vendor>(s => s.UpdateAsync(dana.Id, details with { Track1099 = false }));

        var result = await In<ErasureService, ErasureResult>(s => s.EraseVendorAsync(dana.Id, "dana builder"));

        Assert.StartsWith("Erased vendor ", result.Name, StringComparison.Ordinal);
        Assert.Equal((1, 1), (result.Erased.BankAccounts, result.Erased.Notes));
        var after = (await In<VendorService, Vendor?>(s => s.GetAsync(dana.Id)))!;
        Assert.Equal(result.Name, after.DisplayName);
        Assert.All(new[] { after.CompanyName, after.Email, after.Phone, after.Address, after.AccountNumber, after.Notes, after.TaxId }, v => Assert.Null(v));
        Assert.NotNull(after.ErasedAt);
        Assert.Empty(await _db.InDb(db => db.VendorBankAccounts.ToListAsync()));
        Assert.Equal((50m, dana.Id), ((await In<BillService, Bill?>(s => s.GetAsync(bill.Id)))!.Total, bill.VendorId));
        Assert.Equal(0, (await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync())).Problems);

        Assert.Equal("vendor.erased", (await Refused(() => In<VendorService, Vendor>(s => s.UpdateAsync(dana.Id, details)))).Code);
        Assert.Equal("vendor.erased", (await Refused(() => In<VendorService, Vendor>(s => s.SetActiveAsync(dana.Id, true)))).Code);
        Assert.Equal("vendor.erased", (await Refused(() =>
            In<AchService, VendorBankAccount>(s => s.SetVendorBankAccountAsync(dana.Id, new VendorBankAccountCommand("021000021", "123456789", BankAccountKind.Checking))))).Code);
        Assert.Equal("erase.already", (await Refused(() => In<ErasureService, ErasureResult>(s => s.EraseVendorAsync(dana.Id, result.Name)))).Code);
        var audit = Assert.Single(await ErasureAudits(dana.Id));
        Assert.DoesNotContain("Dana", audit, StringComparison.OrdinalIgnoreCase);
    }
}
