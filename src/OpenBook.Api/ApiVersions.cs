using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Api;

/// <summary>
/// Optimistic concurrency over the API for records people may change at once (those with a version): reading one
/// returns an <c>ETag</c> ("v12"), and a write to it that sends <c>If-Match</c> with a version it no longer has is
/// refused with 412 and code <c>save.stale</c>, so a client that read, waited and wrote can't undo someone's change.
/// Without <c>If-Match</c> nothing is checked, as before. A successful change returns the new <c>ETag</c>.
/// </summary>
public static class ApiVersions
{
    /// <summary>Each record's route up to its id, and the record type whose version it carries.</summary>
    public static readonly IReadOnlyList<(string Route, Type Record)> Routes =
    [
        ("/api/sales/invoices", typeof(Invoice)),
        ("/api/sales/payments", typeof(CustomerPayment)),
        ("/api/sales/receipts", typeof(SalesReceipt)),
        ("/api/sales/estimates", typeof(Estimate)),
        ("/api/sales/credit-memos", typeof(CreditMemo)),
        ("/api/sales/orders", typeof(SalesOrder)),
        ("/api/sales/retainers", typeof(Retainer)),
        ("/api/sales/recurring-invoices", typeof(RecurringInvoice)),
        ("/api/sales/deposits", typeof(BankDeposit)),
        ("/api/purchases/bills", typeof(Bill)),
        ("/api/purchases/payments", typeof(BillPayment)),
        ("/api/purchases/vendor-credits", typeof(VendorCredit)),
        ("/api/purchases/vendor-deposits", typeof(VendorDeposit)),
        ("/api/purchases/orders", typeof(PurchaseOrder)),
        ("/api/purchases/receipts", typeof(ReceiptCapture)),
        ("/api/purchases/recurring-bills", typeof(RecurringBill)),
        ("/api/banking/transactions", typeof(BankTransaction)),
        ("/api/banking/recurring-expenses", typeof(RecurringExpense)),
        ("/api/banking/reconciliations", typeof(Reconciliation)),
        ("/api/recurring-entries", typeof(RecurringEntry)),
        ("/api/customers", typeof(Customer)),
        ("/api/vendors", typeof(Vendor)),
        ("/api/items", typeof(Item)),
        ("/api/nonprofit/gifts", typeof(Domain.Nonprofit.Donation)),
        ("/api/nonprofit/pledges", typeof(Domain.Nonprofit.Pledge)),
        ("/api/nonprofit/grants", typeof(Domain.Nonprofit.Grant)),
        ("/api/nonprofit/recurring-gifts", typeof(Domain.Nonprofit.RecurringGift)),
        ("/api/assets/loans", typeof(Domain.Assets.Loan)),
        ("/api/channels/payouts", typeof(Domain.Channels.ChannelPayout)),
        ("/api/channels/orders", typeof(Domain.Channels.ChannelOrderBatch)),
    ];

    public static string Tag(int version) => $"\"v{version}\"";

    /// <summary>The record a route is about, when it's one of <see cref="Routes"/> followed by <c>{id</c>; whether the route is the record itself.</summary>
    public static (Type Record, bool Itself)? RecordOf(string? rawRoute)
    {
        if (rawRoute is null)
            return null;
        foreach (var (route, record) in Routes)
            if (rawRoute.StartsWith(route + "/{id", StringComparison.OrdinalIgnoreCase))
                return (record, rawRoute.Equals(route + "/{id:guid}", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Whether an <c>If-Match</c> value names <paramref name="version"/> (or is <c>*</c>).</summary>
    public static bool Matches(string ifMatch, int version) =>
        ifMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.StartsWith("W/", StringComparison.Ordinal) ? t[2..] : t)
            .Any(t => t == "*" || t.Trim('"') == $"v{version}");

    internal static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (RecordOf((http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText) is not var (record, itself)
            || !Guid.TryParse(http.GetRouteValue("id")?.ToString(), out var id))
            return await next(context);

        var versions = http.RequestServices.GetRequiredService<IRecordVersions>();
        var ct = http.RequestAborted;
        var reading = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        if (reading)
        {
            // Noted before the read, so a change in between makes the tag older than the body: a later write is refused, never let through.
            if (itself && await versions.GetAsync(record, id, ct) is { } before)
                http.Response.Headers.ETag = Tag(before);
            return await next(context);
        }

        if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } ifMatch
            && await versions.GetAsync(record, id, ct) is { } now && !Matches(ifMatch, now))
        {
            http.Response.Headers.ETag = Tag(now);
            return Results.Problem(
                title: "This was changed by someone else after you read it, so nothing was saved. Read it again to see their change, then make yours.",
                statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = RecordVersionChecks.StaleCode });
        }

        var result = await next(context);
        if (!http.Response.HasStarted && await versions.GetAsync(record, id, ct) is { } after)
            http.Response.Headers.ETag = Tag(after);
        return result;
    }
}
