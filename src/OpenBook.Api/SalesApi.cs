using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Api;

public sealed record CustomerResponse(
    Guid Id,
    string DisplayName,
    string? CompanyName,
    string? Email,
    string? Phone,
    string? BillingAddress,
    string? ShippingAddress,
    int PaymentTermsDays,
    string? Notes,
    bool IsActive,
    decimal? Balance = null,
    decimal EarlyPaymentDiscountPercent = 0m,
    int EarlyPaymentDiscountDays = 0,
    string? Terms = null,
    Guid? TaxCodeId = null,
    string? ExemptionCertificate = null,
    ExemptionReason? ExemptionReason = null,
    DateOnly? ExemptionExpiresOn = null,
    Guid? PriceLevelId = null,
    Guid? ParentId = null,
    bool BillWithParent = false,
    bool NoReminders = false,
    bool NoLateFees = false)
{
    public static CustomerResponse From(Customer c, decimal? balance = null) =>
        new(c.Id, c.DisplayName, c.CompanyName, c.Email, c.Phone, c.BillingAddress, c.ShippingAddress, c.PaymentTermsDays, c.Notes, c.IsActive, balance,
            c.EarlyPaymentDiscountPercent, c.EarlyPaymentDiscountDays, c.Terms.Label, c.TaxCodeId, c.ExemptionCertificate, c.ExemptionReason, c.ExemptionExpiresOn, c.PriceLevelId,
            c.ParentId, c.BillWithParent, c.NoReminders, c.NoLateFees);
}

public sealed record ItemResponse(
    Guid Id, string Name, ItemType Type, string? Description, decimal Rate, Guid IncomeAccountId, bool IsActive, bool Taxable = true,
    Guid? InventoryAssetAccountId = null, Guid? CostAccountId = null, decimal? ReorderPoint = null, decimal PurchaseCost = 0m,
    IReadOnlyList<QuantityBreakDto>? QuantityBreaks = null, InventoryCostMethod CostMethod = InventoryCostMethod.Fifo, IReadOnlyList<ItemComponent>? Components = null,
    OpenBook.Domain.Inventory.ItemTracking Tracking = OpenBook.Domain.Inventory.ItemTracking.None, string? Barcode = null, bool AutoBuild = false,
    ShortStock SellShort = ShortStock.Company, Guid? PreferredVendorId = null, decimal? ReorderQuantity = null)
{
    public static ItemResponse From(Item i) => new(
        i.Id, i.Name, i.Type, i.Description, i.Rate, i.IncomeAccountId, i.IsActive, i.Taxable, i.InventoryAssetAccountId, i.CostAccountId, i.ReorderPoint, i.PurchaseCost,
        i.QuantityBreaks.Select(b => new QuantityBreakDto(b.FromQuantity, b.Kind, b.Value)).ToList(), i.CostMethod, i.Components.ToList(), i.Tracking, i.Barcode, i.AutoBuild, i.SellShort) { PreferredVendorId = i.PreferredVendorId, ReorderQuantity = i.ReorderQuantity };
}

/// <param name="Rate">For a tax line, the percentage charged.</param>
public sealed record SalesLineResponse(
    int LineNumber, Guid? ItemId, string? Description, decimal Quantity, decimal Rate, decimal Amount, SalesLineKind Kind, bool Taxable = false, Guid? TaxRateId = null,
    decimal? ListRate = null, string? PriceSource = null, Guid? ClassId = null)
{
    public static SalesLineResponse From(SalesLine l) =>
        new(l.LineNumber, l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Kind, l.Taxable, l.TaxRateId, l.ListRate, l.PriceSource, l.ClassId);
}

public sealed record InvoiceResponse(
    Guid Id,
    string Number,
    Guid CustomerId,
    DateOnly Date,
    DateOnly DueDate,
    string? Memo,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    DocumentStatus Status,
    Guid JournalEntryId,
    IReadOnlyList<SalesLineResponse> Lines,
    DateOnly? EarlyPaymentDiscountDate,
    decimal EarlyPaymentDiscountPercent,
    Guid? EstimateId,
    Guid? RecurringInvoiceId,
    DateTimeOffset? LastEmailedAt,
    Guid? TaxCodeId = null,
    string? TaxExemption = null,
    decimal Subtotal = 0m,
    decimal TaxTotal = 0m,
    Guid? LocationId = null)
{
    public static InvoiceResponse From(Invoice i) => new(
        i.Id, i.Number, i.CustomerId, i.Date, i.DueDate, i.Memo, i.Total, i.AmountPaid, i.Balance, i.Status, i.JournalEntryId,
        i.Lines.Select(SalesLineResponse.From).ToList(), i.EarlyPaymentDiscountDate, i.EarlyPaymentDiscountPercent, i.EstimateId,
        i.RecurringInvoiceId, i.LastEmailedAt, i.TaxCodeId, i.TaxExemption, i.Subtotal, i.TaxTotal, i.LocationId);
}

public sealed record PaymentResponse(
    Guid Id,
    Guid CustomerId,
    DateOnly Date,
    decimal Amount,
    Guid DepositAccountId,
    string? Reference,
    string? Memo,
    DocumentStatus Status,
    Guid JournalEntryId,
    IReadOnlyList<PaymentApplicationResponse> Applications,
    decimal Unapplied,
    decimal AmountRefunded,
    Guid? DepositId,
    PaymentMethod? Method = null)
{
    public static PaymentResponse From(CustomerPayment p) => new(
        p.Id, p.CustomerId, p.Date, p.Amount, p.DepositAccountId, p.Reference, p.Memo, p.Status, p.JournalEntryId,
        p.Applications.Select(a => new PaymentApplicationResponse(a.InvoiceId, a.Amount, a.Discount, a.AppliedOn)).ToList(),
        p.Unapplied, p.AmountRefunded, p.DepositId, p.Method);
}

public sealed record PaymentApplicationResponse(Guid InvoiceId, decimal Amount, decimal Discount, DateOnly AppliedOn);

public sealed record SalesReceiptResponse(
    Guid Id,
    string Number,
    Guid? CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    string? Reference,
    string? Memo,
    decimal Total,
    DocumentStatus Status,
    Guid JournalEntryId,
    IReadOnlyList<SalesLineResponse> Lines,
    Guid? DepositId,
    Guid? TaxCodeId = null,
    string? TaxExemption = null,
    decimal Subtotal = 0m,
    decimal TaxTotal = 0m,
    Guid? LocationId = null,
    PaymentMethod? Method = null)
{
    public static SalesReceiptResponse From(SalesReceipt r) => new(
        r.Id, r.Number, r.CustomerId, r.Date, r.DepositAccountId, r.Reference, r.Memo, r.Total, r.Status, r.JournalEntryId,
        r.Lines.Select(SalesLineResponse.From).ToList(), r.DepositId, r.TaxCodeId, r.TaxExemption, r.Subtotal, r.TaxTotal, r.LocationId, r.Method);
}

public sealed record SetActiveRequest(bool Active);

internal static class SalesApi
{
    public static void MapSalesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/customers", async (bool? includeInactive, CustomerService s, CancellationToken ct) =>
            (await s.ListAsync(CompanyTime.Today, includeInactive ?? false, ct)).Select(x => CustomerResponse.From(x.Customer, x.Balance)));
        api.MapGet("/customers/{id:guid}", async (Guid id, CustomerService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } c ? Results.Ok(CustomerResponse.From(c)) : Results.NotFound());
        api.MapPost("/customers", async (CustomerDetails r, CustomerService s, CancellationToken ct) =>
        {
            var c = await s.CreateAsync(r, ct);
            return Results.Created($"/api/customers/{c.Id}", CustomerResponse.From(c));
        });
        api.MapPut("/customers/{id:guid}", async (Guid id, CustomerDetails r, CustomerService s, CancellationToken ct) =>
            CustomerResponse.From(await s.UpdateAsync(id, r, ct)));
        api.MapPut("/customers/{id:guid}/active", async (Guid id, SetActiveRequest r, CustomerService s, CancellationToken ct) =>
            CustomerResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
        api.MapPost("/customers/import", async (HttpRequest request, bool? dryRun, CustomerImportService s, CancellationToken ct) =>
        {
            var csv = await Body(request, ct);
            return dryRun == true ? await s.PreviewAsync(csv, ct) : await s.ImportAsync(csv, ct);
        }).Accepts<string>("text/csv");

        api.MapGet("/items", async (bool? includeInactive, ItemService s, CancellationToken ct) =>
            (await s.ListAsync(includeInactive ?? false, ct)).Select(ItemResponse.From));
        api.MapGet("/items/{id:guid}", async (Guid id, ItemService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } i ? Results.Ok(ItemResponse.From(i)) : Results.NotFound());
        api.MapPost("/items", async (ItemDetails r, ItemService s, CancellationToken ct) =>
        {
            var i = await s.CreateAsync(r, ct);
            return Results.Created($"/api/items/{i.Id}", ItemResponse.From(i));
        });
        api.MapPut("/items/{id:guid}", async (Guid id, ItemDetails r, ItemService s, CancellationToken ct) =>
            ItemResponse.From(await s.UpdateAsync(id, r, ct)));
        api.MapPut("/items/{id:guid}/active", async (Guid id, SetActiveRequest r, ItemService s, CancellationToken ct) =>
            ItemResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
        api.MapPost("/items/import", async (HttpRequest request, bool? dryRun, Guid? defaultIncomeAccountId, ItemImportService s, CancellationToken ct) =>
        {
            var csv = await Body(request, ct);
            return dryRun == true ? await s.PreviewAsync(csv, defaultIncomeAccountId, ct) : await s.ImportAsync(csv, defaultIncomeAccountId, ct);
        }).Accepts<string>("text/csv");

        var sales = api.MapGroup("/sales");

        // ?skip=&take= pages through the newest (total in X-Total-Count); without them, everything.
        sales.MapGet("/invoices", async (Guid? customerId, int? skip, int? take, InvoiceService s, ISalesStore store, HttpResponse response, CancellationToken ct) =>
            !ApiPaging.Requested(skip, take) ? (await s.ListAsync(customerId, ct)).Select(InvoiceResponse.From)
            : customerId is not null ? ApiPaging.Page(response, await s.ListAsync(customerId, ct), skip, take, InvoiceResponse.From)
            : await ApiPaging.PageAsync(response, skip, take, (from, count) => store.ListRecentInvoicesAsync(from, count, ct), InvoiceResponse.From));
        // The same invoice for several customers, all or none; ?preview=true shows each without making any.
        sales.MapPost("/invoices/batch", async (BatchInvoiceCommand r, bool? preview, BatchInvoiceService s, CancellationToken ct) =>
            preview == true ? await s.PreviewAsync(r, ct) : await s.CreateAsync(r, ct));
        sales.MapGet("/invoices/next-number", async (InvoiceService s, CancellationToken ct) => new { Number = await s.SuggestNumberAsync(ct) });
        sales.MapGet("/invoices/{id:guid}", async (Guid id, InvoiceService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } i ? Results.Ok(InvoiceResponse.From(i)) : Results.NotFound());
        sales.MapPost("/invoices", async (CreateInvoiceCommand r, InvoiceService s, CancellationToken ct) =>
        {
            var i = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/invoices/{i.Id}", InvoiceResponse.From(i));
        });
        // Correct a posted invoice in place: its entry reversed and a new one posted, same id and number.
        sales.MapPut("/invoices/{id:guid}", async (Guid id, ChangeInvoiceCommand r, InvoiceService s, CancellationToken ct) =>
            InvoiceResponse.From(await s.ChangeAsync(id, Kept(r, await s.GetAsync(id, ct)), ct)));
        sales.MapPost("/invoices/{id:guid}/void", async (Guid id, VoidRequest r, InvoiceService s, CancellationToken ct) =>
            InvoiceResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/payments", async (Guid? customerId, int? skip, int? take, PaymentService s, ISalesStore store, HttpResponse response, CancellationToken ct) =>
            !ApiPaging.Requested(skip, take) ? (await s.ListAsync(customerId, ct)).Select(PaymentResponse.From)
            : customerId is not null ? ApiPaging.Page(response, await s.ListAsync(customerId, ct), skip, take, PaymentResponse.From)
            : await ApiPaging.PageAsync(response, skip, take, (from, count) => store.ListRecentPaymentsAsync(from, count, ct), PaymentResponse.From));
        sales.MapGet("/payments/{id:guid}", async (Guid id, PaymentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(PaymentResponse.From(p)) : Results.NotFound());
        sales.MapPost("/payments", async (ReceivePaymentCommand r, PaymentService s, CancellationToken ct) =>
        {
            var p = await s.ReceiveAsync(r, ct);
            return Results.Created($"/api/sales/payments/{p.Id}", PaymentResponse.From(p));
        });
        // Correct a posted payment in place: its entry reversed and a new one posted, the invoices settled again.
        sales.MapPut("/payments/{id:guid}", async (Guid id, ChangePaymentCommand r, PaymentService s, CancellationToken ct) =>
            PaymentResponse.From(await s.ChangeAsync(id, r, ct)));
        sales.MapPost("/payments/{id:guid}/void", async (Guid id, VoidRequest r, PaymentService s, CancellationToken ct) =>
            PaymentResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/receipts", async (int? skip, int? take, SalesReceiptService s, ISalesStore store, HttpResponse response, CancellationToken ct) =>
            !ApiPaging.Requested(skip, take) ? (await s.ListAsync(ct)).Select(SalesReceiptResponse.From)
            : await ApiPaging.PageAsync(response, skip, take, (from, count) => store.ListRecentReceiptsAsync(from, count, ct), SalesReceiptResponse.From));
        sales.MapGet("/receipts/{id:guid}", async (Guid id, SalesReceiptService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(SalesReceiptResponse.From(r)) : Results.NotFound());
        sales.MapPost("/receipts", async (CreateSalesReceiptCommand r, SalesReceiptService s, CancellationToken ct) =>
        {
            var receipt = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/receipts/{receipt.Id}", SalesReceiptResponse.From(receipt));
        });
        // Correct a posted sales receipt in place: its entry reversed and a new one posted.
        sales.MapPut("/receipts/{id:guid}", async (Guid id, ChangeSalesReceiptCommand r, SalesReceiptService s, CancellationToken ct) =>
            SalesReceiptResponse.From(await s.ChangeAsync(id, Kept(r, await s.GetAsync(id, ct)), ct)));
        sales.MapPost("/receipts/{id:guid}/void", async (Guid id, VoidRequest r, SalesReceiptService s, CancellationToken ct) =>
            SalesReceiptResponse.From(await s.VoidAsync(id, r.Reason, ct)));
    }

    private static async Task<string> Body(HttpRequest request, CancellationToken ct)
    {
        return await OpenBook.Application.Import.TextFiles.ReadAsync(request.Body, ct);
    }

    private static ChangeInvoiceCommand Kept(ChangeInvoiceCommand r, Invoice? e) => e is null ? r : r with { Memo = r.Memo ?? e.Memo, LocationId = ApiKeep.Id(r.LocationId, e.LocationId), WarehouseId = ApiKeep.Id(r.WarehouseId, e.WarehouseId) };


    private static ChangeSalesReceiptCommand Kept(ChangeSalesReceiptCommand r, SalesReceipt? e) => e is null ? r : r with { Memo = r.Memo ?? e.Memo, LocationId = ApiKeep.Id(r.LocationId, e.LocationId), WarehouseId = ApiKeep.Id(r.WarehouseId, e.WarehouseId) };
}
