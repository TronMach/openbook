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
    private static readonly Action<ILogger, string, string, string, Exception?> HookRefused =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(1, "PaymentWebhookRefused"), "A {Provider} webhook was refused ({Code}): {Reason}");

    public static IEndpointRouteBuilder MapOnlineEndpoints(this IEndpointRouteBuilder app)
    {
        // Stripe posts events here; the signature is the authentication.
        // The public giving page (C091): starts a checkout at the payment provider and sends the donor there.
        app.MapPost("/give", async (IFormCollection form, OpenBook.Application.Nonprofit.OnlineGivingService giving, OnlinePaymentActor actor, CancellationToken ct) =>
        {
            actor.Use();
            try
            {
                var amount = FormNumbers.Amount(form["amount"]) ?? throw new DomainException("giving.amount", "Enter the amount you'd like to give.");
                return Results.Redirect(await giving.StartAsync(form["name"], form["email"], amount, form["note"], ct));
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/give?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).AllowAnonymous();

        app.MapPost("/hooks/stripe", (HttpRequest request, OnlinePaymentService online, OnlinePaymentActor actor, ILoggerFactory logs, CancellationToken ct) =>
            Hook("Stripe", request, actor, logs, payload => online.HandleWebhookAsync(payload, request.Headers["Stripe-Signature"], ct), ct))
            .AllowAnonymous().DisableAntiforgery();

        // PayPal's signature is checked by PayPal itself, from the headers it sent.
        app.MapPost("/hooks/paypal", (HttpRequest request, OnlinePaymentService online, OnlinePaymentActor actor, ILoggerFactory logs, CancellationToken ct) =>
            Hook("PayPal", request, actor, logs, payload => online.HandlePayPalWebhookAsync(payload,
                request.Headers.Where(h => h.Key.StartsWith("paypal-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal), ct), ct))
            .AllowAnonymous().DisableAntiforgery();

        app.MapPost("/hooks/square", (HttpRequest request, OnlinePaymentService online, OnlinePaymentActor actor, ILoggerFactory logs, CancellationToken ct) =>
            Hook("Square", request, actor, logs, payload => online.HandleSquareWebhookAsync(payload, request.Headers["x-square-hmacsha256-signature"], ct), ct))
            .AllowAnonymous().DisableAntiforgery();

        // A payment link as sent to the customer, for providers whose own pages don't last (PayPal): the current page, or a new one.
        app.MapGet("/pay/{linkId:guid}", async (Guid linkId, OnlinePaymentService online, OnlinePaymentActor actor, CancellationToken ct) =>
        {
            actor.Use();
            try
            {
                return Results.Redirect(await online.OpenAsync(linkId, ct));
            }
            catch (EntityNotFoundException)
            {
                return Results.NotFound();
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/pay/unavailable?paid={(ex.Code == "online.nothing_to_pay" ? "true" : "false")}");
            }
        }).AllowAnonymous();

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

        // Automatic payment: the customer agrees (a tick on the page that says what to), and goes to the provider to enter the card.
        app.MapPost("/portal/{token}/card/agree", async (string token, IFormCollection form, CustomerPortalService portal, OnlinePaymentActor actor, CancellationToken ct) =>
        {
            actor.Use();
            var page = $"/portal/{Uri.EscapeDataString(token)}/card";
            var via = form["via"] == "link" ? OpenBook.Domain.Sales.CardAgreement.Link : OpenBook.Domain.Sales.CardAgreement.Portal;
            try
            {
                if (form["agree"] != "true")
                    throw new DomainException("card.not_agreed", "Tick the box to agree first.");
                return await portal.AgreeToCardAsync(token, via, ct) is { } url ? Results.Redirect(url) : Results.NotFound();
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{(via == OpenBook.Domain.Sales.CardAgreement.Link ? "&via=link" : "")}");
            }
        }).AllowAnonymous();

        app.MapPost("/portal/{token}/card/stop", async (IFormCollection _, string token, CustomerPortalService portal, OnlinePaymentActor actor, CancellationToken ct) =>
        {
            actor.Use();
            var page = $"/portal/{Uri.EscapeDataString(token)}";
            try
            {
                return await portal.StopCardAsync(token, ct)
                    ? Results.Redirect($"{page}?done={Uri.EscapeDataString("Automatic payment is stopped, and your card has been removed.")}#card")
                    : Results.NotFound();
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}#card");
            }
        }).AllowAnonymous();

        app.MapPost("/customers/{id:guid}/card/remove", async (IFormCollection _, Guid id, OnlinePaymentService online, CancellationToken ct) =>
        {
            try
            {
                await online.RemoveCardAsync(id, "Removed by the business.", ct: ct);
                return Results.Redirect($"/customers/{id}?cardDone=removed#card");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/customers/{id}?cardError={Uri.EscapeDataString(ex.Message)}#card");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/invoices/{id:guid}/charge-card", async (IFormCollection _, Guid id, OnlinePaymentService online, CancellationToken ct) =>
            await InvoicePanel(id, async () => $"cardDone={(await online.ChargeCardAsync(id, ct)).Status}")).RequireAuthorization();

        // Field: hold ("true" leaves the invoice out of automatic charges).
        app.MapPost("/sales/invoices/{id:guid}/auto-charge", async (Guid id, IFormCollection form, OnlinePaymentService online, CancellationToken ct) =>
            await InvoicePanel(id, async () => $"cardDone={((await online.HoldAutoChargeAsync(id, form["hold"] == "true", ct)).HoldAutoCharge ? "held" : "released")}")).RequireAuthorization();

        // Fields: amount, accountId, date, reason.
        app.MapPost("/sales/payments/{id:guid}/online-refund", async (Guid id, IFormCollection form, OnlineRefundService refunds, ClosedPeriodOverride closing, CancellationToken ct) =>
            await RefundPanel(id, form, closing, async () => await refunds.RefundAsync(id, new OnlineRefundCommand(
                FormNumbers.Amount(form["amount"]) ?? throw new DomainException("refund.amount_required", "Enter the amount to refund."),
                Guid.TryParse(form["accountId"], out var account) && account != Guid.Empty ? account : null,
                DateOnly.TryParseExact(form["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                form["reason"].ToString() is { Length: > 0 } reason ? reason : null), ct))).RequireAuthorization();

        app.MapPost("/sales/payments/{id:guid}/online-refunds/{refundId:guid}/{step:regex(^(retry|abandon)$)}",
            async (Guid id, Guid refundId, string step, IFormCollection form, OnlineRefundService refunds, ClosedPeriodOverride closing, CancellationToken ct) =>
                await RefundPanel(id, form, closing, () => step == "retry" ? refunds.RetryAsync(refundId, ct) : refunds.AbandonAsync(refundId, ct))).RequireAuthorization();

        app.MapPost("/sales/invoices/{id:guid}/payment-link", async (IFormCollection _, Guid id, OnlinePaymentService online, CancellationToken ct) =>
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

        // A gift started on the giving page that no webhook or return reported.
        app.MapPost("/nonprofit/gifts/online/{id:guid}/check", async (IFormCollection _, Guid id, OpenBook.Application.Nonprofit.OnlineGivingService giving, CancellationToken ct) =>
            await Back("/nonprofit/gifts", "#online", async () => $"checked={await giving.CheckAsync(id, ct)}")).RequireAuthorization();

        app.MapPost("/sales/online-notices/{id:guid}/resolve", async (Guid id, IFormCollection form, OnlinePaymentService online, CancellationToken ct) =>
        {
            var invoiceId = Guid.TryParse(form["invoiceId"], out var g) ? g : Guid.Empty;
            return await Back($"/sales/invoices/{invoiceId}", "#online", async () =>
            {
                await online.ResolveNoticeAsync(id, ct);
                return "done=resolved";
            });
        }).RequireAuthorization();

        app.MapPost("/settings/online-payments", async (IFormCollection form, OpenBook.Application.IRecordVersions versions, OnlinePaymentService online, CancellationToken ct) =>
            await Back("/settings/online-payments", "", async () =>
            {
                await Components.SettingsVersions.EnsureUnchangedAsync<OpenBook.Domain.Sales.OnlinePaymentSettings>(versions, form, ct);
                // Each provider has its own fields; only the chosen one's count.
                OpenBook.Domain.Sales.PaymentProvider? provider = Enum.TryParse<OpenBook.Domain.Sales.PaymentProvider>(form["provider"], out var p) && Enum.IsDefined(p) ? p : null;
                var command = provider switch
                {
                    OpenBook.Domain.Sales.PaymentProvider.PayPal => new OnlinePaymentSettingsCommand(false, null, null, form["paypalSecret"], null,
                        form["clearPayPalSecret"] == "true", false, provider, form["paypalClientId"], form["paypalWebhookId"], form["paypalSandbox"] == "true"),
                    OpenBook.Domain.Sales.PaymentProvider.Square => new OnlinePaymentSettingsCommand(false, null, null, form["squareToken"], form["squareSignatureKey"],
                        form["clearSquareToken"] == "true", form["clearSquareSignatureKey"] == "true", provider, Sandbox: form["squareSandbox"] == "true"),
                    _ => new OnlinePaymentSettingsCommand(false, null, null, form["secretKey"], form["webhookSecret"],
                        form["clearSecretKey"] == "true", form["clearWebhookSecret"] == "true", provider),
                };
                await online.SaveSettingsAsync(command with
                {
                    Enabled = form["enabled"] == "true",
                    DepositAccountId = Guid.TryParse(form["depositAccountId"], out var account) ? account : null,
                    PublicUrl = form["publicUrl"],
                    LeaveLinks = form["leaveLinks"] == "true",
                }, ct);
                return "done=saved";
            })).RequireAuthorization();

        app.MapPost("/customers/{id:guid}/portal/{accessId:guid}/revoke", async (IFormCollection _, Guid id, Guid accessId, CustomerPortalService portal, CancellationToken ct) =>
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

        // A receipt typed in from paper and recorded at once. Fields as for recording one, and file (a photo of it, optional).
        app.MapPost("/purchases/receipts/new", async (IFormCollection form, ReceiptService receipts, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;
            var file = form.Files.GetFile("file") is { Length: > 0 } f ? f : null;
            try
            {
                await using var photo = file?.OpenReadStream();
                var spend = await receipts.EnterAsync(new RecordReceiptCommand(
                    Guid.TryParse(form["accountId"], out var account) ? account : throw new DomainException("receipt.account_required", "Choose the account it was paid from."),
                    Guid.TryParse(form["categoryId"], out var category) && category != Guid.Empty ? category : null,
                    DateOnly.TryParseExact(form["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
                        : throw new DomainException("receipt.date_required", "Enter the date on the receipt."),
                    FormNumbers.Amount(form["amount"]) ?? 0m,
                    Guid.TryParse(form["vendorId"], out var vendor) && vendor != Guid.Empty ? vendor : null,
                    form["payee"].ToString().Trim() is { Length: > 0 } payee ? payee : null,
                    form["memo"].ToString().Trim() is { Length: > 0 } memo ? memo : null), file?.FileName, photo, ct);
                return Results.Redirect($"/banking/transactions/{spend.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                var message = file is null ? ex.Message : $"{ex.Message} Choose the photo again.";
                return Results.Redirect($"/purchases/receipts/new?error={Uri.EscapeDataString(message)}{password}&"
                    + SettlementEndpoints.Query(form, "accountId", "categoryId", "date", "amount", "vendorId", "payee", "memo"));
            }
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(DocumentEndpoints.MaxUploadBytes));

        app.MapPost("/purchases/receipts/{id:guid}/dismiss", async (IFormCollection _, Guid id, ReceiptService receipts, CancellationToken ct) =>
            await Back($"/purchases/receipts/{id}", "", async () =>
            {
                await receipts.DismissAsync(id, ct);
                return "done=dismissed";
            })).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Hook(string provider, HttpRequest request, OnlinePaymentActor actor, ILoggerFactory logs,
        Func<string, Task<WebhookOutcome>> handle, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        actor.Use();
        try
        {
            return Results.Ok(new { outcome = (await handle(payload)).ToString() });
        }
        catch (DomainException ex)
        {
            // Money may have moved: the System page should show it wasn't recorded.
            HookRefused(logs.CreateLogger("OpenBook.OnlinePayments"), provider, ex.Code, ex.Message, null);
            return Results.BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    private static async Task<IResult> InvoicePanel(Guid invoiceId, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"/sales/invoices/{invoiceId}?{await change()}#online");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/sales/invoices/{invoiceId}?cardError={Uri.EscapeDataString(ex.Message)}#online");
        }
    }

    private static async Task<IResult> RefundPanel(Guid paymentId, IFormCollection form, ClosedPeriodOverride closing, Func<Task<OpenBook.Domain.Sales.OnlineRefund>> change)
    {
        closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } password ? password : null;
        var page = $"/sales/payments/{paymentId}";
        try
        {
            return Results.Redirect($"{page}?refund={(await change()).Status}#refunds");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?refundError={Uri.EscapeDataString(ex.Message)}{(ClosedPeriod.NeedsPassword(ex) ? "&refundPassword=true" : "")}#refunds");
        }
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
