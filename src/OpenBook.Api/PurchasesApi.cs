using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Purchases;
using OpenBook.Application;
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
    decimal? Balance = null,
    bool Track1099 = false,
    string? TaxId = null,
    Form1099Box Form1099Box = Form1099Box.NecNonemployeeCompensation)
{
    /// <summary>The tax ID is masked: the API never returns it in full.</summary>
    public static VendorResponse From(Vendor v, decimal? balance = null) =>
        new(v.Id, v.DisplayName, v.CompanyName, v.Email, v.Phone, v.Address, v.AccountNumber, v.PaymentTermsDays,
            v.DefaultExpenseAccountId, v.Notes, v.IsActive, balance, v.Track1099, v.MaskedTaxId, v.Form1099Box);
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
    IReadOnlyList<BillLineDraft> Lines,
    BillApproval Approval = BillApproval.NotRequired,
    string? HoldReason = null,
    Guid? PurchaseOrderId = null)
{
    public static BillResponse From(Bill b) => new(
        b.Id, b.VendorId, b.Reference, b.Date, b.DueDate, b.Memo, b.Total, b.AmountPaid, b.Balance, b.Status, b.JournalEntryId,
        b.Lines.Select(l => l.ToDraft()).ToList(), b.Approval, b.HoldReason, b.PurchaseOrderId);
}

public sealed record PurchaseOrderResponse(
    Guid Id, string Number, Guid VendorId, DateOnly Date, DateOnly? ExpectedDate, string? Memo, decimal Total, PurchaseOrderStatus Status,
    IReadOnlyList<PurchaseOrderLineResponse> Lines)
{
    public static PurchaseOrderResponse From(PurchaseOrder o, PurchaseOrderProgress? progress = null) => new(
        o.Id, o.Number, o.VendorId, o.Date, o.ExpectedDate, o.Memo, o.Total, o.Status,
        o.Lines.Select(l =>
        {
            var status = progress?.Lines.FirstOrDefault(s => s.Line.LineNumber == l.LineNumber);
            return new PurchaseOrderLineResponse(l.LineNumber, l.ItemId, l.AccountId, l.Description, l.Quantity, l.UnitCost, l.Amount,
                status?.Received ?? 0m, status?.BilledQuantity ?? 0m, status?.BilledAmount ?? 0m);
        }).ToList());
}

public sealed record PurchaseOrderLineResponse(
    int LineNumber, Guid? ItemId, Guid? AccountId, string? Description, decimal Quantity, decimal UnitCost, decimal Amount,
    decimal Received, decimal BilledQuantity, decimal BilledAmount);

public sealed record ApprovalRequest(string? Note);

public sealed record AchFileRequest(DateOnly EffectiveDate, IReadOnlyList<Guid> PaymentIds);

public sealed record AchBatchResponse(Guid Id, Guid AccountId, DateOnly EffectiveDate, int EntryCount, decimal Total, DateTimeOffset CreatedAt)
{
    public static AchBatchResponse From(AchBatch b) => new(b.Id, b.AccountId, b.EffectiveDate, b.EntryCount, b.Total, b.CreatedAt);
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
            (await s.ListAsync(CompanyTime.Today, includeInactive ?? false, ct)).Select(x => VendorResponse.From(x.Vendor, x.Balance)));
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
            var csv = await OpenBook.Application.Import.TextFiles.ReadAsync(request.Body, ct);
            return dryRun == true ? await s.PreviewAsync(csv, ct) : await s.ImportAsync(csv, ct);
        }).Accepts<string>("text/csv");

        var purchases = api.MapGroup("/purchases");

        // Inventory products against open orders; all=true for every one, else those needing reordering.
        purchases.MapGet("/reorder", async (bool? all, Application.Inventory.ReorderService s, CancellationToken ct) =>
            (await s.OutlookAsync(null, ct)).Where(o => all == true || o.NeedsReorder).Select(o => new ReorderResponse(
                o.Item.Id, o.Item.Name, o.Item.PreferredVendorId, o.OnHand, o.OnPurchaseOrders, o.OnSalesOrders, o.Available, o.Item.ReorderPoint, o.NeedsReorder, o.Suggested, o.Item.PurchaseCost)));
        purchases.MapPost("/reorder", async (ReorderRequest r, Application.Inventory.ReorderService s, CancellationToken ct) =>
            (await s.CreateOrdersAsync(r.Lines, r.Date ?? CompanyTime.Today, r.ExpectedDate, r.WarehouseId, ct)).Select(o => PurchaseOrderResponse.From(o)));
        purchases.MapGet("/orders", async (Guid? vendorId, PurchaseOrderService s, CancellationToken ct) =>
            (await s.ListAsync(vendorId, ct)).Select(o => PurchaseOrderResponse.From(o)));
        purchases.MapGet("/orders/{id:guid}", async (Guid id, PurchaseOrderService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } o ? Results.Ok(PurchaseOrderResponse.From(o, await s.ProgressAsync(id, ct))) : Results.NotFound());
        purchases.MapPost("/orders", async (SavePurchaseOrderCommand r, PurchaseOrderService s, CancellationToken ct) =>
        {
            var order = await s.CreateAsync(r, ct);
            return Results.Created($"/api/purchases/orders/{order.Id}", PurchaseOrderResponse.From(order));
        });
        purchases.MapPut("/orders/{id:guid}", async (Guid id, SavePurchaseOrderCommand r, PurchaseOrderService s, CancellationToken ct) =>
            PurchaseOrderResponse.From(await s.UpdateAsync(id, r, ct)));
        purchases.MapGet("/orders/{id:guid}/pdf", async (Guid id, Guid? design, PurchaseOrderForms f, CancellationToken ct) =>
        {
            var pdf = await f.RenderAsync(id, design, ct);
            return Results.File(pdf.Pdf, "application/pdf", pdf.FileName);
        });
        purchases.MapPost("/orders/{id:guid}/email", async (Guid id, EmailRequest r, OpenBook.Application.Forms.EmailService s, CancellationToken ct) =>
        {
            await EmailApi.SendAsync(s, new OpenBook.Application.Forms.EmailSource(OpenBook.Domain.Email.EmailKind.PurchaseOrder, id), r, ct);
            return Results.NoContent();
        });
        purchases.MapPost("/orders/{id:guid}/cancel", async (Guid id, PurchaseOrderService s, CancellationToken ct) =>
            PurchaseOrderResponse.From(await s.CancelAsync(id, ct)));
        purchases.MapPost("/orders/{id:guid}/receipts", async (Guid id, ReceiveGoodsCommand r, PurchaseOrderService s, CancellationToken ct) =>
            new { ReceiptNumber = await s.ReceiveAsync(id, r, ct) });
        purchases.MapPost("/orders/{id:guid}/receipts/{number:int}/void", async (Guid id, int number, PurchaseOrderService s, CancellationToken ct) =>
            PurchaseOrderResponse.From(await s.VoidReceiptAsync(id, number, ct)));
        purchases.MapPost("/orders/{id:guid}/bill", async (Guid id, BillFromOrderCommand r, PurchaseOrderService s, CancellationToken ct) =>
        {
            var bill = await s.CreateBillAsync(id, r, ct);
            return Results.Created($"/api/purchases/bills/{bill.Id}", BillResponse.From(bill));
        });

        purchases.MapGet("/receipts", async (ReceiptService s, CancellationToken ct) => await s.ListAsync(ct));
        purchases.MapGet("/receipts/{id:guid}", async (Guid id, ReceiptService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(r) : Results.NotFound());
        purchases.MapPost("/receipts", async (HttpRequest request, string fileName, ReceiptService s, CancellationToken ct) =>
        {
            var receipt = await s.UploadAsync(fileName, request.Body, ct);
            return Results.Created($"/api/purchases/receipts/{receipt.Id}", receipt);
        });
        purchases.MapPost("/receipts/{id:guid}/record", async (Guid id, RecordReceiptCommand r, ReceiptService s, CancellationToken ct) =>
            new { BankTransactionId = (await s.RecordAsync(id, r, ct)).Id });
        purchases.MapPost("/receipts/{id:guid}/dismiss", async (Guid id, ReceiptService s, CancellationToken ct) => await s.DismissAsync(id, ct));

        purchases.MapGet("/approvals", async (BillApprovalService s, CancellationToken ct) => (await s.PendingAsync(ct)).Select(BillResponse.From));
        purchases.MapPost("/approvals/{billId:guid}/approve", async (Guid billId, ApprovalRequest r, BillApprovalService s, CancellationToken ct) =>
            BillResponse.From(await s.ApproveAsync(billId, r.Note, ct)));
        purchases.MapPost("/approvals/{billId:guid}/reject", async (Guid billId, ApprovalRequest r, BillApprovalService s, CancellationToken ct) =>
            BillResponse.From(await s.RejectAsync(billId, r.Note, ct)));
        purchases.MapGet("/approvals/settings", async (BillApprovalService s, CancellationToken ct) => await s.GetSettingsAsync(ct));
        purchases.MapPut("/approvals/settings", async (PurchasingSettingsCommand r, BillApprovalService s, CancellationToken ct) => await s.SaveSettingsAsync(r, ct));

        purchases.MapPut("/ach/vendors/{vendorId:guid}", async (Guid vendorId, VendorBankAccountCommand r, AchService s, CancellationToken ct) =>
        {
            var account = await s.SetVendorBankAccountAsync(vendorId, r, ct);
            return new { account.VendorId, account.RoutingNumber, account.AccountLast4, account.Kind };
        });
        purchases.MapDelete("/ach/vendors/{vendorId:guid}", async (Guid vendorId, AchService s, CancellationToken ct) =>
        {
            await s.RemoveVendorBankAccountAsync(vendorId, ct);
            return Results.NoContent();
        });
        purchases.MapGet("/ach/{accountId:guid}/settings", async (Guid accountId, AchService s, CancellationToken ct) =>
            await s.GetSettingsAsync(accountId, ct) is { } settings ? Results.Ok(settings) : Results.NotFound());
        purchases.MapPut("/ach/{accountId:guid}/settings", async (Guid accountId, AchSettingsCommand r, AchService s, CancellationToken ct) =>
            await s.SaveSettingsAsync(accountId, r, ct));
        purchases.MapGet("/ach/{accountId:guid}/payments", async (Guid accountId, AchService s, CancellationToken ct) =>
            (await s.CandidatesAsync(accountId, ct)).Select(c => new { PaymentId = c.Payment.Id, c.Payment.Date, c.Payment.Amount, c.Vendor.DisplayName, c.BankAccount.AccountLast4 }));
        purchases.MapGet("/ach/{accountId:guid}/files", async (Guid accountId, AchService s, CancellationToken ct) =>
            (await s.ListBatchesAsync(accountId, ct)).Select(AchBatchResponse.From));
        purchases.MapPost("/ach/{accountId:guid}/files", async (Guid accountId, AchFileRequest r, AchService s, CancellationToken ct) =>
        {
            var batch = await s.CreateFileAsync(accountId, r.EffectiveDate, r.PaymentIds ?? [], ct);
            return Results.Created($"/api/purchases/ach/files/{batch.Id}", AchBatchResponse.From(batch));
        });
        purchases.MapGet("/ach/files/{id:guid}", async (Guid id, AchService s, CancellationToken ct) =>
            await s.GetBatchAsync(id, ct) is { } batch ? Results.Text(s.FileText(batch), "text/plain") : Results.NotFound());

        // ?skip=&take= pages through the newest (total in X-Total-Count); without them, everything.
        purchases.MapGet("/bills", async (Guid? vendorId, int? skip, int? take, BillService s, IPurchasesStore store, HttpResponse response, CancellationToken ct) =>
            !ApiPaging.Requested(skip, take) ? (await s.ListAsync(vendorId, ct)).Select(BillResponse.From)
            : vendorId is not null ? ApiPaging.Page(response, await s.ListAsync(vendorId, ct), skip, take, BillResponse.From)
            : await ApiPaging.PageAsync(response, skip, take, (from, count) => store.ListRecentBillsAsync(from, count, ct), BillResponse.From));
        purchases.MapGet("/bills/{id:guid}", async (Guid id, BillService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } b ? Results.Ok(BillResponse.From(b)) : Results.NotFound());
        // Bills that may be the same one entered before: same vendor and total, within two weeks.
        purchases.MapGet("/bills/duplicates", async (Guid vendorId, DateOnly date, decimal total, Guid? except, BillService s, CancellationToken ct) =>
            (await s.PossibleDuplicatesAsync(vendorId, date, total, except, ct)).Select(BillResponse.From));
        purchases.MapPost("/bills", async (CreateBillCommand r, BillService s, CancellationToken ct) =>
        {
            var b = await s.CreateAsync(r, ct);
            return Results.Created($"/api/purchases/bills/{b.Id}", BillResponse.From(b));
        });
        // A bill entered from a purchase order is corrected by order line: billed again against the order's other bills.
        purchases.MapGet("/bills/{id:guid}/from-order", async (Guid id, PurchaseOrderService s, CancellationToken ct) =>
        {
            var parts = await s.BillPartsAsync(id, ct);
            return new { OrderId = parts.Order.Id, parts.Picks, parts.Extra };
        });
        purchases.MapPut("/bills/{id:guid}/from-order", async (Guid id, ChangeBillFromOrderCommand r, PurchaseOrderService s, CancellationToken ct) =>
            BillResponse.From(await s.ChangeBillAsync(id, r, ct)));
        // Correct a posted bill in place: its entry reversed and a new one posted, the approval rules run again.
        purchases.MapPut("/bills/{id:guid}", async (Guid id, ChangeBillCommand r, BillService s, CancellationToken ct) =>
            BillResponse.From(await s.ChangeAsync(id, Kept(r, await s.GetAsync(id, ct)), ct)));
        purchases.MapPost("/bills/{id:guid}/void", async (Guid id, VoidRequest r, BillService s, CancellationToken ct) =>
            BillResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        purchases.MapGet("/payments", async (Guid? vendorId, int? skip, int? take, BillPaymentService s, IPurchasesStore store, HttpResponse response, CancellationToken ct) =>
            !ApiPaging.Requested(skip, take) ? (await s.ListAsync(vendorId, ct)).Select(BillPaymentResponse.From)
            : vendorId is not null ? ApiPaging.Page(response, await s.ListAsync(vendorId, ct), skip, take, BillPaymentResponse.From)
            : await ApiPaging.PageAsync(response, skip, take, (from, count) => store.ListRecentPaymentsAsync(from, count, ct), BillPaymentResponse.From));
        purchases.MapGet("/payments/{id:guid}", async (Guid id, BillPaymentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(BillPaymentResponse.From(p)) : Results.NotFound());
        purchases.MapPost("/pay-bills", async (PayBillsCommand r, BillPaymentService s, CancellationToken ct) =>
            (await s.PayAsync(r, ct)).Select(BillPaymentResponse.From));
        // Correct a posted bill payment in place: its entry reversed and a new one posted, the bills paid again.
        purchases.MapPut("/payments/{id:guid}", async (Guid id, ChangeBillPaymentCommand r, BillPaymentService s, CancellationToken ct) =>
            BillPaymentResponse.From(await s.ChangeAsync(id, r, ct)));
        purchases.MapPost("/payments/{id:guid}/void", async (Guid id, VoidRequest r, BillPaymentService s, CancellationToken ct) =>
            BillPaymentResponse.From(await s.VoidAsync(id, r.Reason, ct)));
    }


    private static ChangeBillCommand Kept(ChangeBillCommand r, Bill? e) => e is null ? r : r with { Memo = r.Memo ?? e.Memo, LocationId = ApiKeep.Id(r.LocationId, e.LocationId), WarehouseId = ApiKeep.Id(r.WarehouseId, e.WarehouseId) };
}

public sealed record ReorderResponse(
    Guid ItemId, string Name, Guid? PreferredVendorId, decimal OnHand, decimal OnPurchaseOrders, decimal OnSalesOrders, decimal Available,
    decimal? ReorderPoint, bool NeedsReorder, decimal Suggested, decimal PurchaseCost);

/// <param name="Date">Defaults to today.</param>
public sealed record ReorderRequest(IReadOnlyList<Application.Inventory.ReorderLine> Lines, DateOnly? Date = null, DateOnly? ExpectedDate = null, Guid? WarehouseId = null);
