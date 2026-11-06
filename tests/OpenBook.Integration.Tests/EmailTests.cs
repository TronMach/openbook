using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Email (2026-11-03): templates with placeholders, HTML in the design's look with a plain-text copy, copies, the
/// customer's attach-or-link choice and its links, previews, test sends and the history.
/// </summary>
public sealed class EmailTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly byte[] Logo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAKCAIAAABJ+IsHAAAAG0lEQVR4nGOQz48ZEMQwavGoxaMWj1qcT6bFAOsbbbC65kkkAAAAAElFTkSuQmCC");

    private readonly LedgerDatabase _db = new();
    private Item _consulting = null!;
    private Customer _acme = null!;
    private Account _undeposited = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Consulting Income", AccountType.Income, "4000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m, Taxable: false)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test", PaymentTermsDays: 30)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<EmailSettings> SetUpEmail(string? linkAddress = null) =>
        In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "pw", false, "books@studio.test", "Studio", null, linkAddress)));

    private Task<Invoice> Invoice(decimal hours = 2m) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private static EmailSource For(Invoice invoice) => new(EmailKind.Invoice, invoice.Id);

    [Fact]
    public async Task A_changed_template_words_new_drafts_until_it_is_reset()
    {
        var invoice = await Invoice();
        var usual = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));
        Assert.StartsWith($"Invoice {invoice.Number} from ", usual.Subject, StringComparison.Ordinal);
        Assert.Contains("The balance of 200.00 is due on", usual.Body, StringComparison.Ordinal);

        Assert.Equal("email_template.placeholder_unknown",
            await Refused(() => In<EmailService, EmailWording>(s => s.SaveTemplateAsync(EmailKind.Invoice, "Bill {number}", "You owe {amount}."))));
        var saved = await In<EmailService, EmailWording>(s => s.SaveTemplateAsync(EmailKind.Invoice, "Bill {number} for {customer}",
            "Dear {customer},\n\n**{balance}** is due on {due} ({terms})."));
        Assert.False(saved.IsUsual);

        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));
        Assert.Equal($"Bill {invoice.Number} for Acme Corp", draft.Subject);
        Assert.StartsWith("Dear Acme Corp,\n\n**200.00** is due on", draft.Body, StringComparison.Ordinal);
        Assert.EndsWith("(Net 30).", draft.Body, StringComparison.Ordinal);

        var reset = await In<EmailService, EmailWording>(s => s.ResetTemplateAsync(EmailKind.Invoice));
        Assert.True(reset.IsUsual);
        Assert.Equal(usual.Subject, (await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)))).Subject);
        var actions = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityType == EmailService.TemplateEntity).Select(a => a.Action).ToListAsync());
        Assert.Equal([AuditAction.Created, AuditAction.Deleted], actions);
    }

    [Fact]
    public async Task A_payment_receipt_says_what_was_paid_and_attaches_nothing()
    {
        await SetUpEmail();
        var first = await Invoice();
        var second = await Invoice(hours: 3m);
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar1.AddDays(5), _undeposited.Id,
            [new ApplyToInvoice(first.Id, 200m), new ApplyToInvoice(second.Id, 100m)], "1042", Amount: 350m, Method: PaymentMethod.Check)));
        var source = new EmailSource(EmailKind.PaymentReceipt, payment.Id);

        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(source));
        Assert.Equal("ap@acme.test", draft.To);
        Assert.Contains("payment of 350.00", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Paid by check no. 1042.", draft.Body, StringComparison.Ordinal);
        Assert.Contains($"- Invoice {first.Number}: 200.00, paid in full", draft.Body, StringComparison.Ordinal);
        Assert.Contains($"- Invoice {second.Number}: 100.00, 200.00 still to pay", draft.Body, StringComparison.Ordinal);
        Assert.Contains("50.00 is kept as a credit", draft.Body, StringComparison.Ordinal);
        Assert.Contains("balance with us is now 150.00", draft.Body, StringComparison.Ordinal);
        Assert.Null((await In<EmailService, EmailPreview>(s => s.PreviewAsync(source, draft))).Attachment);

        await In<EmailService, SentEmail>(s => s.SendAsync(source, draft));
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Empty(message.Attachments);
        Assert.Single(await In<EmailService, IReadOnlyList<SentEmailEntry>>(s => s.HistoryAsync("payment", payment.Id)));
    }

    [Fact]
    public async Task Emails_go_as_html_in_the_design_look_with_a_plain_copy_and_every_recipient()
    {
        await SetUpEmail();
        var standard = (await In<FormDesignService, IReadOnlyList<FormDesign>>(s => s.ListAsync())).Single(d => d.IsDefault);
        await In<FormDesignService, FormDesign>(s => s.SetLogoAsync(standard.Id, Logo));
        var invoice = await Invoice();
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));

        var sent = await In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft with
        {
            To = "ap@acme.test; pat@acme.test", Cc = "boss@acme.test", Bcc = "me@studio.test",
            Body = "Hi **Pat**,\n\nSee [our terms](https://studio.test/terms) <script>alert(1)</script> [bad](javascript:alert(1)) ![pic](https://track.test/p.png)",
        }));

        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal("ap@acme.test, pat@acme.test", message.To);
        Assert.Equal(["boss@acme.test"], message.Cc);
        Assert.Equal(["me@studio.test"], message.Bcc);
        Assert.Contains("<strong>Pat</strong>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://studio.test/terms\"", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=\"https://track.test", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("cid:logo", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(Logo, Assert.Single(message.InlineImages).Content);
        Assert.StartsWith("Hi Pat,\n\nSee our terms (https://studio.test/terms)", message.Body, StringComparison.Ordinal);
        Assert.Equal("application/pdf", Assert.Single(message.Attachments).ContentType);

        var history = await In<EmailService, IReadOnlyList<SentEmailEntry>>(s => s.HistoryAsync("invoice", invoice.Id));
        Assert.Equal((sent.Id, "boss@acme.test", standard.Id), (history.Single().Email.Id, history.Single().Email.Cc, history.Single().Email.DesignId));
        Assert.NotNull((await _db.InDb(db => db.Invoices.SingleAsync(i => i.Id == invoice.Id))).LastEmailedAt);

        await In<FormDesignService, FormDesign>(s => s.UpdateAsync(standard.Id, standard.ToDetails() with { EmailStyle = EmailStyle.Plain }));
        await In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft));
        Assert.Null(_db.Mail.Sent[1].Message.HtmlBody);
        Assert.Empty(_db.Mail.Sent[1].Message.InlineImages);
    }

    [Fact]
    public async Task A_failed_send_keeps_nothing_and_bad_addresses_are_refused()
    {
        await SetUpEmail("https://books.studio.test");
        await In<EmailService, Customer>(s => s.SetCustomerPreferencesAsync(_acme.Id, null, EmailPdf.Link));
        var invoice = await Invoice();
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));

        Assert.Equal("email.address_invalid", await Refused(() => In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft with { Cc = "not an address" }))));
        _db.Mail.Failure = "connection refused";
        Assert.Equal("email.send_failed", await Refused(() => In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft))));

        Assert.Equal(0, await _db.InDb(db => db.SentEmails.CountAsync()));
        Assert.Equal(0, await _db.InDb(db => db.DocumentLinks.CountAsync()));
        Assert.Null((await _db.InDb(db => db.Invoices.SingleAsync(i => i.Id == invoice.Id))).LastEmailedAt);
    }

    [Fact]
    public async Task Customers_can_get_a_link_instead_of_the_pdf_which_opens_until_it_is_turned_off()
    {
        await SetUpEmail();
        await In<EmailService, Customer>(s => s.SetCustomerPreferencesAsync(_acme.Id, "ar@acme.test, boss@acme.test", EmailPdf.Link));
        var invoice = await Invoice();

        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));
        Assert.Equal(("ar@acme.test, boss@acme.test", EmailPdf.Link), (draft.Cc, draft.Pdf));

        // Without the address customers reach OpenBook at, the PDF goes attached instead, and the preview says so.
        var preview = await In<EmailService, EmailPreview>(s => s.PreviewAsync(For(invoice), draft));
        Assert.Contains(preview.Notes, n => n.Contains("attached instead", StringComparison.Ordinal));
        Assert.NotNull(preview.Attachment);

        await SetUpEmail("https://books.studio.test/");
        preview = await In<EmailService, EmailPreview>(s => s.PreviewAsync(For(invoice), draft));
        Assert.Null(preview.Attachment);
        Assert.Equal(["View invoice"], preview.Buttons);

        await In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft));
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Empty(message.Attachments);
        Assert.Equal(["ar@acme.test", "boss@acme.test"], message.Cc);
        var url = message.Body.Split('\n').Single(l => l.StartsWith("View invoice: ", StringComparison.Ordinal))["View invoice: ".Length..];
        Assert.StartsWith("https://books.studio.test/view/obd_", url, StringComparison.Ordinal);
        var token = url[(url.LastIndexOf('/') + 1)..];

        var opened = await In<EmailService, RenderedForm?>(s => s.OpenLinkAsync(token));
        Assert.Equal("%PDF"u8.ToArray(), opened!.Pdf[..4]);
        Assert.Null(await In<EmailService, RenderedForm?>(s => s.OpenLinkAsync(token + "x")));
        var entry = (await In<EmailService, IReadOnlyList<SentEmailEntry>>(s => s.HistoryAsync("invoice", invoice.Id))).Single();
        Assert.NotNull(entry.Link!.FirstOpenedAt);
        Assert.Null(entry.Email.Attachment);
        Assert.DoesNotContain(token, await _db.InDb(db => db.DocumentLinks.Select(l => l.TokenHash).SingleAsync()), StringComparison.Ordinal);

        await In<EmailService, DocumentLink>(s => s.RevokeLinkAsync(entry.Link.Id));
        Assert.Null(await In<EmailService, RenderedForm?>(s => s.OpenLinkAsync(token)));

        // Both: the PDF and the button.
        await In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft with { Pdf = EmailPdf.Both }));
        Assert.Single(_db.Mail.Sent[1].Message.Attachments);
        Assert.Contains("View invoice: https://books.studio.test/view/", _db.Mail.Sent[1].Message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_invoices_get_a_pay_now_button_when_online_payments_are_on()
    {
        await SetUpEmail();
        await In<OnlinePaymentService, OnlinePaymentSettings>(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
            true, _undeposited.Id, "https://books.studio.test/", FakeStripe.SecretKey, "whsec_test")));
        var invoice = await Invoice();
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));
        Assert.DoesNotContain("Pay online", draft.Body, StringComparison.Ordinal);

        var preview = await In<EmailService, EmailPreview>(s => s.PreviewAsync(For(invoice), draft));
        Assert.Equal(["Pay now"], preview.Buttons);
        Assert.Empty(_db.Stripe.Created);

        await In<EmailService, SentEmail>(s => s.SendAsync(For(invoice), draft));
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Contains(">Pay now</a>", message.HtmlBody, StringComparison.Ordinal);

        // The button goes through OpenBook, so it still works once a checkout page would have expired.
        Assert.Empty(_db.Stripe.Created);
        var token = System.Text.RegularExpressions.Regex.Match(message.Body, @"Pay now: https://books\.studio\.test/view/(obd_[^/]+)/pay").Groups[1].Value;
        Assert.Equal(invoice.Id, await In<EmailService, Guid?>(s => s.InvoiceToPayAsync(token)));
        Assert.Null(await In<EmailService, Guid?>(s => s.InvoiceToPayAsync("obd_nothing")));
    }

    [Fact]
    public async Task A_test_send_goes_to_the_writer_and_records_nothing()
    {
        var invoice = await Invoice();
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));
        Assert.Equal("email.not_configured", await Refused(() => In<EmailService, string>(s => s.SendTestAsync(For(invoice), draft))));
        await SetUpEmail();

        var to = await In<EmailService, string>(s => s.SendTestAsync(For(invoice), draft with { Subject = "Your bill" }));

        Assert.Equal("books@studio.test", to);
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal(("books@studio.test", "[Test] Your bill"), (message.To, message.Subject));
        Assert.Single(message.Attachments);
        Assert.Equal(0, await _db.InDb(db => db.SentEmails.CountAsync()));
        Assert.Null((await _db.InDb(db => db.Invoices.SingleAsync(i => i.Id == invoice.Id))).LastEmailedAt);
    }

    [Fact]
    public async Task Previews_show_problems_as_notes_and_templates_preview_with_samples()
    {
        var invoice = await Invoice();
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));

        var preview = await In<EmailService, EmailPreview>(s => s.PreviewAsync(For(invoice), draft with { To = "", Cc = "oops", Body = "Owed {amount}" }));
        Assert.Contains(preview.Notes, n => n.Contains("Enter the address", StringComparison.Ordinal));
        Assert.Contains(preview.Notes, n => n.Contains("'oops' isn't an email address", StringComparison.Ordinal));
        Assert.Contains(preview.Notes, n => n.Contains("{amount}", StringComparison.Ordinal));
        Assert.Contains("Owed {amount}", preview.Html, StringComparison.Ordinal);
        Assert.Contains("<base target=\"_blank\">", preview.Html, StringComparison.Ordinal);

        var sample = await In<EmailService, EmailPreview>(s => s.PreviewTemplateAsync(EmailKind.Statement, "Statement for {customer}", "Balance {balance} as of {to}."));
        Assert.Equal("Statement for Acme Corp", sample.Subject);
        Assert.Contains("Balance 2,480.00 as of", sample.Text, StringComparison.Ordinal);
        Assert.Empty(sample.Notes);
        var invoiceSample = await In<EmailService, EmailPreview>(s => s.PreviewTemplateAsync(EmailKind.Invoice, "Invoice {number}", "Hello {customer}"));
        Assert.Equal(["View invoice (when the customer gets links)"], invoiceSample.Buttons);
        Assert.Contains("button shows when the customer gets links", Assert.Single(invoiceSample.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduled_emails_keep_their_copies_and_go_in_the_design_look()
    {
        await SetUpEmail();
        await In<EmailService, Customer>(s => s.SetCustomerPreferencesAsync(_acme.Id, "ar@acme.test", EmailPdf.Attach));
        var invoice = await Invoice();
        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(For(invoice)));

        var scheduled = await In<ScheduledEmailService, ScheduledEmail>(s => s.ScheduleAsync(invoice.Id, draft with { Bcc = "me@studio.test" }, _db.Clock.UtcNow.AddHours(1)));
        Assert.Equal(("ar@acme.test", "me@studio.test"), (scheduled.Cc, scheduled.Bcc));
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddHours(2);
        await In<ScheduledEmailService, IReadOnlyList<ScheduledEmailResult>>(s => s.SendDueAsync());
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal(["ar@acme.test"], message.Cc);
        Assert.Equal(["me@studio.test"], message.Bcc);
        Assert.NotNull(message.HtmlBody);

        var history = await In<EmailService, IReadOnlyList<SentEmailEntry>>(s => s.HistoryAsync("invoice", invoice.Id));
        Assert.Equal(EmailKind.Invoice, history.Single().Email.Kind);
        Assert.Contains(Encoding.UTF8.GetString(message.Attachments.Single().Content, 0, 4), "%PDF", StringComparison.Ordinal);
    }
}
