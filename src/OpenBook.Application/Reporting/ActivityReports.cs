using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
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
    PayablesService payables)
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
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}"],
            [new("Customer"), new("Income", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            reportBasis,
            "Income accounts only, after discounts and before sales tax; the total matches income on the profit and loss.");
    }

    /// <summary>Item lines of invoices and sales receipts, less credit memos, dated in the period (accrual).</summary>
    public async Task<ReportTable> SalesByItemAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        bool Counts(DocumentStatus status, DateOnly date) => status == DocumentStatus.Posted && date >= from && date <= to;
        var documents = new List<(IReadOnlyList<SalesLine> Lines, decimal Sign)>();
        documents.AddRange((await sales.ListInvoicesAsync(null, ct)).Where(i => Counts(i.Status, i.Date)).Select(i => (i.Lines, 1m)));
        documents.AddRange((await sales.ListReceiptsAsync(ct)).Where(r => Counts(r.Status, r.Date)).Select(r => (r.Lines, 1m)));
        documents.AddRange((await sales.ListCreditMemosAsync(null, ct)).Where(c => Counts(c.Status, c.Date)).Select(c => (c.Lines, -1m)));

        var byItem = new Dictionary<Guid, (decimal Quantity, decimal Amount)>();
        var discounts = 0m;
        foreach (var (lines, sign) in documents)
        {
            foreach (var line in lines)
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

        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var total = byItem.Values.Sum(v => v.Amount) + discounts;
        var rows = byItem
            .Where(kv => kv.Value.Amount != 0m || kv.Value.Quantity != 0m)
            .OrderBy(kv => items[kv.Key].Name, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ReportTableRow(
            [
                new ReportCell(items[kv.Key].Name, new ReportLink("item", kv.Key)),
                new ReportCell(kv.Value.Quantity),
                kv.Value.Amount,
                new ReportCell(Share(kv.Value.Amount, total)),
                new ReportCell(kv.Value.Quantity == 0m ? null : decimal.Round(kv.Value.Amount / kv.Value.Quantity, 2)),
            ]))
            .ToList();
        if (discounts != 0m)
            rows.Add(new ReportTableRow(["Discounts", ReportCell.Empty, discounts, new ReportCell(Share(discounts, total)), ReportCell.Empty]));
        rows.Add(new ReportTableRow(["Total", ReportCell.Empty, total, new ReportCell(total == 0m ? null : 100m), ReportCell.Empty], 0, ReportRowStyle.Total));

        return new ReportTable(
            "Sales by product or service",
            [company.Name, $"{Comparisons.Describe(from, to)} · Accrual basis"],
            [new("Product or service"), new("Quantity", ColumnFormat.Number), new("Amount", ColumnFormat.Money), new("% of sales", ColumnFormat.Percent), new("Average price", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "From invoices and sales receipts, less credit memos, dated in the period. Amounts are before sales tax.");
    }

    /// <summary>
    /// Expenses (cost of goods sold, expense and other expense accounts) from the ledger, by the vendor on the bill,
    /// vendor credit, bill payment (cash basis), vendor refund or expense that posted them.
    /// </summary>
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
            [company.Name, $"{Comparisons.Describe(from, to)} · {StatementReports.Describe(reportBasis)}"],
            [new("Vendor"), new("Expenses", ColumnFormat.Money), new("% of total", ColumnFormat.Percent)],
            rows,
            reportBasis,
            "Cost of goods sold and expense accounts; the total matches those sections of the profit and loss.");
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

    /// <summary>Each payment counted on the 1099 summary, plus card payments shown but not counted (1099-K).</summary>
    public async Task<ReportTable> Detail1099Async(int year, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
            throw new DomainException("report.year_invalid", "Choose a year between 2000 and 2100.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var vendors = (await purchases.ListVendorsAsync(ct)).Where(v => v.Track1099).ToDictionary(v => v.Id);

        var payments = new List<(Guid VendorId, DateOnly Date, string Type, string? Reference, Guid AccountId, decimal Amount, ReportLink Link)>();
        foreach (var p in (await purchases.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && p.Date.Year == year && vendors.ContainsKey(p.VendorId)))
            payments.Add((p.VendorId, p.Date, "Bill payment", p.Reference, p.PaymentAccountId, p.Amount, new ReportLink("bill_payment", p.Id)));
        foreach (var e in (await purchases.ListExpensesAsync(null, ct)).Where(e => e.Status == BankTransactionStatus.Posted && e.Date.Year == year && e.VendorId is { } v && vendors.ContainsKey(v)))
            payments.Add((e.VendorId!.Value, e.Date, "Expense", e.Reference, e.AccountId, e.Amount, new ReportLink("bank_transaction", e.Id)));

        bool IsCard(Guid accountId) => chart.Find(accountId)?.Type == AccountType.CreditCard;
        var rows = new List<ReportTableRow>();
        foreach (var group in payments.GroupBy(p => p.VendorId).OrderBy(g => vendors[g.Key].DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var vendor = vendors[group.Key];
            rows.Add(new ReportTableRow([new ReportCell(vendor.DisplayName, new ReportLink("vendor", vendor.Id)), .. Blank(4), ReportCell.Empty], 0, ReportRowStyle.Section));
            rows.AddRange(group.OrderBy(p => p.Date).Select(p => new ReportTableRow(
            [
                new ReportCell(p.Type, p.Link),
                new ReportCell(p.Date),
                p.Reference,
                chart.Find(p.AccountId) is { } a ? chart.FullName(a.Id) : null,
                new ReportCell(IsCard(p.AccountId) ? null : p.Amount),
                new ReportCell(IsCard(p.AccountId) ? p.Amount : null),
            ], 1)));
            rows.Add(new ReportTableRow(
                [$"Total {vendor.DisplayName}", .. Blank(3), group.Where(p => !IsCard(p.AccountId)).Sum(p => p.Amount), group.Where(p => IsCard(p.AccountId)).Sum(p => p.Amount)],
                0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(
            ["Total", .. Blank(3), payments.Where(p => !IsCard(p.AccountId)).Sum(p => p.Amount), payments.Where(p => IsCard(p.AccountId)).Sum(p => p.Amount)],
            0, ReportRowStyle.Total));
        return new ReportTable(
            $"1099 detail for {year}",
            [company.Name, "Payments to vendors tracked for 1099s"],
            [new("Vendor and payment"), new("Date", ColumnFormat.Date), new("Reference"), new("Paid from"), new("Counted", ColumnFormat.Money), new("Card (1099-K)", ColumnFormat.Money)],
            rows,
            Note: "Card payments are left out of the 1099 totals because the card company reports them on Form 1099-K.");
    }

    private static decimal? Share(decimal amount, decimal total) => total == 0m ? null : decimal.Round(amount / total * 100m, 1);

    private static ReportCell[] Blank(int count) => Enumerable.Repeat(ReportCell.Empty, count).ToArray();
}
