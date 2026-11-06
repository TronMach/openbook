using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Email;

/// <summary>The messages OpenBook sends, each with its own wording (<see cref="EmailTemplate"/>) and placeholders.</summary>
public enum EmailKind
{
    Invoice,
    Estimate,
    CreditMemo,
    SalesReceipt,
    Statement,
    PurchaseOrder,
    GiftAcknowledgment,
    GivingStatement,

    /// <summary>Payment reminders; their wording lives with the collection settings.</summary>
    Reminder,
    Report,
    Invitation,

    /// <summary>A thank-you for a customer payment, with what it paid; nothing attached.</summary>
    PaymentReceipt,
}

/// <summary>How a customer gets their documents by email.</summary>
public enum EmailPdf
{
    /// <summary>The PDF attached (the usual).</summary>
    Attach,

    /// <summary>A "View" button opening the document as it stands, nothing attached.</summary>
    Link,

    /// <summary>Attached, and the button too.</summary>
    Both,
}

/// <summary>How a form design's emails look.</summary>
public enum EmailStyle
{
    /// <summary>Logo and company name over a band of the accent color.</summary>
    Branded,

    /// <summary>The company name in the accent color, no logo.</summary>
    Simple,

    /// <summary>Plain text only.</summary>
    Plain,
}

/// <summary>A word in braces filled in when the message is written ("{customer}").</summary>
public sealed record EmailPlaceholder(string Key, string Description, string Sample);

/// <summary>The placeholders each message takes, filling them in, and the usual wording.</summary>
public static partial class EmailTemplates
{
    public const int MaxSubject = 300;
    public const int MaxBody = 20000;

    private static readonly EmailPlaceholder Company = new("company", "Your company's name", "Northwind Studio");
    private static readonly EmailPlaceholder Customer = new("customer", "The customer's name", "Acme Corp");
    private static readonly EmailPlaceholder Number = new("number", "The document's number", "1001");
    private static readonly EmailPlaceholder Date = new("date", "The document's date", Sample(new DateOnly(2026, 3, 1)));
    private static readonly EmailPlaceholder Total = new("total", "The document's total", "1,250.00");

    /// <summary>Every message kind in menu order (reminders, whose wording is kept with collections, included).</summary>
    public static IReadOnlyList<EmailKind> All { get; } = Enum.GetValues<EmailKind>();

    public static string Label(EmailKind kind) => kind switch
    {
        EmailKind.CreditMemo => "Credit memo",
        EmailKind.SalesReceipt => "Sales receipt",
        EmailKind.PurchaseOrder => "Purchase order",
        EmailKind.GiftAcknowledgment => "Gift acknowledgment",
        EmailKind.GivingStatement => "Giving statement",
        EmailKind.Reminder => "Payment reminder",
        EmailKind.Report => "Scheduled report",
        EmailKind.PaymentReceipt => "Payment receipt",
        _ => kind.ToString(),
    };

    /// <summary>The kind's key in addresses and the API ("credit-memo").</summary>
    public static string Key(EmailKind kind) => kind switch
    {
        EmailKind.CreditMemo => "credit-memo",
        EmailKind.SalesReceipt => "sales-receipt",
        EmailKind.PurchaseOrder => "purchase-order",
        EmailKind.GiftAcknowledgment => "gift-acknowledgment",
        EmailKind.GivingStatement => "giving-statement",
        EmailKind.PaymentReceipt => "payment-receipt",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static EmailKind? FromKey(string? key) =>
        All.Cast<EmailKind?>().FirstOrDefault(k => string.Equals(Key(k!.Value), key?.Trim(), StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(k!.Value.ToString(), key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<EmailPlaceholder> Placeholders(EmailKind kind) => kind switch
    {
        EmailKind.Invoice =>
        [
            Customer, Number, Date, new("due", "The due date", Sample(new DateOnly(2026, 3, 31))), Total,
            new("balance", "What's still owed", "1,250.00"), new("terms", "The payment terms", "Net 30"), Company,
        ],
        EmailKind.Estimate => [Customer, Number, Date, Total, new("valid_until", "The date it's valid until (blank when none)", Sample(new DateOnly(2026, 4, 30))), Company],
        EmailKind.CreditMemo or EmailKind.SalesReceipt => [Customer, Number, Date, Total, Company],
        EmailKind.Statement =>
        [
            Customer, new("from", "The statement's first day", Sample(new DateOnly(2026, 3, 1))),
            new("to", "The statement's last day", Sample(new DateOnly(2026, 3, 31))), new("balance", "The balance at the end", "2,480.00"), Company,
        ],
        EmailKind.PurchaseOrder =>
        [
            new("vendor", "The vendor's name", "Parts Supply Co."), Number, Date,
            new("expected", "The date you'd like it by (blank when none)", Sample(new DateOnly(2026, 3, 15))), Total, Company,
        ],
        EmailKind.GiftAcknowledgment => [new("donor", "The donor's name", "Pat Lee"), Number, Date, new("amount", "The gift's amount", "250.00"), Company],
        EmailKind.GivingStatement => [new("donor", "The donor's name", "Pat Lee"), new("year", "The year", "2026"), new("total", "The year's gifts", "1,200.00"), Company],
        EmailKind.Reminder =>
        [
            Customer, new("invoice", "The invoice's number", "1001"), new("balance", "What's still owed", "1,250.00"),
            new("due", "The due date", Sample(new DateOnly(2026, 3, 31))), new("days", "Days past the due date (0 before)", "5"),
            new("when", "\"is due on …\", \"is due today\" or \"was due on …, n days ago\"", $"was due on {Sample(new DateOnly(2026, 3, 31))}, 5 days ago"), Company,
        ],
        EmailKind.PaymentReceipt =>
        [
            Customer, new("amount", "The payment's amount", "1,250.00"), new("date", "The payment's date", Sample(new DateOnly(2026, 3, 1))),
            new("paid_by", "\"by check no. 1042\", \"by card\" (blank when not given)", "by check no. 1042"),
            new("applied", "A line per invoice it paid, with what's left on each", "- Invoice 1001: 1,250.00, paid in full"),
            new("credit", "\"… is kept as a credit\" when some wasn't used (blank otherwise)", ""),
            new("balance", "What the customer owes after it", "0.00"), Company,
        ],
        EmailKind.Report =>
        [
            new("report", "The saved report's name", "Profit and loss this month"), new("as_of", "The day it's sent", Sample(new DateOnly(2026, 3, 31))),
            new("scheduled_by", "Who set up the schedule", "pat"), Company,
        ],
        EmailKind.Invitation =>
        [
            new("greeting", "\"Hello Pat,\" or \"Hello,\" when there's no name", "Hello Pat,"), new("invited_by", "Who sent the invitation", "jordan"),
            new("access", "What they'll be able to do", "as standard user"), new("link", "The invitation link", "https://books.example.com/invite/obi_…"),
            new("expires", "The last day the link works", Sample(new DateOnly(2026, 3, 8))),
            new("access_ends", "\"Your access ends on …\" (blank when it doesn't)", ""), Company,
        ],
        _ => [Company],
    };

    /// <summary>Sample values for every placeholder of the kind, for previews.</summary>
    public static IReadOnlyDictionary<string, string> Samples(EmailKind kind) => Placeholders(kind).ToDictionary(p => p.Key, p => p.Sample);

    /// <summary>The wording OpenBook uses until it's changed.</summary>
    public static (string Subject, string Body) Usual(EmailKind kind) => kind switch
    {
        EmailKind.Invoice => ("Invoice {number} from {company}",
            "Hello,\n\nHere is invoice {number}. The balance of {balance} is due on {due}.\n\nThank you,\n{company}"),
        EmailKind.Estimate => ("Estimate {number} from {company}",
            "Hello,\n\nHere is estimate {number}, for {total}.\nIt's valid until {valid_until}.\n\nThank you,\n{company}"),
        EmailKind.CreditMemo => ("Credit memo {number} from {company}",
            "Hello,\n\nHere is credit memo {number}, for {total}.\n\nThank you,\n{company}"),
        EmailKind.SalesReceipt => ("Receipt {number} from {company}",
            "Hello,\n\nThank you for your purchase. Here is your receipt {number}.\n\nThank you,\n{company}"),
        EmailKind.Statement => ("Statement from {company}",
            "Hello,\n\nPlease find your statement of account as of {to} attached.\n\nThank you,\n{company}"),
        EmailKind.PurchaseOrder => ("Purchase order {number} from {company}",
            "Hello,\n\nPlease find our purchase order {number} attached.\nWe'd like it by {expected}.\n\nThank you,\n{company}"),
        EmailKind.GiftAcknowledgment => ("Thank you from {company}",
            "Dear {donor},\n\nThank you for your gift. Your acknowledgment letter is attached; please keep it for your tax records.\n\nWith gratitude,\n{company}"),
        EmailKind.GivingStatement => ("Your {year} giving statement from {company}",
            "Dear {donor},\n\nThank you for your support in {year}. Your giving statement is attached; please keep it for your tax records.\n\nWith gratitude,\n{company}"),
        EmailKind.Reminder => (Sales.CollectionSettings.DefaultSubject, Sales.CollectionSettings.DefaultBody),
        EmailKind.PaymentReceipt => ("Payment received: thank you from {company}",
            "Hello,\n\nThank you: we received your payment of {amount} on {date}.\nPaid {paid_by}.\n\n{applied}\n\n{credit}\nYour balance with us is now {balance}.\n\nThank you,\n{company}"),
        EmailKind.Report => ("{report}",
            "{report}, as of {as_of}, is attached.\n\nSent by OpenBook on a schedule set up by {scheduled_by}."),
        EmailKind.Invitation => ("You're invited to {company} in OpenBook",
            "{greeting}\n\n{invited_by} has invited you to the books of {company} in OpenBook, {access}.\n\nChoose your username and password here:\n{link}\n\n" +
            "The link works until {expires}. {access_ends}\nIf you weren't expecting this, you can ignore this email."),
        _ => ("{company}", ""),
    };

    /// <summary>
    /// Fills the placeholders the values give. A line whose placeholders all come out blank is left out, so
    /// "It's valid until {valid_until}." disappears when there's no date. Placeholders without a value stay as written.
    /// </summary>
    public static string Fill(string? template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrEmpty(template))
            return "";
        var lines = template.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var tokens = Token().Matches(line).Select(m => m.Groups[1].Value).Where(values.ContainsKey).ToList();
            if (tokens.Count > 0 && tokens.All(t => string.IsNullOrWhiteSpace(values[t])))
                continue;
            result.Add(Token().Replace(line, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value));
        }

        return string.Join('\n', result);
    }

    /// <summary>Placeholders in the text the kind doesn't take ("{amout}"), in the order they appear.</summary>
    public static IReadOnlyList<string> Unknown(EmailKind kind, string? text)
    {
        var known = Placeholders(kind).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return Token().Matches(text ?? "").Select(m => m.Groups[1].Value).Where(k => !known.Contains(k)).Distinct().ToList();
    }

    /// <summary>Refuses placeholders the kind doesn't take, so a typing slip doesn't reach customers.</summary>
    public static void Check(EmailKind kind, string? subject, string? body)
    {
        var unknown = Unknown(kind, subject).Concat(Unknown(kind, body)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new DomainException("email_template.placeholder_unknown",
                $"{string.Join(", ", unknown.Select(u => "{" + u + "}"))} isn't a placeholder for {Label(kind).ToLowerInvariant()} emails. " +
                $"You can use {string.Join(", ", Placeholders(kind).Select(p => "{" + p.Key + "}"))}.");
    }

    private static string Sample(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex Token();
}

/// <summary>A changed subject and message for one kind of email; kinds without one use <see cref="EmailTemplates.Usual"/>.</summary>
public sealed class EmailTemplate
{
    private EmailTemplate()
    {
        Subject = string.Empty;
        Body = string.Empty;
        UpdatedBy = string.Empty;
    }

    public EmailKind Kind { get; private set; }
    public string Subject { get; private set; }

    /// <summary>Simple formatting: **bold**, _italics_, [links](https://…), lists.</summary>
    public string Body { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; }

    public static EmailTemplate Create(EmailKind kind, string? subject, string? body, DateTimeOffset now, string actor)
    {
        if (kind == EmailKind.Reminder || !Enum.IsDefined(kind))
            throw new DomainException("email_template.kind_invalid", "Payment reminders are worded in the collection settings.");
        var template = new EmailTemplate { Kind = kind };
        template.Update(subject, body, now, actor);
        return template;
    }

    public void Update(string? subject, string? body, DateTimeOffset now, string actor)
    {
        var (cleanSubject, cleanBody) = Clean(Kind, subject, body);
        Subject = cleanSubject;
        Body = cleanBody;
        UpdatedAt = now.ToUniversalTime();
        UpdatedBy = actor;
    }

    /// <summary>A subject and message checked and tidied (trimmed subject, Unix line ends), as templates store them.</summary>
    public static (string Subject, string Body) Clean(EmailKind kind, string? subject, string? body)
    {
        var s = subject?.Trim() ?? "";
        if (s.Length == 0)
            throw new DomainException("email_template.subject_required", "Enter a subject.");
        if (s.Length > EmailTemplates.MaxSubject || s.Contains('\n', StringComparison.Ordinal))
            throw new DomainException("email.subject_too_long", $"The subject is one line of at most {EmailTemplates.MaxSubject} characters.");
        var b = (body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        if (b.Length > EmailTemplates.MaxBody)
            throw new DomainException("email.body_too_long", $"The message can have at most {EmailTemplates.MaxBody:N0} characters.");
        EmailTemplates.Check(kind, s, b);
        return (s, b);
    }
}

/// <summary>Lists of addresses as people type them ("a@x.com, b@y.com"; semicolons too).</summary>
public static class EmailAddresses
{
    public const int MaxEach = 10;

    /// <summary>The addresses, checked, without repeats; empty when blank.</summary>
    public static IReadOnlyList<string> Parse(string? text, string label, int max = MaxEach)
    {
        var list = (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => EmailSettings.Address(a, label)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count > max)
            throw new DomainException("email.too_many_addresses", $"{label} can have at most {max} addresses.");
        return list;
    }

    /// <summary>The addresses joined as stored and shown ("a@x.com, b@y.com"), or null when there are none.</summary>
    public static string? Join(IEnumerable<string> addresses)
    {
        var text = string.Join(", ", addresses);
        return text.Length == 0 ? null : text;
    }

    /// <summary>Checks and tidies a list, or null when blank.</summary>
    public static string? Normalize(string? text, string label, int max = MaxEach) => Join(Parse(text, label, max));
}

/// <summary>One email as it was sent: who it went to, what it said and what came with it. Append-only.</summary>
public sealed class SentEmail
{
    public const int MaxAddresses = 4000;

    private SentEmail()
    {
        EntityType = string.Empty;
        To = string.Empty;
        Subject = string.Empty;
        Body = string.Empty;
        SentBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public EmailKind Kind { get; private set; }

    /// <summary>The record it's about, as the audit log names it ("invoice", "customer" for statements).</summary>
    public string EntityType { get; private set; }

    public Guid EntityId { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public string SentBy { get; private set; }
    public string To { get; private set; }
    public string? Cc { get; private set; }
    public string? Bcc { get; private set; }
    public string Subject { get; private set; }

    /// <summary>The message as written (simple formatting), placeholders filled in.</summary>
    public string Body { get; private set; }

    /// <summary>The attached file's name; null when nothing was attached.</summary>
    public string? Attachment { get; private set; }

    /// <summary>The "View" link sent with it, if any.</summary>
    public Guid? DocumentLinkId { get; private set; }

    /// <summary>The form design it was sent with (its look, and the PDF's).</summary>
    public Guid? DesignId { get; private set; }

    public static SentEmail Create(
        EmailKind kind, string entityType, Guid entityId, DateTimeOffset now, string actor, string to, string? cc, string? bcc, string subject, string body,
        string? attachment, Guid? linkId, Guid? designId) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind,
        EntityType = entityType,
        EntityId = entityId,
        SentAt = now.ToUniversalTime(),
        SentBy = actor,
        To = Cut(to, MaxAddresses),
        Cc = cc is null ? null : Cut(cc, MaxAddresses),
        Bcc = bcc is null ? null : Cut(bcc, MaxAddresses),
        Subject = Cut(subject, EmailTemplates.MaxSubject),
        Body = Cut(body, EmailTemplates.MaxBody),
        Attachment = attachment is null ? null : Cut(attachment, 255),
        DocumentLinkId = linkId,
        DesignId = designId == Guid.Empty ? null : designId,
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>
/// A link sent instead of (or beside) the PDF: anyone holding it can open that document as it stands, until it's turned off.
/// The token is only in the email; its SHA-256 hash is stored.
/// </summary>
public sealed class DocumentLink
{
    public const string TokenPrefix = "obd_";

    private DocumentLink()
    {
        TokenHash = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public EmailKind Kind { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset? FirstOpenedAt { get; private set; }
    public DateTimeOffset? LastOpenedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    /// <summary>Invoices, estimates, credit memos and sales receipts can be sent as a link.</summary>
    public static bool CanLink(EmailKind kind) => kind is EmailKind.Invoice or EmailKind.Estimate or EmailKind.CreditMemo or EmailKind.SalesReceipt;

    public static (DocumentLink Link, string Token) Create(EmailKind kind, Guid documentId, Guid customerId, DateTimeOffset now, string actor)
    {
        if (!CanLink(kind))
            throw new DomainException("email.link_unsupported", $"{EmailTemplates.Label(kind)}s can't be sent as a link.");
        var token = TokenPrefix + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (new DocumentLink
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            DocumentId = documentId,
            CustomerId = customerId,
            TokenHash = Hash(token),
            CreatedAt = now.ToUniversalTime(),
            CreatedBy = actor,
        }, token);
    }

    public static string Hash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    public void Revoke(DateTimeOffset now)
    {
        if (!IsActive)
            throw new DomainException("email.link_revoked", "This link is already turned off.");
        RevokedAt = now.ToUniversalTime();
    }

    /// <summary>Records an opening; true when something changed (the first opening, or the last one more than a minute ago).</summary>
    public bool Opened(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (LastOpenedAt is { } last && now - last < TimeSpan.FromMinutes(1))
            return false;
        FirstOpenedAt ??= now;
        LastOpenedAt = now;
        return true;
    }
}
