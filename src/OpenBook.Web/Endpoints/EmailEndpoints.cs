using Microsoft.Net.Http.Headers;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Email: the live preview and test send beside each email form (posted by email.js with the form's fields and its
/// antiforgery token), template previews, turning a "View" link off, a customer's email preferences, and the link
/// itself (open to anyone holding it).
/// </summary>
public static class EmailEndpoints
{
    public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder app)
    {
        // The compose pages' addresses (see EmailCompose.razor); each takes /preview and /test.
        Compose(app, "/sales/email/{kind}/{id:guid}", r => SalesSource(r));
        Compose(app, "/purchases/orders/{id:guid}/email", r => new EmailSource(EmailKind.PurchaseOrder, Route(r, "id")));
        Compose(app, "/nonprofit/gifts/{id:guid}/email", r => new EmailSource(EmailKind.GiftAcknowledgment, Route(r, "id")));
        Compose(app, "/nonprofit/statements/{year:int}/{id:guid}/email",
            r => new EmailSource(EmailKind.GivingStatement, Route(r, "id"), Year: int.Parse(r.RouteValues["year"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture)));

        // A template with sample values. Fields: Input.Subject, Input.Body, Input.DesignId (or subject/body/design).
        app.MapPost("/settings/email-templates/{key}/preview", async (string key, IFormCollection form, EmailService email, CancellationToken ct) =>
            await TemplatePreviewAsync(key, form, email, ct)).RequireAuthorization();
        // Payment reminders are worded on the collection settings page (its own rule: accountant).
        app.MapPost("/sales/collections/settings/preview", async (IFormCollection form, EmailService email, CancellationToken ct) =>
            await TemplatePreviewAsync(EmailTemplates.Key(EmailKind.Reminder), form, email, ct)).RequireAuthorization();

        // Fields: returnUrl.
        app.MapPost("/sales/email-links/{id:guid}/revoke", async (Guid id, IFormCollection form, EmailService email, CancellationToken ct) =>
        {
            var back = Local(form["returnUrl"]);
            try
            {
                await email.RevokeLinkAsync(id, ct);
                return Results.Redirect(back);
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back.Split('#')[0]}{(back.Contains('?', StringComparison.Ordinal) ? "&" : "?")}error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Fields: cc, pdf (Attach / Link / Both).
        app.MapPost("/customers/{id:guid}/email-settings", async (Guid id, IFormCollection form, EmailService email, CancellationToken ct) =>
        {
            try
            {
                var pdf = Enum.TryParse<EmailPdf>(form["pdf"], out var choice) ? choice : EmailPdf.Attach;
                await email.SetCustomerPreferencesAsync(id, form["cc"].ToString(), pdf, ct);
                return Results.Redirect($"/customers/{id}#email-settings");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/customers/{id}?emailError={Uri.EscapeDataString(ex.Message)}#email-settings");
            }
        }).RequireAuthorization();

        // A "View" link from an email: the document as it stands, or a short page when the link is unknown or turned off.
        app.MapGet("/view/{token}", async (string token, HttpContext http, EmailService email, CancellationToken ct) =>
        {
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.CacheControl = "no-store";
            if (await email.OpenLinkAsync(token, ct) is not { } form)
                return Results.Content(
                    "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Link not available</title></head>" +
                    "<body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;\"><h1>This link doesn't work any more</h1>" +
                    "<p>It may have been turned off. Please ask whoever sent it for a new copy.</p></body></html>", "text/html", statusCode: StatusCodes.Status404NotFound);
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = form.FileName }.ToString();
            return Results.File(form.Pdf, "application/pdf");
        }).AllowAnonymous();

        // The "Pay now" button in an email: a fresh checkout for what's owed when the customer clicks.
        app.MapGet("/view/{token}/pay", async (string token, EmailService email, OpenBook.Application.Sales.OnlinePaymentService online, OpenBook.Web.Security.OnlinePaymentActor actor, CancellationToken ct) =>
        {
            static IResult Page(string title, string text, int status) => Results.Content(
                $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{title}</title></head>" +
                $"<body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;\"><h1>{title}</h1><p>{System.Net.WebUtility.HtmlEncode(text)}</p></body></html>", "text/html", statusCode: status);

            if (await email.InvoiceToPayAsync(token, ct) is not { } invoiceId)
                return Page("This link doesn't work any more", "Please ask whoever sent it for a new copy.", StatusCodes.Status404NotFound);
            actor.Use();
            try
            {
                return Results.Redirect((await online.LinkForInvoiceAsync(invoiceId, ct)).Url);
            }
            catch (OpenBook.Domain.Common.DomainException ex)
            {
                return Page("Nothing to pay here", ex.Message, StatusCodes.Status200OK);
            }
        }).AllowAnonymous();

        return app;
    }

    private static void Compose(IEndpointRouteBuilder app, string pattern, Func<HttpRequest, EmailSource?> source)
    {
        app.MapPost(pattern + "/preview", async (HttpRequest request, IFormCollection form, EmailService email, CancellationToken ct) =>
        {
            if (source(request) is not { } s)
                return Results.NotFound();
            try
            {
                return Results.Json(Json(await email.PreviewAsync(s, Draft(form), ct)));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        app.MapPost(pattern + "/test", async (HttpRequest request, IFormCollection form, EmailService email, CancellationToken ct) =>
        {
            if (source(request) is not { } s)
                return Results.NotFound();
            try
            {
                var to = await email.SendTestAsync(s, Draft(form), ct);
                return Results.Json(new { message = $"A test copy went to {to}. Its buttons don't work, and nothing is marked as sent." });
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();
    }

    private static async Task<IResult> TemplatePreviewAsync(string key, IFormCollection form, EmailService email, CancellationToken ct)
    {
        if (EmailTemplates.FromKey(key) is not { } kind)
            return Results.NotFound();
        try
        {
            var design = Guid.TryParse(Field(form, "DesignId", "design"), out var id) ? id : (Guid?)null;
            return Results.Json(Json(await email.PreviewTemplateAsync(kind, Field(form, "Subject", "subject"), Field(form, "Body", "body"), design, ct)));
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static EmailSource? SalesSource(HttpRequest request)
    {
        var id = Route(request, "id");
        DateOnly? Date(string name) => DateOnly.TryParseExact(request.Query[name], "yyyy-MM-dd", out var d) ? d : null;
        return request.RouteValues["kind"]?.ToString() switch
        {
            "invoices" => new EmailSource(EmailKind.Invoice, id),
            "estimates" => new EmailSource(EmailKind.Estimate, id),
            "credit-memos" => new EmailSource(EmailKind.CreditMemo, id),
            "receipts" => new EmailSource(EmailKind.SalesReceipt, id),
            "payments" => new EmailSource(EmailKind.PaymentReceipt, id),
            "statement" => new EmailSource(EmailKind.Statement, id, Date("from"), Date("to")),
            _ => null,
        };
    }

    private static Guid Route(HttpRequest request, string name) => Guid.Parse(request.RouteValues[name]!.ToString()!);

    /// <summary>The compose form's fields (EditForm names them Input.X).</summary>
    private static EmailDraft Draft(IFormCollection form) => new(
        Field(form, "To", "to") ?? "", Field(form, "Subject", "subject") ?? "", Field(form, "Body", "body") ?? "",
        Field(form, "Cc", "cc"), Field(form, "Bcc", "bcc"),
        Guid.TryParse(Field(form, "DesignId", "design"), out var design) ? design : null,
        Enum.TryParse<EmailPdf>(Field(form, "Pdf", "pdf"), out var pdf) ? pdf : null);

    private static string? Field(IFormCollection form, string inputName, string plainName) =>
        form.TryGetValue("Input." + inputName, out var value) || form.TryGetValue(plainName, out value) ? value.ToString() : null;

    private static object Json(EmailPreview p) => new
    {
        from = p.From, to = p.To, cc = p.Cc, bcc = p.Bcc, subject = p.Subject, html = p.Html, attachment = p.Attachment,
        buttons = p.Buttons, notes = p.Notes, usualDesign = p.UsualDesign,
    };

    private static string Local(string? url) =>
        url is { Length: > 1 } u && u[0] == '/' && u[1] != '/' && u[1] != '\\' ? u : "/";
}
