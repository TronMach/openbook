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
}

/// <summary>The area (as in permission sets and API key scopes) each kind of result belongs to.</summary>
public static class SearchAreas
{
    public static string Of(SearchKind kind) => kind switch
    {
        SearchKind.Customer or SearchKind.Item or SearchKind.Invoice or SearchKind.SalesReceipt or SearchKind.CustomerPayment => "sales",
        SearchKind.Vendor or SearchKind.Bill or SearchKind.BillPayment => "purchases",
        SearchKind.BankTransaction => "banking",
        _ => "ledger",
    };
}

public sealed record SearchHit(SearchKind Kind, Guid Id, string Title, string? Detail = null, DateOnly? Date = null, decimal? Amount = null);

public sealed record SearchResults(string Query, IReadOnlyList<SearchHit> Hits, bool Truncated);

/// <summary>
/// One box that finds anything: names, numbers, references, memos, and amounts ("1,250", "$89.99").
/// Searches in memory, which suits a small business's data; a full-text index can come later.
/// </summary>
public sealed class SearchService(
    AccountService accounts,
    ISalesStore sales,
    IPurchasesStore purchases,
    IBankingStore banking,
    ILedgerStore ledger,
    CustomFields.CustomFieldService customFields)
{
    public const int MaxPerKind = 20;

    public async Task<SearchResults> SearchAsync(string? query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        if (q.Length < 2)
            return new SearchResults(q, [], Truncated: false);

        decimal? amount = StatementValues.TryParseAmount(q, out var parsed) && parsed != 0m ? Math.Abs(parsed) : null;
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

        AddAll((await sales.ListInvoicesAsync(null, ct))
            .Where(i => Text(i.Number, i.Memo, Customer(i.CustomerId)) || Money(i.Total) || Money(i.Balance) || Field(i.Id))
            .Select(i => new SearchHit(SearchKind.Invoice, i.Id, $"Invoice {i.Number}", Customer(i.CustomerId), i.Date, i.Total)));
        AddAll((await purchases.ListBillsAsync(null, ct))
            .Where(b => Text(b.Reference, b.Memo, Vendor(b.VendorId)) || Money(b.Total) || Money(b.Balance) || Field(b.Id))
            .Select(b => new SearchHit(SearchKind.Bill, b.Id, b.Reference is null ? "Bill" : $"Bill {b.Reference}", Vendor(b.VendorId), b.Date, b.Total)));
        AddAll((await sales.ListReceiptsAsync(ct))
            .Where(r => Text(r.Number, r.Reference, r.Memo, Customer(r.CustomerId)) || Money(r.Total) || Field(r.Id))
            .Select(r => new SearchHit(SearchKind.SalesReceipt, r.Id, $"Sales receipt {r.Number}", Customer(r.CustomerId), r.Date, r.Total)));
        AddAll((await sales.ListPaymentsAsync(null, ct))
            .Where(p => Text(p.Reference, p.Memo, Customer(p.CustomerId)) || Money(p.Amount))
            .Select(p => new SearchHit(SearchKind.CustomerPayment, p.Id, $"Payment from {Customer(p.CustomerId)}", p.Reference, p.Date, p.Amount)));
        AddAll((await purchases.ListPaymentsAsync(null, ct))
            .Where(p => Text(p.Reference, p.Memo, Vendor(p.VendorId)) || Money(p.Amount))
            .Select(p => new SearchHit(SearchKind.BillPayment, p.Id, $"Bill payment to {Vendor(p.VendorId)}", p.Reference, p.Date, p.Amount)));
        AddAll((await banking.ListAllTransactionsAsync(ct))
            .Where(t => Text(t.Payee, t.Reference, t.Memo) || Money(t.Amount))
            .OrderByDescending(t => t.Date)
            .Select(t => new SearchHit(SearchKind.BankTransaction, t.Id, t.Payee ?? t.Memo ?? t.Kind.ToString(), chart.Find(t.AccountId) is null ? null : chart.FullName(t.AccountId), t.Date, t.Amount)));

        // Manual journal entries; documents' entries are found through their documents above.
        AddAll((await ledger.GetLinesAsync(null, DateOnly.MaxValue, ct))
            .Where(l => l.Source == JournalEntry.ManualSource)
            .GroupBy(l => l.JournalEntryId)
            .Where(g => entryNumber == g.First().EntryNumber || Text(g.First().EntryMemo) || g.Any(l => Text(l.LineMemo) || Money(l.Amount)))
            .OrderByDescending(g => g.First().Date)
            .Select(g => new SearchHit(SearchKind.JournalEntry, g.Key, $"Journal entry {g.First().EntryNumber}", g.First().EntryMemo,
                g.First().Date, g.Where(l => l.Amount > 0m).Sum(l => l.Amount))));

        return new SearchResults(q, hits, truncated);
    }
}
