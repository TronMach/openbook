using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Api;

public sealed record VendorResponse(
    Guid Id,
    string DisplayName,
    string? CompanyName,
    string? Email,
    string? Phone,
    string? Address,
    string? AccountNumber,
    int PaymentTermsDays,
    Guid? DefaultExpenseAccountId,
    string? Notes,
    bool IsActive,
    decimal? Balance = null)
{
    public static VendorResponse From(Vendor v, decimal? balance = null) =>
        new(v.Id, v.DisplayName, v.CompanyName, v.Email, v.Phone, v.Address, v.AccountNumber, v.PaymentTermsDays,
            v.DefaultExpenseAccountId, v.Notes, v.IsActive, balance);
}

public sealed record BillResponse(
    Guid Id,
    Guid VendorId,
    string? Reference,
    DateOnly Date,
    DateOnly DueDate,
    string? Memo,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    DocumentStatus Status,
    Guid JournalEntryId,
    IReadOnlyList<BillLineDraft> Lines)
{
    public static BillResponse From(Bill b) => new(
        b.Id, b.VendorId, b.Reference, b.Date, b.DueDate, b.Memo, b.Total, b.AmountPaid, b.Balance, b.Status, b.JournalEntryId,
        b.Lines.Select(l => new BillLineDraft(l.AccountId, l.Amount, l.Description)).ToList());
}

public sealed record BillPaymentResponse(
    Guid Id,
    Guid VendorId,
    DateOnly Date,
    decimal Amount,
    Guid PaymentAccountId,
    string? Reference,
    string? Memo,
    DocumentStatus Status,
    Guid JournalEntryId,
    IReadOnlyList<ApplyToBill> Applications)
{
    public static BillPaymentResponse From(BillPayment p) => new(
        p.Id, p.VendorId, p.Date, p.Amount, p.PaymentAccountId, p.Reference, p.Memo, p.Status, p.JournalEntryId,
        p.Applications.Select(a => new ApplyToBill(a.BillId, a.Amount)).ToList());
}

internal static class PurchasesApi
{
    public static void MapPurchasesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/vendors", async (bool? includeInactive, VendorService s, CancellationToken ct) =>
            (await s.ListAsync(DateOnly.FromDateTime(DateTime.Today), includeInactive ?? false, ct)).Select(x => VendorResponse.From(x.Vendor, x.Balance)));
        api.MapGet("/vendors/{id:guid}", async (Guid id, VendorService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(VendorResponse.From(v)) : Results.NotFound());
        api.MapPost("/vendors", async (VendorDetails r, VendorService s, CancellationToken ct) =>
        {
            var v = await s.CreateAsync(r, ct);
            return Results.Created($"/api/vendors/{v.Id}", VendorResponse.From(v));
        });
        api.MapPut("/vendors/{id:guid}", async (Guid id, VendorDetails r, VendorService s, CancellationToken ct) =>
            VendorResponse.From(await s.UpdateAsync(id, r, ct)));
        api.MapPut("/vendors/{id:guid}/active", async (Guid id, SetActiveRequest r, VendorService s, CancellationToken ct) =>
            VendorResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
        api.MapPost("/vendors/import", async (HttpRequest request, bool? dryRun, VendorImportService s, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var csv = await reader.ReadToEndAsync(ct);
            return dryRun == true ? await s.PreviewAsync(csv, ct) : await s.ImportAsync(csv, ct);
        }).Accepts<string>("text/csv");

        var purchases = api.MapGroup("/purchases");

        purchases.MapGet("/bills", async (Guid? vendorId, BillService s, CancellationToken ct) =>
            (await s.ListAsync(vendorId, ct)).Select(BillResponse.From));
        purchases.MapGet("/bills/{id:guid}", async (Guid id, BillService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } b ? Results.Ok(BillResponse.From(b)) : Results.NotFound());
        purchases.MapPost("/bills", async (CreateBillCommand r, BillService s, CancellationToken ct) =>
        {
            var b = await s.CreateAsync(r, ct);
            return Results.Created($"/api/purchases/bills/{b.Id}", BillResponse.From(b));
        });
        purchases.MapPost("/bills/{id:guid}/void", async (Guid id, VoidRequest r, BillService s, CancellationToken ct) =>
            BillResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        purchases.MapGet("/payments", async (Guid? vendorId, BillPaymentService s, CancellationToken ct) =>
            (await s.ListAsync(vendorId, ct)).Select(BillPaymentResponse.From));
        purchases.MapGet("/payments/{id:guid}", async (Guid id, BillPaymentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(BillPaymentResponse.From(p)) : Results.NotFound());
        purchases.MapPost("/pay-bills", async (PayBillsCommand r, BillPaymentService s, CancellationToken ct) =>
            (await s.PayAsync(r, ct)).Select(BillPaymentResponse.From));
        purchases.MapPost("/payments/{id:guid}/void", async (Guid id, VoidRequest r, BillPaymentService s, CancellationToken ct) =>
            BillPaymentResponse.From(await s.VoidAsync(id, r.Reason, ct)));
    }
}
