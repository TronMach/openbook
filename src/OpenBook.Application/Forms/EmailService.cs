using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Forms;

public sealed record EmailSettingsCommand(
    EmailDelivery Delivery,
    string? Host,
    int Port,
    SmtpSecurity Security,
    string? Username,
    string? NewPassword,
    bool ClearPassword,
    string? FromAddress,
    string? FromName,
    string? BccAddress);

/// <summary>A suggested message for a form; the user can edit it before sending.</summary>
public sealed record EmailDraft(string To, string Subject, string Body);

/// <summary>What happened when statements were emailed to several customers.</summary>
public sealed record StatementEmailRun(IReadOnlyList<string> Sent, IReadOnlyList<string> NoEmail, IReadOnlyList<(string Customer, string Reason)> Failed);

/// <summary>Email settings, and sending forms to customers as PDF attachments.</summary>
public sealed class EmailService(
    ICompanyStore companies,
    ISalesStore sales,
    FormService forms,
    IEmailSender sender,
    ISecretProtector secrets,
    Sales.OnlinePaymentService online,
    IClock clock,
    ICurrentUser user)
{
    private const string SettingsEntity = "email_settings";

    public async Task<EmailSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await companies.GetEmailSettingsAsync(ct) ?? EmailSettings.Default();

    public async Task<EmailSettings> SaveSettingsAsync(EmailSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var settings = await companies.GetEmailSettingsAsync(ct);
        if (settings is null)
        {
            settings = EmailSettings.Default();
            companies.Add(settings);
        }

        var before = Snapshot(settings);
        var password = command.ClearPassword ? "" : string.IsNullOrEmpty(command.NewPassword) ? null : secrets.Protect(command.NewPassword);
        settings.Update(command.Delivery, command.Host, command.Port, command.Security, command.Username, password,
            command.FromAddress, command.FromName, command.BccAddress);
        companies.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, company.Id, before, Snapshot(settings)));
        await companies.SaveChangesAsync(ct);
        return settings;
    }

    public async Task SendTestAsync(string to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        await SendAsync(new EmailMessage(
            EmailSettings.Address(to, "To") ?? throw new DomainException("email.to_required", "Enter an address to send to."),
            $"Test message from {company.Name}",
            "This is a test message from OpenBook. If you can read it, email is set up correctly.",
            []), ct);
    }

    /// <summary>The usual message for a form, addressed to the customer's email.</summary>
    public async Task<EmailDraft> DraftAsync(FormKind kind, Guid id, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var (customerId, subject, body) = kind switch
        {
            FormKind.Invoice => await sales.GetInvoiceAsync(id, ct) is { } i
                ? (i.CustomerId, $"Invoice {i.Number} from {company.Name}",
                    $"Please find invoice {i.Number} attached. The balance of {i.Balance:N2} is due on {i.DueDate:d}.")
                : throw new EntityNotFoundException("invoice", id),
            FormKind.Estimate => await sales.GetEstimateAsync(id, ct) is { } e
                ? (e.CustomerId, $"Estimate {e.Number} from {company.Name}",
                    $"Please find estimate {e.Number} attached, for {e.Total:N2}." + (e.ExpirationDate is { } x ? $" It's valid until {x:d}." : ""))
                : throw new EntityNotFoundException("estimate", id),
            FormKind.CreditMemo => await sales.GetCreditMemoAsync(id, ct) is { } c
                ? (c.CustomerId, $"Credit memo {c.Number} from {company.Name}", $"Please find credit memo {c.Number} attached, for {c.Total:N2}.")
                : throw new EntityNotFoundException("credit_memo", id),
            _ => await sales.GetReceiptAsync(id, ct) is { } r
                ? (r.CustomerId ?? Guid.Empty, $"Receipt {r.Number} from {company.Name}", $"Thank you for your purchase. Your receipt {r.Number} is attached.")
                : throw new EntityNotFoundException("sales_receipt", id),
        };
        var email = await sales.GetCustomerAsync(customerId, ct) is { Email: { } e2 } ? e2 : "";
        if (kind == FormKind.Invoice && await PayLinkAsync(id, ct) is { } url)
            body += $"\n\nPay online: {url}";
        return new EmailDraft(email, subject, $"Hello,\n\n{body}\n\nThank you,\n{company.Name}");
    }

    /// <summary>A payment link for an open invoice when online payments are on; null otherwise or if the provider can't be reached.</summary>
    private async Task<string?> PayLinkAsync(Guid invoiceId, CancellationToken ct)
    {
        if (!(await online.GetSettingsAsync(ct)).IsReady || await sales.GetInvoiceAsync(invoiceId, ct) is not { IsOpen: true })
            return null;
        try
        {
            return (await online.LinkForInvoiceAsync(invoiceId, ct)).Url;
        }
        catch (DomainException)
        {
            return null;
        }
    }

    public async Task<EmailDraft> DraftStatementAsync(Guid customerId, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        return new EmailDraft(customer.Email ?? "", $"Statement from {company.Name}",
            $"Hello,\n\nPlease find your statement of account as of {to:d} attached.\n\nThank you,\n{company.Name}");
    }

    /// <summary>Renders the form, sends it as an attachment, and records the send.</summary>
    public async Task SendFormAsync(FormKind kind, Guid id, EmailDraft message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var rendered = await forms.RenderAsync(kind, id, ct);
        await SendAsync(Message(message, rendered), ct);

        var now = clock.UtcNow;
        switch (kind)
        {
            case FormKind.Invoice:
                (await sales.GetInvoiceAsync(id, ct))!.MarkEmailed(now);
                break;
            case FormKind.Estimate:
                (await sales.GetEstimateAsync(id, ct))!.MarkEmailed(now);
                break;
            case FormKind.SalesReceipt:
                (await sales.GetReceiptAsync(id, ct))!.MarkEmailed(now);
                break;
        }

        sales.Add(Sent(EntityTypeOf(kind), id, message.To, rendered.Title));
        await sales.SaveChangesAsync(ct);
    }

    public async Task SendStatementAsync(Guid customerId, DateOnly from, DateOnly to, EmailDraft message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var rendered = await forms.RenderStatementAsync(customerId, from, to, ct);
        await SendAsync(Message(message, rendered), ct);
        sales.Add(Sent("customer", customerId, message.To, rendered.Title));
        await sales.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Emails each customer their statement for the period with the usual message, in name order, each send recorded
    /// on its own. Customers without an email address are skipped and listed; a failed send is listed and the rest go
    /// on. Stops before sending anything when email isn't set up.
    /// </summary>
    public async Task<StatementEmailRun> SendStatementsAsync(IReadOnlyCollection<Guid> customerIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        if (from > to)
            throw new DomainException("statement.dates_invalid", "The statement's end date is before its start date.");
        if (!(await GetSettingsAsync(ct)).IsConfigured)
            throw new DomainException("email.not_configured", "Set up email in Settings before sending.");

        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var sent = new List<string>();
        var noEmail = new List<string>();
        var failed = new List<(string, string)>();
        foreach (var customer in customerIds.Distinct().Select(id => customers.GetValueOrDefault(id) ?? throw new EntityNotFoundException("customer", id))
                     .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(customer.Email))
            {
                noEmail.Add(customer.DisplayName);
                continue;
            }

            try
            {
                await SendStatementAsync(customer.Id, from, to, await DraftStatementAsync(customer.Id, to, ct), ct);
                sent.Add(customer.DisplayName);
            }
            catch (DomainException ex)
            {
                failed.Add((customer.DisplayName, ex.Message));
            }
        }

        return new StatementEmailRun(sent, noEmail, failed);
    }

    private static EmailMessage Message(EmailDraft draft, RenderedForm rendered)
    {
        var to = EmailSettings.Address(draft.To, "To") ?? throw new DomainException("email.to_required", "Enter the customer's email address.");
        var subject = string.IsNullOrWhiteSpace(draft.Subject) ? rendered.Title : draft.Subject.Trim();
        if (subject.Length > 300)
            throw new DomainException("email.subject_too_long", "The subject can have at most 300 characters.");
        return new EmailMessage(to, subject, draft.Body ?? "", [new EmailAttachment(rendered.FileName, "application/pdf", rendered.Pdf)]);
    }

    /// <summary>Sends a message as it is (for scheduled reports); the caller records the send.</summary>
    internal Task SendMessageAsync(EmailMessage message, CancellationToken ct) => SendAsync(message, ct);

    private async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.IsConfigured)
            throw new DomainException("email.not_configured", "Set up email in Settings before sending.");
        string? password = null;
        if (settings.ProtectedPassword is { } stored)
            password = secrets.Unprotect(stored)
                ?? throw new DomainException("email.password_unreadable", "The saved SMTP password can't be read on this computer. Enter it again in Settings.");
        try
        {
            await sender.SendAsync(settings, password, message, ct);
        }
        catch (Exception ex) when (ex is not DomainException and not OperationCanceledException)
        {
            throw new DomainException("email.send_failed", $"The email wasn't sent: {ex.Message}");
        }
    }

    private AuditRecord Sent(string entityType, Guid id, string to, string what) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, entityType, id, null,
            JsonSerializer.Serialize(new { To = to, Form = what }));

    private static string EntityTypeOf(FormKind kind) => kind switch
    {
        FormKind.Invoice => "invoice",
        FormKind.Estimate => EstimateService.EntityType,
        FormKind.CreditMemo => "credit_memo",
        _ => "sales_receipt",
    };

    private static string Snapshot(EmailSettings s) => JsonSerializer.Serialize(new
    {
        Delivery = s.Delivery.ToString(),
        s.Host,
        s.Port,
        Security = s.Security.ToString(),
        s.Username,
        HasPassword = s.ProtectedPassword is not null,
        s.FromAddress,
        s.FromName,
        s.BccAddress,
    });
}
