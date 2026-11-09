using System.Globalization;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Centers;

/// <summary>Where a transaction stands, for the centers' lists.</summary>
public enum CenterState
{
    Posted,
    Open,
    Overdue,
    Paid,
    WrittenOff,
    Voided,
    Pending,
    Accepted,
    Declined,
    Closed,
    Cancelled,
}

/// <summary>
/// One transaction in a center's list. <see cref="EntityType"/> is the audit entity type (the page links it), the
/// amount is the document's own total in its usual sign, <see cref="Open"/> what's still unpaid or unused, and
/// <see cref="Quantity"/> the item's quantity on it (item center only).
/// </summary>
public sealed record CenterRow(
    string EntityType,
    Guid Id,
    DateOnly Date,
    string? Number,
    string? Detail,
    decimal Amount,
    decimal? Open,
    CenterState State,
    decimal? Quantity = null);

/// <summary>
/// The customer, vendor and item centers: every transaction that belongs to one record, newest first. Reads only;
/// each row links to the document's own page, where everything is done as usual.
/// </summary>
public sealed class CenterService(ISalesStore sales, IPurchasesStore purchases, IInventoryStore inventory, Nonprofit.INonprofitStore nonprofit)
{
    public async Task<IReadOnlyList<CenterRow>> CustomerAsync(Guid customerId, DateOnly today, CancellationToken ct = default)
    {
        var rows = new List<CenterRow>();
        foreach (var i in await sales.ListInvoicesAsync(customerId, ct))
            rows.Add(new(InvoiceType, i.Id, i.Date, i.Number, i.Memo, i.Total, i.IsOpen ? i.Balance : null,
                i.Status == DocumentStatus.Voided ? CenterState.Voided : i.IsOverdue(today) ? CenterState.Overdue : i.IsOpen ? CenterState.Open
                : i.WrittenOff > 0m ? CenterState.WrittenOff : CenterState.Paid));
        foreach (var p in await sales.ListPaymentsAsync(customerId, ct))
            rows.Add(new("payment", p.Id, p.Date, p.Reference, p.Memo, p.Amount, p.Status == DocumentStatus.Posted && p.Unapplied > 0m ? p.Unapplied : null, Posted(p.Status)));
        foreach (var r in await sales.ListReceiptsForCustomerAsync(customerId, ct))
            rows.Add(new("sales_receipt", r.Id, r.Date, r.Number, r.Memo, r.Total, null, Posted(r.Status)));
        foreach (var c in await sales.ListCreditMemosAsync(customerId, ct))
            rows.Add(new("credit_memo", c.Id, c.Date, c.Number, c.Memo, c.Total, c.Status == DocumentStatus.Posted && c.Balance > 0m ? c.Balance : null, Posted(c.Status)));
        var gifts = await nonprofit.FindDonationsAsync(g => g.DonorId == customerId, ct);
        foreach (var g in gifts)
            rows.Add(new("donation", g.Id, g.Date, g.Number.ToString(CultureInfo.InvariantCulture), g.Reference ?? g.Memo, g.Amount, null, Posted(g.Status)));
        foreach (var p in (await nonprofit.ListPledgesAsync(ct)).Where(p => p.DonorId == customerId))
        {
            var unpaid = p.Amount - p.CancelledAmount - gifts.Where(g => g.PledgeId == p.Id && g.Status == DocumentStatus.Posted).Sum(g => g.Amount);
            rows.Add(new("pledge", p.Id, p.Date, p.Number.ToString(CultureInfo.InvariantCulture), p.Memo, p.Amount, p.Status == DocumentStatus.Posted && unpaid > 0m ? unpaid : null,
                p.Status == DocumentStatus.Voided ? CenterState.Voided : unpaid > 0m ? CenterState.Open : CenterState.Paid));
        }

        foreach (var g in (await nonprofit.ListGrantsAsync(ct)).Where(g => g.FunderId == customerId))
            rows.Add(new("grant", g.Id, g.AwardedOn, g.Reference, g.Name, g.Amount, null, g.Status == Domain.Nonprofit.GrantStatus.Voided ? CenterState.Voided
                : g.Status == Domain.Nonprofit.GrantStatus.Closed ? CenterState.Closed : CenterState.Open));
        foreach (var r in await sales.ListRefundsAsync(customerId, ct))
            rows.Add(new("refund", r.Id, r.Date, r.Reference, r.Memo, r.Amount, null, Posted(r.Status)));
        foreach (var e in await sales.ListEstimatesAsync(customerId, ct))
            rows.Add(new("estimate", e.Id, e.Date, e.Number, e.Memo, e.Total, null, e.Status switch
            {
                EstimateStatus.Accepted => CenterState.Accepted,
                EstimateStatus.Declined => CenterState.Declined,
                EstimateStatus.Closed => CenterState.Closed,
                _ => CenterState.Pending,
            }));
        foreach (var o in await sales.ListSalesOrdersAsync(customerId, ct))
            rows.Add(new("sales_order", o.Id, o.Date, o.Number, o.Memo, o.Total, null, Order(o.Status == SalesOrderStatus.Open, o.Status == SalesOrderStatus.Cancelled)));
        foreach (var r in await sales.ListRetainersAsync(customerId, ct))
            rows.Add(new("retainer", r.Id, r.Date, r.Reference, r.Memo, r.Amount, r.Status == DocumentStatus.Posted && r.Balance > 0m ? r.Balance : null, Posted(r.Status)));
        foreach (var w in await sales.ListWriteOffsAsync(customerId, ct))
            rows.Add(new("write_off", w.Id, w.Date, null, w.Memo, w.Amount, null, Posted(w.Status)));
        return Newest(rows);
    }

    public async Task<IReadOnlyList<CenterRow>> VendorAsync(Guid vendorId, DateOnly today, CancellationToken ct = default)
    {
        var rows = new List<CenterRow>();
        foreach (var b in await purchases.ListBillsAsync(vendorId, ct))
            rows.Add(new("bill", b.Id, b.Date, b.Reference, b.Memo, b.Total, b.IsOpen ? b.Balance : null,
                b.Status == DocumentStatus.Voided ? CenterState.Voided : b.IsOverdue(today) ? CenterState.Overdue : b.IsOpen ? CenterState.Open : CenterState.Paid));
        foreach (var p in await purchases.ListPaymentsAsync(vendorId, ct))
            rows.Add(new("bill_payment", p.Id, p.Date, p.Reference, p.Memo, p.Amount, null, Posted(p.Status)));
        foreach (var e in await purchases.ListExpensesAsync(vendorId, ct))
            rows.Add(new("bank_transaction", e.Id, e.Date, e.Reference, e.Memo ?? e.Payee, e.Amount, null,
                e.Status == BankTransactionStatus.Voided ? CenterState.Voided : CenterState.Posted));
        foreach (var c in await purchases.ListVendorCreditsAsync(vendorId, ct))
            rows.Add(new("vendor_credit", c.Id, c.Date, c.Reference, c.Memo, c.Total, c.Status == DocumentStatus.Posted && c.Balance > 0m ? c.Balance : null, Posted(c.Status)));
        foreach (var r in await purchases.ListVendorRefundsAsync(vendorId, ct))
            rows.Add(new("vendor_refund", r.Id, r.Date, r.Reference, r.Memo, r.Amount, null, Posted(r.Status)));
        foreach (var o in await purchases.ListPurchaseOrdersAsync(vendorId, ct))
            rows.Add(new("purchase_order", o.Id, o.Date, o.Number, o.Memo, o.Total, null, Order(o.Status == PurchaseOrderStatus.Open, o.Status == PurchaseOrderStatus.Cancelled)));
        return Newest(rows);
    }

    /// <summary>
    /// Documents with a line for the item (quantity and amount summed over its lines), and the stock movements of
    /// anything else that moved it (adjustments, builds, transfers, channel orders), at their cost.
    /// </summary>
    /// <param name="since">Only what's dated from then (null: everything).</param>
    public async Task<IReadOnlyList<CenterRow>> ItemAsync(Guid itemId, DateOnly? since = null, CancellationToken ct = default)
    {
        var rows = new List<CenterRow>();
        void Sold<TStatus>(string type, IEnumerable<ItemDocumentRow<TStatus>> documents, Func<TStatus, CenterState> state)
        {
            foreach (var d in documents)
                rows.Add(new(type, d.Id, d.Date, d.Number, d.Memo, d.Amount, null, state(d.Status), d.Quantity));
        }

        var documents = await sales.ListDocumentsWithItemAsync(itemId, since, ct);
        Sold(InvoiceType, documents.Invoices, Posted);
        Sold("sales_receipt", documents.Receipts, Posted);
        Sold("credit_memo", documents.CreditMemos, Posted);
        Sold("estimate", documents.Estimates, s => s == EstimateStatus.Closed ? CenterState.Closed : CenterState.Pending);
        Sold("sales_order", documents.Orders, s => Order(s == SalesOrderStatus.Open, s == SalesOrderStatus.Cancelled));
        Sold("bill", await purchases.ListBillsWithItemAsync(itemId, since, ct), Posted);
        Sold("purchase_order", await purchases.ListPurchaseOrdersWithItemAsync(itemId, since, ct),
            s => Order(s == PurchaseOrderStatus.Open, s == PurchaseOrderStatus.Cancelled));

        // Documents above already show their own quantities; the rest of the stock ledger shows by source.
        // A component sold in a bundle moved on a document with no line of its own, so that one shows here too.
        var shown = rows.Select(r => (r.EntityType, r.Id)).ToHashSet();
        foreach (var source in (await inventory.ListMovementsAsync(itemId, ct)).Where(m => !shown.Contains((m.SourceType, m.SourceId)))
                     .GroupBy(m => (m.SourceType, m.SourceId)).Where(g => since is null || g.Min(m => m.Date) >= since))
        {
            var quantity = source.Sum(m => m.Quantity);
            var cost = source.Sum(m => m.Cost);
            if (quantity == 0m && cost == 0m && source.Key.SourceType == InventorySources.Transfer)
                continue; // a transfer moves stock between places, not in or out
            rows.Add(new(source.Key.SourceType, source.Key.SourceId, source.Min(m => m.Date), null, null, cost, null, CenterState.Posted, quantity));
        }

        return Newest(rows);
    }

    private const string InvoiceType = "invoice";

    private static CenterState Posted(DocumentStatus status) => status == DocumentStatus.Voided ? CenterState.Voided : CenterState.Posted;

    private static CenterState Order(bool open, bool cancelled) => cancelled ? CenterState.Cancelled : open ? CenterState.Open : CenterState.Closed;

    private static List<CenterRow> Newest(List<CenterRow> rows) =>
        rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();
}
