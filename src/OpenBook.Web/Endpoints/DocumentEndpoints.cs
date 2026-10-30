using System.Globalization;
using OpenBook.Application.Documents;
using OpenBook.Application.Import;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;

namespace OpenBook.Web.Endpoints;

/// <summary>The document inbox's form posts: upload, check the fields, read again, dismiss or put back, and the Claude setting.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: file, kind, claude ("true" to let Claude read it when switched on).
        app.MapPost("/documents/upload", async (IFormFile? file, HttpRequest request, DocumentCaptureService documents, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            if (file is null || file.Length == 0)
                return Results.Redirect($"/documents?error={Uri.EscapeDataString("Choose a file.")}");
            if (!Enum.TryParse<DocumentKind>(form["kind"], out var kind) || !Enum.IsDefined(kind))
                return Results.Redirect($"/documents?error={Uri.EscapeDataString("Choose what kind of document it is.")}");
            try
            {
                await using var stream = file.OpenReadStream();
                var document = await documents.UploadAsync(kind, file.FileName, stream, form["claude"] == "true", ct);
                return Results.Redirect($"/documents/{document.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/documents?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Fields: party, partyId, number, date, dueDate, orderReference, purchaseOrderId, subtotal, tax, shipping, total, rows,
        // and per row i: desc:i, sku:i, qty:i, price:i, amount:i, item:i.
        app.MapPost("/documents/{id:guid}/review", async (Guid id, IFormCollection form, DocumentCaptureService documents, CancellationToken ct) =>
            await Back($"/documents/{id}", async () =>
            {
                var document = await documents.GetAsync(id, ct) ?? throw new EntityNotFoundException(DocumentCaptureService.EntityType, id);
                var fromVendor = DocumentKinds.IsFromVendor(document.Kind);
                var party = Id(form["partyId"]);
                var rows = int.TryParse(form["rows"], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? Math.Min(r, CapturedDocument.MaxLines + 20) : 0;
                var lines = Enumerable.Range(0, rows).Select(i => new DocumentReviewLine(
                    Text(form[$"desc:{i}"]), Text(form[$"sku:{i}"]), Number(form[$"qty:{i}"], "Quantity"), Number(form[$"price:{i}"], "Unit price"),
                    Amount(form[$"amount:{i}"], "Amount"), Id(form[$"item:{i}"]))).ToList();
                await documents.ReviewAsync(id, new DocumentReview(
                    Text(form["party"]), fromVendor ? party : null, fromVendor ? null : party, Text(form["number"]), Date(form["date"]), Date(form["dueDate"]),
                    Text(form["orderReference"]), fromVendor ? Id(form["purchaseOrderId"]) : null,
                    Amount(form["subtotal"], "Subtotal"), Amount(form["tax"], "Tax"), Amount(form["shipping"], "Shipping"), Amount(form["total"], "Total"), lines), ct);
                return "done=saved";
            })).RequireAuthorization();

        // Field: with = claude | server.
        app.MapPost("/documents/{id:guid}/read", async (Guid id, IFormCollection form, DocumentCaptureService documents, CancellationToken ct) =>
            await Back($"/documents/{id}", async () =>
            {
                await documents.ReadAgainAsync(id, form["with"] == "claude", ct);
                return "done=read";
            })).RequireAuthorization();

        app.MapPost("/documents/{id:guid}/dismiss", async (Guid id, DocumentCaptureService documents, CancellationToken ct) =>
            await Back($"/documents/{id}", async () =>
            {
                await documents.DismissAsync(id, ct);
                return "done=dismissed";
            })).RequireAuthorization();

        app.MapPost("/documents/{id:guid}/restore", async (Guid id, DocumentCaptureService documents, CancellationToken ct) =>
            await Back($"/documents/{id}", async () =>
            {
                await documents.RestoreAsync(id, ct);
                return "done=restored";
            })).RequireAuthorization();

        // Fields: useClaude, model, apiKey, clearApiKey.
        app.MapPost("/settings/document-reading", async (IFormCollection form, DocumentCaptureService documents, CancellationToken ct) =>
            await Back("/settings/document-reading", async () =>
            {
                await documents.SaveSettingsAsync(new DocumentReadingSettingsCommand(
                    form["useClaude"] == "true", Text(form["model"]), form["apiKey"], form["clearApiKey"] == "true"), ct);
                return "done=saved";
            })).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Back(string page, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Amount(string? value, string name) =>
        Text(value) is not { } text ? null
        : StatementValues.TryParseAmount(text, out var amount) ? amount
        : throw new DomainException("document.amount_invalid", $"{name} isn't an amount: \"{text}\".");

    private static decimal? Number(string? value, string name) =>
        Text(value) is not { } text ? null
        : decimal.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number
        : throw new DomainException("document.number_invalid", $"{name} isn't a number: \"{text}\".");
}
