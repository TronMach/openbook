using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Search;

public enum SearchKind
{
    Customer,
    Vendor,
    Item,
    Account,
    Invoice,
    Bill,
    SalesReceipt,
    CustomerPayment,
    BillPayment,
    BankTransaction,
    JournalEntry,
    Gift,
    Pledge,
    Grant,
    Estimate,
    SalesOrder,
    CreditMemo,
    Deposit,
    PurchaseOrder,
    VendorCredit,
    ChannelOrder,
}

/// <summary>The area (as in permission sets and API key scopes) each kind of result belongs to.</summary>
public static class SearchAreas
{
    public static string Of(SearchKind kind) => kind switch
    {
        SearchKind.Customer or SearchKind.Item or SearchKind.Invoice or SearchKind.SalesReceipt or SearchKind.CustomerPayment
            or SearchKind.Estimate or SearchKind.SalesOrder or SearchKind.CreditMemo => "sales",
        SearchKind.Vendor or SearchKind.Bill or SearchKind.BillPayment or SearchKind.PurchaseOrder or SearchKind.VendorCredit => "purchases",
        SearchKind.BankTransaction or SearchKind.Deposit => "banking",
        SearchKind.Gift or SearchKind.Pledge or SearchKind.Grant => "nonprofit",
        SearchKind.ChannelOrder => "channels",
        _ => "ledger",
    };
}

public sealed record SearchHit(SearchKind Kind, Guid Id, string Title, string? Detail = null, DateOnly? Date = null, decimal? Amount = null);

public sealed record SearchResults(string Query, IReadOnlyList<SearchHit> Hits, bool Truncated);

/// <summary>
/// One box that finds anything: names, numbers, references, memos, and amounts ("1,250", "$89.99"), ignoring case.
/// Lists (customers, vendors, items, accounts, custom field values) are searched in memory; documents and journal
/// entries are narrowed by the database (<see cref="DocumentSearch"/>: text in lower case, so on SQLite only ASCII
/// letters' case is ignored), so a search doesn't read every document ever entered. The newest come first.
/// </summary>
public sealed class SearchService(
    AccountService accounts,
    ISalesStore sales,
    IPurchasesStore purchases,
    IBankingStore banking,
    ILedgerStore ledger,
    CustomFields.CustomFieldService customFields, Nonprofit.INonprofitStore nonprofit,
    Channels.IChannelOrderStore channelOrders, Channels.IChannelStore channels)
{
    public const int MaxPerKind = 20;

    public async Task<SearchResults> SearchAsync(string? query, CancellationToken ct = default)
    {
        query = query is null ? null : OpenBook.Domain.Common.TextCleaning.Clean(query);
        var q = query?.Trim() ?? string.Empty;
        if (q.Length < 2)
            return new SearchResults(q, [], Truncated: false);

        // Beyond what cents can hold, it's no amount: a long reference number, say.
        decimal? amount = StatementValues.TryParseAmount(q, out var parsed) && parsed != 0m && Math.Abs(parsed) < 1_000_000_000_000_000m ? Math.Abs(parsed) : null;
        long? entryNumber = long.TryParse(q.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        bool Text(params string?[] fields) => fields.Any(f => f is not null && f.Contains(q, StringComparison.OrdinalIgnoreCase));
        bool Money(decimal value) => amount is { } a && Math.Abs(value) == a;

        var hits = new List<SearchHit>();
        var truncated = false;
        void AddAll(IEnumerable<SearchHit> found)
        {
            var list = found.Take(MaxPerKind + 1).ToList();
            truncated |= list.Count > MaxPerKind;
            hits.AddRange(list.Take(MaxPerKind));
        }

        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id);
        var chart = await accounts.GetChartAsync(ct);
        // Custom field values count as text of their record.
        var fieldValues = await customFields.ValuesByRecordAsync(["customer", "vendor", "item", "invoice", "bill", "sales_receipt"], ct);
        bool Field(Guid id) => fieldValues.TryGetValue(id, out var entries) && entries.Any(e => Text(e.Value, e.Display));

        AddAll(customers.Values
            .Where(c => Text(c.DisplayName, c.CompanyName, c.Email, c.Phone) || Field(c.Id))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new SearchHit(SearchKind.Customer, c.Id, c.DisplayName, c.Email ?? c.CompanyName)));
        AddAll(vendors.Values
            .Where(v => Text(v.DisplayName, v.CompanyName, v.Email, v.Phone, v.AccountNumber) || Field(v.Id))
            .OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new SearchHit(SearchKind.Vendor, v.Id, v.DisplayName, v.Email ?? v.CompanyName)));
        AddAll((await sales.ListItemsAsync(ct))
            .Where(i => Text(i.Name, i.Description) || Money(i.Rate) || Field(i.Id))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new SearchHit(SearchKind.Item, i.Id, i.Name, i.Description, Amount: i.Rate)));
        AddAll(chart.Ordered()
            .Where(x => Text(chart.FullName(x.Account.Id), x.Account.Number, x.Account.Description))
            .Select(x => new SearchHit(SearchKind.Account, x.Account.Id, chart.FullName(x.Account.Id), x.Account.Number)));

        string Customer(Guid? id) => id is { } c && customers.TryGetValue(c, out var customer) ? customer.DisplayName : string.Empty;
        string Vendor(Guid id) => vendors.TryGetValue(id, out var vendor) ? vendor.DisplayName : string.Empty;

        // Documents: the database narrows them (one more than shown, to know there are more), names and field values
        // having been matched above.
        var text = q.ToLowerInvariant();
        var fieldRecords = fieldValues.Where(f => Field(f.Key)).Select(f => f.Key).ToList();
        DocumentSearch For(IEnumerable<Guid> parties) => new(text, amount, parties.ToList(), fieldRecords, MaxPerKind + 1);
        var customerMatches = customers.Values.Where(c => Text(c.DisplayName)).Select(c => c.Id);
        var vendorMatches = vendors.Values.Where(v => Text(v.DisplayName)).Select(v => v.Id);
        var (invoices, receipts, payments) = await sales.SearchAsync(For(customerMatches), ct);
        var (bills, billPayments) = await purchases.SearchAsync(For(vendorMatches), ct);

        AddAll(invoices.Select(i => new SearchHit(SearchKind.Invoice, i.Id, $"Invoice {i.Number}", Customer(i.CustomerId), i.Date, i.Total)));
        AddAll(bills.Select(b => new SearchHit(SearchKind.Bill, b.Id, b.Reference is null ? "Bill" : $"Bill {b.Reference}", Vendor(b.VendorId), b.Date, b.Total)));
        AddAll(receipts.Select(r => new SearchHit(SearchKind.SalesReceipt, r.Id, $"Sales receipt {r.Number}", Customer(r.CustomerId), r.Date, r.Total)));
        AddAll(payments.Select(p => new SearchHit(SearchKind.CustomerPayment, p.Id, $"Payment from {Customer(p.CustomerId)}", p.Reference, p.Date, p.Amount)));
        AddAll(billPayments.Select(p => new SearchHit(SearchKind.BillPayment, p.Id, $"Bill payment to {Vendor(p.VendorId)}", p.Reference, p.Date, p.Amount)));
        var more = await sales.SearchMoreAsync(For(customerMatches), ct);
        AddAll(more.Estimates.Select(e => new SearchHit(SearchKind.Estimate, e.Id, $"Estimate {e.Number}", Customer(e.CustomerId), e.Date, e.Total)));
        AddAll(more.Orders.Select(o => new SearchHit(SearchKind.SalesOrder, o.Id, $"Sales order {o.Number}", Customer(o.CustomerId), o.Date, o.Total)));
        AddAll(more.CreditMemos.Select(c => new SearchHit(SearchKind.CreditMemo, c.Id, $"Credit memo {c.Number}", Customer(c.CustomerId), c.Date, c.Total)));
        var (orders, vendorCredits) = await purchases.SearchMoreAsync(For(vendorMatches), ct);
        AddAll(orders.Select(o => new SearchHit(SearchKind.PurchaseOrder, o.Id, $"Purchase order {o.Number}", Vendor(o.VendorId), o.Date, o.Total)));
        AddAll(vendorCredits.Select(c => new SearchHit(SearchKind.VendorCredit, c.Id, c.Reference is null ? "Vendor credit" : $"Vendor credit {c.Reference}", Vendor(c.VendorId), c.Date, c.Total)));
        AddAll(more.Deposits.Select(d => new SearchHit(SearchKind.Deposit, d.Id, "Deposit", d.Memo ?? (chart.Find(d.BankAccountId) is null ? null : chart.FullName(d.BankAccountId)), d.Date, d.Total)));
        AddAll((await banking.SearchAsync(For([]), ct))
            .Select(t => new SearchHit(SearchKind.BankTransaction, t.Id, t.Payee ?? t.Memo ?? t.Kind.ToString(), chart.Find(t.AccountId) is null ? null : chart.FullName(t.AccountId), t.Date, t.Amount)));

        var (gifts, pledges, grants) = await nonprofit.SearchAsync(For(customerMatches), ct);
        AddAll(gifts.Select(g => new SearchHit(SearchKind.Gift, g.Id, $"Gift {g.Number} from {Customer(g.DonorId)}", g.Reference ?? g.Memo, g.Date, g.Amount)));
        AddAll(pledges.Select(p => new SearchHit(SearchKind.Pledge, p.Id, $"Pledge {p.Number} from {Customer(p.DonorId)}", p.Memo, p.Date, p.Amount)));
        AddAll(grants.Select(g => new SearchHit(SearchKind.Grant, g.Id, g.Name, Customer(g.FunderId), g.AwardedOn, g.Amount)));

        // Channel orders by their order id; the hit opens the day's batch the order came in.
        if (q.Length >= 3)
        {
            var channelNames = (await channels.ListChannelsAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
            AddAll((await channelOrders.SearchOrdersAsync(text, MaxPerKind + 1, ct))
                .SelectMany(b => b.Lines.Where(l => l.OrderId.Contains(q, StringComparison.OrdinalIgnoreCase)).GroupBy(l => l.OrderId)
                    .Select(o => new SearchHit(SearchKind.ChannelOrder, b.Id, $"Order {o.Key}", channelNames.GetValueOrDefault(b.ChannelId),
                        o.Min(l => l.Date), o.Sum(l => l.Sales + l.Shipping + l.Tax)))));
        }

        // Journal entries (by hand, moved in, opening balances, recurring); documents' entries are found through their documents above.
        AddAll((await ledger.SearchManualEntriesAsync(For([]), entryNumber, ct))
            .Select(e => new SearchHit(SearchKind.JournalEntry, e.Id, $"Journal entry {e.Number}", e.Memo, e.Date, e.Lines.Where(l => l.Amount > 0m).Sum(l => l.Amount))));

        return new SearchResults(q, hits, truncated);
    }
}
