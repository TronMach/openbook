using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Reporting;

/// <summary>Sales, expense, open item and 1099 detail reports.</summary>
public sealed class ActivityReports(
    ReportLedger ledger,
    AccountService accounts,
    ICompanyStore companies,
    ISalesStore sales,
    IPurchasesStore purchases,
    ReceivablesService receivables,
    PayablesService payables,
    Channels.ChannelOrderService channelOrders,
    IInventoryStore inventory)
{
    /// <summary>
    /// Income from the ledger (so it ties to the profit and loss, on either basis) credited to each customer
    /// through their invoices, receipts, credit memos, payments and refunds. Income posted any other way, such as
    /// journal entries, is one row of its own.
    /// </summary>
    public async Task<ReportTable> SalesByCustomerAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = (await ledger.LinesAsync(reportBasis, from, to, filter, ct))
            .Where(l => chart.Find(l.AccountId)?.Type.ClassOf() == AccountClass.Income)
            .ToList();

        var owners = new Dictionary<(string, Guid), Guid?>();
        foreach (var i in await sales.ListInvoicesAsync(null, ct))
            owners[(Invoice.Source, i.Id)] = i.CustomerId;
        foreach (var r in await sales.ListReceiptsAsync(ct))
            owners[(SalesReceipt.Source, r.Id)] = r.CustomerId;
        foreach (var c in await sales.ListCreditMemosAsync(null, ct))
            owners[(CreditMemo.Source, c.Id)] = c.CustomerId;
        foreach (var p in await sales.ListPaymentsAsync(null, ct))
            owners[(CustomerPayment.Source, p.Id)] = p.CustomerId;
        foreach (var r in await sales.ListRefundsAsync(null, ct))
            owners[(CustomerRefund.Source, r.Id)] = r.CustomerId;
        foreach (var r in await sales.ListRetainersAsync(null, ct))
            owners[(Retainer.ApplicationSource, r.Id)] = r.CustomerId;

        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var byCustomer = new Dictionary<Guid, decimal>();
        decimal noCustomer = 0m, other = 0m;
        foreach (var line in lines)
        {
            if (line.SourceId is not { } sourceId || !owners.TryGetValue((line.Source, sourceId), out var customerId))
                other -= line.Amount;
            else if (customerId is { } id)
                byCustomer[id] = byCustomer.GetValueOrDefault(id) - line.Amount;
            else
                noCustomer -= line.Amount;
        }

        var total = byCustomer.Values.Sum() + noCustomer + other;
        var rows = byCustomer
            .Where(kv => kv.Value != 0m)
            .OrderBy(kv => customers[kv.Key].DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ReportTableRow(
                [new ReportCell(customers[kv.Key].DisplayName, new ReportLink("customer", kv.Key)), kv.Value, new ReportCell(Share(kv.Value, total))]))
            .ToList();
        if (noCustomer != 0m)
            rows.Add(new ReportTableRow(["Sales receipts with no customer", noCustomer, new ReportCell(Share(noCustomer, total))]));
        if (other != 0m)
            rows.Add(new ReportTableRow(["Income not from sales forms", other, new ReportCell(Share(other, total))]));
        rows.Add(new ReportTableRow(["Total", total, new ReportCell(total == 0m ? null : 100m)], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Sales by customer",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}", .. await FilteredAsync(filter, ct)],
            [new("Customer"), new("Income", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            reportBasis,
            "Income and other income accounts, after discounts and before sales tax; the total matches those sections of the profit and loss.");
    }

    /// <summary>Item lines of invoices and sales receipts, less credit memos, dated in the period (accrual).</summary>
    private async Task<(Dictionary<Guid, (decimal Quantity, decimal Amount)> ByItem, decimal Discounts)> SoldByItemAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        bool Counts(DocumentStatus status, DateOnly date) => status == DocumentStatus.Posted && date >= from && date <= to;
        var documents = new List<(IReadOnlyList<SalesLine> Lines, decimal Sign)>();
        documents.AddRange((await sales.ListInvoicesAsync(null, ct)).Where(i => Counts(i.Status, i.Date)).Select(i => (i.Lines, 1m)));
        documents.AddRange((await sales.ListReceiptsAsync(ct)).Where(r => Counts(r.Status, r.Date)).Select(r => (r.Lines, 1m)));
        documents.AddRange((await sales.ListCreditMemosAsync(null, ct)).Where(c => Counts(c.Status, c.Date)).Select(c => (c.Lines, -1m)));

        // Gift cards and retainage hold money: they aren't sold.
        var holding = await SalesTax.SalesFigures.HoldingAccountsAsync(accounts, ct);
        var byItem = new Dictionary<Guid, (decimal Quantity, decimal Amount)>();
        var discounts = 0m;
        foreach (var (lines, sign) in documents)
        {
            foreach (var line in lines.Where(l => SalesTax.SalesFigures.Counts(l, holding)))
            {
                if (line.Kind is SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount)
                    discounts += sign * line.Amount;
                else if (line.Kind == SalesLineKind.Item && line.ItemId is { } itemId)
                {
                    var (q, a) = byItem.GetValueOrDefault(itemId);
                    byItem[itemId] = (q + sign * line.Quantity, a + sign * line.Amount);
                }
            }
        }

        return (byItem, discounts);
    }

    /// <summary>
    /// Sales, cost and margin of each product and service. Sales are as on sales by product; cost is what stock moved
    /// out for them, on the same dates. A bundle's cost lands on its components, and services show no cost.
    /// </summary>
    public async Task<ReportTable> ProfitByProductAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var (byItem, _) = await SoldByItemAsync(from, to, ct);
        var channel = await channelOrders.SoldAsync(from, to, ct);
        foreach (var line in channel.Where(c => c.ItemId is not null))
        {
            var (q, a) = byItem.GetValueOrDefault(line.ItemId!.Value);
            byItem[line.ItemId.Value] = (q + line.Quantity, a + line.Amount);
        }

        string[] sold_ = [InventorySources.Invoice, InventorySources.SalesReceipt, InventorySources.CreditMemo, InventorySources.ChannelOrders, InventorySources.ChannelReturn];
        var costs = (await inventory.ListMovementsAsync(null, ct))
            .Where(m => m.Date >= from && m.Date <= to && sold_.Contains(m.SourceType))
            .GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => -g.Sum(m => m.Cost));
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var rows = byItem.Keys.Union(costs.Keys)
            .Select(id => (Item: items[id], Sold: byItem.GetValueOrDefault(id), Cost: costs.GetValueOrDefault(id)))
            .Where(x => x.Sold.Amount != 0m || x.Sold.Quantity != 0m || x.Cost != 0m)
            .OrderBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ReportTableRow(
            [
                new ReportCell(x.Item.Name, new ReportLink("item", x.Item.Id)),
                new ReportCell(x.Sold.Quantity),
                x.Sold.Amount,
                x.Cost,
                x.Sold.Amount - x.Cost,
                new ReportCell(Share(x.Sold.Amount - x.Cost, x.Sold.Amount)),
            ]))
            .ToList();
        var (sold, cost) = (byItem.Values.Sum(v => v.Amount), costs.Values.Sum());
        rows.Add(new ReportTableRow(["Total", ReportCell.Empty, sold, cost, sold - cost, new ReportCell(Share(sold - cost, sold))], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Profit by product",
            [company.Name, $"{Comparisons.Describe(from, to)} · Accrual basis"],
            [new("Product or service"), new("Quantity", ColumnFormat.Number), new("Sales", ColumnFormat.Money), new("Cost", ColumnFormat.Money), new("Profit", ColumnFormat.Money), new("Margin", ColumnFormat.Percent)],
            rows,
            ReportBasis.Accrual,
            "Sales from invoices, sales receipts and channel orders, less credit memos and returns, before discounts given on the whole document. Cost is what stock left for them. A bundle's cost shows on its components; a service has none.");
    }

    /// <summary>Sales by item category (the items' own figures, as on sales by product or service, added up per category).</summary>
    public Task<ReportTable> SalesByCategoryAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        SalesByGroupAsync(from, to, "Sales by category", "Category", "No category", (item, _) => item?.Category,
            "Each product or service's sales, as on sales by product or service, added up by the category on its page.", ct);

    /// <summary>Sales by style (C329): each item's variants added to it; items without variants on their own.</summary>
    public Task<ReportTable> SalesByStyleAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        SalesByGroupAsync(from, to, "Sales by style", "Style", "Not stocked", (item, all) =>
            item is null ? null : item.VariantOfId is { } style && all.TryGetValue(style, out var parent) ? parent.Name : item.Name,
            "Each product or service's sales, as on sales by product or service, with variants (sizes, colours) added to the item they were made from.", ct);

    private async Task<ReportTable> SalesByGroupAsync(
        DateOnly from, DateOnly to, string title, string heading, string none, Func<Item?, IReadOnlyDictionary<Guid, Item>, string?> group, string note,
        CancellationToken ct)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var (byItem, discounts) = await SoldByItemAsync(from, to, ct);
        var channel = await channelOrders.SoldAsync(from, to, ct);
        foreach (var line in channel.Where(c => c.ItemId is not null))
        {
            var (q, a) = byItem.GetValueOrDefault(line.ItemId!.Value);
            byItem[line.ItemId.Value] = (q + line.Quantity, a + line.Amount);
        }

        var unstocked = channel.Where(c => c.ItemId is null).Sum(c => c.Amount);
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var total = byItem.Values.Sum(v => v.Amount) + discounts + unstocked;
        var groups = byItem.Where(kv => kv.Value.Amount != 0m || kv.Value.Quantity != 0m)
            .GroupBy(kv => group(items.GetValueOrDefault(kv.Key), items) ?? none, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == none ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var rows = groups.Select(g => new ReportTableRow(
        [
            g.Key, new ReportCell(g.Count()), g.Sum(kv => kv.Value.Amount), new ReportCell(Share(g.Sum(kv => kv.Value.Amount), total)),
        ])).ToList();
        if (unstocked != 0m)
            rows.Add(new ReportTableRow(["Channel sales not stocked", ReportCell.Empty, unstocked, new ReportCell(Share(unstocked, total))]));
        if (discounts != 0m)
            rows.Add(new ReportTableRow(["Discounts", ReportCell.Empty, discounts, new ReportCell(Share(discounts, total))]));
        rows.Add(new ReportTableRow(["Total", ReportCell.Empty, total, new ReportCell(total == 0m ? null : 100m)], 0, ReportRowStyle.Total));
        return new ReportTable(
            title,
            [company.Name, $"{Comparisons.Describe(from, to)} · Accrual basis"],
            [new(heading), new("Items sold", ColumnFormat.Number), new("Amount", ColumnFormat.Money), new("% of sales", ColumnFormat.Percent)],
            rows,
            Note: note);
    }

    /// <summary>
    /// The card a line is about: the first word of its description with a digit in it ("Gift card 0001 redeemed" is card
    /// 0001, so is "#0001"), else the whole description; null without one.
    /// </summary>
    public static string? CardNumber(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;
        var word = description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.Any(char.IsDigit));
        return word?.Trim('#', ',', '.', ':', ';', '(', ')').TrimStart('#') is { Length: > 0 } number ? number : description.Trim();
    }

    /// <summary>
    /// Gift cards and other prepaid items (products or services that post to a liability, C318): each card's sales and
    /// redemptions up to a date and what's left on it. A card is told apart by the number in its line's description
    /// (<see cref="CardNumber"/>), typed when it's sold and redeemed; lines without one add up under "No card number".
    /// </summary>
    public async Task<ReportTable> GiftCardBalancesAsync(DateOnly asOf, bool all, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var prepaid = (await sales.ListItemsAsync(ct)).Where(i => chart.Find(i.IncomeAccountId)?.Type == AccountType.OtherCurrentLiability).ToDictionary(i => i.Id);
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
        bool Counts(DocumentStatus status, DateOnly date) => status == DocumentStatus.Posted && date <= asOf;
        var documents = new List<(IReadOnlyList<SalesLine> Lines, Guid? CustomerId, decimal Sign)>();
        documents.AddRange((await sales.ListInvoicesAsync(null, ct)).Where(i => Counts(i.Status, i.Date)).Select(i => (i.Lines, (Guid?)i.CustomerId, 1m)));
        documents.AddRange((await sales.ListReceiptsAsync(ct)).Where(r => Counts(r.Status, r.Date)).Select(r => (r.Lines, r.CustomerId, 1m)));
        documents.AddRange((await sales.ListCreditMemosAsync(null, ct)).Where(c => Counts(c.Status, c.Date)).Select(c => (c.Lines, (Guid?)c.CustomerId, -1m)));

        const string NoNumber = "No card number";
        var cards = new Dictionary<(Guid Item, string Card), (decimal Sold, decimal Redeemed, Guid? SoldTo)>();
        foreach (var (lines, customerId, sign) in documents)
        {
            foreach (var line in lines.Where(l => l.Kind == SalesLineKind.Item && l.ItemId is { } id && prepaid.ContainsKey(id)))
            {
                var key = (line.ItemId!.Value, CardNumber(line.Description) ?? NoNumber);
                var (sold, redeemed, soldTo) = cards.GetValueOrDefault(key);
                var amount = sign * line.Amount;
                cards[key] = amount >= 0m ? (sold + amount, redeemed, soldTo ?? customerId) : (sold, redeemed - amount, soldTo);
            }
        }

        var rows = cards.Where(c => all || c.Value.Sold != c.Value.Redeemed)
            .OrderBy(c => prepaid[c.Key.Item].Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Key.Card == NoNumber ? 1 : 0)
            .ThenBy(c => c.Key.Card, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ReportTableRow(
            [
                c.Key.Card, prepaid[c.Key.Item].Name, c.Value.SoldTo is { } who ? customers.GetValueOrDefault(who, "") : "",
                c.Value.Sold, c.Value.Redeemed, c.Value.Sold - c.Value.Redeemed,
            ])).ToList();
        var shown = cards.Where(c => all || c.Value.Sold != c.Value.Redeemed).Select(c => c.Value).ToList();
        rows.Add(new ReportTableRow(["Total", "", "", shown.Sum(v => v.Sold), shown.Sum(v => v.Redeemed), shown.Sum(v => v.Sold - v.Redeemed)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Gift card balances",
            [company.Name, $"As of {asOf:MMM d, yyyy}" + (all ? " · every card" : " · cards with money left")],
            [new("Card"), new("Product or service"), new("Sold to"), new("Sold", ColumnFormat.Money), new("Redeemed", ColumnFormat.Money), new("Balance", ColumnFormat.Money)],
            rows,
            Note: "Products and services that post to a liability account (gift cards, prepaid packages). Each card is told apart by the number in its lines' description (the first word with a digit, as in 'Gift card 0001'): type it when the card is sold and when it's redeemed (a negative line). The balance of all of them is the liability account's balance.");
    }

    public async Task<ReportTable> SalesByItemAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var (byItem, discounts) = await SoldByItemAsync(from, to, ct);
        var channel = await channelOrders.SoldAsync(from, to, ct);
        foreach (var line in channel.Where(c => c.ItemId is not null))
        {
            var (q, a) = byItem.GetValueOrDefault(line.ItemId!.Value);
            byItem[line.ItemId.Value] = (q + line.Quantity, a + line.Amount);
        }

        var unstocked = channel.Where(c => c.ItemId is null).ToList();
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);        var total = byItem.Values.Sum(v => v.Amount) + discounts + unstocked.Sum(c => c.Amount);
        var rows = byItem
            .Where(kv => kv.Value.Amount != 0m || kv.Value.Quantity != 0m)
            .OrderBy(kv => items[kv.Key].Name, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ReportTableRow(
            [
                new ReportCell(items[kv.Key].Name, new ReportLink("item", kv.Key)),
                new ReportCell(kv.Value.Quantity),
                kv.Value.Amount,
                new ReportCell(Share(kv.Value.Amount, total)),
                new ReportCell(kv.Value.Quantity == 0m ? null : decimal.Round(kv.Value.Amount / kv.Value.Quantity, 2, MidpointRounding.AwayFromZero)),
            ]))
            .ToList();
        rows.AddRange(unstocked.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new ReportTableRow(
            [c.Name, new ReportCell(c.Quantity), c.Amount, new ReportCell(Share(c.Amount, total)), ReportCell.Empty])));
        if (discounts != 0m)
            rows.Add(new ReportTableRow(["Discounts", ReportCell.Empty, discounts, new ReportCell(Share(discounts, total)), ReportCell.Empty]));
        rows.Add(new ReportTableRow(["Total", ReportCell.Empty, total, new ReportCell(total == 0m ? null : 100m), ReportCell.Empty], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Sales by product or service",
            [company.Name, $"{Comparisons.Describe(from, to)} · Accrual basis"],
            [new("Product or service"), new("Quantity", ColumnFormat.Number), new("Amount", ColumnFormat.Money), new("% of sales", ColumnFormat.Percent), new("Average price", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "From invoices, sales receipts and posted channel orders, less credit memos and returns, dated in the period. Amounts are before sales tax.");
    }

    /// <summary>
    /// Expenses (cost of goods sold, expense and other expense accounts) from the ledger, by the vendor on the bill,
    /// vendor credit, bill payment (cash basis), vendor refund or expense that posted them.
    /// </summary>
    /// <summary>The filter as a subtitle: a printed report for one class mustn't read like the whole company's.</summary>
    private async Task<IReadOnlyList<string>> FilteredAsync(SegmentFilter? filter, CancellationToken ct) =>
        await ledger.DescribeAsync(filter, ct) is { } segments ? [segments] : [];

    public async Task<ReportTable> ExpensesByVendorAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = (await ledger.LinesAsync(reportBasis, from, to, filter, ct))
            .Where(l => chart.Find(l.AccountId)?.Type.ClassOf() == AccountClass.Expense)
            .ToList();

        var owners = new Dictionary<(string, Guid), Guid?>();
        foreach (var b in await purchases.ListBillsAsync(null, ct))
            owners[(Bill.Source, b.Id)] = b.VendorId;
        foreach (var c in await purchases.ListVendorCreditsAsync(null, ct))
            owners[(VendorCredit.Source, c.Id)] = c.VendorId;
        foreach (var p in await purchases.ListPaymentsAsync(null, ct))
            owners[(BillPayment.Source, p.Id)] = p.VendorId;
        foreach (var r in await purchases.ListVendorRefundsAsync(null, ct))
            owners[(VendorRefund.Source, r.Id)] = r.VendorId;
        foreach (var e in await purchases.ListExpensesAsync(null, ct))
            owners[(BankTransaction.SpendSource, e.Id)] = e.VendorId;

        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id);
        var byVendor = new Dictionary<Guid, decimal>();
        var noVendor = 0m;
        foreach (var line in lines)
        {
            if (line.SourceId is { } sourceId && owners.TryGetValue((line.Source, sourceId), out var vendorId) && vendorId is { } id)
                byVendor[id] = byVendor.GetValueOrDefault(id) + line.Amount;
            else
                noVendor += line.Amount;
        }

        var total = byVendor.Values.Sum() + noVendor;
        var rows = byVendor
            .Where(kv => kv.Value != 0m)
            .OrderBy(kv => vendors[kv.Key].DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ReportTableRow(
                [new ReportCell(vendors[kv.Key].DisplayName, new ReportLink("vendor", kv.Key)), kv.Value, new ReportCell(Share(kv.Value, total))]))
            .ToList();
        if (noVendor != 0m)
            rows.Add(new ReportTableRow(["No vendor", noVendor, new ReportCell(Share(noVendor, total))]));
        rows.Add(new ReportTableRow(["Total", total, new ReportCell(total == 0m ? null : 100m)], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Expenses by vendor",
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}", .. await FilteredAsync(filter, ct)],
            [new("Vendor"), new("Expenses", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            reportBasis,
            "Cost of goods sold, expense and other expense accounts; the total matches those sections of the profit and loss.");
    }

    public async Task<ReportTable> OpenInvoicesAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var aging = await receivables.AgingAsync(asOf, ct);
        var rows = new List<ReportTableRow>();
        foreach (var customer in aging.Customers.Where(c => c.Invoices.Count > 0))
        {
            rows.Add(new ReportTableRow([new ReportCell(customer.Customer.DisplayName, new ReportLink("customer", customer.Customer.Id)), .. Blank(5)], 0, ReportRowStyle.Section));
            rows.AddRange(customer.Invoices.Select(o => new ReportTableRow(
            [
                new ReportCell($"Invoice {o.Invoice.Number}", new ReportLink("invoice", o.Invoice.Id)),
                new ReportCell(o.Invoice.Date),
                new ReportCell(o.Invoice.DueDate),
                new ReportCell(o.DaysPastDue > 0 ? o.DaysPastDue : null),
                o.Invoice.Total,
                o.Balance,
            ], 1)));
            rows.Add(new ReportTableRow([$"Total {customer.Customer.DisplayName}", .. Blank(3), customer.Invoices.Sum(o => o.Invoice.Total), customer.Invoices.Sum(o => o.Balance)], 0, ReportRowStyle.Subtotal));
        }

        var all = aging.Customers.SelectMany(c => c.Invoices).ToList();
        rows.Add(new ReportTableRow(["Total", .. Blank(3), all.Sum(o => o.Invoice.Total), all.Sum(o => o.Balance)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Open invoices",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [new("Customer and invoice"), new("Date", ColumnFormat.Date), new("Due", ColumnFormat.Date), new("Days past due", ColumnFormat.Number), new("Amount", ColumnFormat.Money), new("Open balance", ColumnFormat.Money)],
            rows,
            Note: "Unapplied credits aren't subtracted here; the A/R aging report nets them.");
    }

    public async Task<ReportTable> UnpaidBillsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var aging = await payables.AgingAsync(asOf, ct);
        var rows = new List<ReportTableRow>();
        foreach (var vendor in aging.Vendors.Where(v => v.Bills.Count > 0))
        {
            rows.Add(new ReportTableRow([new ReportCell(vendor.Vendor.DisplayName, new ReportLink("vendor", vendor.Vendor.Id)), .. Blank(5)], 0, ReportRowStyle.Section));
            rows.AddRange(vendor.Bills.Select(o => new ReportTableRow(
            [
                new ReportCell(o.Bill.Reference is { } r ? $"Bill {r}" : "Bill", new ReportLink("bill", o.Bill.Id)),
                new ReportCell(o.Bill.Date),
                new ReportCell(o.Bill.DueDate),
                new ReportCell(o.DaysPastDue > 0 ? o.DaysPastDue : null),
                o.Bill.Total,
                o.Balance,
            ], 1)));
            rows.Add(new ReportTableRow([$"Total {vendor.Vendor.DisplayName}", .. Blank(3), vendor.Bills.Sum(o => o.Bill.Total), vendor.Bills.Sum(o => o.Balance)], 0, ReportRowStyle.Subtotal));
        }

        var all = aging.Vendors.SelectMany(v => v.Bills).ToList();
        rows.Add(new ReportTableRow(["Total", .. Blank(3), all.Sum(o => o.Bill.Total), all.Sum(o => o.Balance)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Unpaid bills",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [new("Vendor and bill"), new("Date", ColumnFormat.Date), new("Due", ColumnFormat.Date), new("Days past due", ColumnFormat.Number), new("Amount", ColumnFormat.Money), new("Open balance", ColumnFormat.Money)],
            rows,
            Note: "Unapplied vendor credits aren't subtracted here; the A/P aging report nets them.");
    }

    /// <summary>
    /// Each payment counted on the 1099 summary (<see cref="PayablesService.Payments1099Async"/>, which the summary adds
    /// up), plus card payments and not reportable parts, shown but not counted.
    /// </summary>
    public async Task<ReportTable> Detail1099Async(int year, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
            throw new DomainException("report.year_invalid", "Choose a year between 2000 and 2100.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var all = await payables.Payments1099Async(year, ct);
        var withheldFrom = all.Where(p => p.Withheld != 0m).Select(p => p.VendorId).ToHashSet();
        var vendors = (await purchases.ListVendorsAsync(ct)).Where(v => v.Track1099 || withheldFrom.Contains(v.Id)).ToDictionary(v => v.Id);

        var payments = all.Where(p => vendors.ContainsKey(p.VendorId)).ToList();
        var rows = new List<ReportTableRow>();
        foreach (var group in payments.GroupBy(p => p.VendorId).OrderBy(g => vendors[g.Key].DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var vendor = vendors[group.Key];
            rows.Add(new ReportTableRow([new ReportCell(vendor.DisplayName, new ReportLink("vendor", vendor.Id)), .. Blank(7)], 0, ReportRowStyle.Section));
            rows.AddRange(group.OrderBy(p => p.Date ?? DateOnly.MinValue).Select(p => new ReportTableRow(
            [
                new ReportCell(p.Type, new ReportLink(p.EntityType, p.EntityId)),
                p.Date is { } on ? new ReportCell(on) : ReportCell.Empty,
                p.Reference,
                p.AccountId is { } paidFrom && chart.Find(paidFrom) is { } a ? chart.FullName(a.Id) : null,
                new ReportCell(p.ByCard ? null : p.Counted),
                new ReportCell(p.ByCard ? p.Amount : null),
                new ReportCell(p.NotReportable == 0m ? null : p.NotReportable),
                new ReportCell(p.Withheld == 0m ? null : p.Withheld),
            ], 1)));
            rows.Add(new ReportTableRow(
                [$"Total {vendor.DisplayName}", .. Blank(3), group.Sum(p => p.Counted), group.Where(p => p.ByCard).Sum(p => p.Amount), group.Sum(p => p.NotReportable), group.Sum(p => p.Withheld)],
                0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(
            ["Total", .. Blank(3), payments.Sum(p => p.Counted), payments.Where(p => p.ByCard).Sum(p => p.Amount), payments.Sum(p => p.NotReportable), payments.Sum(p => p.Withheld)],
            0, ReportRowStyle.Total));
        return new ReportTable(
            $"1099 detail for {year}",
            [company.Name, "Payments to vendors tracked for 1099s"],
            [new("Vendor and payment"), new("Date", ColumnFormat.Date), new("Reference"), new("Paid from"), new("Counted", ColumnFormat.Money), new("Card (1099-K)", ColumnFormat.Money),
                new("Not reportable", ColumnFormat.Money), new("Withheld (box 4)", ColumnFormat.Money)],
            rows,
            Note: "Card payments are left out of the 1099 totals because the card company reports them on Form 1099-K. Deposits count on the day they were paid, so the bill payments made from them don't.");
    }

    private static decimal? Share(decimal amount, decimal total) => total == 0m ? null : decimal.Round(amount / total * 100m, 1, MidpointRounding.AwayFromZero);

    private static ReportCell[] Blank(int count) => Enumerable.Repeat(ReportCell.Empty, count).ToArray();
}
