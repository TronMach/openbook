using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;

namespace OpenBook.Api;

/// <param name="Body">The message, with fill-ins such as {number} and {link}; null = the usual wording.</param>
public sealed record TextRequest(string? Body = null);

public sealed record TextPreviewResponse(string? To, string Text, int Characters, int Parts, bool Unicode, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public static TextPreviewResponse Of(TextPreview p) => new(p.To, p.Text, p.Length.Characters, p.Length.Parts, p.Length.Unicode, p.Problems, p.Notes);
}

public sealed record SentTextResponse(
    Guid Id, string Kind, DateTimeOffset SentAt, string SentBy, string To, string Body, string? ProviderId,
    Guid? LinkId, bool? LinkActive, DateTimeOffset? LinkFirstOpenedAt, DateTimeOffset? LinkLastOpenedAt)
{
    public static SentTextResponse From(SentTextEntry e) => new(
        e.Text.Id, EmailTemplates.Key(e.Text.Kind), e.Text.SentAt, e.Text.SentBy, e.Text.To, e.Text.Body, e.Text.ProviderId,
        e.Link?.Id, e.Link?.IsActive, e.Link?.FirstOpenedAt, e.Link?.LastOpenedAt);
}

public sealed record CustomerTextRequest(string? Number, bool Allowed);

public sealed record TextSettingsResponse(string? AccountSid, bool HasAuthToken, string? Sender, bool IsReady)
{
    public static TextSettingsResponse From(TextSettings s) => new(s.AccountSid, s.ProtectedAuthToken is not null, s.Sender, s.IsReady);
}

/// <summary>Sending invoices, estimates, credit memos and sales receipts by text, their history, customers' numbers and the Twilio settings.</summary>
internal static class TextApi
{
    public static void MapTextApi(this RouteGroupBuilder api)
    {
        // Kinds: invoices, estimates, credit-memos, receipts.
        api.MapPost("/sales/{kind}/{id:guid}/text/preview", async (string kind, Guid id, TextRequest? r, TextMessageService s, CancellationToken ct) =>
        {
            var source = Source(kind, id);
            return TextPreviewResponse.Of(await s.PreviewAsync(source, r?.Body ?? TextMessageService.Draft(source.Kind), ct));
        });
        api.MapPost("/sales/{kind}/{id:guid}/text", async (string kind, Guid id, TextRequest? r, TextMessageService s, CancellationToken ct) =>
        {
            var source = Source(kind, id);
            var sent = await s.SendAsync(source, r?.Body ?? TextMessageService.Draft(source.Kind), ct);
            var entry = (await s.HistoryAsync(sent.EntityType, sent.EntityId, ct)).First(e => e.Text.Id == sent.Id);
            return Results.Created($"/api/sales/{kind}/{id}/texts", SentTextResponse.From(entry));
        });
        api.MapGet("/sales/{kind}/{id:guid}/texts", async (string kind, Guid id, TextMessageService s, CancellationToken ct) =>
            (await s.HistoryAsync(EmailService.EntityTypeOf(Source(kind, id).Kind), id, ct)).Select(SentTextResponse.From));
        api.MapPut("/customers/{id:guid}/text-settings", async (Guid id, CustomerTextRequest r, TextMessageService s, CancellationToken ct) =>
        {
            var c = await s.SetCustomerAsync(id, r.Number, r.Allowed, ct);
            return new { c.Id, c.TextNumber, TextsAllowed = c.TextsAllowedAt is not null, c.TextsAllowedAt };
        });

        api.MapGet("/settings/texts", async (TextMessageService s, CancellationToken ct) => TextSettingsResponse.From(await s.GetSettingsAsync(ct)));
        api.MapPut("/settings/texts", async (TextSettingsCommand r, TextMessageService s, CancellationToken ct) =>
            TextSettingsResponse.From(await s.SaveSettingsAsync(r, ct)));
    }

    private static EmailSource Source(string kind, Guid id) => kind switch
    {
        "invoices" => new EmailSource(EmailKind.Invoice, id),
        "estimates" => new EmailSource(EmailKind.Estimate, id),
        "credit-memos" => new EmailSource(EmailKind.CreditMemo, id),
        "receipts" => new EmailSource(EmailKind.SalesReceipt, id),
        _ => throw new DomainException("text.kind_unsupported", $"'{kind}' can't be sent by text: invoices, estimates, credit-memos and receipts can."),
    };
}
