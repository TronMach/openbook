using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Sales;

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
    string? BccAddress,
    string? LinkAddress = null);

/// <summary>
/// Email settings, the wording of each kind of email (<see cref="EmailTemplate"/>), and writing, previewing and
/// sending messages: a document's email is filled in from its template, looks like its form design, carries the PDF
/// (or a "View" link, as the customer prefers) and a "Pay now" button for open invoices, and is kept in the history.
/// </summary>
public sealed class EmailService(
    ICompanyStore companies,
    ISalesStore sales,
    IEmailStore store,
    IEnumerable<IEmailSource> sources,
    FormDesignService designs,
    IEmailSender sender,
    ISecretProtector secrets,
    OnlinePaymentService online,
    IClock clock,
    ICurrentUser user)
{
    public const string TemplateEntity = "email_template";
    private const string SettingsEntity = "email_settings";
    private const string PreviewLink = "#";

    // ---- Settings ----

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
            command.FromAddress, command.FromName, command.BccAddress, command.LinkAddress);
        companies.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, company.Id, before, Snapshot(settings)));
        await companies.SaveChangesAsync(ct);
        return settings;
    }

    public async Task SendTestAsync(string to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        await TransportAsync(new EmailMessage(
            EmailSettings.Address(to, "To") ?? throw new DomainException("email.to_required", "Enter an address to send to."),
            $"Test message from {company.Name}",
            "This is a test message from OpenBook. If you can read it, email is set up correctly.",
            []), ct);
    }

    // ---- Wording ----

    /// <summary>Every kind's subject and message (payment reminders excepted: they're worded in the collection settings).</summary>
    public async Task<IReadOnlyList<EmailWording>> TemplatesAsync(CancellationToken ct = default)
    {
        var saved = (await store.ListTemplatesAsync(ct)).ToDictionary(t => t.Kind);
        return [.. EmailTemplates.All.Where(k => k != EmailKind.Reminder).Select(k => Wording(k, saved.GetValueOrDefault(k)))];
    }

    public async Task<EmailWording> TemplateAsync(EmailKind kind, CancellationToken ct = default) =>
        Wording(kind, kind == EmailKind.Reminder ? null : await store.GetTemplateAsync(kind, ct));

    public async Task<EmailWording> SaveTemplateAsync(EmailKind kind, string? subject, string? body, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var template = await store.GetTemplateAsync(kind, ct);
        var before = template is null ? null : TemplateSnapshot(template.Kind, template.Subject, template.Body);
        if (template is null)
        {
            template = EmailTemplate.Create(kind, subject, body, clock.UtcNow, user.Actor);
            store.Add(template);
        }
        else
        {
            template.Update(subject, body, clock.UtcNow, user.Actor);
        }

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, before is null ? AuditAction.Created : AuditAction.Updated, TemplateEntity, company.Id,
            before, TemplateSnapshot(kind, template.Subject, template.Body)));
        await store.SaveChangesAsync(ct);
        return Wording(kind, template);
    }

    /// <summary>Goes back to the usual wording.</summary>
    public async Task<EmailWording> ResetTemplateAsync(EmailKind kind, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (await store.GetTemplateAsync(kind, ct) is { } template)
        {
            store.Remove(template);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, TemplateEntity, company.Id,
                TemplateSnapshot(kind, template.Subject, template.Body), null));
            await store.SaveChangesAsync(ct);
        }

        return Wording(kind, null);
    }

    /// <summary>A template's preview with sample values, in a design's look (the default design when none is given).</summary>
    public async Task<EmailPreview> PreviewTemplateAsync(EmailKind kind, string? subject, string? body, Guid? designId = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var values = new Dictionary<string, string>(EmailTemplates.Samples(kind)) { ["company"] = company.Name };
        var notes = new List<string>();
        try
        {
            EmailTemplates.Check(kind, subject, body);
        }
        catch (DomainException ex)
        {
            notes.Add(ex.Message);
        }

        var design = await designs.ChosenAsync(FormDocuments.Invoice, null, designId, ct);
        var buttons = new List<string>();
        if (DocumentLink.CanLink(LinkKind(kind)))
            buttons.Add(Article(kind) + " (when the customer gets links)");
        if (kind is EmailKind.Invoice or EmailKind.Reminder && (await online.GetSettingsAsync(ct)).IsReady)
            buttons.Add("Pay now (open invoices)");
        var composed = EmailFormatter.Compose(Look(design, company), EmailTemplates.Fill(subject, values), EmailTemplates.Fill(body, values),
            [.. buttons.Select(b => new EmailButton(b[..b.IndexOf(" (", StringComparison.Ordinal)], PreviewLink))], preview: true);
        if (buttons.Count > 0)
            notes.Add($"Sample values. The {string.Join(" and ", buttons.Select(b => b[..b.IndexOf(" (", StringComparison.Ordinal)]))} button shows {string.Join("; ", buttons.Select(b => b[(b.IndexOf(" (", StringComparison.Ordinal) + 2)..^1]))}.");
        var settings = await GetSettingsAsync(ct);
        return new EmailPreview(From(settings), "pat@example.com", null, null, composed.Subject, composed.Html ?? EmailFormatter.TextPage(composed.Text),
            composed.Text, kind is EmailKind.Report or EmailKind.Invitation or EmailKind.PaymentReceipt ? null : $"{EmailTemplates.Label(kind)}.pdf", buttons, notes);
    }

    // ---- Writing and sending a document's email ----

    /// <summary>The usual message for a record, addressed to its customer, vendor or donor.</summary>
    public async Task<EmailDraft> DraftAsync(EmailSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var context = await Source(source.Kind).ContextAsync(source, ct);
        var wording = await TemplateAsync(source.Kind, ct);
        return new EmailDraft(context.To ?? "", EmailTemplates.Fill(wording.Subject, context.Values), EmailTemplates.Fill(wording.Body, context.Values),
            context.Customer?.EmailCc, null, null, context.CanLink(source.Kind) ? context.Customer!.EmailPdf : null);
    }

    /// <summary>What the message will look like. Mistakes (a bad address) come back as notes, not errors, so the preview keeps up with typing.</summary>
    public async Task<EmailPreview> PreviewAsync(EmailSource source, EmailDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draft);
        var context = await Source(source.Kind).ContextAsync(source, ct);
        var settings = await GetSettingsAsync(ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var notes = new List<string>();
        string Addresses(string? text, string label)
        {
            try
            {
                return EmailAddresses.Join(EmailAddresses.Parse(text, label)) ?? "";
            }
            catch (DomainException ex)
            {
                notes.Add(ex.Message);
                return text?.Trim() ?? "";
            }
        }

        var to = Addresses(draft.To, "To");
        if (to.Length == 0)
            notes.Add("Enter the address to send to.");
        var cc = Addresses(draft.Cc, "Cc");
        var bcc = Addresses(draft.Bcc, "Bcc");
        if (context.Refusal is { } refusal)
            notes.Add(refusal.Message);
        var leftOver = Leftovers(draft);
        if (leftOver.Count > 0)
            notes.Add($"{string.Join(", ", leftOver)} will be sent as written: it isn't filled in here.");

        FormDesign design;
        try
        {
            design = await DesignAsync(context, draft.DesignId, ct);
        }
        catch (DomainException ex)
        {
            notes.Add(ex.Message);
            design = await DesignAsync(context, null, ct);
        }

        var pdf = Delivery(source.Kind, context, draft, await LinkBaseAsync(settings, ct), notes);
        var buttons = new List<EmailButton>();
        if (pdf != EmailPdf.Attach)
            buttons.Add(new EmailButton(Article(source.Kind), PreviewLink));
        if (context.PayOnline)
            buttons.Add(new EmailButton("Pay now", PreviewLink));
        if (buttons.Count > 0)
            notes.Add("The buttons get their links when the email is sent.");

        var subject = string.IsNullOrWhiteSpace(draft.Subject) ? context.Title : draft.Subject.Trim();
        var composed = EmailFormatter.Compose(Look(design, company), subject, draft.Body ?? "", buttons, preview: true);
        return new EmailPreview(From(settings), to, cc.Length == 0 ? null : cc, bcc.Length == 0 ? null : bcc, subject,
            composed.Html ?? EmailFormatter.TextPage(composed.Text), composed.Text, pdf == EmailPdf.Link || !context.Attaches ? null : context.FileName,
            [.. buttons.Select(b => b.Label)], notes, (await DesignAsync(context, null, ct)).Name);
    }

    /// <summary>Sends the message with the PDF (or link), records it in the history and audit log, and marks the record emailed.</summary>
    public async Task<SentEmail> SendAsync(EmailSource source, EmailDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draft);
        var provider = Source(source.Kind);
        var context = await provider.ContextAsync(source, ct);
        if (context.Refusal is { } refusal)
            throw new DomainException(refusal.Code, refusal.Message);
        var (to, cc, bcc) = Recipients(draft);
        var subject = Subject(draft, context);
        var body = Body(draft);
        var settings = await ConfiguredAsync(ct);
        var design = await DesignAsync(context, draft.DesignId, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);

        var linkBase = await LinkBaseAsync(settings, ct);
        var pdf = Delivery(source.Kind, context, draft, linkBase, []);
        var attachments = new List<EmailAttachment>();
        string? attached = null;
        if (pdf != EmailPdf.Link && context.Attaches)
        {
            var rendered = await provider.RenderAsync(source, draft.DesignId, ct);
            attachments.Add(new EmailAttachment(rendered.FileName, "application/pdf", rendered.Pdf));
            attached = rendered.FileName;
        }

        var buttons = new List<EmailButton>();
        DocumentLink? link = null;
        string? token = null;
        if (pdf != EmailPdf.Attach)
        {
            (link, token) = DocumentLink.Create(LinkKind(source.Kind), source.Id, context.Customer!.Id, clock.UtcNow, user.Actor, draft.DesignId);
            buttons.Add(new EmailButton(Article(source.Kind), $"{linkBase}/view/{token}"));
        }

        if (context.PayOnline)
        {
            // Through OpenBook when customers can reach it: a checkout page lasts a day, an invoice's terms a month.
            if (token is null && linkBase is not null)
                (link, token) = DocumentLink.Create(LinkKind(source.Kind), source.Id, context.Customer!.Id, clock.UtcNow, user.Actor);
            var payUrl = token is null ? await provider.PayLinkAsync(source, ct) : $"{linkBase}/view/{token}/pay";
            if (payUrl is not null)
                buttons.Add(new EmailButton("Pay now", payUrl));
        }

        var composed = EmailFormatter.Compose(Look(design, company), subject, body, buttons);
        await TransportAsync(Message(to, cc, bcc, composed, attachments), ct);

        // Only once it's gone: a failed send leaves nothing behind (not even the link).
        var toText = EmailAddresses.Join(to)!;
        if (link is not null)
            store.Add(link);
        var sent = SentEmail.Create(source.Kind, context.EntityType, context.EntityId, clock.UtcNow, user.Actor, toText, EmailAddresses.Join(cc),
            EmailAddresses.Join(bcc), subject, body, attached, link?.Id, design.Id);
        store.Add(sent);
        store.Add(await provider.SentAsync(source, toText, context.Title, ct) ?? Sent(context.EntityType, context.EntityId, toText, context.Title));
        await store.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>
    /// Sends the message exactly as it would go, but to the person writing it (their email, else the from address) with
    /// "[Test]" before the subject; nothing is marked emailed or kept, and the buttons don't work. Returns the address.
    /// </summary>
    public async Task<string> SendTestAsync(EmailSource source, EmailDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draft);
        var settings = await ConfiguredAsync(ct);
        var provider = Source(source.Kind);
        var context = await provider.ContextAsync(source, ct);
        Recipients(draft with { To = string.IsNullOrWhiteSpace(draft.To) ? settings.FromAddress! : draft.To });
        var me = await companies.FindUserAsync(User.NormalizeUsername(user.Actor), ct) is { Email: { } email } ? email : settings.FromAddress!;
        var design = await DesignAsync(context, draft.DesignId, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var pdf = Delivery(source.Kind, context, draft, await LinkBaseAsync(settings, ct), []);
        var attachments = new List<EmailAttachment>();
        if (pdf != EmailPdf.Link && context.Attaches)
        {
            var rendered = await provider.RenderAsync(source, draft.DesignId, ct);
            attachments.Add(new EmailAttachment(rendered.FileName, "application/pdf", rendered.Pdf));
        }

        var buttons = new List<EmailButton>();
        if (pdf != EmailPdf.Attach)
            buttons.Add(new EmailButton(Article(source.Kind), PreviewLink));
        if (context.PayOnline)
            buttons.Add(new EmailButton("Pay now", PreviewLink));
        var composed = EmailFormatter.Compose(Look(design, company), "[Test] " + Subject(draft, context), Body(draft), buttons);
        await TransportAsync(Message([me], [], [], composed, attachments), ct);
        return me;
    }

    private const string SendFailedCode = "email.send_failed";
    private const int MaxFailuresInARow = 2;

    /// <summary>A bulk send run again within this long leaves out who already has theirs: that's a second press, or a run picked up after a failure.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Sends each record its usual email, in the order given, each send kept on its own. Records without an address
    /// are skipped and listed; a failed send is listed and the rest go on. Stops before sending anything when email isn't set up.
    /// </summary>
    public async Task<EmailRun> SendEachAsync(IReadOnlyList<EmailSource> each, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(each);
        await ConfiguredAsync(ct);
        var sent = new List<string>();
        var noEmail = new List<string>();
        var failed = new List<(string, string)>();
        var already = new List<string>();
        var inARow = 0;
        foreach (var source in each)
        {
            var context = await Source(source.Kind).ContextAsync(source, ct);
            if (string.IsNullOrWhiteSpace(context.To))
            {
                noEmail.Add(context.Name);
                continue;
            }

            if ((await store.ListSentAsync(context.EntityType, context.EntityId, ct)).Any(e => e.Kind == source.Kind && clock.UtcNow - e.SentAt < RepeatWindow))
            {
                already.Add(context.Name);
                continue;
            }

            // A mail server that's stopped answering would otherwise hold this request for half a minute per person.
            if (inARow >= MaxFailuresInARow)
            {
                failed.Add((context.Name, "Not tried: sending had stopped working."));
                continue;
            }

            try
            {
                await SendAsync(source, await DraftAsync(source, ct), ct);
                sent.Add(context.Name);
                inARow = 0;
            }
            catch (DomainException ex)
            {
                failed.Add((context.Name, ex.Message));
                if (ex.Code == SendFailedCode)
                    inARow++;
            }
        }

        return new EmailRun(sent, noEmail, failed, already);
    }

    /// <summary>
    /// Emails each customer their statement for the period with the usual message, in name order.
    /// </summary>
    public async Task<EmailRun> SendStatementsAsync(IReadOnlyCollection<Guid> customerIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        if (from > to)
            throw new DomainException("statement.dates_invalid", "The statement's end date is before its start date.");
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        // A billed sub-customer's statement is its parent's: one each, to whoever is billed.
        var ordered = customerIds.Distinct().Select(id => customers.GetValueOrDefault(id) ?? throw new EntityNotFoundException("customer", id))
            .Select(c => customers[CustomerTree.BillTo(c.Id, customers)]).DistinctBy(c => c.Id)
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new EmailSource(EmailKind.Statement, c.Id, from, to)).ToList();
        return await SendEachAsync(ordered, ct);
    }

    /// <summary>
    /// A message that isn't about a document (a scheduled report, an invitation): the kind's wording filled in, the
    /// default design's look, kept in the history. The caller saves (with its own audit record).
    /// </summary>
    public async Task SendWordedAsync(
        EmailKind kind, string to, IReadOnlyDictionary<string, string> values, IReadOnlyList<EmailAttachment> attachments, EmailButton? button,
        string entityType, Guid entityId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(attachments);
        await ConfiguredAsync(ct);
        var wording = await TemplateAsync(kind, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var design = await designs.DefaultAsync(ct);
        var subject = EmailTemplates.Fill(wording.Subject, values);
        var body = EmailTemplates.Fill(wording.Body, values);
        var composed = EmailFormatter.Compose(Look(design, company), subject, body, button is null ? [] : [button]);
        var address = EmailSettings.Address(to, "To") ?? throw new DomainException("email.to_required", "Enter the address to send to.");
        await TransportAsync(Message([address], [], [], composed, attachments), ct);

        // An invitation's link signs someone up: it goes in the email and nowhere else (the history is in backups and exports).
        var kept = kind == EmailKind.Invitation
            ? EmailTemplates.Fill(wording.Body, new Dictionary<string, string>(values) { ["link"] = "(link removed)" })
            : body;
        store.Add(SentEmail.Create(kind, entityType, entityId, clock.UtcNow, user.Actor, address, null, null, subject, kept,
            attachments.Count == 0 ? null : string.Join(", ", attachments.Select(a => a.FileName)), null, design.Id));
    }

    // ---- The older entry points, kept for their callers ----

    public Task<EmailDraft> DraftAsync(FormKind kind, Guid id, CancellationToken ct = default) => DraftAsync(new EmailSource(KindOf(kind), id), ct);

    public Task<EmailDraft> DraftStatementAsync(Guid customerId, DateOnly to, CancellationToken ct = default) =>
        DraftAsync(new EmailSource(EmailKind.Statement, customerId, To: to), ct);

    public Task SendFormAsync(FormKind kind, Guid id, EmailDraft message, CancellationToken ct = default) =>
        SendAsync(new EmailSource(KindOf(kind), id), message, ct);

    public Task SendStatementAsync(Guid customerId, DateOnly from, DateOnly to, EmailDraft message, CancellationToken ct = default) =>
        SendAsync(new EmailSource(EmailKind.Statement, customerId, from, to), message, ct);

    public static EmailKind KindOf(FormKind kind) => kind switch
    {
        FormKind.Invoice => EmailKind.Invoice,
        FormKind.Estimate => EmailKind.Estimate,
        FormKind.CreditMemo => EmailKind.CreditMemo,
        _ => EmailKind.SalesReceipt,
    };

    // ---- History, links, customers ----

    /// <summary>The emails sent about a record, newest first, with their links.</summary>
    public async Task<IReadOnlyList<SentEmailEntry>> HistoryAsync(string entityType, Guid entityId, CancellationToken ct = default)
    {
        var sent = await store.ListSentAsync(entityType, entityId, ct);
        var ids = sent.Select(s => s.DocumentLinkId).OfType<Guid>().ToList();
        var links = ids.Count == 0 ? new Dictionary<Guid, DocumentLink>() : (await store.GetLinksAsync(ids, ct)).ToDictionary(l => l.Id);
        return [.. sent.Select(s => new SentEmailEntry(s, s.DocumentLinkId is { } id ? links.GetValueOrDefault(id) : null))];
    }

    /// <summary>Turns a "View" link off; it stops working at once. Audited on the document.</summary>
    public async Task<DocumentLink> RevokeLinkAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await store.GetLinkAsync(linkId, ct) ?? throw new EntityNotFoundException("document_link", linkId);
        link.Revoke(clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityTypeOf(link.Kind), link.DocumentId, null,
            JsonSerializer.Serialize(new { Link = "turned off", Sent = link.CreatedAt })));
        await store.SaveChangesAsync(ct);
        return link;
    }

    /// <summary>The document behind a "View" link, as it stands now, in the customer's design; null when the link is unknown or turned off.</summary>
    /// <summary>The invoice an emailed "Pay now" button is for, while its link is on; null otherwise.</summary>
    public async Task<Guid?> InvoiceToPayAsync(string token, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(token) && token.StartsWith(DocumentLink.TokenPrefix, StringComparison.Ordinal)
        && await store.FindLinkAsync(DocumentLink.Hash(token), ct) is { IsActive: true, Kind: EmailKind.Invoice } link ? link.DocumentId : null;

    public async Task<RenderedForm?> OpenLinkAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(DocumentLink.TokenPrefix, StringComparison.Ordinal)
            || await store.FindLinkAsync(DocumentLink.Hash(token), ct) is not { IsActive: true } link)
            return null;
        RenderedForm rendered;
        try
        {
            rendered = await Source(link.Kind).RenderAsync(new EmailSource(link.Kind, link.DocumentId), link.DesignId, ct);
        }
        catch (EntityNotFoundException)
        {
            return null;
        }
        catch (DomainException) when (link.DesignId is not null)
        {
            // The design chosen then has been switched off since.
            rendered = await Source(link.Kind).RenderAsync(new EmailSource(link.Kind, link.DocumentId), null, ct);
        }

        if (link.Opened(clock.UtcNow))
            await store.SaveChangesAsync(ct);
        return rendered;
    }

    /// <summary>Who's copied on a customer's emails and how their documents go. Audited on the customer.</summary>
    public async Task<Customer> SetCustomerPreferencesAsync(Guid customerId, string? cc, EmailPdf pdf, CancellationToken ct = default)
    {
        var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        var before = JsonSerializer.Serialize(new { customer.EmailCc, EmailPdf = customer.EmailPdf.ToString() });
        customer.SetEmailPreferences(cc, pdf);
        sales.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "customer", customer.Id, before,
            JsonSerializer.Serialize(new { customer.EmailCc, EmailPdf = customer.EmailPdf.ToString() })));
        await sales.SaveChangesAsync(ct);
        return customer;
    }

    /// <summary>Whether links can go out: they need the address customers reach OpenBook at.</summary>
    public async Task<string?> LinkBaseAsync(CancellationToken ct = default) => await LinkBaseAsync(await GetSettingsAsync(ct), ct);

    /// <summary>The audit entity type of a kind's record.</summary>
    public static string EntityTypeOf(EmailKind kind) => kind switch
    {
        EmailKind.Invoice or EmailKind.Reminder => "invoice",
        EmailKind.Estimate => EstimateService.EntityType,
        EmailKind.CreditMemo => "credit_memo",
        EmailKind.SalesReceipt => "sales_receipt",
        EmailKind.PurchaseOrder => Purchases.PurchaseOrderService.EntityType,
        EmailKind.GiftAcknowledgment => Nonprofit.GiftService.DonationEntity,
        EmailKind.PaymentReceipt => Sales.PaymentService.EntityType,
        _ => "customer",
    };

    /// <summary>"View invoice" and so on: the label of a document link's button.</summary>
    public static string Article(EmailKind kind) => $"View {EmailTemplates.Label(LinkKind(kind)).ToLowerInvariant()}";

    /// <summary>The document a kind's link opens: a reminder's is its invoice.</summary>
    public static EmailKind LinkKind(EmailKind kind) => kind == EmailKind.Reminder ? EmailKind.Invoice : kind;

    // ---- Sending ----

    /// <summary>Sends a message as it is; the caller records it.</summary>
    internal Task SendMessageAsync(EmailMessage message, CancellationToken ct) => TransportAsync(message, ct);

    private async Task TransportAsync(EmailMessage message, CancellationToken ct)
    {
        var settings = await ConfiguredAsync(ct);
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
            throw new DomainException(SendFailedCode, $"The email wasn't sent: {ex.Message}");
        }
    }

    private async Task<EmailSettings> ConfiguredAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        return settings.IsConfigured ? settings : throw new DomainException("email.not_configured", "Set up email in Settings before sending.");
    }

    private static EmailMessage Message(
        IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc, ComposedEmail composed, IReadOnlyList<EmailAttachment> attachments) =>
        new(string.Join(", ", to), composed.Subject, composed.Text, attachments)
        {
            Cc = cc,
            Bcc = bcc,
            HtmlBody = composed.Html,
            InlineImages = composed.Images,
        };

    private static (IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc) Recipients(EmailDraft draft)
    {
        var to = EmailAddresses.Parse(draft.To, "To");
        if (to.Count == 0)
            throw new DomainException("email.to_required", "Enter the address to send to.");
        return (to, EmailAddresses.Parse(draft.Cc, "Cc"), EmailAddresses.Parse(draft.Bcc, "Bcc"));
    }

    private static string Subject(EmailDraft draft, EmailContext context)
    {
        var subject = string.IsNullOrWhiteSpace(draft.Subject) ? context.Title : draft.Subject.Trim();
        if (subject.Length > EmailTemplates.MaxSubject || subject.Contains('\n', StringComparison.Ordinal))
            throw new DomainException("email.subject_too_long", $"The subject is one line of at most {EmailTemplates.MaxSubject} characters.");
        return subject;
    }

    private static string Body(EmailDraft draft)
    {
        var body = (draft.Body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        return body.Length <= EmailTemplates.MaxBody ? body
            : throw new DomainException("email.body_too_long", $"The message can have at most {EmailTemplates.MaxBody:N0} characters.");
    }

    /// <summary>Attach, link or both: the draft's choice, else the customer's; links only where they can be made.</summary>
    private static EmailPdf Delivery(EmailKind kind, EmailContext context, EmailDraft draft, string? linkBase, List<string> notes)
    {
        if (!context.CanLink(kind))
            return EmailPdf.Attach;
        var wanted = draft.Pdf ?? context.Customer!.EmailPdf;
        if (wanted == EmailPdf.Attach || !Enum.IsDefined(wanted))
            return EmailPdf.Attach;
        if (linkBase is null)
        {
            notes.Add("Links need the address customers open OpenBook at (Settings, Email), so the PDF is attached instead.");
            return EmailPdf.Attach;
        }

        return wanted;
    }

    /// <summary>The address in Settings, else the online payments' public address; null when there's neither.</summary>
    private async Task<string?> LinkBaseAsync(EmailSettings settings, CancellationToken ct) =>
        settings.LinkAddress ?? (await online.GetSettingsAsync(ct)).PublicUrl?.TrimEnd('/');

    private async Task<FormDesign> DesignAsync(EmailContext context, Guid? designId, CancellationToken ct)
    {
        return context.Document is { } document
            ? await designs.ChosenAsync(document, context.Customer?.Id, designId, ct)
            : designId is { } id && id != Guid.Empty ? await designs.ChosenAsync(FormDocuments.Invoice, null, id, ct) : await designs.DefaultAsync(ct);
    }

    private static EmailLook Look(FormDesign design, Company company) => new(
        design.EmailStyle, design.AccentColor, design.Logo, design.LogoContentType, company.LegalName ?? company.Name,
        [company.Address?.Replace("\r\n", ", ", StringComparison.Ordinal).Replace("\n", ", ", StringComparison.Ordinal) ?? "", company.Phone ?? "", company.Email ?? ""],
        design.Footer);

    private static string From(EmailSettings settings) =>
        settings.FromAddress is { } address ? settings.FromName is { } name ? $"{name} <{address}>" : address : "(email isn't set up)";

    private static List<string> Leftovers(EmailDraft draft) =>
        System.Text.RegularExpressions.Regex.Matches($"{draft.Subject}\n{draft.Body}", @"\{[a-z_]+\}").Select(m => m.Value).Distinct().ToList();

    private IEmailSource Source(EmailKind kind) =>
        sources.FirstOrDefault(s => s.Kinds.Contains(kind))
        ?? throw new DomainException("email.kind_unsupported", $"{EmailTemplates.Label(kind)} emails are sent from their own settings.");

    private static EmailWording Wording(EmailKind kind, EmailTemplate? template)
    {
        if (template is not null)
            return new EmailWording(kind, template.Subject, template.Body, false, template.UpdatedAt, template.UpdatedBy);
        var (subject, body) = EmailTemplates.Usual(kind);
        return new EmailWording(kind, subject, body, true);
    }

    private AuditRecord Sent(string entityType, Guid id, string to, string what) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, entityType, id, null,
            JsonSerializer.Serialize(new { To = to, Form = what }));

    private static string TemplateSnapshot(EmailKind kind, string subject, string body) =>
        JsonSerializer.Serialize(new { Email = EmailTemplates.Label(kind), Subject = subject, Body = body });

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
        s.LinkAddress,
    });

    internal static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    internal static string M(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture);
}
