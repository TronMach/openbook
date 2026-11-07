using System.Globalization;
using OpenBook.Application.Documents;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>The document inbox's form posts: upload, check the fields, read again, dismiss or put back, and the Claude setting.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: file (one or several), kind, claude ("true" to let Claude read it when switched on), purchaseOrderId (optional:
        // uploaded from that order). One file opens its page; several go back to the inbox, saying what was read and what was refused.
        // The form is bound (not read by hand) so the antiforgery check runs.
        app.MapPost("/documents/upload", async (IFormCollection form, DocumentCaptureService documents, CancellationToken ct) =>
        {
            var files = form.Files.GetFiles("file").Where(f => f.Length > 0).ToList();
            if (files.Count == 0)
                return Results.Redirect($"/documents?error={Uri.EscapeDataString("Choose a file.")}");
            if (files.Count > MaxFiles)
                return Results.Redirect($"/documents?error={Uri.EscapeDataString($"Upload at most {MaxFiles} files at once.")}");
            if (!Enum.TryParse<DocumentKind>(form["kind"], out var kind) || !Enum.IsDefined(kind))
                return Results.Redirect($"/documents?error={Uri.EscapeDataString("Choose what kind of document it is.")}");
            var order = Id(form["purchaseOrderId"]);
            var made = new List<Guid>();
            var refused = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    made.Add((await documents.UploadAsync(kind, file.FileName, stream, form["claude"] == "true", order, ct)).Id);
                }
                catch (DomainException ex)
                {
                    refused.Add($"{file.FileName}: {ex.Message}");
                }
            }

            if (made.Count == 1 && refused.Count == 0)
                return Results.Redirect($"/documents/{made[0]}");
            var query = $"uploaded={made.Count}";
            if (refused.Count > 0)
                query += $"&error={Uri.EscapeDataString(string.Join(" ", refused))}";
            return Results.Redirect($"/documents?{query}");
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes));

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

        app.MapPost("/documents/item-codes/{id:guid}/forget", async (IFormCollection form, Guid id, DocumentCaptureService documents, CancellationToken ct) =>
        {
            var back = AccessRules.LocalPath(form["back"].ToString()) ?? "/documents";
            try
            {
                await documents.ForgetItemCodeAsync(id, ct);
                return Results.Redirect(back + "#product-codes");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/documents/{id:guid}/dismiss", async (IFormCollection _, Guid id, DocumentCaptureService documents, CancellationToken ct) =>
            await Back($"/documents/{id}", async () =>
            {
                await documents.DismissAsync(id, ct);
                return "done=dismissed";
            })).RequireAuthorization();

        app.MapPost("/documents/{id:guid}/restore", async (IFormCollection _, Guid id, DocumentCaptureService documents, CancellationToken ct) =>
            await Back($"/documents/{id}", async () =>
            {
                await documents.RestoreAsync(id, ct);
                return "done=restored";
            })).RequireAuthorization();

        // Fields: defaultAccountId, taxAccountId, shippingAccountId, account:{line number}, ClosingPassword.
        app.MapPost("/documents/{id:guid}/bill", async (Guid id, IFormCollection form, DocumentConversionService conversions, ClosedPeriodOverride closing, CancellationToken ct) =>
            await Make(id, form, closing, async () => $"/purchases/bills/{(await conversions.MakeBillAsync(id, Accounts(form), ct)).Id}")).RequireAuthorization();

        app.MapPost("/documents/{id:guid}/sales-order", async (Guid id, IFormCollection form, DocumentConversionService conversions, ClosedPeriodOverride closing, CancellationToken ct) =>
            await Make(id, form, closing, async () => $"/sales/orders/{(await conversions.MakeSalesOrderAsync(id, ct)).Id}")).RequireAuthorization();

        // Fields: date, ClosingPassword.
        app.MapPost("/documents/{id:guid}/receive", async (Guid id, IFormCollection form, DocumentConversionService conversions, ClosedPeriodOverride closing, CancellationToken ct) =>
            await Make(id, form, closing, async () => $"/purchases/orders/{(await conversions.ReceiveAsync(id, Date(form["date"]), ct)).Id}")).RequireAuthorization();

        app.MapPost("/documents/{id:guid}/purchase-order", async (Guid id, IFormCollection form, DocumentConversionService conversions, ClosedPeriodOverride closing, CancellationToken ct) =>
            await Make(id, form, closing, async () => $"/purchases/orders/{(await conversions.MakePurchaseOrderAsync(id, Accounts(form), ct)).Id}")).RequireAuthorization();

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

    /// <summary>Files one upload may carry; each is read in turn.</summary>
    public const int MaxFiles = 10;

    /// <summary>Room for <see cref="MaxFiles"/> files of the largest size an attachment may be.</summary>
    public const long MaxUploadBytes = MaxFiles * (Domain.Attachments.Attachment.MaxBytes + (64 * 1024));

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

    private static async Task<IResult> Make(Guid id, IFormCollection form, ClosedPeriodOverride closing, Func<Task<string>> make)
    {
        closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;
        try
        {
            return Results.Redirect(await make());
        }
        catch (DomainException ex)
        {
            var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
            return Results.Redirect($"/documents/{id}?error={Uri.EscapeDataString(ex.Message)}{password}#make");
        }
    }

    private static DocumentAccounts Accounts(IFormCollection form) => new(
        Id(form["defaultAccountId"]),
        form.Keys.Where(k => k.StartsWith("account:", StringComparison.Ordinal))
            .Select(k => (Line: int.TryParse(k["account:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0, Account: Id(form[k])))
            .Where(x => x.Line > 0 && x.Account is not null)
            .ToDictionary(x => x.Line, x => x.Account!.Value),
        Id(form["taxAccountId"]),
        Id(form["shippingAccountId"]));

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
