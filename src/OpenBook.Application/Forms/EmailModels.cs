using OpenBook.Domain.Auditing;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Forms;

/// <summary>What an email is about: a document (by id), a customer's statement for a period, or a donor's year.</summary>
public sealed record EmailSource(EmailKind Kind, Guid Id, DateOnly? From = null, DateOnly? To = null, int? Year = null)
{
    /// <summary>A statement's period: as given, else the month up to today.</summary>
    public (DateOnly From, DateOnly To) Period(DateOnly today)
    {
        var to = To ?? today;
        return (From ?? to.AddMonths(-1).AddDays(1), to);
    }
}

/// <summary>A message as written, before sending; the user can change all of it.</summary>
/// <param name="To">One address or several, separated by commas.</param>
/// <param name="DesignId">The form design to send with (its look, and the PDF's); null = the usual one.</param>
/// <param name="Pdf">Attach the PDF, send a link, or both; null = what the customer is set to get.</param>
public sealed record EmailDraft(string To, string Subject, string Body, string? Cc = null, string? Bcc = null, Guid? DesignId = null, EmailPdf? Pdf = null);

/// <summary>A button under the message ("Pay now", "View invoice").</summary>
public sealed record EmailButton(string Label, string Url);

/// <summary>How a message looks, from its form design and the company.</summary>
public sealed record EmailLook(
    EmailStyle Style, string AccentColor, byte[]? Logo, string? LogoContentType, string CompanyName, IReadOnlyList<string> ContactLines, string? Footer);

/// <summary>A message ready to send: subject, plain text, and HTML (null for plain-text designs) with its inline images.</summary>
public sealed record ComposedEmail(string Subject, string Text, string? Html, IReadOnlyList<EmailInlineImage> Images);

public sealed record EmailInlineImage(string ContentId, string ContentType, byte[] Content);

/// <summary>What a message will look like, for the preview beside the form.</summary>
/// <param name="Html">A whole page for a sandboxed frame (plain-text messages are shown as text in it).</param>
/// <param name="Notes">Things to know before sending (an address that isn't valid, a link that can't be made).</param>
/// <param name="UsualDesign">The design the email goes with when none is chosen.</param>
public sealed record EmailPreview(
    string From, string To, string? Cc, string? Bcc, string Subject, string Html, string Text, string? Attachment, IReadOnlyList<string> Buttons,
    IReadOnlyList<string> Notes, string? UsualDesign = null);

/// <summary>What happened when the same kind of email went to several people.</summary>
/// <param name="Sent">Who got theirs.</param>
/// <param name="NoEmail">Who has no address.</param>
/// <param name="Failed">Who couldn't be reached, and why.</param>
/// <param name="AlreadySent">Who got this kind of email in the last hour, and was left out so a second press doesn't send it twice.</param>
public sealed record EmailRun(IReadOnlyList<string> Sent, IReadOnlyList<string> NoEmail, IReadOnlyList<(string Name, string Reason)> Failed, IReadOnlyList<string>? AlreadySent = null);

/// <summary>An email from the history, with its link when one was sent.</summary>
public sealed record SentEmailEntry(SentEmail Email, DocumentLink? Link);

/// <summary>A kind of email's subject and message, and whether they're still the usual ones.</summary>
public sealed record EmailWording(EmailKind Kind, string Subject, string Body, bool IsUsual, DateTimeOffset? UpdatedAt = null, string? UpdatedBy = null);

/// <summary>What an <see cref="IEmailSource"/> knows about the record an email is about.</summary>
/// <param name="Name">Who it's for, as lists show them ("Acme Corp").</param>
/// <param name="Title">The document as the subject falls back to ("Invoice 1001").</param>
/// <param name="FileName">The attached PDF's name.</param>
/// <param name="Document">The design the email and PDF use; null = the default design.</param>
/// <param name="Customer">The customer whose copy addresses, design and link choice apply.</param>
/// <param name="PayOnline">An open invoice and online payments set up: the email gets a "Pay now" button.</param>
/// <param name="Refusal">Why it can't be sent (a cancelled order), as a domain error code and message.</param>
/// <param name="Attaches">False for messages that say it all themselves (a payment receipt): no PDF and no link.</param>
public sealed record EmailContext(
    string EntityType, Guid EntityId, string Name, string Title, string FileName, string? To, IReadOnlyDictionary<string, string> Values,
    FormDocuments? Document = null, Customer? Customer = null, bool PayOnline = false, (string Code, string Message)? Refusal = null, bool Attaches = true)
{
    public bool CanLink(EmailKind kind) => Attaches && Customer is not null && DocumentLink.CanLink(EmailService.LinkKind(kind));
}

/// <summary>One family of documents that can be emailed (sales forms, purchase orders, donor letters).</summary>
public interface IEmailSource
{
    IReadOnlyCollection<EmailKind> Kinds { get; }

    /// <summary>Throws <c>EntityNotFoundException</c> when the record doesn't exist.</summary>
    Task<EmailContext> ContextAsync(EmailSource source, CancellationToken ct);

    Task<RenderedForm> RenderAsync(EmailSource source, Guid? designId, CancellationToken ct);

    /// <summary>A payment link for an open invoice; null when there's none to give.</summary>
    Task<string?> PayLinkAsync(EmailSource source, CancellationToken ct);

    /// <summary>Marks the record emailed (in memory; the email service saves) and returns its own audit record, or null for the usual one.</summary>
    Task<AuditRecord?> SentAsync(EmailSource source, string recipients, string title, CancellationToken ct);
}

/// <summary>Templates, sent emails and their links.</summary>
public interface IEmailStore
{
    Task<IReadOnlyList<EmailTemplate>> ListTemplatesAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<EmailTemplate?> GetTemplateAsync(EmailKind kind, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SentEmail>> ListSentAsync(string entityType, Guid entityId, CancellationToken ct);

    Task<IReadOnlyList<DocumentLink>> GetLinksAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<DocumentLink?> GetLinkAsync(Guid id, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<DocumentLink?> FindLinkAsync(string tokenHash, CancellationToken ct);

    void Add(EmailTemplate emailTemplate);
    void Remove(EmailTemplate emailTemplate);
    void Add(SentEmail email);
    void Add(DocumentLink link);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}
