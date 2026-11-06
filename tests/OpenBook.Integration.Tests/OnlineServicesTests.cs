using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Attachments;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L3 slice 4: online payment links (Stripe), the customer portal and receipt capture.</summary>
public sealed class OnlineServicesTests : IAsyncLifetime
{
    private const string WebhookSecret = "whsec_test";
    private static readonly DateOnly Jan10 = new(2026, 1, 10);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, services.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(decimal hours, Customer? customer = null) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand((customer ?? _acme).Id, Jan10, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private Task<OnlinePaymentSettings> TurnOnPayments() =>
        In<OnlinePaymentService, OnlinePaymentSettings>(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
            true, _undeposited.Id, "https://books.studio.test/", FakeStripe.SecretKey, WebhookSecret)));

    private Task<WebhookOutcome> Webhook(string payload, DateTimeOffset? signedAt = null, string secret = WebhookSecret) =>
        In<OnlinePaymentService, WebhookOutcome>(s => s.HandleWebhookAsync(payload, StripeSignature.Sign(payload, secret, signedAt ?? _db.Clock.UtcNow)));

    // ---- Online payments ----

    [Fact]
    public async Task Paying_a_checkout_link_records_the_payment_once()
    {
        var invoice = await Invoice(3m);
        Assert.Equal("online.not_set_up", await Refused(() => In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id))));
        Assert.Equal("online.incomplete", await Refused(() => In<OnlinePaymentService, OnlinePaymentSettings>(s =>
            s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, "https://books.studio.test")))));
        var settings = await TurnOnPayments();
        Assert.Equal(("https://books.studio.test", "protected:sk_test_fake"), (settings.PublicUrl, settings.ProtectedSecretKey));
        Assert.DoesNotContain(await _db.InDb(db => db.AuditRecords.AsNoTracking().Select(a => a.After).ToListAsync()), a => a != null && a.Contains("sk_test", StringComparison.Ordinal));

        var link = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id));
        Assert.Equal((300m, PaymentLinkStatus.Open), (link.Amount, link.Status));
        var sent = Assert.Single(_db.Stripe.Created);
        Assert.Equal(("30000", "usd", "ap@acme.test"), (sent["line_items[0][price_data][unit_amount]"], sent["line_items[0][price_data][currency]"], sent["customer_email"]));
        Assert.Equal($"https://books.studio.test/pay/done?invoice={invoice.Id}", sent["success_url"]);
        // Asking again for the same balance reuses the link.
        Assert.Equal(link.Id, (await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id))).Id);

        Assert.Equal("online.bad_signature", await Refused(() => Webhook(_db.Stripe.Event("checkout.session.completed", link.SessionId), secret: "whsec_wrong")));
        Assert.Equal("online.bad_signature", await Refused(() => Webhook(_db.Stripe.Event("checkout.session.completed", link.SessionId), _db.Clock.UtcNow.AddMinutes(-10))));

        _db.Stripe.Pay(link.SessionId);
        var paid = _db.Stripe.Event("checkout.session.completed", link.SessionId);
        Assert.Equal(WebhookOutcome.Handled, await Webhook(paid));
        Assert.Equal(WebhookOutcome.Ignored, await Webhook(paid));

        Assert.Equal(0m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Balance);
        var payment = Assert.Single(await In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id)));
        Assert.Equal((300m, _undeposited.Id, "pi_" + link.SessionId[3..]), (payment.Amount, payment.DepositAccountId, payment.Reference));
        var recorded = Assert.Single(await In<OnlinePaymentService, IReadOnlyList<PaymentLink>>(s => s.LinksAsync(invoice.Id)));
        Assert.Equal((PaymentLinkStatus.Paid, payment.Id), (recorded.Status, recorded.CustomerPaymentId));
        Assert.Equal("online.nothing_to_pay", await Refused(() => In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id))));
    }

    [Fact]
    public async Task Checking_a_link_records_a_payment_the_webhook_missed()
    {
        await TurnOnPayments();
        var invoice = await Invoice(2m);
        var link = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id));
        Assert.Equal(PaymentLinkStatus.Open, (await In<OnlinePaymentService, PaymentLink>(s => s.CheckAsync(link.Id))).Status);

        _db.Stripe.Pay(link.SessionId);
        Assert.Equal(PaymentLinkStatus.Paid, (await In<OnlinePaymentService, PaymentLink>(s => s.CheckAsync(link.Id))).Status);
        Assert.Equal(PaymentLinkStatus.Paid, (await In<OnlinePaymentService, PaymentLink>(s => s.CheckAsync(link.Id))).Status);
        Assert.Single(await In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id)));
    }

    private async Task<PaymentLink> LinkAgain(PaymentLink link) =>
        (await In<OnlinePaymentService, IReadOnlyList<PaymentLink>>(s => s.LinksAsync(link.InvoiceId))).Single(l => l.Id == link.Id);

    private Task<string?> CloseOutdated() => In<OnlinePaymentService, string?>(s => s.ExpireOutdatedAsync());

    [Fact]
    public async Task A_link_closes_once_the_invoice_no_longer_owes_its_amount()
    {
        await TurnOnPayments();
        var paid = await Invoice(3m);
        var partly = await Invoice(2m);
        var voided = await Invoice(1m);
        var untouched = await Invoice(4m);
        var paidLink = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(paid.Id));
        var partlyLink = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(partly.Id));
        var voidedLink = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(voided.Id));
        var standing = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(untouched.Id));

        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(paid.Id, 300m)])));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(partly.Id, 50m)])));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(voided.Id, "Wrong customer"));

        Assert.Null(await CloseOutdated());

        foreach (var link in new[] { paidLink, partlyLink, voidedLink })
        {
            Assert.Equal(PaymentLinkStatus.Expired, (await LinkAgain(link)).Status);
            Assert.True(_db.Stripe.IsExpired(link.SessionId));
        }

        Assert.Equal(PaymentLinkStatus.Open, (await LinkAgain(standing)).Status);
        Assert.False(_db.Stripe.IsExpired(standing.SessionId));

        // What's left gets a link of its own.
        var fresh = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(partly.Id));
        Assert.Equal(150m, fresh.Amount);
        Assert.NotEqual(partlyLink.Id, fresh.Id);
    }

    [Fact]
    public async Task A_link_paid_just_before_it_would_have_closed_is_recorded_as_credit()
    {
        await TurnOnPayments();
        var invoice = await Invoice(3m);
        var link = await In<OnlinePaymentService, PaymentLink>(s => s.LinkForInvoiceAsync(invoice.Id));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Jan10, _undeposited.Id, [new ApplyToInvoice(invoice.Id, 300m)])));
        _db.Stripe.Pay(link.SessionId);

        Assert.Null(await CloseOutdated());

        Assert.Equal(PaymentLinkStatus.Paid, (await LinkAgain(link)).Status);
        var payments = await In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id));
        Assert.Equal(2, payments.Count);
        Assert.Contains(payments, p => p.Unapplied == 300m);
    }

    // ---- Portal ----

    [Fact]
    public async Task Portal_links_show_the_customer_their_own_invoices_until_revoked()
    {
        var store = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Store 12", ParentId: _acme.Id, BillWithParent: true)));
        var other = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Globex")));
        var own = await Invoice(1m);
        var stores = await Invoice(2m, store);
        var others = await Invoice(5m, other);

        var (access, token) = await In<CustomerPortalService, (PortalAccess, string)>(s => s.CreateAsync(store.Id));
        Assert.StartsWith(PortalAccess.TokenPrefix, token, StringComparison.Ordinal);
        Assert.DoesNotContain(token, await _db.InDb(db => db.PortalAccess.AsNoTracking().Select(a => a.TokenHash).SingleAsync()), StringComparison.Ordinal);

        // A sub-customer billed with its parent sees the parent's account.
        var view = await In<CustomerPortalService, PortalView?>(s => s.ViewAsync(token));
        Assert.Equal(_acme.Id, view!.Customer.Id);
        Assert.Equal(new[] { own.Id, stores.Id }.Order(), view.OpenInvoices.Select(i => i.Id).Order());
        Assert.False(view.CanPayOnline);
        Assert.Null(await In<CustomerPortalService, Invoice?>(s => s.InvoiceForAsync(token, others.Id)));
        Assert.NotNull(await In<CustomerPortalService, Invoice?>(s => s.InvoiceForAsync(token, stores.Id)));

        Assert.Null(await In<CustomerPortalService, PortalView?>(s => s.ViewAsync(token + "x")));
        Assert.Null(await In<CustomerPortalService, PortalView?>(s => s.ViewAsync("not-a-token")));
        await In<CustomerPortalService, int>(async s => { await s.RevokeAsync(access.Id); return 0; });
        Assert.Null(await In<CustomerPortalService, PortalView?>(s => s.ViewAsync(token)));
    }

    // ---- Receipts ----

    [Fact]
    public void Receipt_text_gives_the_vendor_date_total_and_tax()
    {
        const string text = "Corner Hardware\n123 Main St\nMar. 4, 2026  10:42\nHammer 1 @ 18.99   18.99\nNails      4.50\nSubtotal  23.49\nSales Tax 1.94\nTOTAL    $25.43\nVisa ****1234   25.43\n";
        var fields = ReceiptParser.Parse(text, ["Acme Corp", "Corner Hardware Co"]);
        Assert.Equal(("Corner Hardware", new DateOnly(2026, 3, 4), 25.43m, 1.94m), (fields.Vendor, fields.Date, fields.Total, fields.Tax));

        var known = ReceiptParser.Parse("Receipt from STAPLES #0442\n2026-02-11\nPaper 12.00\nAmount due 12.00", ["Staples"]);
        Assert.Equal(("Staples", new DateOnly(2026, 2, 11), 12.00m, (decimal?)null), (known.Vendor, known.Date, known.Total, known.Tax));
        Assert.Equal(new ReceiptFields(null, null, null, null), ReceiptParser.Parse("", []));
    }

    [Fact]
    public async Task Pdf_receipts_are_read_from_their_text()
    {
        // An invoice PDF from OpenBook stands in for a vendor's emailed receipt.
        var invoice = await Invoice(2.5m);
        var pdf = await In<OpenBook.Application.Forms.FormService, OpenBook.Application.Forms.RenderedForm>(s =>
            s.RenderAsync(OpenBook.Application.Forms.FormKind.Invoice, invoice.Id));
        var capture = await In<ReceiptService, ReceiptCapture>(s => s.UploadAsync("invoice.pdf", new MemoryStream(pdf.Pdf)));
        Assert.Contains("Consulting", capture.Text, StringComparison.Ordinal);
        Assert.Equal((250.00m, Jan10), (capture.Total, capture.Date));
    }

    [Fact]
    public async Task Receipts_are_read_and_recorded_as_expenses_with_the_file()
    {
        var supplies = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6100");
        var staples = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Staples", DefaultExpenseAccountId: supplies.Id)));
        var text = "STAPLES store 0442\n02/11/2026\nPaper 10.00\nTax 0.80\nTotal 10.80\n";

        var capture = await In<ReceiptService, ReceiptCapture>(s => s.UploadAsync("staples.txt", new MemoryStream(Encoding.UTF8.GetBytes(text))));
        Assert.Equal((staples.Id, new DateOnly(2026, 2, 11), 10.80m, 0.80m), (capture.VendorId, capture.Date, capture.Total, capture.Tax));
        Assert.Equal("attachment.type_not_allowed", await Refused(() => In<ReceiptService, ReceiptCapture>(s => s.UploadAsync("virus.exe", new MemoryStream([1, 2, 3])))));
        Assert.Single(await In<ReceiptService, IReadOnlyList<ReceiptCapture>>(s => s.ListAsync()));

        // An image without an OCR engine is kept, with nothing read.
        var photo = await In<ReceiptService, ReceiptCapture>(s => s.UploadAsync("photo.jpg", new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0])));
        Assert.Equal((null, null), (photo.Text, photo.Total));
        await In<ReceiptService, ReceiptCapture>(s => s.DismissAsync(photo.Id));

        var spend = await In<ReceiptService, BankTransaction>(s => s.RecordAsync(capture.Id,
            new RecordReceiptCommand(_checking.Id, null, capture.Date!.Value, capture.Total!.Value, capture.VendorId)));
        Assert.Equal((10.80m, staples.Id, "Staples"), (spend.Amount, spend.VendorId, spend.Payee));
        var files = await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bank_transaction", spend.Id));
        Assert.Equal("staples.txt", Assert.Single(files).FileName);
        Assert.Equal((ReceiptStatus.Recorded, spend.Id), ((await In<ReceiptService, ReceiptCapture?>(s => s.GetAsync(capture.Id)))!.Status, spend.Id));
        Assert.Equal("receipt.done", await Refused(() => In<ReceiptService, BankTransaction>(s =>
            s.RecordAsync(capture.Id, new RecordReceiptCommand(_checking.Id, supplies.Id, Jan10, 1m)))));
    }
}
