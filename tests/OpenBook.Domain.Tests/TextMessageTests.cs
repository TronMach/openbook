using OpenBook.Domain.Common;
using OpenBook.Domain.Email;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Tests;

/// <summary>Text messages: phone numbers as people type them, a text's length in parts, the wording's checks and a customer's agreement.</summary>
public sealed class TextMessageTests
{
    [Theory]
    [InlineData("(555) 010-2030", null, "+15550102030")]
    [InlineData("1-555-010-2030", "en-US", "+15550102030")]
    [InlineData("555.010.2030", "en-CA", "+15550102030")]
    [InlineData("+44 20 7946 0000", "en-US", "+442079460000")]
    [InlineData("0044 20 7946 0000", "en-US", "+442079460000")]
    [InlineData("07700 900123", "en-GB", "+447700900123")]
    [InlineData("0412 345 678", "en-AU", "+61412345678")]
    [InlineData("021 123 4567", "en-NZ", "+64211234567")]
    [InlineData("  ", "en-US", null)]
    public void Numbers_are_written_with_their_country_code(string typed, string? region, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(typed, "Mobile number", region));

    [Theory]
    [InlineData("call me")]
    [InlineData("555-0102")]
    [InlineData("+1 555 010 2030 ext 4")]
    [InlineData("+0123456789")]
    [InlineData("+1234567")]
    public void Anything_else_is_refused(string typed) =>
        Assert.Equal("text.number_invalid", Assert.Throws<DomainException>(() => PhoneNumbers.Normalize(typed, "Mobile number", "en-US")).Code);

    [Fact]
    public void A_text_is_counted_in_the_parts_the_network_sends()
    {
        Assert.Equal(new TextLength(160, 1, false), TextTemplates.Measure(new string('a', 160)));
        Assert.Equal(2, TextTemplates.Measure(new string('a', 161)).Parts);
        Assert.Equal(3, TextTemplates.Measure(new string('a', 307)).Parts);
        // The GSM alphabet's extra characters take two places; anything outside it makes every part 70 long.
        Assert.Equal(2, TextTemplates.Measure(new string('€', 81)).Parts);
        Assert.Equal(new TextLength(70, 1, true), TextTemplates.Measure(new string('“', 70)));
        Assert.Equal(2, TextTemplates.Measure(new string('“', 71)).Parts);
        Assert.Equal(new TextLength(1, 1, true), TextTemplates.Measure("🙏"));
        Assert.Equal(0, TextTemplates.Measure("").Parts);
    }

    [Fact]
    public void The_usual_wording_passes_its_own_checks_and_ends_with_the_way_to_stop()
    {
        foreach (var kind in new[] { EmailKind.Invoice, EmailKind.Estimate, EmailKind.CreditMemo, EmailKind.SalesReceipt })
            TextTemplates.Check(kind, TextTemplates.Usual(kind));
        Assert.False(TextTemplates.CanText(EmailKind.Statement));
        Assert.Equal("text.placeholder_unknown", Assert.Throws<DomainException>(() => TextTemplates.Check(EmailKind.Estimate, "{pay_link} {link}")).Code);
        Assert.Equal("text.link_required", Assert.Throws<DomainException>(() => TextTemplates.Check(EmailKind.Invoice, "Invoice {number}")).Code);

        var text = TextTemplates.Compose("Invoice {number}: {link}\nPay: {pay_link}", new Dictionary<string, string> { ["number"] = "7", ["link"] = "L", ["pay_link"] = "" });
        Assert.Equal("Invoice 7: L\n" + TextTemplates.OptOutLine, text);
    }

    [Fact]
    public void Agreeing_belongs_to_one_number()
    {
        var customer = Customer.Create(new CustomerDetails("Acme Corp"));
        var monday = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
        customer.SetTextPreferences("555 010 2030", true, monday, "en-US");
        Assert.Equal(("+15550102030", (DateTimeOffset?)monday, true), (customer.TextNumber, customer.TextsAllowedAt, customer.CanBeTexted));

        customer.SetTextPreferences("+1 (555) 010-2030", true, monday.AddDays(1), "en-US");
        Assert.Equal(monday, customer.TextsAllowedAt);
        customer.SetTextPreferences("555 010 9999", true, monday.AddDays(2), "en-US");
        Assert.Equal(monday.AddDays(2), customer.TextsAllowedAt);

        customer.StopTexts();
        Assert.False(customer.CanBeTexted);
        Assert.Equal("+15550109999", customer.TextNumber);
    }

    /// <summary>Made-up ids are joined from pieces, so that secret scanners don't take them for real ones.</summary>
    private const string Hex32 = "0123456789abcdef" + "0123456789abcdef";

    [Fact]
    public void Sender_and_account_are_checked()
    {
        var settings = TextSettings.Create();
        settings.Update("ac" + "0123456789ABCDEF" + "0123456789abcdef", "protected", "(500) 555-0006", null);
        Assert.Equal(("AC" + Hex32, "+15005550006", false), (settings.AccountSid, settings.Sender, settings.SendsThroughService));
        Assert.Equal("text.sender_invalid", Assert.Throws<DomainException>(() => settings.Update(settings.AccountSid, null, "MG12", null)).Code);
        Assert.Equal("text.account_sid_invalid", Assert.Throws<DomainException>(() => settings.Update("SK" + Hex32, null, null, null)).Code);
    }
}
