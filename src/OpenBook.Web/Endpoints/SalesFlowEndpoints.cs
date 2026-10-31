using System.Globalization;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the sales order, estimate (progress invoicing), retainer and billable expense pages (antiforgery-checked, form-bound).</summary>
public static class SalesFlowEndpoints
{
    public static IEndpointRouteBuilder MapSalesFlowEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: mode (rest/percent/lines), percent, qty:{lineNumber}, date.
        app.MapPost("/sales/orders/{id:guid}/invoice", async (Guid id, IFormCollection form, SalesOrderService orders, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await ToInvoice($"/sales/orders/{id}", () => orders.InvoiceAsync(id, InvoiceCommand(form), ct));
        }).RequireAuthorization();

        app.MapPost("/sales/estimates/{id:guid}/invoice", async (Guid id, IFormCollection form, EstimateService estimates, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await ToInvoice($"/sales/estimates/{id}", () => estimates.ProgressInvoiceAsync(id, InvoiceCommand(form), ct));
        }).RequireAuthorization();

        app.MapPost("/sales/orders/{id:guid}/cancel", async (Guid id, SalesOrderService orders, CancellationToken ct) =>
            await Back($"/sales/orders/{id}", async () =>
            {
                await orders.CancelAsync(id, ct);
                return "done=cancelled";
            })).RequireAuthorization();

        app.MapPost("/sales/retainers", async (IFormCollection form, RetainerService retainers, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var retainer = await retainers.ReceiveAsync(new ReceiveRetainerCommand(
                    Id(form["customerId"]) ?? throw new DomainException("retainer.customer_required", "Choose the customer."),
                    Date(form["date"]) ?? Today(),
                    Id(form["accountId"]) ?? throw new DomainException("retainer.deposit_account", "Choose the bank account it went into."),
                    Amount(form["amount"]) ?? throw new DomainException("retainer.amount_not_positive", "Enter an amount above zero."),
                    form["reference"], form["memo"]), ct);
                return Results.Redirect($"/sales/retainers/{retainer.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
                return Results.Redirect($"/sales/retainers/new?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/retainers/{id:guid}/apply", async (Guid id, IFormCollection form, RetainerService retainers, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/sales/retainers/{id}", async () =>
            {
                var invoiceId = Id(form["invoiceId"]) ?? throw new DomainException("retainer.invoice_required", "Choose the invoice.");
                await retainers.ApplyAsync(id, new ApplyRetainerCommand(invoiceId, Date(form["date"]) ?? Today(), Amount(form["amount"])), ct);
                return "done=applied";
            });
        }).RequireAuthorization();

        app.MapPost("/sales/retainers/{id:guid}/refund", async (Guid id, IFormCollection form, RetainerService retainers, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/sales/retainers/{id}", async () =>
            {
                await retainers.RefundAsync(id, new RefundRetainerCommand(
                    Date(form["date"]) ?? Today(),
                    Id(form["accountId"]) ?? throw new DomainException("retainer.refund_account", "Choose the account to pay from."),
                    Amount(form["amount"]) ?? 0m, form["reference"]), ct);
                return "done=refunded";
            });
        }).RequireAuthorization();

        app.MapPost("/sales/retainers/{id:guid}/applications/{applicationId:guid}/void", async (Guid id, Guid applicationId, IFormCollection form, RetainerService retainers, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/sales/retainers/{id}", async () =>
            {
                await retainers.VoidApplicationAsync(id, applicationId, Reason(form), ct);
                return "done=voided";
            });
        }).RequireAuthorization();

        app.MapPost("/sales/retainers/{id:guid}/refunds/{refundId:guid}/void", async (Guid id, Guid refundId, IFormCollection form, RetainerService retainers, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/sales/retainers/{id}", async () =>
            {
                await retainers.VoidRefundAsync(id, refundId, Reason(form), ct);
                return "done=voided";
            });
        }).RequireAuthorization();

        app.MapPost("/sales/retainers/{id:guid}/void", async (Guid id, IFormCollection form, RetainerService retainers, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/sales/retainers/{id}", async () =>
            {
                await retainers.VoidAsync(id, Reason(form), ct);
                return "done=voided";
            });
        }).RequireAuthorization();

        // Fields: kind, documentId, lineNumber, customerId, markup, back (the bill or expense page).
        app.MapPost("/sales/billable", async (IFormCollection form, BillableExpenseService billables, CancellationToken ct) =>
        {
            var back = BackTo(form);
            try
            {
                if (!Enum.TryParse<BillableSource>(form["kind"], out var kind) || Id(form["documentId"]) is not { } documentId
                    || !int.TryParse(form["lineNumber"], NumberStyles.None, CultureInfo.InvariantCulture, out var lineNumber))
                    throw new DomainException("billable.line_invalid", "That line can't be billed.");
                var customerId = Id(form["customerId"]) ?? throw new DomainException("billable.customer_required", "Choose the customer to bill.");
                await billables.MarkAsync(new MarkBillableCommand(kind, documentId, lineNumber, customerId, Amount(form["markup"]) ?? 0m,
                    Billable: form["billable"] == "true"), ct);
                return Results.Redirect($"{back}#billable");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}?billableError={Uri.EscapeDataString(ex.Message)}#billable");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/billable/{id:guid}/remove", async (Guid id, IFormCollection form, BillableExpenseService billables, CancellationToken ct) =>
        {
            var back = BackTo(form);
            try
            {
                await billables.RemoveAsync(id, ct);
                return Results.Redirect($"{back}#billable");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}?billableError={Uri.EscapeDataString(ex.Message)}#billable");
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>The invoice command from the partial invoice form: everything left, a percentage, or the quantities entered.</summary>
    private static InvoiceFromSourceCommand InvoiceCommand(IFormCollection form)
    {
        var date = Date(form["date"]) ?? Today();
        switch (form["mode"].ToString())
        {
            case "percent":
                return new InvoiceFromSourceCommand(date, Percent: Amount(form["percent"])
                    ?? throw new DomainException("invoice.percent_invalid", "Enter a percentage above 0 and up to 100."));
            case "lines":
                var lines = new List<InvoiceSourceLine>();
                foreach (var (field, value) in form)
                {
                    if (field.StartsWith("qty:", StringComparison.Ordinal) && int.TryParse(field[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var line)
                        && value.ToString().Trim() is { Length: > 0 } text)
                        lines.Add(new InvoiceSourceLine(line, decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity)
                            ? quantity
                            : throw new DomainException("invoice.quantity_invalid", $"'{text}' isn't a quantity.")));
                }

                return new InvoiceFromSourceCommand(date, lines);
            default:
                return new InvoiceFromSourceCommand(date);
        }
    }

    private static async Task<IResult> ToInvoice(string page, Func<Task<Invoice>> create)
    {
        try
        {
            return Results.Redirect($"/sales/invoices/{(await create()).Id}");
        }
        catch (DomainException ex)
        {
            var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{password}#invoice");
        }
    }

    private static async Task<IResult> Back(string page, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}");
        }
        catch (DomainException ex)
        {
            var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{password}");
        }
    }

    /// <summary>A local page to return to (a bill or expense page), never another site.</summary>
    private static string BackTo(IFormCollection form) =>
        form["back"].ToString() is { } back && (back.StartsWith("/purchases/bills/", StringComparison.Ordinal) || back.StartsWith("/banking/transactions/", StringComparison.Ordinal))
            ? back
            : "/sales/billable";

    private static string Reason(IFormCollection form) =>
        form["reason"].ToString().Trim() is { Length: > 0 } reason ? reason : throw new DomainException("void.reason_required", "Say why it's being voided.");

    private static string? Password(IFormCollection form) => form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Amount(string? value) =>
        StatementValues.TryParseAmount(value, out var amount) ? amount : null;

    private static DateOnly Today() => CompanyTime.Today;
}
