using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;

namespace OpenBook.Api;

/// <summary>A message to send, previewed or sent; fields left out take the usual draft's values.</summary>
/// <param name="To">One address or several, separated by commas.</param>
/// <param name="DesignId">The form design for the email's look and the PDF; null = the usual one.</param>
/// <param name="Pdf">Attach, Link or Both (customer documents only); null = what the customer is set to get.</param>
public sealed record EmailRequest(
    string? To = null, string? Subject = null, string? Body = null, string? Cc = null, string? Bcc = null, Guid? DesignId = null, EmailPdf? Pdf = null);

public sealed record EmailTemplateRequest(string? Subject, string? Body);

public sealed record EmailTemplatePreviewRequest(string? Subject, string? Body, Guid? DesignId = null);

public sealed record CustomerEmailRequest(string? Cc, EmailPdf Pdf = EmailPdf.Attach);

public sealed record EmailPreviewResponse(
    string From, string To, string? Cc, string? Bcc, string Subject, string Html, string Text, string? Attachment, IReadOnlyList<string> Buttons, IReadOnlyList<string> Notes)
{
    public static EmailPreviewResponse Of(EmailPreview p) => new(p.From, p.To, p.Cc, p.Bcc, p.Subject, p.Html, p.Text, p.Attachment, p.Buttons, p.Notes);
}

public sealed record SentEmailResponse(
    Guid Id, string Kind, DateTimeOffset SentAt, string SentBy, string To, string? Cc, string? Bcc, string Subject, string Body, string? Attachment,
    Guid? LinkId, bool? LinkActive, DateTimeOffset? LinkFirstOpenedAt, DateTimeOffset? LinkLastOpenedAt)
{
    public static SentEmailResponse From(SentEmailEntry e) => new(
        e.Email.Id, EmailTemplates.Key(e.Email.Kind), e.Email.SentAt, e.Email.SentBy, e.Email.To, e.Email.Cc, e.Email.Bcc, e.Email.Subject, e.Email.Body,
        e.Email.Attachment, e.Link?.Id, e.Link?.IsActive, e.Link?.FirstOpenedAt, e.Link?.LastOpenedAt);

    public static SentEmailResponse From(SentEmail e) => From(new SentEmailEntry(e, null));
}

public sealed record EmailTemplateResponse(string Key, string Label, string Subject, string Body, bool IsUsual, IReadOnlyList<EmailPlaceholder> Placeholders)
{
    public static EmailTemplateResponse From(EmailWording w) =>
        new(EmailTemplates.Key(w.Kind), EmailTemplates.Label(w.Kind), w.Subject, w.Body, w.IsUsual, EmailTemplates.Placeholders(w.Kind));
}

/// <summary>Email wording, previews, history, links and customers' email preferences.</summary>
internal static class EmailApi
{
    public static void MapEmailApi(this RouteGroupBuilder api)
    {
        // Sales documents and statements: preview, send, history (the send itself stays at /api/sales/{kind}/{id}/email).
        api.MapPost("/sales/{kind}/{id:guid}/email/preview", async (string kind, Guid id, DateOnly? from, DateOnly? to, EmailRequest r, EmailService s, CancellationToken ct) =>
            EmailPreviewResponse.Of(await PreviewAsync(s, SalesSource(kind, id, from, to), r, ct)));
        api.MapGet("/sales/{kind}/{id:guid}/emails", async (string kind, Guid id, EmailService s, CancellationToken ct) =>
            await HistoryAsync(s, SalesSource(kind, id, null, null), ct));
        api.MapGet("/customers/{id:guid}/emails", async (Guid id, EmailService s, CancellationToken ct) =>
            (await s.HistoryAsync("customer", id, ct)).Select(SentEmailResponse.From));
        api.MapPost("/sales/email-links/{id:guid}/revoke", async (Guid id, EmailService s, CancellationToken ct) =>
        {
            await s.RevokeLinkAsync(id, ct);
            return Results.NoContent();
        });
        api.MapPut("/customers/{id:guid}/email-settings", async (Guid id, CustomerEmailRequest r, EmailService s, CancellationToken ct) =>
        {
            var c = await s.SetCustomerPreferencesAsync(id, r.Cc, r.Pdf, ct);
            return new { c.Id, c.EmailCc, c.EmailPdf };
        });

        api.MapPost("/purchases/orders/{id:guid}/email/preview", async (Guid id, EmailRequest r, EmailService s, CancellationToken ct) =>
            EmailPreviewResponse.Of(await PreviewAsync(s, new EmailSource(EmailKind.PurchaseOrder, id), r, ct)));
        api.MapGet("/purchases/orders/{id:guid}/emails", async (Guid id, EmailService s, CancellationToken ct) =>
            await HistoryAsync(s, new EmailSource(EmailKind.PurchaseOrder, id), ct));

        api.MapPost("/nonprofit/gifts/{id:guid}/email", async (Guid id, EmailRequest r, EmailService s, CancellationToken ct) =>
            SentEmailResponse.From(await SendAsync(s, new EmailSource(EmailKind.GiftAcknowledgment, id), r, ct)));
        api.MapPost("/nonprofit/statements/{year:int}/{donorId:guid}/email", async (int year, Guid donorId, EmailRequest r, EmailService s, CancellationToken ct) =>
            SentEmailResponse.From(await SendAsync(s, new EmailSource(EmailKind.GivingStatement, donorId, Year: year), r, ct)));

        // Wording. Keys: invoice, estimate, credit-memo, sales-receipt, statement, purchase-order, gift-acknowledgment,
        // giving-statement, report, invitation (payment reminders are worded in /api/sales/collections/settings).
        api.MapGet("/settings/email-templates", async (EmailService s, CancellationToken ct) => (await s.TemplatesAsync(ct)).Select(EmailTemplateResponse.From));
        api.MapGet("/settings/email-templates/{key}", async (string key, EmailService s, CancellationToken ct) =>
            EmailTemplateResponse.From(await s.TemplateAsync(Kind(key), ct)));
        api.MapPut("/settings/email-templates/{key}", async (string key, EmailTemplateRequest r, EmailService s, CancellationToken ct) =>
            EmailTemplateResponse.From(await s.SaveTemplateAsync(Kind(key), r.Subject, r.Body, ct)));
        api.MapDelete("/settings/email-templates/{key}", async (string key, EmailService s, CancellationToken ct) =>
            EmailTemplateResponse.From(await s.ResetTemplateAsync(Kind(key), ct)));
        api.MapPost("/settings/email-templates/{key}/preview", async (string key, EmailTemplatePreviewRequest r, EmailService s, CancellationToken ct) =>
            EmailPreviewResponse.Of(await s.PreviewTemplateAsync(Kind(key), r.Subject, r.Body, r.DesignId, ct)));
    }

    /// <summary>The usual draft with the request's fields over it.</summary>
    internal static async Task<EmailDraft> DraftAsync(EmailService s, EmailSource source, EmailRequest? r, CancellationToken ct)
    {
        var draft = await s.DraftAsync(source, ct);
        return r is null ? draft : new EmailDraft(r.To ?? draft.To, r.Subject ?? draft.Subject, r.Body ?? draft.Body, r.Cc ?? draft.Cc, r.Bcc ?? draft.Bcc,
            r.DesignId, r.Pdf ?? draft.Pdf);
    }

    internal static async Task<SentEmail> SendAsync(EmailService s, EmailSource source, EmailRequest? r, CancellationToken ct) =>
        await s.SendAsync(source, await DraftAsync(s, source, r, ct), ct);

    internal static EmailSource SalesSource(string kind, Guid id, DateOnly? from, DateOnly? to) => kind switch
    {
        "invoices" => new EmailSource(EmailKind.Invoice, id),
        "estimates" => new EmailSource(EmailKind.Estimate, id),
        "credit-memos" => new EmailSource(EmailKind.CreditMemo, id),
        "receipts" => new EmailSource(EmailKind.SalesReceipt, id),
        "customers" => new EmailSource(EmailKind.Statement, id, from, to),
        _ => throw new DomainException("form.unknown", $"'{kind}' isn't a printable form."),
    };

    private static async Task<EmailPreview> PreviewAsync(EmailService s, EmailSource source, EmailRequest? r, CancellationToken ct) =>
        await s.PreviewAsync(source, await DraftAsync(s, source, r, ct), ct);

    private static async Task<IEnumerable<SentEmailResponse>> HistoryAsync(EmailService s, EmailSource source, CancellationToken ct) =>
        (await s.HistoryAsync(EmailService.EntityTypeOf(source.Kind), source.Id, ct)).Select(SentEmailResponse.From);

    private static EmailKind Kind(string key) =>
        EmailTemplates.FromKey(key) ?? throw new DomainException("email_template.kind_invalid", $"There's no email called '{key}'.");
}
