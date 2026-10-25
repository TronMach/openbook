using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Pricing;

/// <summary>The price list (what a customer or level pays for each item) and what levels, breaks and rules gave away.</summary>
public sealed class PricingReports(PricingService pricing, ISalesStore sales, ICompanyStore companies)
{
    /// <summary>
    /// Each active item's list price and the price for one unit on <paramref name="asOf"/>: for the customer (their level
    /// and the rules that apply to them), else for the level, else list prices with the rules for every customer.
    /// </summary>
    public async Task<ReportTable> PriceListAsync(Guid? customerId, Guid? levelId, DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        Customer? customer = null;
        PricingContext context;
        string scope;
        if (customerId is { } cid)
        {
            customer = await sales.GetCustomerAsync(cid, ct) ?? throw new EntityNotFoundException("customer", cid);
            context = await pricing.ContextAsync(customer, ct);
            scope = context.Level is { } l ? $"{customer.DisplayName} · price level {l.Name}" : $"{customer.DisplayName} · list prices";
        }
        else if (levelId is { } lid)
        {
            var level = await pricing.GetLevelAsync(lid, ct) ?? throw new EntityNotFoundException(PricingService.LevelEntity, lid);
            var all = await pricing.ContextAsync((Customer?)null, ct);
            context = all with { Level = level };
            scope = $"Price level {level.Name}{(level.IsActive ? "" : " (inactive)")}";
        }
        else
        {
            context = await pricing.ContextAsync((Customer?)null, ct);
            scope = "List prices, with rules for all customers";
        }

        var rows = (await sales.ListItemsAsync(ct))
            .Where(i => i.IsActive)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item =>
            {
                var quote = context.Quote(item, asOf, 1m);
                return new ReportTableRow(
                [
                    new ReportCell(item.Name, new ReportLink("item", item.Id)),
                    item.Rate,
                    quote.Price,
                    new ReportCell(item.Rate == 0m || quote.Price == item.Rate ? null : decimal.Round((quote.Price - item.Rate) / item.Rate * 100m, 1)),
                    item.QuantityBreaks.Count == 0 ? null : string.Join(", ", item.QuantityBreaks.Select(b => b.Describe())),
                    quote.Source,
                ]);
            })
            .ToList();

        return new ReportTable(
            "Price list",
            [company.Name, $"{scope} · {asOf:MMM d, yyyy}"],
            [new("Product or service"), new("List price", ColumnFormat.Money), new("Price", ColumnFormat.Money), new("Change", ColumnFormat.Percent),
             new("Quantity breaks"), new("Priced by")],
            rows,
            Note: "Prices for one unit. Quantity breaks and quantity-based rules lower them on larger orders; manual prices on documents always win.");
    }

    /// <summary>
    /// Posted invoices and sales receipts dated in the period: list amount (quantity × list price), what was charged,
    /// and the difference, by what set the price. Summary by pricing source, or each line.
    /// </summary>
    public async Task<ReportTable> DiscountsGivenAsync(DateOnly from, DateOnly to, bool detail, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var invoices = (await sales.ListInvoicesAsync(null, ct)).Where(i => i.Status == DocumentStatus.Posted && i.Date >= from && i.Date <= to)
            .SelectMany(i => i.Lines.Select(l => (Kind: "invoice", i.Id, Label: $"Invoice {i.Number}", i.Date, CustomerId: (Guid?)i.CustomerId, Line: l)));
        var receipts = (await sales.ListReceiptsAsync(ct)).Where(r => r.Status == DocumentStatus.Posted && r.Date >= from && r.Date <= to)
            .SelectMany(r => r.Lines.Select(l => (Kind: "sales_receipt", r.Id, Label: $"Sales receipt {r.Number}", r.Date, r.CustomerId, Line: l)));
        var lines = invoices.Concat(receipts)
            .Where(x => x.Line.Kind == SalesLineKind.Item && x.Line.PriceSource is not null && x.Line.ListRate is not null)
            .OrderBy(x => x.Date).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();

        var rows = new List<ReportTableRow>();
        foreach (var group in lines.GroupBy(x => x.Line.PriceSource!).OrderBy(g => g.Key == SalesLine.ManualPrice).ThenByDescending(g => g.Sum(x => x.Line.Saved)))
        {
            var list = group.Sum(x => x.Line.Amount + x.Line.Saved);
            var charged = group.Sum(x => x.Line.Amount);
            if (detail)
            {
                rows.Add(new ReportTableRow([group.Key, .. Enumerable.Repeat(ReportCell.Empty, 5)], 0, ReportRowStyle.Section));
                rows.AddRange(group.Select(x => new ReportTableRow(
                [
                    new ReportCell(x.Label, new ReportLink(x.Kind, x.Id)),
                    new ReportCell(x.Date),
                    x.CustomerId is { } c && customers.TryGetValue(c, out var customer) ? customer.DisplayName : "Cash sale",
                    new ReportCell(x.Line.Quantity),
                    x.Line.Amount + x.Line.Saved,
                    x.Line.Saved,
                ], 1)));
                rows.Add(new ReportTableRow([$"Total {group.Key}", null, null, group.Sum(x => x.Line.Quantity), list, list - charged], 0, ReportRowStyle.Subtotal));
            }
            else
            {
                rows.Add(new ReportTableRow([group.Key, new ReportCell(group.Count()), group.Sum(x => x.Line.Quantity), list, charged, list - charged]));
            }
        }

        var totalList = lines.Sum(x => x.Line.Amount + x.Line.Saved);
        var totalSaved = lines.Sum(x => x.Line.Saved);
        rows.Add(detail
            ? new ReportTableRow(["Total", null, null, lines.Sum(x => x.Line.Quantity), totalList, totalSaved], 0, ReportRowStyle.Total)
            : new ReportTableRow(["Total", new ReportCell(lines.Count), lines.Sum(x => x.Line.Quantity), totalList, totalList - totalSaved, totalSaved], 0, ReportRowStyle.Total));

        ReportColumn[] columns = detail
            ? [new("Pricing and document"), new("Date", ColumnFormat.Date), new("Customer"), new("Quantity", ColumnFormat.Number), new("At list price", ColumnFormat.Money), new("Discount", ColumnFormat.Money)]
            : [new("Priced by"), new("Lines", ColumnFormat.Number), new("Quantity", ColumnFormat.Number), new("At list price", ColumnFormat.Money), new("Charged", ColumnFormat.Money), new("Discount", ColumnFormat.Money)];
        return new ReportTable(
            "Discounts given by price level and rule",
            [company.Name, $"{Comparisons.Describe(from, to)} · posted invoices and sales receipts"],
            columns,
            rows,
            Note: "Discount = quantity × list price at the time of sale, less the line amount. Manual prices count too (negative when above list). Discount lines are separate and not included.");
    }
}
