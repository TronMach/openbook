using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
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
    decimal? Balance = null)
{
    public static CustomerResponse From(Customer c, decimal? balance = null) =>
        new(c.Id, c.DisplayName, c.CompanyName, c.Email, c.Phone, c.BillingAddress, c.ShippingAddress, c.PaymentTermsDays, c.Notes, c.IsActive, balance);
}

public sealed record ItemResponse(Guid Id, string Name, ItemType Type, string? Description, decimal Rate, Guid IncomeAccountId, bool IsActive)
{
    public static ItemResponse From(Item i) => new(i.Id, i.Name, i.Type, i.Description, i.Rate, i.IncomeAccountId, i.IsActive);
}

public sealed record SalesLineResponse(int LineNumber, Guid? ItemId, string? Description, decimal Quantity, decimal Rate, decimal Amount)
{
    public static SalesLineResponse From(SalesLine l) => new(l.LineNumber, l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount);
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
    IReadOnlyList<SalesLineResponse> Lines)
{
    public static InvoiceResponse From(Invoice i) => new(
        i.Id, i.Number, i.CustomerId, i.Date, i.DueDate, i.Memo, i.Total, i.AmountPaid, i.Balance, i.Status, i.JournalEntryId,
        i.Lines.Select(SalesLineResponse.From).ToList());
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
    IReadOnlyList<ApplyToInvoice> Applications)
{
    public static PaymentResponse From(CustomerPayment p) => new(
        p.Id, p.CustomerId, p.Date, p.Amount, p.DepositAccountId, p.Reference, p.Memo, p.Status, p.JournalEntryId,
        p.Applications.Select(a => new ApplyToInvoice(a.InvoiceId, a.Amount)).ToList());
}

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
    IReadOnlyList<SalesLineResponse> Lines)
{
    public static SalesReceiptResponse From(SalesReceipt r) => new(
        r.Id, r.Number, r.CustomerId, r.Date, r.DepositAccountId, r.Reference, r.Memo, r.Total, r.Status, r.JournalEntryId,
        r.Lines.Select(SalesLineResponse.From).ToList());
}

public sealed record SetActiveRequest(bool Active);

internal static class SalesApi
{
    public static void MapSalesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/customers", async (bool? includeInactive, CustomerService s, CancellationToken ct) =>
            (await s.ListAsync(DateOnly.FromDateTime(DateTime.Today), includeInactive ?? false, ct)).Select(x => CustomerResponse.From(x.Customer, x.Balance)));
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

        sales.MapGet("/invoices", async (Guid? customerId, InvoiceService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Select(InvoiceResponse.From));
        sales.MapGet("/invoices/next-number", async (InvoiceService s, CancellationToken ct) => new { Number = await s.SuggestNumberAsync(ct) });
        sales.MapGet("/invoices/{id:guid}", async (Guid id, InvoiceService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } i ? Results.Ok(InvoiceResponse.From(i)) : Results.NotFound());
        sales.MapPost("/invoices", async (CreateInvoiceCommand r, InvoiceService s, CancellationToken ct) =>
        {
            var i = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/invoices/{i.Id}", InvoiceResponse.From(i));
        });
        sales.MapPost("/invoices/{id:guid}/void", async (Guid id, VoidRequest r, InvoiceService s, CancellationToken ct) =>
            InvoiceResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/payments", async (Guid? customerId, PaymentService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Select(PaymentResponse.From));
        sales.MapGet("/payments/{id:guid}", async (Guid id, PaymentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } p ? Results.Ok(PaymentResponse.From(p)) : Results.NotFound());
        sales.MapPost("/payments", async (ReceivePaymentCommand r, PaymentService s, CancellationToken ct) =>
        {
            var p = await s.ReceiveAsync(r, ct);
            return Results.Created($"/api/sales/payments/{p.Id}", PaymentResponse.From(p));
        });
        sales.MapPost("/payments/{id:guid}/void", async (Guid id, VoidRequest r, PaymentService s, CancellationToken ct) =>
            PaymentResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/receipts", async (SalesReceiptService s, CancellationToken ct) =>
            (await s.ListAsync(ct)).Select(SalesReceiptResponse.From));
        sales.MapGet("/receipts/{id:guid}", async (Guid id, SalesReceiptService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(SalesReceiptResponse.From(r)) : Results.NotFound());
        sales.MapPost("/receipts", async (CreateSalesReceiptCommand r, SalesReceiptService s, CancellationToken ct) =>
        {
            var receipt = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/receipts/{receipt.Id}", SalesReceiptResponse.From(receipt));
        });
        sales.MapPost("/receipts/{id:guid}/void", async (Guid id, VoidRequest r, SalesReceiptService s, CancellationToken ct) =>
            SalesReceiptResponse.From(await s.VoidAsync(id, r.Reason, ct)));
    }

    private static async Task<string> Body(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(ct);
    }
}
