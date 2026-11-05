using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Attachments;
using OpenBook.Application.Purchases;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Api;

public sealed record VendorCreditResponse(
    Guid Id, Guid VendorId, string? Reference, DateOnly Date, string? Memo, decimal Total, decimal AmountApplied, decimal AmountRefunded,
    decimal Balance, DocumentStatus Status, Guid JournalEntryId, IReadOnlyList<BillLineDraft> Lines)
{
    public static VendorCreditResponse From(VendorCredit c) => new(
        c.Id, c.VendorId, c.Reference, c.Date, c.Memo, c.Total, c.AmountApplied, c.AmountRefunded, c.Balance, c.Status, c.JournalEntryId,
        c.Lines.Select(l => l.ToDraft()).ToList());
}

public sealed record VendorRefundResponse(
    Guid Id, Guid VendorId, Guid VendorCreditId, DateOnly Date, decimal Amount, Guid DepositAccountId, string? Reference, string? Memo,
    DocumentStatus Status, Guid JournalEntryId)
{
    public static VendorRefundResponse From(VendorRefund r) => new(
        r.Id, r.VendorId, r.VendorCreditId, r.Date, r.Amount, r.DepositAccountId, r.Reference, r.Memo, r.Status, r.JournalEntryId);
}

public sealed record RecurringBillResponse(
    Guid Id, string Name, Guid VendorId, string? Memo, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate,
    int? MaxOccurrences, int OccurrencesPosted, DateOnly? NextDate, bool AutoCreate, bool IsActive, IReadOnlyList<BillLineDraft> Lines)
{
    public static RecurringBillResponse From(RecurringBill r) => new(
        r.Id, r.Name, r.VendorId, r.Memo, r.Frequency, r.Interval, r.StartDate, r.EndDate, r.MaxOccurrences, r.OccurrencesPosted, r.NextDate,
        r.AutoCreate, r.IsActive, r.Lines.Select(l => l.ToDraft()).ToList());
}

public sealed record ApplyVendorCreditRequest(Guid VendorId, DateOnly Date, IReadOnlyList<ApplyVendorCredit> Applications);

internal static class PayablesApi
{
    public static void MapPayablesApi(this RouteGroupBuilder api)
    {
        var purchases = api.MapGroup("/purchases");
        static DateOnly Today() => CompanyTime.Today;

        purchases.MapGet("/vendor-credits", async (Guid? vendorId, VendorCreditService s, CancellationToken ct) =>
            (await s.ListAsync(vendorId, ct)).Select(VendorCreditResponse.From));
        purchases.MapGet("/vendor-credits/{id:guid}", async (Guid id, VendorCreditService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } c ? Results.Ok(VendorCreditResponse.From(c)) : Results.NotFound());
        purchases.MapPost("/vendor-credits", async (CreateVendorCreditCommand r, VendorCreditService s, CancellationToken ct) =>
        {
            var c = await s.CreateAsync(r, ct);
            return Results.Created($"/api/purchases/vendor-credits/{c.Id}", VendorCreditResponse.From(c));
        });
        // Correct a posted vendor credit in place: its entry reversed and a new one posted.
        purchases.MapPut("/vendor-credits/{id:guid}", async (Guid id, ChangeVendorCreditCommand r, VendorCreditService s, CancellationToken ct) =>
            VendorCreditResponse.From(await s.ChangeAsync(id, r, ct)));
        purchases.MapDelete("/vendor-credits/{id:guid}/applications/{billId:guid}", async (Guid id, Guid billId, VendorCreditService s, CancellationToken ct) =>
        {
            await s.UnapplyAsync(id, billId, ct);
            return Results.NoContent();
        });
        purchases.MapPost("/vendor-credits/{id:guid}/void", async (Guid id, VoidRequest r, VendorCreditService s, CancellationToken ct) =>
            VendorCreditResponse.From(await s.VoidAsync(id, r.Reason, ct)));
        purchases.MapPost("/vendor-credits/apply", async (ApplyVendorCreditRequest r, VendorCreditService s, CancellationToken ct) =>
        {
            await s.ApplyAsync(r.VendorId, r.Date, r.Applications ?? [], ct);
            return Results.NoContent();
        });

        purchases.MapGet("/vendor-refunds", async (Guid? vendorId, VendorCreditService s, CancellationToken ct) =>
            (await s.ListRefundsAsync(vendorId, ct)).Select(VendorRefundResponse.From));
        purchases.MapGet("/vendor-refunds/{id:guid}", async (Guid id, VendorCreditService s, CancellationToken ct) =>
            await s.GetRefundAsync(id, ct) is { } r ? Results.Ok(VendorRefundResponse.From(r)) : Results.NotFound());
        purchases.MapPost("/vendor-refunds", async (RecordVendorRefundCommand r, VendorCreditService s, CancellationToken ct) =>
        {
            var refund = await s.RecordRefundAsync(r, ct);
            return Results.Created($"/api/purchases/vendor-refunds/{refund.Id}", VendorRefundResponse.From(refund));
        });
        purchases.MapPost("/vendor-refunds/{id:guid}/void", async (Guid id, VoidRequest r, VendorCreditService s, CancellationToken ct) =>
            VendorRefundResponse.From(await s.VoidRefundAsync(id, r.Reason, ct)));

        purchases.MapGet("/recurring-bills", async (RecurringBillService s, CancellationToken ct) =>
            (await s.ListAsync(ct)).Select(RecurringBillResponse.From));
        purchases.MapGet("/recurring-bills/{id:guid}", async (Guid id, RecurringBillService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(RecurringBillResponse.From(r)) : Results.NotFound());
        purchases.MapPost("/recurring-bills", async (RecurringBillDetails r, RecurringBillService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(r, ct);
            return Results.Created($"/api/purchases/recurring-bills/{created.Id}", RecurringBillResponse.From(created));
        });
        purchases.MapPut("/recurring-bills/{id:guid}", async (Guid id, RecurringBillDetails r, RecurringBillService s, CancellationToken ct) =>
            RecurringBillResponse.From(await s.UpdateAsync(id, r, ct)));
        purchases.MapPut("/recurring-bills/{id:guid}/active", async (Guid id, SetActiveRequest r, bool? catchUp, RecurringBillService s, CancellationToken ct) =>
            RecurringBillResponse.From(await s.SetActiveAsync(id, r.Active, catchUp == true, ct)));
        purchases.MapPost("/recurring-bills/create-due", async (RecurringBillService s, CancellationToken ct) =>
            (await s.CreateDueAsync(Today(), automaticOnly: false, ct)).Select(x => new
            {
                x.Template.Id,
                x.Template.Name,
                Created = x.Created.Select(b => new { b.Id, b.Reference, b.Date }),
                x.Error,
            }));

        purchases.MapGet("/aging", async (DateOnly? asOf, PayablesService s, CancellationToken ct) =>
        {
            var report = await s.AgingAsync(asOf ?? Today(), ct);
            return new
            {
                report.AsOf,
                report.Totals,
                Vendors = report.Vendors.Select(v => new
                {
                    v.Vendor.Id,
                    v.Vendor.DisplayName,
                    v.Buckets,
                    Bills = v.Bills.Select(b => new { b.Bill.Id, b.Bill.Reference, b.Bill.Date, b.Bill.DueDate, b.Balance, b.DaysPastDue }),
                    Credits = v.Credits.Select(c => new { c.Credit.Id, c.Credit.Reference, c.Credit.Date, c.Amount }),
                }),
            };
        });
        purchases.MapGet("/1099", async (int? year, decimal? threshold, PayablesService s, CancellationToken ct) =>
        {
            var report = await s.Report1099Async(year ?? Today().Year - 1, threshold, ct);
            return new
            {
                report.Year,
                report.Threshold,
                Vendors = report.Vendors.Select(v => new
                {
                    v.Vendor.Id,
                    v.Vendor.DisplayName,
                    TaxId = v.Vendor.MaskedTaxId,
                    Box = v.Vendor.Form1099Box,
                    v.Total,
                    v.CardPayments,
                    v.Refunds,
                    v.MeetsThreshold,
                }),
            };
        });

        // Uploads go through the web app's form endpoint, which checks the antiforgery token.
        api.MapGet("/attachments", async (string entityType, Guid entityId, System.Security.Claims.ClaimsPrincipal user, AttachmentService s, CancellationToken ct) =>
            Application.Companies.AuthClaims.LevelIn(user, Application.Companies.RecordAreas.Of(entityType)) is null
                ? Results.Forbid()
                : Results.Ok((await s.ListAsync(entityType, entityId, ct)).Select(a => new { a.Id, a.FileName, a.ContentType, a.Size, a.AddedAt, a.AddedBy })));
        api.MapDelete("/attachments/{id:guid}", async (Guid id, AttachmentService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(id, ct);
            return Results.NoContent();
        });
    }
}
