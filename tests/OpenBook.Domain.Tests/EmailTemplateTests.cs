using OpenBook.Domain.Common;
using OpenBook.Domain.Email;

namespace OpenBook.Domain.Tests;

public sealed class EmailTemplateTests
{
    [Fact]
    public void Placeholders_are_filled_and_a_line_whose_fill_ins_are_all_blank_is_left_out()
    {
        var text = EmailTemplates.Fill("Estimate {number}, for {total}.\nIt's valid until {valid_until}.\nThanks,\n{company}",
            new Dictionary<string, string> { ["number"] = "E-7", ["total"] = "50.00", ["valid_until"] = "", ["company"] = "Studio" });

        Assert.Equal("Estimate E-7, for 50.00.\nThanks,\nStudio", text);
    }

    [Fact]
    public void Placeholders_without_a_value_stay_as_written()
    {
        Assert.Equal("Hello {nobody}, from Studio", EmailTemplates.Fill("Hello {nobody}, from {company}", new Dictionary<string, string> { ["company"] = "Studio" }));
    }

    [Fact]
    public void Templates_refuse_placeholders_their_kind_does_not_take()
    {
        var ex = Assert.Throws<DomainException>(() => EmailTemplate.Create(EmailKind.Invoice, "Invoice {number}", "Owed: {amout}", DateTimeOffset.UnixEpoch, "pat"));

        Assert.Equal("email_template.placeholder_unknown", ex.Code);
        Assert.Contains("{amout}", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["vendor"], EmailTemplates.Unknown(EmailKind.Invoice, "{customer} and {vendor}"));
    }

    [Fact]
    public void Templates_need_a_one_line_subject_and_reminders_are_kept_elsewhere()
    {
        Assert.Equal("email_template.subject_required",
            Assert.Throws<DomainException>(() => EmailTemplate.Create(EmailKind.Estimate, " ", "Body", DateTimeOffset.UnixEpoch, "pat")).Code);
        Assert.Equal("email.subject_too_long",
            Assert.Throws<DomainException>(() => EmailTemplate.Create(EmailKind.Estimate, "Two\nlines", "Body", DateTimeOffset.UnixEpoch, "pat")).Code);
        Assert.Equal("email_template.kind_invalid",
            Assert.Throws<DomainException>(() => EmailTemplate.Create(EmailKind.Reminder, "Due", "Body", DateTimeOffset.UnixEpoch, "pat")).Code);

        var template = EmailTemplate.Create(EmailKind.Estimate, "  Estimate {number}  ", "Hi\r\nthere\n\n", DateTimeOffset.UnixEpoch, "pat");
        Assert.Equal(("Estimate {number}", "Hi\nthere"), (template.Subject, template.Body));
    }

    [Fact]
    public void Every_usual_wording_uses_only_its_own_placeholders()
    {
        foreach (var kind in EmailTemplates.All)
        {
            var (subject, body) = EmailTemplates.Usual(kind);
            EmailTemplates.Check(kind, subject, body);
            Assert.Equal(kind, EmailTemplates.FromKey(EmailTemplates.Key(kind)));
        }
    }

    [Fact]
    public void Address_lists_are_split_checked_and_kept_once()
    {
        Assert.Equal(["a@x.com", "b@y.org"], EmailAddresses.Parse(" a@x.com; b@y.org,A@X.com ", "To"));
        Assert.Equal("a@x.com, b@y.org", EmailAddresses.Normalize("a@x.com,b@y.org", "Cc"));
        Assert.Null(EmailAddresses.Normalize("  ", "Cc"));
        Assert.Equal("email.address_invalid", Assert.Throws<DomainException>(() => EmailAddresses.Parse("a@x.com, nobody", "To")).Code);
        Assert.Equal("email.too_many_addresses",
            Assert.Throws<DomainException>(() => EmailAddresses.Parse(string.Join(",", Enumerable.Range(1, 6).Select(i => $"p{i}@x.com")), "Copy to", 5)).Code);
    }

    [Fact]
    public void Document_links_keep_only_a_hash_and_count_openings_at_most_once_a_minute()
    {
        var start = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var (link, token) = DocumentLink.Create(EmailKind.Invoice, Guid.NewGuid(), Guid.NewGuid(), start, "pat");

        Assert.StartsWith(DocumentLink.TokenPrefix, token, StringComparison.Ordinal);
        Assert.Equal(DocumentLink.Hash(token), link.TokenHash);
        Assert.DoesNotContain(token, link.TokenHash, StringComparison.Ordinal);
        Assert.True(link.Opened(start.AddHours(1)));
        Assert.False(link.Opened(start.AddHours(1).AddSeconds(30)));
        Assert.True(link.Opened(start.AddHours(2)));
        Assert.Equal((start.AddHours(1), start.AddHours(2)), (link.FirstOpenedAt, link.LastOpenedAt));

        link.Revoke(start.AddDays(1));
        Assert.False(link.IsActive);
        Assert.Equal("email.link_revoked", Assert.Throws<DomainException>(() => link.Revoke(start.AddDays(2))).Code);
        Assert.Equal("email.link_unsupported",
            Assert.Throws<DomainException>(() => DocumentLink.Create(EmailKind.Statement, Guid.NewGuid(), Guid.NewGuid(), start, "pat")).Code);
    }
}
