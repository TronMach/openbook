using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Documents;
using OpenBook.Domain.Documents;

namespace OpenBook.Api;

public sealed record DocumentReadingSettingsResponse(bool UseClaude, string Model, bool HasApiKey, bool IsReady)
{
    public static DocumentReadingSettingsResponse From(DocumentReadingSettings s) => new(s.UseClaude, s.Model, s.ProtectedApiKey is not null, s.IsReady);
}

public sealed record ReadDocumentRequest(bool UseClaude);

public sealed record ReceiveDocumentRequest(DateOnly? Date = null);

/// <summary>The document inbox: upload (raw body), check, read again, dismiss; the Claude setting (admin).</summary>
public static class DocumentsApi
{
    public static RouteGroupBuilder MapDocumentsApi(this RouteGroupBuilder api)
    {
        var documents = api.MapGroup("/documents");
        documents.MapGet("", async (DocumentCaptureService s, CancellationToken ct) => await s.ListAsync(ct));
        documents.MapGet("/{id:guid}", async (Guid id, DocumentCaptureService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        // The file is the raw body: ?kind=VendorInvoice&fileName=invoice.pdf[&claude=false][&purchaseOrderId=…].
        documents.MapPost("", async (HttpRequest request, DocumentKind kind, string fileName, bool? claude, Guid? purchaseOrderId, DocumentCaptureService s, CancellationToken ct) =>
        {
            var document = await s.UploadAsync(kind, fileName, request.Body, claude, purchaseOrderId, ct);
            return Results.Created($"/api/documents/{document.Id}", document);
        });
        // Earlier uploads of the same file, and bills that may already stand for it.
        documents.MapGet("/{id:guid}/duplicates", async (Guid id, DocumentCaptureService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } d ? Results.Ok(await s.DuplicatesAsync(d, ct)) : Results.NotFound());
        documents.MapPut("/{id:guid}", async (Guid id, DocumentReview r, DocumentCaptureService s, CancellationToken ct) => await s.ReviewAsync(id, r, ct));
        documents.MapPost("/{id:guid}/read", async (Guid id, ReadDocumentRequest r, DocumentCaptureService s, CancellationToken ct) => await s.ReadAgainAsync(id, r.UseClaude, ct));
        documents.MapPost("/{id:guid}/dismiss", async (Guid id, DocumentCaptureService s, CancellationToken ct) => await s.DismissAsync(id, ct));
        documents.MapPost("/{id:guid}/restore", async (Guid id, DocumentCaptureService s, CancellationToken ct) => await s.RestoreAsync(id, ct));
        documents.MapPost("/{id:guid}/bill", async (Guid id, DocumentAccounts r, DocumentConversionService s, CancellationToken ct) =>
        {
            var bill = await s.MakeBillAsync(id, r, ct);
            return Results.Created($"/api/purchases/bills/{bill.Id}", new { BillId = bill.Id });
        });
        documents.MapPost("/{id:guid}/sales-order", async (Guid id, DocumentConversionService s, CancellationToken ct) =>
        {
            var order = await s.MakeSalesOrderAsync(id, ct);
            return Results.Created($"/api/sales/orders/{order.Id}", new { SalesOrderId = order.Id });
        });
        documents.MapPost("/{id:guid}/receive", async (Guid id, ReceiveDocumentRequest r, DocumentConversionService s, CancellationToken ct) =>
            new { PurchaseOrderId = (await s.ReceiveAsync(id, r.Date, ct)).Id });
        documents.MapPost("/{id:guid}/purchase-order", async (Guid id, DocumentAccounts r, DocumentConversionService s, CancellationToken ct) =>
        {
            var order = await s.MakePurchaseOrderAsync(id, r, ct);
            return Results.Created($"/api/purchases/orders/{order.Id}", new { PurchaseOrderId = order.Id });
        });

        api.MapGet("/settings/document-reading", async (DocumentCaptureService s, CancellationToken ct) =>
            DocumentReadingSettingsResponse.From(await s.GetSettingsAsync(ct)));
        api.MapPut("/settings/document-reading", async (DocumentReadingSettingsCommand r, DocumentCaptureService s, CancellationToken ct) =>
            DocumentReadingSettingsResponse.From(await s.SaveSettingsAsync(r, ct)));
        return api;
    }
}
