using OpenBook.Domain.Companies;

namespace OpenBook.Application.Forms;

/// <summary>Who a form is from or to.</summary>
public sealed record FormParty(string Name, string? Address = null, string? Email = null, string? Phone = null);

public sealed record FormStyle(FormTemplate Template, string AccentColor, string? Footer);

/// <param name="Quantity">Null for notes and discount lines.</param>
public sealed record FormLine(string? Description, decimal? Quantity, decimal? Rate, decimal? Amount);

public sealed record FormTotal(string Label, decimal Amount, bool Emphasis = false);

/// <summary>An invoice, estimate, credit memo or sales receipt, ready to render.</summary>
/// <param name="Facts">Label/value pairs under the title: date, due date, terms…</param>
public sealed record SalesForm(
    string Title,
    string Number,
    FormParty Company,
    FormParty Customer,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<FormLine> Lines,
    IReadOnlyList<FormTotal> Totals,
    string? Message,
    string Currency,
    FormStyle Style);

public sealed record StatementFormLine(DateOnly Date, string Activity, string Reference, decimal Amount, decimal Balance);

public sealed record StatementForm(
    FormParty Company,
    FormParty Customer,
    DateOnly From,
    DateOnly To,
    decimal OpeningBalance,
    IReadOnlyList<StatementFormLine> Lines,
    decimal ClosingBalance,
    IReadOnlyList<(string Label, decimal Amount)> Aging,
    string Currency,
    FormStyle Style);

/// <summary>Renders forms to PDF. Implemented with MigraDoc in Infrastructure.</summary>
public interface IFormRenderer
{
    byte[] Render(SalesForm form);
    byte[] Render(StatementForm form);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    string To, string Subject, string Body, IReadOnlyList<EmailAttachment> Attachments, string? ReplyTo = null);

/// <summary>Sends email with the company's settings. Implemented with MailKit in Infrastructure.</summary>
public interface IEmailSender
{
    /// <param name="password">The SMTP password, already decrypted.</param>
    Task SendAsync(EmailSettings settings, string? password, EmailMessage message, CancellationToken ct);
}

/// <summary>Encrypts secrets kept in the company file (the SMTP password). Implemented by the host.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the secret can't be read, e.g. it was encrypted on another computer.</summary>
    string? Unprotect(string protectedText);
}
