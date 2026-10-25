using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Pricing;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

/// <summary>The settings a report's filter offers, which are also the query string parameters it reads.</summary>
[Flags]
public enum ReportInputs
{
    None = 0,

    /// <summary>preset, from, to.</summary>
    Period = 1,

    /// <summary>asOf.</summary>
    AsOf = 2,

    /// <summary>basis.</summary>
    Basis = 4,

    /// <summary>compare: a <see cref="Comparison"/>.</summary>
    Compare = 8,

    /// <summary>accountId.</summary>
    Account = 16,

    /// <summary>year (calendar).</summary>
    Year = 32,

    /// <summary>threshold (1099).</summary>
    Threshold = 64,

    /// <summary>budgetId, fromMonth, toMonth.</summary>
    Budget = 128,

    /// <summary>detail: true for one row per document.</summary>
    Detail = 256,

    /// <summary>customerId or levelId: whose prices.</summary>
    PriceFor = 512,
}

public sealed record ReportDefinition(
    string Key,
    string Title,
    string Description,
    string Group,
    ReportInputs Inputs,
    DatePreset DefaultPreset = DatePreset.ThisFiscalYearToDate,
    IReadOnlyList<Comparison>? Comparisons = null)
{
    public string Path => $"/reports/{Key}";
}

/// <summary>A report's settings read from a query string, with the same defaults as its page.</summary>
public sealed class ReportParameters
{
    private readonly Dictionary<string, string> _values;

    public ReportParameters(IEnumerable<KeyValuePair<string, string?>> values) =>
        _values = values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value!.Trim(), StringComparer.OrdinalIgnoreCase);

    public static ReportParameters Parse(string? query) => new(
        (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((at < 0 ? pair : pair[..at]).Replace('+', ' '));
            var value = at < 0 ? string.Empty : Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            return new KeyValuePair<string, string?>(key, value);
        }));

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public DateOnly? GetDate(string name) =>
        DateOnly.TryParseExact(Get(name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public Guid? GetId(string name) => System.Guid.TryParse(Get(name), out var g) ? g : null;

    public int? GetNumber(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public decimal? GetAmount(string name) => decimal.TryParse(Get(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    public bool GetFlag(string name) => bool.TryParse(Get(name), out var b) && b;

    public T? GetEnum<T>(string name) where T : struct, Enum => System.Enum.TryParse<T>(Get(name), ignoreCase: true, out var v) && System.Enum.IsDefined(v) ? v : null;

    public (DatePreset Preset, DateRange Range) Period(Company company, DatePreset defaultPreset, DateOnly today)
    {
        var preset = GetEnum<DatePreset>("preset") ?? defaultPreset;
        return (preset, DateRanges.For(preset, today, company.FiscalYearStartMonth, GetDate("from"), GetDate("to")));
    }

    public ReportBasis Basis(Company company) => GetEnum<ReportBasis>("basis") ?? company.DefaultReportBasis;
}

/// <summary>
/// Every report that can be shown as a table, exported and saved, by key. Report pages and the export
/// endpoint both run reports through here, so a download always matches the screen.
/// </summary>
public sealed class ReportCatalog(IServiceProvider services, ICompanyStore companies)
{
    private static readonly Comparison[] PeriodComparisons =
        [Comparison.None, Comparison.PreviousPeriod, Comparison.PreviousYear, Comparison.Months, Comparison.Quarters];

    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new("profit-and-loss", "Profit and loss", "Income and expenses over a period and the net income they leave. Compare periods, months or quarters.",
            "Financial statements", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Compare, Comparisons: PeriodComparisons),
        new("balance-sheet", "Balance sheet", "What the business owns and owes on a date, and the owner's equity. Compare with a month or year earlier.",
            "Financial statements", ReportInputs.AsOf | ReportInputs.Basis | ReportInputs.Compare,
            Comparisons: [Comparison.None, Comparison.PreviousPeriod, Comparison.PreviousYear]),
        new("cash-flow", "Statement of cash flows", "Where cash came from and went: operating, investing and financing activities.",
            "Financial statements", ReportInputs.Period | ReportInputs.Basis),
        new("trial-balance", "Trial balance", "Every account's debit or credit balance on a date. Debits always equal credits.",
            "Accountant", ReportInputs.AsOf | ReportInputs.Basis),
        new("general-ledger", "General ledger", "Every transaction in each account, with opening and running balances.",
            "Accountant", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Account, DatePreset.ThisMonth),
        new("journal", "Journal", "Each journal entry in a period with all its debits and credits.",
            "Accountant", ReportInputs.Period, DatePreset.ThisMonth),
        new("sales-by-customer", "Sales by customer", "Income from each customer over a period. Ties to the profit and loss.",
            "Sales and customers", ReportInputs.Period | ReportInputs.Basis),
        new("price-list", "Price list", "Each product and service's list price and what a customer or price level pays, with quantity breaks.",
            "Sales and customers", ReportInputs.AsOf | ReportInputs.PriceFor),
        new("discounts-given", "Discounts given", "What price levels, quantity breaks, rules and manual prices took off list prices, by what set the price.",
            "Sales and customers", ReportInputs.Period | ReportInputs.Detail),
        new("sales-by-item", "Sales by product or service", "Quantity sold, amount and average price of each product and service.",
            "Sales and customers", ReportInputs.Period),
        new("open-invoices", "Open invoices", "Every unpaid invoice on a date, by customer, with what's still owed.",
            "Sales and customers", ReportInputs.AsOf),
        new("ar-aging", "A/R aging", "What each customer owes, by how long past due. Summary or invoice by invoice.",
            "Sales and customers", ReportInputs.AsOf | ReportInputs.Detail),
        new("expenses-by-vendor", "Expenses by vendor", "What you spent with each vendor over a period. Ties to the profit and loss.",
            "Expenses and vendors", ReportInputs.Period | ReportInputs.Basis),
        new("unpaid-bills", "Unpaid bills", "Every unpaid bill on a date, by vendor, with what's still owed.",
            "Expenses and vendors", ReportInputs.AsOf),
        new("ap-aging", "A/P aging", "What you owe each vendor, by how long past due. Summary or bill by bill.",
            "Expenses and vendors", ReportInputs.AsOf | ReportInputs.Detail),
        new("inventory-valuation", "Inventory valuation", "Quantity on hand and FIFO cost of each inventory item on a date. Summary or layer by layer.",
            "Inventory", ReportInputs.AsOf | ReportInputs.Detail),
        new("inventory-stock", "Inventory stock status", "What's on hand against each item's reorder point, with what to reorder first.",
            "Inventory", ReportInputs.None),
        new("1099", "1099 summary", "What you paid each 1099 vendor in a year, and who needs a form.",
            "Taxes", ReportInputs.Year | ReportInputs.Threshold),
        new("1099-detail", "1099 detail", "Each payment behind the 1099 summary, by vendor.",
            "Taxes", ReportInputs.Year),
        new("sales-tax", "Sales tax liability", "Taxable, non-taxable and exempt sales and the tax charged, by agency and rate, with what you owe.",
            "Taxes", ReportInputs.Period, DatePreset.LastQuarter),
        new("tax-summary", "Tax summary", "Income and expenses totalled by the lines of your income tax return.",
            "Taxes", ReportInputs.Period | ReportInputs.Basis, DatePreset.LastFiscalYear),
        new("budget-vs-actual", "Budget vs. actual", "Actual income and expenses beside a budget, with what's over or under.",
            "Budgets", ReportInputs.Budget | ReportInputs.Basis),
    ];

    public static ReportDefinition? Find(string? key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    public async Task<ReportTable> RunAsync(string key, ReportParameters p, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(p);
        var definition = Find(key) ?? throw new DomainException("report.unknown", "Unknown report.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (_, range) = p.Period(company, definition.DefaultPreset, today);
        var from = range.From ?? DateRanges.Beginning;
        var asOf = p.GetDate("asOf") ?? today;
        var basis = p.Basis(company);
        T Get<T>() where T : notnull => services.GetRequiredService<T>();

        return definition.Key switch
        {
            "profit-and-loss" => await Get<StatementReports>().ProfitAndLossAsync(from, range.To, basis, Compare(p, definition), ct),
            "balance-sheet" => await Get<StatementReports>().BalanceSheetAsync(asOf, basis, Compare(p, definition), ct),
            "cash-flow" => await Get<StatementReports>().CashFlowTableAsync(from, range.To, basis, ct),
            "trial-balance" => TrialBalance(company, await Get<TrialBalanceQuery>().RunAsync(asOf, basis, ct)),
            "general-ledger" => GeneralLedger(company, range, await Get<FinancialReports>().GeneralLedgerAsync(range.From, range.To, basis, p.GetId("accountId"), ct)),
            "journal" => await JournalAsync(company, range, ct),
            "sales-by-customer" => await Get<ActivityReports>().SalesByCustomerAsync(from, range.To, basis, ct),
            "sales-by-item" => await Get<ActivityReports>().SalesByItemAsync(from, range.To, ct),
            "price-list" => await Get<PricingReports>().PriceListAsync(p.GetId("customerId"), p.GetId("levelId"), asOf, ct),
            "discounts-given" => await Get<PricingReports>().DiscountsGivenAsync(from, range.To, p.GetFlag("detail"), ct),
            "open-invoices" => await Get<ActivityReports>().OpenInvoicesAsync(asOf, ct),
            "ar-aging" => ReceivablesAging(company, await Get<ReceivablesService>().AgingAsync(asOf, ct), p.GetFlag("detail")),
            "expenses-by-vendor" => await Get<ActivityReports>().ExpensesByVendorAsync(from, range.To, basis, ct),
            "unpaid-bills" => await Get<ActivityReports>().UnpaidBillsAsync(asOf, ct),
            "ap-aging" => PayablesAging(company, await Get<PayablesService>().AgingAsync(asOf, ct), p.GetFlag("detail")),
            "inventory-valuation" => await Get<InventoryReports>().ValuationAsync(asOf, p.GetFlag("detail"), ct),
            "inventory-stock" => await Get<InventoryReports>().StockStatusAsync(ct),
            "1099" => Summary1099(company, await Get<PayablesService>().Report1099Async(p.GetNumber("year") ?? today.Year - 1, p.GetAmount("threshold"), ct)),
            "1099-detail" => await Get<ActivityReports>().Detail1099Async(p.GetNumber("year") ?? today.Year - 1, ct),
            "sales-tax" => SalesTax(company, await Get<SalesTaxReports>().LiabilityAsync(from, range.To, ct)),
            "tax-summary" => await Get<StatementReports>().TaxSummaryTableAsync(from, range.To, basis, ct),
            "budget-vs-actual" => await BudgetVsActualAsync(p, basis, ct),
            _ => throw new DomainException("report.unknown", "Unknown report."),
        };
    }

    private static Comparison Compare(ReportParameters p, ReportDefinition definition)
    {
        var comparison = p.GetEnum<Comparison>("compare") ?? Comparison.None;
        return definition.Comparisons?.Contains(comparison) == true ? comparison : Comparison.None;
    }

    private async Task<ReportTable> BudgetVsActualAsync(ReportParameters p, ReportBasis basis, CancellationToken ct)
    {
        var budgetId = p.GetId("budgetId");
        if (budgetId is null)
        {
            // Default to the budget for the current fiscal year, else the latest one.
            var budgets = await services.GetRequiredService<BudgetService>().ListAsync(ct);
            var today = DateOnly.FromDateTime(DateTime.Today);
            budgetId = (budgets.FirstOrDefault(b => b.FiscalYearStart <= today && today <= b.FiscalYearEnd) ?? (budgets.Count > 0 ? budgets[0] : null))?.Id
                ?? throw new DomainException("budget.none", "There are no budgets yet. Create one under Reports › Budgets.");
        }

        return await services.GetRequiredService<StatementReports>().BudgetVsActualAsync(
            budgetId.Value, p.GetNumber("fromMonth") ?? 1, p.GetNumber("toMonth") ?? Domain.Reporting.Budget.Periods, basis, ct);
    }

    private static string AsOf(DateOnly date) => $"As of {date:MMM d, yyyy}";

    private static string Period(DateRange range) => range.From is { } f ? Comparisons.Describe(f, range.To) : $"All dates to {range.To:MMM d, yyyy}";

    private static ReportTable TrialBalance(Company company, TrialBalance tb) => new(
        "Trial balance",
        [company.Name, $"{AsOf(tb.AsOf)} · {StatementReports.Describe(tb.Basis)}"],
        [new("Account"), new("Debit", ColumnFormat.Money), new("Credit", ColumnFormat.Money)],
        [
            .. tb.Lines.Select(l => new ReportTableRow(
            [
                new ReportCell(l.Number is { } n && company.UseAccountNumbers ? $"{n} {l.FullName}" : l.FullName, new ReportLink("account", l.AccountId) { To = tb.AsOf }),
                new ReportCell(l.Debit.Amount == 0m ? null : l.Debit.Amount),
                new ReportCell(l.Credit.Amount == 0m ? null : l.Credit.Amount),
            ])),
            new ReportTableRow(["Total", tb.TotalDebits.Amount, tb.TotalCredits.Amount], 0, ReportRowStyle.Total),
        ],
        tb.Basis);

    private static ReportTable GeneralLedger(Company company, DateRange range, GeneralLedger gl)
    {
        var rows = new List<ReportTableRow>();
        foreach (var account in gl.Accounts)
        {
            rows.Add(new ReportTableRow(
                [new ReportCell(account.FullName, new ReportLink("account", account.Account.Id) { From = range.From, To = range.To }), .. Enumerable.Repeat(ReportCell.Empty, 6)],
                0, ReportRowStyle.Section));
            if (range.From is { } from)
                rows.Add(new ReportTableRow(["Opening balance", new ReportCell(from), null, null, null, null, account.OpeningBalance], 1));
            rows.AddRange(account.Lines.Select(l => new ReportTableRow(
            [
                new ReportCell(l.Line.EntryMemo ?? l.Line.LineMemo ?? "", new ReportLink("entry", l.Line.JournalEntryId) { Source = l.Line.Source, SourceId = l.Line.SourceId }),
                new ReportCell(l.Line.Date),
                new ReportCell((int)l.Line.EntryNumber),
                new ReportCell(DocumentType(l.Line.Source)),
                new ReportCell(l.Debit == 0m ? null : l.Debit),
                new ReportCell(l.Credit == 0m ? null : l.Credit),
                l.Balance,
            ], 1)));
            rows.Add(new ReportTableRow([$"Total for {account.FullName}", null, null, null, account.TotalDebits, account.TotalCredits, account.ClosingBalance], 0, ReportRowStyle.Subtotal));
        }

        return new ReportTable(
            "General ledger",
            [company.Name, $"{Period(range)} · {StatementReports.Describe(gl.Basis)}"],
            [new("Account and description"), new("Date", ColumnFormat.Date), new("No.", ColumnFormat.Number), new("Type"), new("Debit", ColumnFormat.Money), new("Credit", ColumnFormat.Money), new("Balance", ColumnFormat.Money)],
            rows,
            gl.Basis,
            "Balances are in each account's normal direction: debits for assets and expenses, credits for the rest.");
    }

    private async Task<ReportTable> JournalAsync(Company company, DateRange range, CancellationToken ct)
    {
        const int maxEntries = 500;
        var chart = await services.GetRequiredService<Accounts.AccountService>().GetChartAsync(ct);
        var (entries, total) = await services.GetRequiredService<PostingEngine>().ListAsync(new JournalEntryFilter(range.From, range.To, 0, maxEntries), ct);
        var rows = new List<ReportTableRow>();
        foreach (var entry in entries.OrderBy(e => e.Date).ThenBy(e => e.Number))
        {
            var first = true;
            foreach (var line in entry.Lines.OrderBy(l => l.LineNumber))
            {
                rows.Add(new ReportTableRow(
                [
                    new ReportCell(chart.Find(line.AccountId) is { } a ? chart.FullName(a.Id) : "", new ReportLink("account", line.AccountId) { From = range.From, To = range.To }),
                    new ReportCell(first ? entry.Date : null),
                    new ReportCell(first ? (int)entry.Number : null, first ? new ReportLink("entry", entry.Id) { Source = entry.Source, SourceId = entry.SourceId } : null),
                    first ? DocumentType(entry.Source) + (entry.Status == JournalEntryStatus.Voided ? " (voided)" : "") : null,
                    first ? entry.Memo : line.Memo,
                    new ReportCell(line.Debit == 0m ? null : line.Debit),
                    new ReportCell(line.Credit == 0m ? null : line.Credit),
                ], 0, first ? ReportRowStyle.Normal : ReportRowStyle.Normal));
                first = false;
            }
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, null, entries.Sum(e => e.TotalDebits.Amount), entries.Sum(e => e.TotalCredits.Amount)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Journal",
            [company.Name, $"{Period(range)} · every entry as posted"],
            [new("Account"), new("Date", ColumnFormat.Date), new("No.", ColumnFormat.Number), new("Type"), new("Memo"), new("Debit", ColumnFormat.Money), new("Credit", ColumnFormat.Money)],
            rows,
            Note: total > entries.Count ? $"Only the first {entries.Count} of {total} entries are included. Choose a shorter period." : null);
    }

    private static readonly ReportColumn[] AgingColumns =
    [
        new("Current", ColumnFormat.Money), new("1–30", ColumnFormat.Money), new("31–60", ColumnFormat.Money),
        new("61–90", ColumnFormat.Money), new("Over 90", ColumnFormat.Money), new("Total", ColumnFormat.Money),
    ];

    private static ReportCell[] Buckets(AgingBuckets b) =>
        [.. new[] { b.Current, b.Days1To30, b.Days31To60, b.Days61To90, b.Over90 }.Select(x => new ReportCell(x == 0m ? null : x)), b.Total];

    private static ReportTable ReceivablesAging(Company company, AgingReport aging, bool detail)
    {
        var rows = new List<ReportTableRow>();
        foreach (var c in aging.Customers)
        {
            var name = new ReportCell(c.Customer.DisplayName, new ReportLink("customer", c.Customer.Id));
            if (!detail)
            {
                rows.Add(new ReportTableRow([name, .. Buckets(c.Buckets)]));
                continue;
            }

            rows.Add(new ReportTableRow([name, .. Enumerable.Repeat(ReportCell.Empty, 8)], 0, ReportRowStyle.Section));
            rows.AddRange(c.Invoices.Select(o => new ReportTableRow(
                [new ReportCell($"Invoice {o.Invoice.Number}", new ReportLink("invoice", o.Invoice.Id)), new ReportCell(o.Invoice.DueDate), new ReportCell(o.DaysPastDue > 0 ? o.DaysPastDue : null),
                 .. Buckets(AgingBuckets.Zero.Add(o.DaysPastDue, o.Balance))], 1)));
            rows.AddRange(c.Credits.Select(cr => new ReportTableRow(
                [new ReportCell(cr.Label, new ReportLink(cr.Kind == CreditKind.CreditMemo ? "credit_memo" : "payment", cr.Id)), new ReportCell(cr.Date), ReportCell.Empty,
                 .. Buckets(AgingBuckets.Zero with { Current = -cr.Amount })], 1)));
            rows.Add(new ReportTableRow([$"Total {c.Customer.DisplayName}", ReportCell.Empty, ReportCell.Empty, .. Buckets(c.Buckets)], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", .. detail ? new[] { ReportCell.Empty, ReportCell.Empty } : [], .. Buckets(aging.Totals)], 0, ReportRowStyle.Total));
        return new ReportTable(
            $"Accounts receivable aging {(detail ? "detail" : "summary")}",
            [company.Name, $"{AsOf(aging.AsOf)} · days past each invoice's due date"],
            [new("Customer"), .. detail ? new ReportColumn[] { new("Due", ColumnFormat.Date), new("Days past due", ColumnFormat.Number) } : [], .. AgingColumns],
            rows);
    }

    private static ReportTable PayablesAging(Company company, PayablesAgingReport aging, bool detail)
    {
        var rows = new List<ReportTableRow>();
        foreach (var v in aging.Vendors)
        {
            var name = new ReportCell(v.Vendor.DisplayName, new ReportLink("vendor", v.Vendor.Id));
            if (!detail)
            {
                rows.Add(new ReportTableRow([name, .. Buckets(v.Buckets)]));
                continue;
            }

            rows.Add(new ReportTableRow([name, .. Enumerable.Repeat(ReportCell.Empty, 8)], 0, ReportRowStyle.Section));
            rows.AddRange(v.Bills.Select(o => new ReportTableRow(
                [new ReportCell(o.Bill.Reference is { } r ? $"Bill {r}" : "Bill", new ReportLink("bill", o.Bill.Id)), new ReportCell(o.Bill.DueDate), new ReportCell(o.DaysPastDue > 0 ? o.DaysPastDue : null),
                 .. Buckets(AgingBuckets.Zero.Add(o.DaysPastDue, o.Balance))], 1)));
            rows.AddRange(v.Credits.Select(cr => new ReportTableRow(
                [new ReportCell(cr.Credit.Reference is { } r ? $"Vendor credit {r}" : "Vendor credit", new ReportLink("vendor_credit", cr.Credit.Id)), new ReportCell(cr.Credit.Date), ReportCell.Empty,
                 .. Buckets(AgingBuckets.Zero with { Current = -cr.Amount })], 1)));
            rows.Add(new ReportTableRow([$"Total {v.Vendor.DisplayName}", ReportCell.Empty, ReportCell.Empty, .. Buckets(v.Buckets)], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", .. detail ? new[] { ReportCell.Empty, ReportCell.Empty } : [], .. Buckets(aging.Totals)], 0, ReportRowStyle.Total));
        return new ReportTable(
            $"Accounts payable aging {(detail ? "detail" : "summary")}",
            [company.Name, $"{AsOf(aging.AsOf)} · days past each bill's due date"],
            [new("Vendor"), .. detail ? new ReportColumn[] { new("Due", ColumnFormat.Date), new("Days past due", ColumnFormat.Number) } : [], .. AgingColumns],
            rows);
    }

    private static ReportTable Summary1099(Company company, Report1099 report) => new(
        $"1099 summary for {report.Year}",
        [company.Name, $"Vendors tracked for 1099s · threshold {report.Threshold:N2}"],
        [new("Vendor"), new("Tax ID"), new("Form and box"), new("Paid by cash, check or bank", ColumnFormat.Money), new("Paid by card (1099-K)", ColumnFormat.Money), new("File a 1099?")],
        [
            .. report.Vendors.Select(v => new ReportTableRow(
            [
                new ReportCell(v.Vendor.DisplayName, new ReportLink("vendor", v.Vendor.Id)),
                v.Vendor.MaskedTaxId ?? "Missing",
                v.Vendor.Form1099Box.ToString(),
                v.Total,
                new ReportCell(v.CardPayments == 0m ? null : v.CardPayments),
                v.MeetsThreshold ? v.Vendor.TaxId is null ? "Yes, tax ID needed" : "Yes" : "Below threshold",
            ])),
            new ReportTableRow(["Total reportable", null, null, report.Vendors.Where(v => v.MeetsThreshold).Sum(v => v.Total), null, null], 0, ReportRowStyle.Total),
        ],
        Note: "Counts bill payments and expenses dated in the year. Card payments are left out (Form 1099-K). Check with your tax adviser before filing.");

    private static ReportTable SalesTax(Company company, TaxLiabilityReport report)
    {
        var rows = new List<ReportTableRow>();
        foreach (var agency in report.Agencies)
        {
            rows.Add(new ReportTableRow([new ReportCell(agency.Agency.Name, new ReportLink("tax_agency", agency.Agency.Id)), .. Enumerable.Repeat(ReportCell.Empty, 6)], 0, ReportRowStyle.Section));
            rows.AddRange(agency.Rates.Select(r => new ReportTableRow(
                [r.RateName, r.TotalSales, r.NonTaxable, r.Exempt, r.Taxable, new ReportCell(r.Percent), r.Tax], 1)));
            rows.Add(new ReportTableRow(["Tax charged", null, null, null, null, null, agency.Tax], 0, ReportRowStyle.Subtotal));
            rows.Add(new ReportTableRow(["Owed at the start of the period", null, null, null, null, null, agency.OwedAtStart], 1));
            rows.Add(new ReportTableRow(["Paid in the period (incl. adjustments)", null, null, null, null, null, -agency.Paid], 1));
            rows.Add(new ReportTableRow(
                [$"Owed at {report.To:MMM d, yyyy}", null, null, null, null, null,
                 new ReportCell(agency.OwedAtEnd, new ReportLink("account", agency.Agency.LiabilityAccountId) { From = report.From, To = report.To })],
                0, ReportRowStyle.Total));
        }

        if (report.UntaxedSales != 0m)
            rows.Add(Row.Of("Sales with no tax code", report.UntaxedSales));
        return new ReportTable(
            "Sales tax liability",
            [company.Name, $"{Comparisons.Describe(report.From, report.To)} · Accrual basis"],
            [new("Agency and rate"), new("Total sales", ColumnFormat.Money), new("Non-taxable", ColumnFormat.Money), new("Exempt", ColumnFormat.Money),
             new("Taxable", ColumnFormat.Money), new("Rate", ColumnFormat.Percent), new("Tax", ColumnFormat.Money)],
            rows,
            ReportBasis.Accrual,
            "From invoices, sales receipts and credit memos (subtracted) dated in the period. Check the figures against your return before filing.");
    }

    /// <summary>"Invoice", "Bill payment", ... or "Journal entry".</summary>
    public static string DocumentType(string source) => source switch
    {
        Domain.Sales.Invoice.Source => "Invoice",
        Domain.Sales.CustomerPayment.Source => "Payment",
        Domain.Sales.SalesReceipt.Source => "Sales receipt",
        Domain.Sales.CreditMemo.Source => "Credit memo",
        Domain.Sales.CustomerRefund.Source => "Refund",
        Domain.Sales.BankDeposit.Source => "Deposit",
        Domain.Purchases.Bill.Source => "Bill",
        Domain.Purchases.BillPayment.Source => "Bill payment",
        Domain.Purchases.VendorCredit.Source => "Vendor credit",
        Domain.Purchases.VendorRefund.Source => "Vendor refund",
        Domain.SalesTax.SalesTaxPayment.Source => "Sales tax payment",
        Domain.Inventory.InventoryAdjustment.Source => "Inventory adjustment",
        Domain.Banking.BankTransaction.SpendSource => "Expense",
        Domain.Banking.BankTransaction.ReceiveSource => "Deposit",
        Domain.Banking.BankTransaction.TransferSource => "Transfer",
        _ => "Journal entry",
    };
}
