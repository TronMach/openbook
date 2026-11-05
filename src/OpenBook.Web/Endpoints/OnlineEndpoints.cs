using System.Globalization;
using Microsoft.Net.Http.Headers;
using OpenBook.Application.Forms;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Online payments (the provider's webhook, payment links), the customer portal's downloads and "Pay now", and receipt
/// capture. The webhook and portal endpoints are open to anyone: the webhook checks its signature, the portal its token.
/// </summary>
public static class OnlineEndpoints
{
    public static IEndpointRouteBuilder MapOnlineEndpoints(this IEndpointRouteBuilder app)
    {
        // Stripe posts events here; the signature is the authentication.
        app.MapPost("/hooks/stripe", async (HttpRequest request, OnlinePaymentService online, OnlinePaymentActor actor, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            actor.Use();
            try
            {
                return Results.Ok(new { outcome = (await online.HandleWebhookAsync(payload, request.Headers["Stripe-Signature"], ct)).ToString() });
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { code = ex.Code, message = ex.Message });
            }
        }).AllowAnonymous().DisableAntiforgery();

        app.MapGet("/portal/{token}/invoices/{id:guid}/pdf", async (string token, Guid id, HttpContext http, CustomerPortalService portal, FormService forms, CancellationToken ct) =>
        {
            if (await portal.InvoiceForAsync(token, id, ct) is null)
                return Results.NotFound();
            var form = await forms.RenderAsync(FormKind.Invoice, id, ct);
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = form.FileName }.ToString();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(form.Pdf, "application/pdf");
        }).AllowAnonymous();

        app.MapPost("/portal/{token}/invoices/{id:guid}/pay", async (string token, Guid id, CustomerPortalService portal, OnlinePaymentService online, OnlinePaymentActor actor, CancellationToken ct) =>
        {
            if (await portal.InvoiceForAsync(token, id, ct) is null)
                return Results.NotFound();
            actor.Use();
            try
            {
                return Results.Redirect((await online.LinkForInvoiceAsync(id, ct)).Url);
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/portal/{Uri.EscapeDataString(token)}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).AllowAnonymous();

        app.MapGet("/portal/{token}/estimates/{id:guid}/pdf", async (string token, Guid id, HttpContext http, CustomerPortalService portal, FormService forms, CancellationToken ct) =>
        {
            if (await portal.EstimateForAsync(token, id, ct) is null)
                return Results.NotFound();
            var form = await forms.RenderAsync(FormKind.Estimate, id, ct);
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = form.FileName }.ToString();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(form.Pdf, "application/pdf");
        }).AllowAnonymous();

        // Fields: name (required to accept), note.
        app.MapPost("/portal/{token}/estimates/{id:guid}/{answer:regex(^(accept|decline)$)}", async (string token, Guid id, string answer, IFormCollection form, CustomerPortalService portal, CancellationToken ct) =>
        {
            var page = $"/portal/{Uri.EscapeDataString(token)}";
            try
            {
                var accept = answer == "accept";
                var estimate = await portal.RespondAsync(token, id, accept, form["name"], form["note"], ct);
                return estimate is null
                    ? Results.NotFound()
                    : Results.Redirect($"{page}?done={Uri.EscapeDataString(accept ? $"Thank you: estimate {estimate.Number} is accepted." : $"Estimate {estimate.Number} is declined. Thank you for letting us know.")}#estimates");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}#estimates");
            }
        }).AllowAnonymous();

        app.MapPost("/sales/invoices/{id:guid}/payment-link", async (Guid id, OnlinePaymentService online, CancellationToken ct) =>
            await Back($"/sales/invoices/{id}", "#online", async () =>
            {
                await online.LinkForInvoiceAsync(id, ct);
                return "done=link";
            })).RequireAuthorization();

        app.MapPost("/sales/payment-links/{linkId:guid}/check", async (Guid linkId, IFormCollection form, OnlinePaymentService online, CancellationToken ct) =>
        {
            var invoiceId = Guid.TryParse(form["invoiceId"], out var g) ? g : Guid.Empty;
            return await Back($"/sales/invoices/{invoiceId}", "#online", async () =>
            {
                var link = await online.CheckAsync(linkId, ct);
                return $"done=checked&status={link.Status}";
            });
        }).RequireAuthorization();

        app.MapPost("/settings/online-payments", async (IFormCollection form, OnlinePaymentService online, CancellationToken ct) =>
            await Back("/settings/online-payments", "", async () =>
            {
                await online.SaveSettingsAsync(new OnlinePaymentSettingsCommand(
                    form["enabled"] == "true", Guid.TryParse(form["depositAccountId"], out var account) ? account : null, form["publicUrl"],
                    form["secretKey"], form["webhookSecret"], form["clearSecretKey"] == "true", form["clearWebhookSecret"] == "true"), ct);
                return "done=saved";
            })).RequireAuthorization();

        app.MapPost("/customers/{id:guid}/portal/{accessId:guid}/revoke", async (Guid id, Guid accessId, CustomerPortalService portal, CancellationToken ct) =>
            await Back($"/customers/{id}", "#portal", async () =>
            {
                await portal.RevokeAsync(accessId, ct);
                return "portal=revoked";
            })).RequireAuthorization();

        // Field: file (one or several). One opens its page; several go back to the list, saying what was refused.
        app.MapPost("/purchases/receipts/upload", async (IFormCollection form, ReceiptService receipts, CancellationToken ct) =>
        {
            var files = form.Files.GetFiles("file").Where(f => f.Length > 0).ToList();
            if (files.Count == 0)
                return Results.Redirect($"/purchases/receipts?error={Uri.EscapeDataString("Choose a receipt file.")}");
            if (files.Count > DocumentEndpoints.MaxFiles)
                return Results.Redirect($"/purchases/receipts?error={Uri.EscapeDataString($"Upload at most {DocumentEndpoints.MaxFiles} receipts at once.")}");
            var made = new List<Guid>();
            var refused = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    made.Add((await receipts.UploadAsync(file.FileName, stream, ct)).Id);
                }
                catch (DomainException ex)
                {
                    refused.Add($"{file.FileName}: {ex.Message}");
                }
            }

            if (made.Count == 1 && refused.Count == 0)
                return Results.Redirect($"/purchases/receipts/{made[0]}");
            var query = $"uploaded={made.Count}";
            if (refused.Count > 0)
                query += $"&error={Uri.EscapeDataString(string.Join(" ", refused))}";
            return Results.Redirect($"/purchases/receipts?{query}");
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(DocumentEndpoints.MaxUploadBytes));

        // Fields: accountId, categoryId, vendorId, payee, date, amount, memo.
        app.MapPost("/purchases/receipts/{id:guid}/record", async (Guid id, IFormCollection form, ReceiptService receipts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;
            try
            {
                var spend = await receipts.RecordAsync(id, new RecordReceiptCommand(
                    Guid.TryParse(form["accountId"], out var account) ? account : throw new DomainException("receipt.account_required", "Choose the account it was paid from."),
                    Guid.TryParse(form["categoryId"], out var category) && category != Guid.Empty ? category : null,
                    DateOnly.TryParseExact(form["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : CompanyTime.Today,
                    StatementValues.TryParseAmount(form["amount"], out var amount) ? amount : 0m,
                    Guid.TryParse(form["vendorId"], out var vendor) && vendor != Guid.Empty ? vendor : null,
                    form["payee"].ToString() is { Length: > 0 } payee ? payee : null,
                    form["memo"].ToString() is { Length: > 0 } memo ? memo : null), ct);
                return Results.Redirect($"/banking/transactions/{spend.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/purchases/receipts/{id}?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/purchases/receipts/{id:guid}/dismiss", async (Guid id, ReceiptService receipts, CancellationToken ct) =>
            await Back($"/purchases/receipts/{id}", "", async () =>
            {
                await receipts.DismissAsync(id, ct);
                return "done=dismissed";
            })).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Back(string page, string anchor, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}{anchor}");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{anchor}");
        }
    }
}
