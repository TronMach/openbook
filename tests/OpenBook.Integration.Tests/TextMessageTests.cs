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
/// Text messages (2026-11-21): the company's Twilio account, a customer's mobile number and agreement, sending a
/// document with a "View" link through the real Twilio client, STOP replies, and the history.
/// </summary>
public sealed class TextMessageTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Consulting Income", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m, Taxable: false)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test", Phone: "(555) 010-2030", PaymentTermsDays: 30)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Texts<T>(Func<TextMessageService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<TextSettings> SetUpTwilio(string sender = FakeTwilio.Number) =>
        Texts(s => s.SaveSettingsAsync(new TextSettingsCommand(FakeTwilio.AccountSid, FakeTwilio.AuthToken, sender)));

    private Task<EmailSettings> SetUpLinks() =>
        In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "pw", false, "books@studio.test", "Studio", null, "https://books.studio.test")));

    private Task<Customer> Agreed(string number = "555-010-2030") => Texts(s => s.SetCustomerAsync(_acme.Id, number, allowed: true));

    private Task<Invoice> Invoice() =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, null, 2m, 100m)])));

    private static EmailSource For(Invoice invoice) => new(EmailKind.Invoice, invoice.Id);

    private async Task<Invoice> ReadyToText()
    {
        await SetUpTwilio();
        await SetUpLinks();
        await Agreed();
        return await Invoice();
    }

    [Fact]
    public async Task The_auth_token_is_kept_protected_and_never_in_the_audit()
    {
        Assert.False((await Texts(s => s.GetSettingsAsync())).IsReady);
        Assert.Equal("text.account_sid_invalid",
            await Refused(() => Texts(s => s.SaveSettingsAsync(new TextSettingsCommand("AC123", FakeTwilio.AuthToken, FakeTwilio.Number)))));
        Assert.Equal("text.auth_token_invalid",
            await Refused(() => Texts(s => s.SaveSettingsAsync(new TextSettingsCommand(FakeTwilio.AccountSid, "short", FakeTwilio.Number)))));

        var saved = await SetUpTwilio("(500) 555-0006");
        Assert.Equal(("protected:" + FakeTwilio.AuthToken, "+15005550006", true), (saved.ProtectedAuthToken, saved.Sender, saved.IsReady));

        // Saving again without a token keeps it; a messaging service can stand in for the number; clearing takes the token away.
        var service = await Texts(s => s.SaveSettingsAsync(new TextSettingsCommand(FakeTwilio.AccountSid, null, "MG0123456789ABCDEF0123456789abcdef")));
        Assert.Equal(("protected:" + FakeTwilio.AuthToken, "MG0123456789abcdef0123456789abcdef", true), (service.ProtectedAuthToken, service.Sender, service.SendsThroughService));
        var cleared = await Texts(s => s.SaveSettingsAsync(new TextSettingsCommand(FakeTwilio.AccountSid, null, FakeTwilio.Number, ClearAuthToken: true)));
        Assert.False(cleared.IsReady);

        var audit = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityType == TextMessageService.SettingsEntity).ToListAsync());
        Assert.Equal(3, audit.Count);
        Assert.DoesNotContain(audit, a => (a.After ?? "").Contains(FakeTwilio.AuthToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_test_text_goes_to_the_number_given_through_the_company_account()
    {
        Assert.Equal("text.not_configured", await Refused(() => Texts(s => s.SendTestAsync("555 010 2030"))));
        await SetUpTwilio();
        Assert.Equal("+15550102030", await Texts(s => s.SendTestAsync("555 010 2030")));
        var message = Assert.Single(_db.Twilio.Sent);
        Assert.Equal(("+15550102030", FakeTwilio.Number, (string?)null), (message.To, message.From, message.Service));
        Assert.StartsWith("Test message from ", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_texted_until_everything_is_in_place_and_the_customer_agreed()
    {
        var invoice = await Invoice();
        var body = TextMessageService.Draft(EmailKind.Invoice);
        async Task<IReadOnlyList<string>> Problems() => (await Texts(s => s.PreviewAsync(For(invoice), body))).Problems;

        Assert.Equal(3, (await Problems()).Count);
        Assert.Equal("text.not_configured", await Refused(() => Texts(s => s.SendAsync(For(invoice), body))));
        await SetUpTwilio();
        Assert.Equal("text.link_address_required", await Refused(() => Texts(s => s.SendAsync(For(invoice), body))));
        await SetUpLinks();
        Assert.Equal("text.number_required", await Refused(() => Texts(s => s.SendAsync(For(invoice), body))));

        // A number without the agreement isn't enough: carriers and the law want the customer's yes.
        await Texts(s => s.SetCustomerAsync(_acme.Id, "555-010-2030", allowed: false));
        Assert.Equal("text.not_allowed", await Refused(() => Texts(s => s.SendAsync(For(invoice), body))));
        Assert.Contains("hasn't agreed", Assert.Single(await Problems()), StringComparison.Ordinal);
        Assert.Empty(_db.Twilio.Sent);

        await Agreed();
        Assert.Empty(await Problems());
    }

    [Fact]
    public async Task An_invoice_goes_with_a_view_link_that_opens_it_and_is_kept_without_its_token()
    {
        var invoice = await ReadyToText();
        var preview = await Texts(s => s.PreviewAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)));
        Assert.True(preview.CanSend);
        Assert.Equal("+15550102030", preview.To);
        Assert.DoesNotContain("Pay online", preview.Text, StringComparison.Ordinal); // no online payments: the line drops out
        Assert.Equal(1, preview.Length.Parts);

        var sent = await Texts(s => s.SendAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)));

        var message = Assert.Single(_db.Twilio.Sent);
        Assert.Equal("+15550102030", message.To);
        Assert.StartsWith($"{(await _db.InDb(db => db.Companies.SingleAsync())).Name}: invoice {invoice.Number} for 200.00 is due on ", message.Body, StringComparison.Ordinal);
        Assert.EndsWith("\n" + TextTemplates.OptOutLine, message.Body, StringComparison.Ordinal);
        var url = message.Body.Split('\n').Single(l => l.StartsWith("View it: ", StringComparison.Ordinal))["View it: ".Length..];
        Assert.StartsWith("https://books.studio.test/view/obd_", url, StringComparison.Ordinal);
        // The preview counted a link as long as the real one.
        Assert.Equal(preview.Text.Length, message.Body.Length);

        // The link opens the invoice; the kept copy doesn't hold its token.
        var token = url[(url.LastIndexOf('/') + 1)..];
        Assert.NotNull(await In<EmailService, RenderedForm?>(s => s.OpenLinkAsync(token)));
        Assert.Contains("View it: (link)", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, sent.Body, StringComparison.Ordinal);
        Assert.Equal(message.Sid, sent.ProviderId);

        var history = Assert.Single(await Texts(s => s.HistoryAsync("invoice", invoice.Id)));
        Assert.NotNull(history.Link?.FirstOpenedAt);
        var audit = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityId == invoice.Id && a.Action == AuditAction.Texted).ToListAsync());
        Assert.Contains("15550102030", Assert.Single(audit).After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_customer_who_replied_stop_is_switched_off_and_nothing_is_kept()
    {
        var invoice = await ReadyToText();
        _db.Twilio.Unsubscribed.Add("+15550102030");

        Assert.Equal(Twilio.OptedOutCode, await Refused(() => Texts(s => s.SendAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)))));

        var customer = await _db.InDb(db => db.Customers.SingleAsync(c => c.Id == _acme.Id));
        Assert.Equal(("+15550102030", (DateTimeOffset?)null), (customer.TextNumber, customer.TextsAllowedAt));
        Assert.Empty(await _db.InDb(db => db.SentTexts.ToListAsync()));
        Assert.Empty(await _db.InDb(db => db.DocumentLinks.ToListAsync()));
        Assert.Equal("text.not_allowed", await Refused(() => Texts(s => s.SendAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)))));
    }

    [Theory]
    [InlineData(21211, "text.number_refused")]
    [InlineData(21408, "text.country_not_allowed")]
    [InlineData(21606, "text.sender_refused")]
    [InlineData(30001, "text.send_failed")]
    public async Task Twilio_refusals_say_what_to_do_and_keep_nothing(int code, string expected)
    {
        var invoice = await ReadyToText();
        _db.Twilio.FailWith = code;
        Assert.Equal(expected, await Refused(() => Texts(s => s.SendAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)))));
        Assert.Empty(await _db.InDb(db => db.SentTexts.ToListAsync()));
        Assert.NotNull((await _db.InDb(db => db.Customers.SingleAsync(c => c.Id == _acme.Id))).TextsAllowedAt);
    }

    [Fact]
    public async Task Wrong_credentials_are_refused_as_such()
    {
        var invoice = await ReadyToText();
        await Texts(s => s.SaveSettingsAsync(new TextSettingsCommand(FakeTwilio.AccountSid, "ffffffffffffffffffffffffffffffff", FakeTwilio.Number)));
        Assert.Equal("text.unauthorized", await Refused(() => Texts(s => s.SendAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)))));
    }

    [Fact]
    public async Task The_wording_can_change_but_keeps_its_link_and_known_fill_ins()
    {
        var invoice = await ReadyToText();
        Assert.Equal("text.link_required", await Refused(() => Texts(s => s.SendAsync(For(invoice), "Invoice {number} is due."))));
        Assert.Equal("text.placeholder_unknown", await Refused(() => Texts(s => s.SendAsync(For(invoice), "Pay {amount}: {link}"))));
        Assert.Contains((await Texts(s => s.PreviewAsync(For(invoice), "Pay {amount}: {link}"))).Problems, p => p.Contains("{amount}", StringComparison.Ordinal));

        await Texts(s => s.SendAsync(For(invoice), "Hi {customer}, {balance} please 🙏 {link}"));
        Assert.StartsWith("Hi Acme Corp, 200.00 please 🙏 https://", Assert.Single(_db.Twilio.Sent).Body, StringComparison.Ordinal);
        Assert.True((await Texts(s => s.PreviewAsync(For(invoice), "Hi {customer}, {balance} please 🙏 {link}"))).Length.Unicode);
    }

    [Fact]
    public async Task A_new_number_needs_agreeing_to_again()
    {
        Assert.Equal("customer.text_number_required", await Refused(() => Texts(s => s.SetCustomerAsync(_acme.Id, null, allowed: true))));
        Assert.Equal("text.number_invalid", await Refused(() => Texts(s => s.SetCustomerAsync(_acme.Id, "call me", allowed: true))));

        var agreed = await Agreed();
        Assert.Equal(_db.Clock.UtcNow, agreed.TextsAllowedAt);
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(3);
        Assert.Equal(agreed.TextsAllowedAt, (await Agreed("+1 555 010 2030")).TextsAllowedAt); // the same number, written another way
        Assert.Equal(_db.Clock.UtcNow, (await Agreed("+44 20 7946 0000")).TextsAllowedAt);

        // The customer's phone is suggested until a number is saved.
        var other = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Beta LLC", Phone: "555.010.9999")));
        Assert.Equal("+15550109999", await Texts(s => s.SuggestedNumberAsync(other)));
        var audit = await _db.InDb(db => db.AuditRecords.Where(a => a.EntityId == _acme.Id && a.EntityType == "customer" && a.Action == AuditAction.Updated).CountAsync());
        Assert.Equal(2, audit);
    }

    [Fact]
    public async Task Voided_documents_and_other_kinds_are_refused()
    {
        var invoice = await ReadyToText();
        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Sent twice"));
        Assert.Equal("document.voided", await Refused(() => Texts(s => s.SendAsync(For(invoice), TextMessageService.Draft(EmailKind.Invoice)))));
        Assert.Equal("text.kind_unsupported", await Refused(() => Texts(s => s.SendAsync(new EmailSource(EmailKind.Statement, _acme.Id), "{link}"))));
    }
}
