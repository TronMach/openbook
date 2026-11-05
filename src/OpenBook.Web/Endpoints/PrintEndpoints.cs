using OpenBook.Application.Companies;
using System.Globalization;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Printing: the "print later" queue, and printing a batch of documents and statements as one PDF.</summary>
public static class PrintEndpoints
{
    public static IEndpointRouteBuilder MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: document (a FormDocuments name), id, returnUrl.
        app.MapPost("/print/queue", async (IFormCollection form, PrintService print, CancellationToken ct) =>
            await Back(form, async () => await print.AddAsync(Document(form["document"]), Id(form["id"]), ct))).RequireAuthorization();

        // Fields: document and id, or item (a queue item's id); returnUrl.
        app.MapPost("/print/queue/remove", async (IFormCollection form, PrintService print, CancellationToken ct) =>
            await Back(form, async () =>
            {
                if (Guid.TryParse(form["item"], out var item))
                    await print.RemoveAsync(item, ct);
                else
                    await print.RemoveAsync(Document(form["document"]), Id(form["id"]), ct);
            })).RequireAuthorization();

        // Field: item.
        app.MapPost("/print/put-back", async (IFormCollection form, PrintService print, CancellationToken ct) =>
            await Back(form, async () => await print.PutBackAsync(Id(form["item"]), ct))).RequireAuthorization();

        // Fields: customer (each), from, to. Emails each their statement, then back to /print saying how it went.
        app.MapPost("/print/statements/email", async (IFormCollection form, EmailService email, CancellationToken ct) =>
        {
            try
            {
                var customers = form["customer"].OfType<string>().Select(c => Id(c)).ToList();
                if (customers.Count == 0)
                    throw new DomainException("print.no_customers", "Tick the customers whose statements to send.");
                var run = await email.SendStatementsAsync(customers, Date(form["from"], "start"), Date(form["to"], "end"), ct);
                // Counts only: the page's list marks who has no email address, and names don't belong in an address bar.
                var query = $"emailed={run.Sent.Count}&noEmail={run.NoEmail.Count}"
                    + (run.Failed.Count == 0 ? "" : $"&printError={Uri.EscapeDataString($"{run.Failed.Count} couldn't be sent: {run.Failed[0].Reason}")}");
                return Results.Redirect($"/print?{query}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/print?printError={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Fields: doc (each "Document:id", in print order), customer (each customer whose statement to print), from, to.
        // Answers with the PDF to show or print; refusals as a short page.
        app.MapPost("/print/pdf", async (IFormCollection form, HttpContext http, PrintService print, CancellationToken ct) =>
        {
            try
            {
                var documents = form["doc"].OfType<string>().Select(d => d.Split(':', 2))
                    .Select(p => (Document(p[0]), Id(p.Length > 1 ? p[1] : null))).ToList();
                var customers = form["customer"].OfType<string>().Select(c => Id(c)).ToList();
                StatementRun? statements = customers.Count == 0 ? null : new StatementRun(customers, Date(form["from"], "start"), Date(form["to"], "end"));
                if (documents.Any(d => d.Item1 == FormDocuments.PurchaseOrder) && AuthClaims.LevelIn(http.User, "purchases") is null)
                    throw new DomainException("print.no_access", "Your access doesn't cover purchase orders.");
                var rendered = await print.PrintAsync(documents, statements, ct);
                return Results.File(rendered.Pdf, "application/pdf");
            }
            catch (DomainException ex)
            {
                return Results.Content(
                    $"<!doctype html><meta charset=\"utf-8\"><title>Nothing printed</title><body style=\"font-family:sans-serif;padding:1rem\"><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p><p><a href=\"/print\">Back to printing</a></p></body>",
                    "text/html", statusCode: 400);
            }
        }).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Back(IFormCollection form, Func<Task> change)
    {
        var back = WebSecurity.SafeReturnUrl(form["returnUrl"]);
        try
        {
            await change();
            return Results.Redirect(back);
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{back}{(back.Contains('?', StringComparison.Ordinal) ? '&' : '?')}printError={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static FormDocuments Document(string? value) =>
        Enum.TryParse<FormDocuments>(value, out var d) && FormDesign.Each(PrintQueueItem.Queueable).Contains(d)
            ? d
            : throw new DomainException("print.document_invalid", "That isn't a document that can be printed here.");

    private static Guid Id(string? value) =>
        Guid.TryParse(value, out var id) ? id : throw new DomainException("print.id_invalid", "That isn't a document.");

    private static DateOnly Date(string? value, string which) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new DomainException("print.period_invalid", $"Give the statement period's {which} date.");
}
