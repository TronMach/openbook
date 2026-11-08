using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Reporting;

/// <summary>
/// The everyday lists and balances people expect to print or export: customer and vendor balances and contact lists,
/// the item and account lists, open sales and purchase orders, purchases by item, deposit detail and estimates.
/// </summary>
public sealed class ListReports(
    ICompanyStore companies,
    ISalesStore sales,
    IPurchasesStore purchases,
    AccountService accounts,
    CustomerService customers,
    VendorService vendors,
    ReceivablesService receivables,
    PayablesService payables,
    InventoryService inventory,
    ReportLedger ledger,
    DepositService deposits,
    PurchaseOrderService purchaseOrders,
    SalesOrderService salesOrders)
{
    /// <summary>Retainers held for each customer on a date: received, applied to invoices and refunded by then, and what's left.</summary>
    public async Task<ReportTable> RetainerBalancesAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var names = Domain.Sales.CustomerTree.FullNames(await sales.ListCustomersAsync(ct));
        var retainers = (await sales.ListRetainersAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted && r.Date <= asOf).ToList();
        var rows = new List<ReportTableRow>();
        decimal received = 0m, applied = 0m, refunded = 0m;
        foreach (var group in retainers.GroupBy(r => r.CustomerId).OrderBy(g => names.GetValueOrDefault(g.Key, ""), StringComparer.OrdinalIgnoreCase))
        {
            var r = group.Sum(x => x.Amount);
            var a = group.SelectMany(x => x.Applications).Where(x => !x.IsVoided && x.AppliedOn <= asOf).Sum(x => x.Amount);
            var f = group.SelectMany(x => x.Refunds).Where(x => !x.IsVoided && x.Date <= asOf).Sum(x => x.Amount);
            if (r - a - f == 0m)
                continue;
            (received, applied, refunded) = (received + r, applied + a, refunded + f);
            rows.Add(new ReportTableRow([new ReportCell(names.GetValueOrDefault(group.Key, "?"), new ReportLink("customer", group.Key)), r, a, f, r - a - f]));
        }

        rows.Add(new ReportTableRow(["Total", received, applied, refunded, received - applied - refunded], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Retainer balances",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [new("Customer"), new("Received", ColumnFormat.Money), new("Applied", ColumnFormat.Money), new("Refunded", ColumnFormat.Money), new("Held", ColumnFormat.Money)],
            rows,
            Note: "Retainers received by the date, less what was applied to invoices or refunded by then. The total is what Customer Deposits holds for retainers.");
    }

    /// <param name="responsibleId">Only customers that person is responsible for (their own or inherited from a parent).</param>
    public async Task<ReportTable> CustomerBalancesAsync(DateOnly asOf, Guid? responsibleId = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var aging = await receivables.AgingAsync(asOf, ct);
        var byId = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var shown = aging.Customers
            .Where(c => c.Buckets.Total != 0m && (responsibleId is null || Domain.Sales.CustomerTree.Responsible(c.Customer.Id, byId) == responsibleId))
            .ToList();
        var rows = shown
            .Select(c => new ReportTableRow(
            [
                new ReportCell(c.Customer.DisplayName, new ReportLink("customer", c.Customer.Id)),
                new ReportCell(c.Buckets.Total - c.Buckets.Current == 0m ? null : c.Buckets.Total - c.Buckets.Current),
                c.Buckets.Total,
            ]))
            .ToList();
        var total = responsibleId is null ? aging.Totals.Total : shown.Sum(c => c.Buckets.Total);
        var current = responsibleId is null ? aging.Totals.Current : shown.Sum(c => c.Buckets.Current);
        rows.Add(new ReportTableRow(["Total", total - current, total], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Customer balance summary",
            [company.Name, $"As of {asOf:MMM d, yyyy}", .. responsibleId is null ? Array.Empty<string>() : ["Responsible: one person's clients"]],
            [new("Customer"), new("Past due", ColumnFormat.Money), new("Balance", ColumnFormat.Money)],
            rows,
            Note: "Open invoices less unapplied credits, as on the A/R aging; the total matches it.");
    }

    public async Task<ReportTable> VendorBalancesAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var aging = await payables.AgingAsync(asOf, ct);
        var rows = aging.Vendors
            .Where(v => v.Buckets.Total != 0m)
            .Select(v => new ReportTableRow(
            [
                new ReportCell(v.Vendor.DisplayName, new ReportLink("vendor", v.Vendor.Id)),
                new ReportCell(v.Buckets.Total - v.Buckets.Current == 0m ? null : v.Buckets.Total - v.Buckets.Current),
                v.Buckets.Total,
            ]))
            .ToList();
        rows.Add(new ReportTableRow(["Total", aging.Totals.Total - aging.Totals.Current, aging.Totals.Total], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Vendor balance summary",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [new("Vendor"), new("Past due", ColumnFormat.Money), new("Balance", ColumnFormat.Money)],
            rows,
            Note: "Unpaid bills less unapplied vendor credits, as on the A/P aging; the total matches it.");
    }

    /// <param name="includeInactive">The <c>detail</c> setting: inactive customers too.</param>
    public async Task<ReportTable> CustomerContactsAsync(bool includeInactive, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var list = await customers.ListAsync(CompanyTime.Today, includeInactive, ct);
        return new ReportTable(
            "Customer contact list",
            [company.Name, includeInactive ? "Every customer" : "Active customers"],
            [new("Customer"), new("Company"), new("Email"), new("Phone"), new("Billing address"), new("Shipping address"), new("Terms"), new("Balance", ColumnFormat.Money)],
            [
                .. list.Select(c => new ReportTableRow(
                [
                    new ReportCell(c.FullName ?? c.Customer.DisplayName, new ReportLink("customer", c.Customer.Id)),
                    c.Customer.CompanyName,
                    c.Customer.Email,
                    c.Customer.Phone,
                    OneLine(c.Customer.BillingAddress),
                    OneLine(c.Customer.ShippingAddress),
                    c.Customer.Terms.Label,
                    new ReportCell(c.Balance == 0m ? null : c.Balance),
                ], 0)),
                Count(list.Count, "customer", 8, list.Sum(c => c.Balance)),
            ],
            Note: "Balances are as of today.");
    }

    /// <param name="includeInactive">The <c>detail</c> setting: inactive vendors too.</param>
    public async Task<ReportTable> VendorContactsAsync(bool includeInactive, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var list = await vendors.ListAsync(CompanyTime.Today, includeInactive, ct);
        return new ReportTable(
            "Vendor contact list",
            [company.Name, includeInactive ? "Every vendor" : "Active vendors"],
            [new("Vendor"), new("Company"), new("Email"), new("Phone"), new("Address"), new("Your account no."), new("Terms"), new("1099"), new("Balance", ColumnFormat.Money)],
            [
                .. list.OrderBy(v => v.Vendor.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(v => new ReportTableRow(
                [
                    new ReportCell(v.Vendor.DisplayName, new ReportLink("vendor", v.Vendor.Id)),
                    v.Vendor.CompanyName,
                    v.Vendor.Email,
                    v.Vendor.Phone,
                    OneLine(v.Vendor.Address),
                    v.Vendor.AccountNumber,
                    v.Vendor.PaymentTermsDays == 0 ? "Due on receipt" : $"Net {v.Vendor.PaymentTermsDays}",
                    v.Vendor.Track1099 ? "Yes" : null,
                    new ReportCell(v.Balance == 0m ? null : v.Balance),
                ])),
                Count(list.Count, "vendor", 9, list.Sum(v => v.Balance)),
            ],
            Note: "Balances are as of today. Tax IDs are left out; the 1099 reports show them masked.");
    }

    /// <param name="includeInactive">The <c>detail</c> setting: inactive items too.</param>
    public async Task<ReportTable> ItemListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var stock = (await inventory.StockAsync(null, ct)).ToDictionary(s => s.Item.Id, s => s.Quantity);
        var vendorNames = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var items = (await sales.ListItemsAsync(ct)).Where(i => includeInactive || i.IsActive).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new ReportTable(
            "Product and service list",
            [company.Name, includeInactive ? "Every item" : "Active items"],
            [new("Item"), new("Type"), new("Description"), new("Barcode"), new("Price", ColumnFormat.Money), new("Purchase cost", ColumnFormat.Money),
             new("Income account"), new("Taxable"), new("Preferred vendor"), new("On hand", ColumnFormat.Number)],
            [
                .. items.Select(i => new ReportTableRow(
                [
                    new ReportCell(i.Name, new ReportLink("item", i.Id)),
                    TypeLabel(i.Type),
                    i.Description,
                    i.Barcode,
                    i.Rate,
                    new ReportCell(i.PurchaseCost == 0m ? null : i.PurchaseCost),
                    chart.Find(i.IncomeAccountId) is { } a ? chart.FullName(a.Id) : null,
                    i.Taxable ? "Yes" : "No",
                    i.PreferredVendorId is { } v ? vendorNames.GetValueOrDefault(v) : null,
                    new ReportCell(i.IsInventory ? stock.GetValueOrDefault(i.Id) : null),
                ])),
                Count(items.Count, "item", 10, null),
            ]);
    }

    /// <summary>Every account with its type and balance (natural sign: debit-normal accounts positive when debited).</summary>
    /// <param name="includeInactive">The <c>detail</c> setting: inactive accounts too.</param>
    public async Task<ReportTable> AccountListAsync(DateOnly asOf, bool includeInactive, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var balances = (await ledger.TotalsAsync(ReportBasis.Accrual, asOf, asOf, ct)).GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        return new ReportTable(
            "Account list",
            [company.Name, $"Balances as of {asOf:MMM d, yyyy} · Accrual basis"],
            [new("Number"), new("Account"), new("Type"), new("Description"), new("Tax line"), new("Balance", ColumnFormat.Money)],
            [
                .. chart.Ordered(includeInactive).Select(x =>
                {
                    var a = x.Account;
                    var balance = balances.GetValueOrDefault(a.Id);
                    var shown = a.Type.IsBalanceSheet() ? (a.Type.IsDebitNormal() ? balance : -balance) : (decimal?)null;
                    return new ReportTableRow(
                    [
                        a.Number,
                        new ReportCell(chart.FullName(a.Id) + (a.IsActive ? "" : " (inactive)"), new ReportLink("account", a.Id) { To = asOf }),
                        a.Type.Describe(),
                        a.Description,
                        TaxLines.Find(a.TaxLineKey)?.Label ?? a.TaxLineKey,
                        new ReportCell(shown is 0m ? null : shown),
                    ], x.Depth);
                }),
                Count(chart.Ordered(includeInactive).Count(), "account", 6, null),
            ],
            ReportBasis.Accrual,
            "Balance sheet accounts show their balance in their normal direction; see the profit and loss for income and expense accounts.");
    }

    /// <summary>Open purchase orders by vendor: what's still to receive and to bill.</summary>
    /// <summary>
    /// Goods received on purchase orders that the vendor hasn't billed yet, by vendor and order, at the order's unit cost,
    /// and the "Received Not Billed" account's balance beside it (it holds deliveries received into stock).
    /// </summary>
    public async Task<ReportTable> ReceivedNotBilledAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var vendorNames = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var orders = (await purchases.ListPurchaseOrdersAsync(null, ct)).ToList();
        var rows = new List<ReportTableRow>();
        var grand = 0m;
        foreach (var group in orders.GroupBy(o => o.VendorId).OrderBy(g => vendorNames.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var lines = new List<ReportTableRow>();
            var subtotal = 0m;
            foreach (var order in group.OrderBy(o => o.Date).ThenBy(o => o.Number, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var line in (await purchaseOrders.ProgressAsync(order, ct)).Lines.Where(l => l.ReceivedNotBilled > 0m))
                {
                    var value = decimal.Round(line.ReceivedNotBilled * line.Line.UnitCost, 2, MidpointRounding.AwayFromZero);
                    lines.Add(new ReportTableRow(
                    [
                        new ReportCell($"Purchase order {order.Number}", new ReportLink("purchase_order", order.Id)),
                        line.Line.Description, new ReportCell(line.ReceivedNotBilled), new ReportCell(line.Line.UnitCost), value,
                    ], 1));
                    subtotal += value;
                }
            }

            if (lines.Count == 0)
                continue;
            var name = vendorNames.GetValueOrDefault(group.Key) ?? "";
            rows.Add(new ReportTableRow([new ReportCell(name, new ReportLink("vendor", group.Key)), null, null, null, null], 0, ReportRowStyle.Section));
            rows.AddRange(lines);
            rows.Add(new ReportTableRow([$"Total {name}", null, null, null, subtotal], 0, ReportRowStyle.Subtotal));
            grand += subtotal;
        }

        rows.Add(new ReportTableRow(["Total received not billed", null, null, null, grand], 0, ReportRowStyle.Total));
        var chart = await accounts.GetChartAsync(ct);
        if (chart.FindByFullName(PurchaseOrderService.ReceivedNotBilledName) is { } account)
        {
            var balance = -(await ledger.TotalsAsync(ReportBasis.Accrual, CompanyTime.Today, CompanyTime.Today, ct)).Where(l => l.AccountId == account.Id).Sum(l => l.Amount);
            rows.Add(new ReportTableRow([new ReportCell($"{account.Name} account in the books", new ReportLink("account", account.Id)), null, null, null, balance]));
        }

        return new ReportTable(
            "Received not billed",
            [company.Name, $"As of {CompanyTime.Today:MMM d, yyyy}"],
            [new("Vendor and order"), new("Item"), new("Quantity", ColumnFormat.Number), new("Unit cost", ColumnFormat.Money), new("Value", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "Goods received and not billed, at the order's cost. The account holds only deliveries received into stock (Purchasing settings); goods received without it enter the books with the bill.");
    }

    public async Task<ReportTable> OpenPurchaseOrdersAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var vendorNames = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var open = (await purchases.ListPurchaseOrdersAsync(null, ct)).Where(o => o.Status == PurchaseOrderStatus.Open).ToList();
        var rows = new List<ReportTableRow>();
        decimal allTotal = 0m, allToBill = 0m;
        foreach (var group in open.GroupBy(o => o.VendorId).OrderBy(g => vendorNames.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var name = vendorNames.GetValueOrDefault(group.Key) ?? "";
            rows.Add(new ReportTableRow([new ReportCell(name, new ReportLink("vendor", group.Key)), null, null, null, null, null], 0, ReportRowStyle.Section));
            decimal total = 0m, toBill = 0m;
            foreach (var order in group.OrderBy(o => o.Date).ThenBy(o => o.Number, StringComparer.OrdinalIgnoreCase))
            {
                var progress = await purchaseOrders.ProgressAsync(order, ct);
                var left = progress.Lines.Sum(l => l.Line.IsItem ? Math.Max(0m, l.ToBill) * l.Line.UnitCost : Math.Max(0m, l.ToBill));
                var toReceive = progress.Lines.Where(l => l.Line.IsItem).Sum(l => Math.Max(0m, l.Line.Quantity - Math.Max(l.Received, l.BilledQuantity)));
                left = decimal.Round(left, 2, MidpointRounding.AwayFromZero);
                rows.Add(new ReportTableRow(
                [
                    new ReportCell($"Purchase order {order.Number}", new ReportLink("purchase_order", order.Id)),
                    new ReportCell(order.Date),
                    new ReportCell(order.ExpectedDate),
                    new ReportCell(toReceive == 0m ? null : toReceive),
                    order.Total,
                    left,
                ], 1));
                (total, toBill) = (total + order.Total, toBill + left);
            }

            rows.Add(new ReportTableRow([$"Total {name}", null, null, null, total, toBill], 0, ReportRowStyle.Subtotal));
            (allTotal, allToBill) = (allTotal + total, allToBill + toBill);
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, allTotal, allToBill], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Open purchase orders",
            [company.Name, $"As of {CompanyTime.Today:MMM d, yyyy}"],
            [new("Vendor and order"), new("Date", ColumnFormat.Date), new("Expected", ColumnFormat.Date), new("Units to receive", ColumnFormat.Number),
             new("Amount", ColumnFormat.Money), new("Still to bill", ColumnFormat.Money)],
            rows,
            Note: "Still to bill is at the order's costs: units not billed yet and what's left of category lines.");
    }

    /// <summary>Open sales orders by customer: what's still to invoice.</summary>
    public async Task<ReportTable> OpenSalesOrdersAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var names = await customers.FullNamesAsync(ct);
        var open = (await sales.ListSalesOrdersAsync(null, ct)).Where(o => o.Status == SalesOrderStatus.Open).ToList();
        var rows = new List<ReportTableRow>();
        decimal allTotal = 0m, allLeft = 0m;
        foreach (var group in open.GroupBy(o => o.CustomerId).OrderBy(g => names.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var name = names.GetValueOrDefault(group.Key) ?? "";
            rows.Add(new ReportTableRow([new ReportCell(name, new ReportLink("customer", group.Key)), null, null, null, null, null], 0, ReportRowStyle.Section));
            decimal total = 0m, left = 0m;
            foreach (var order in group.OrderBy(o => o.Date).ThenBy(o => o.Number, StringComparer.OrdinalIgnoreCase))
            {
                var progress = await salesOrders.ProgressAsync(order, ct);
                var remaining = Math.Max(0m, order.Subtotal - progress.InvoicedSubtotal);
                rows.Add(new ReportTableRow(
                [
                    new ReportCell($"Sales order {order.Number}", new ReportLink("sales_order", order.Id)),
                    new ReportCell(order.Date),
                    new ReportCell(order.ExpectedDate),
                    order.CustomerReference,
                    order.Subtotal,
                    remaining,
                ], 1));
                (total, left) = (total + order.Subtotal, left + remaining);
            }

            rows.Add(new ReportTableRow([$"Total {name}", null, null, null, total, left], 0, ReportRowStyle.Subtotal));
            (allTotal, allLeft) = (allTotal + total, allLeft + left);
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, allTotal, allLeft], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Open sales orders",
            [company.Name, $"As of {CompanyTime.Today:MMM d, yyyy}"],
            [new("Customer and order"), new("Date", ColumnFormat.Date), new("Expected", ColumnFormat.Date), new("Their PO"),
             new("Amount", ColumnFormat.Money), new("Still to invoice", ColumnFormat.Money)],
            rows,
            Note: "Amounts are before sales tax; tax is worked out again on each invoice.");
    }

    /// <summary>
    /// Inventory bought in a period: item lines of posted bills, plus deliveries received into stock before their bill
    /// (at their received value). Bills from those deliveries clear them on category lines, so nothing counts twice.
    /// </summary>
    public async Task<ReportTable> PurchasesByItemAsync(DateOnly from, DateOnly to, bool detail, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var vendorNames = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var bought = new List<(Guid ItemId, Guid VendorId, DateOnly Date, string Label, ReportLink Link, decimal Quantity, decimal Amount)>();
        foreach (var bill in (await purchases.ListBillsAsync(null, ct)).Where(b => b.Status == DocumentStatus.Posted && b.Date >= from && b.Date <= to))
        {
            foreach (var line in bill.Lines.Where(l => l.ItemId is not null))
                bought.Add((line.ItemId!.Value, bill.VendorId, bill.Date, bill.Reference is { } r ? $"Bill {r}" : "Bill", new ReportLink("bill", bill.Id), line.Quantity ?? 0m, line.Amount));
        }

        foreach (var order in await purchases.ListPurchaseOrdersAsync(null, ct))
        {
            foreach (var receipt in order.Receipts.Where(r => r.InStock && !r.IsVoided && r.Date >= from && r.Date <= to))
            {
                if (order.Lines.FirstOrDefault(l => l.LineNumber == receipt.LineNumber)?.ItemId is { } itemId)
                    bought.Add((itemId, order.VendorId, receipt.Date, $"Received on purchase order {order.Number}", new ReportLink("purchase_order", order.Id), receipt.Quantity, receipt.Value));
            }
        }

        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var rows = new List<ReportTableRow>();
        foreach (var group in bought.GroupBy(b => b.ItemId).OrderBy(g => items.GetValueOrDefault(g.Key)?.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = new ReportCell(items.GetValueOrDefault(group.Key)?.Name ?? "", new ReportLink("item", group.Key));
            var quantity = group.Sum(b => b.Quantity);
            var amount = group.Sum(b => b.Amount);
            var average = new ReportCell(quantity == 0m ? null : decimal.Round(amount / quantity, 4, MidpointRounding.AwayFromZero));
            if (!detail)
            {
                rows.Add(new ReportTableRow([name, null, null, new ReportCell(quantity), amount, average]));
                continue;
            }

            rows.Add(new ReportTableRow([name, null, null, null, null, null], 0, ReportRowStyle.Section));
            rows.AddRange(group.OrderBy(b => b.Date).Select(b => new ReportTableRow(
                [new ReportCell(b.Label, b.Link), new ReportCell(b.Date), vendorNames.GetValueOrDefault(b.VendorId), new ReportCell(b.Quantity), b.Amount,
                 new ReportCell(b.Quantity == 0m ? null : decimal.Round(b.Amount / b.Quantity, 4, MidpointRounding.AwayFromZero))], 1)));
            rows.Add(new ReportTableRow([$"Total {name.Value}", null, null, new ReportCell(quantity), amount, average], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, bought.Sum(b => b.Amount), null], 0, ReportRowStyle.Total));
        return new ReportTable(
            $"Purchases by product {(detail ? "detail" : "summary")}",
            [company.Name, $"{Comparisons.Describe(from, to)} · Accrual basis"],
            [new("Item"), new("Date", ColumnFormat.Date), new("Vendor"), new("Quantity", ColumnFormat.Number), new("Amount", ColumnFormat.Money), new("Average cost", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "Inventory item lines of posted bills, and deliveries received into stock before their bill. Freight and other category lines aren't included.");
    }

    /// <summary>Each bank deposit in a period with what went into it.</summary>
    public async Task<ReportTable> DepositDetailAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var names = await customers.FullNamesAsync(ct);
        var rows = new List<ReportTableRow>();
        var list = (await sales.ListDepositsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Date >= from && d.Date <= to)
            .OrderBy(d => d.Date).ThenBy(d => d.CreatedAt).ToList();
        foreach (var deposit in list)
        {
            var bank = chart.Find(deposit.BankAccountId) is { } a ? chart.FullName(a.Id) : "";
            rows.Add(new ReportTableRow(
                [new ReportCell($"Deposit to {bank}", new ReportLink("deposit", deposit.Id)), new ReportCell(deposit.Date), null, null, deposit.Memo, null], 0, ReportRowStyle.Section));
            foreach (var item in await deposits.ContentsAsync(deposit, ct))
            {
                rows.Add(new ReportTableRow(
                [
                    new ReportCell(item.Label, new ReportLink(DepositedEntity(item.Kind), item.Id)),
                    new ReportCell(item.Date),
                    item.CustomerId is { } c ? names.GetValueOrDefault(c) : null,
                    PaymentMethods.Label(item.Method) is { Length: > 0 } m ? m : null,
                    item.Reference,
                    item.Amount,
                ], 1));
            }

            rows.Add(new ReportTableRow([$"Total deposit {deposit.Date:MMM d}", null, null, null, null, deposit.Total], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, null, list.Sum(d => d.Total)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Deposit detail",
            [company.Name, Comparisons.Describe(from, to)],
            [new("Deposit and item"), new("Date", ColumnFormat.Date), new("Received from"), new("Method"), new("Reference"), new("Amount", ColumnFormat.Money)],
            rows,
            Note: "Deposits made with Make deposit, each with the payments, receipts and other items in it, the way the bank statement shows one total.");
    }

    /// <summary>
    /// A deposit slip to take to the bank: each item with how it was paid and its check number, then the cash, check
    /// and other totals the bank's own slip asks for.
    /// </summary>
    public async Task<ReportTable> DepositSlipAsync(Guid depositId, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var deposit = await sales.GetDepositAsync(depositId, ct) ?? throw new EntityNotFoundException("deposit", depositId);
        var chart = await accounts.GetChartAsync(ct);
        var names = await customers.FullNamesAsync(ct);
        var items = await deposits.ContentsAsync(deposit, ct);
        var bank = chart.Find(deposit.BankAccountId);
        var rows = items.Select(item => new ReportTableRow(
        [
            item.CustomerId is { } c ? names.GetValueOrDefault(c) ?? item.Label : item.Label,
            PaymentMethods.Label(item.Method) is { Length: > 0 } m ? m : null,
            item.Reference,
            item.Amount,
        ])).ToList();
        var checks = items.Where(i => i.Method == PaymentMethod.Check).ToList();
        decimal Sum(Func<UndepositedItem, bool> which) => items.Where(which).Sum(i => i.Amount);
        rows.Add(new ReportTableRow(["Cash", null, null, Sum(i => i.Method == PaymentMethod.Cash)], 0, ReportRowStyle.Subtotal));
        rows.Add(new ReportTableRow([$"Checks ({checks.Count})", null, null, checks.Sum(i => i.Amount)], 0, ReportRowStyle.Subtotal));
        if (Sum(i => i.Method is not (PaymentMethod.Cash or PaymentMethod.Check)) is not 0m and var other)
            rows.Add(new ReportTableRow(["Other (card, transfer, not given)", null, null, other], 0, ReportRowStyle.Subtotal));
        foreach (var extra in deposit.Extras)
            rows.Add(new ReportTableRow([$"{(extra.Amount < 0m ? "Less" : "Plus")} {extra.Memo ?? chart.FullName(extra.AccountId)}", null, null, extra.Amount], 0, ReportRowStyle.Subtotal));
        rows.Add(new ReportTableRow(["Total deposit", null, null, deposit.Total], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Deposit slip",
            [
                company.LegalName ?? company.Name,
                $"{(bank is null ? "" : chart.FullName(bank.Id))}{(bank?.Number is { } n ? $" ({n})" : "")} · {deposit.Date:MMMM d, yyyy}",
            ],
            [new("Received from"), new("Method"), new("Check no. / reference"), new("Amount", ColumnFormat.Money)],
            rows,
            Note: deposit.Status == DocumentStatus.Voided ? "This deposit is voided." : deposit.Memo);
    }

    /// <summary>Estimates dated in a period, by customer, with their status.</summary>
    public async Task<ReportTable> EstimatesByCustomerAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var names = await customers.FullNamesAsync(ct);
        var list = (await sales.ListEstimatesAsync(null, ct)).Where(e => e.Date >= from && e.Date <= to).ToList();
        var rows = new List<ReportTableRow>();
        foreach (var group in list.GroupBy(e => e.CustomerId).OrderBy(g => names.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var name = names.GetValueOrDefault(group.Key) ?? "";
            rows.Add(new ReportTableRow([new ReportCell(name, new ReportLink("customer", group.Key)), null, null, null, null], 0, ReportRowStyle.Section));
            rows.AddRange(group.OrderBy(e => e.Date).ThenBy(e => e.Number, StringComparer.OrdinalIgnoreCase).Select(e => new ReportTableRow(
            [
                new ReportCell($"Estimate {e.Number}", new ReportLink("estimate", e.Id)),
                new ReportCell(e.Date),
                new ReportCell(e.ExpirationDate),
                EstimateStatusLabel(e.Status),
                e.Total,
            ], 1)));
            rows.Add(new ReportTableRow([$"Total {name}", null, null, null, group.Sum(e => e.Total)], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, list.Sum(e => e.Total)], 0, ReportRowStyle.Total));
        var accepted = list.Where(e => e.Status is EstimateStatus.Accepted or EstimateStatus.Closed).Sum(e => e.Total);
        return new ReportTable(
            "Estimates by customer",
            [company.Name, Comparisons.Describe(from, to)],
            [new("Customer and estimate"), new("Date", ColumnFormat.Date), new("Expires", ColumnFormat.Date), new("Status"), new("Amount", ColumnFormat.Money)],
            rows,
            Note: list.Count == 0 ? null : $"Accepted or invoiced: {accepted:N2} of {list.Sum(e => e.Total):N2}.");
    }

    /// <summary>A list's last row: "3 customers", with an optional total in the last column, so even an empty list has a row.</summary>
    private static ReportTableRow Count(int count, string noun, int columns, decimal? total)
    {
        var cells = new ReportCell?[columns];
        cells[0] = $"{count} {noun}{(count == 1 ? "" : "s")}";
        if (total is { } t)
            cells[columns - 1] = t;
        return new ReportTableRow(cells, 0, ReportRowStyle.Total);
    }

    internal static string EstimateStatusLabel(EstimateStatus status) => status switch
    {
        EstimateStatus.Pending => "Pending",
        EstimateStatus.Accepted => "Accepted",
        EstimateStatus.Declined => "Declined",
        _ => "Invoiced",
    };

    private static string DepositedEntity(DepositedKind kind) => kind switch
    {
        DepositedKind.Payment => "payment",
        DepositedKind.SalesReceipt => "sales_receipt",
        DepositedKind.Retainer => "retainer",
        _ => "donation",
    };

    private static string TypeLabel(ItemType type) => type switch
    {
        ItemType.Service => "Service",
        ItemType.Inventory => "Inventory product",
        ItemType.Bundle => "Bundle",
        ItemType.Assembly => "Assembly",
        _ => "Non-inventory product",
    };

    private static string? OneLine(string? address) =>
        address is null ? null : string.Join(", ", address.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
