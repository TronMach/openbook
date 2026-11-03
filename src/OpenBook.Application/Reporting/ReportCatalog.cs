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

    /// <summary>classId, locationId, tagId: only those lines ("none" for lines without a class or location).</summary>
    Segments = 1024,

    /// <summary>warehouseId: only stock in that warehouse (blank for all).</summary>
    Warehouse = 2048,
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

    /// <summary>classId, locationId and tagId; "none" for a class or location means lines without one.</summary>
    public SegmentFilter Segments()
    {
        Guid? Segment(string name) => string.Equals(Get(name), "none", StringComparison.OrdinalIgnoreCase) ? Guid.Empty : GetId(name);
        return new SegmentFilter(Segment("classId"), Segment("locationId"), GetId("tagId"));
    }
}

/// <summary>
/// Every report that can be shown as a table, exported and saved, by key. Report pages and the export
/// endpoint both run reports through here, so a download always matches the screen.
/// </summary>
public sealed class ReportCatalog(IServiceProvider services, ICompanyStore companies, Plugins.PluginRegistry plugins)
{
    private static readonly Comparison[] PeriodComparisons =
        [Comparison.None, Comparison.PreviousPeriod, Comparison.PreviousYear, Comparison.Months, Comparison.Quarters];

    /// <summary>The reports OpenBook itself has; add-ins add more (<see cref="All"/>).</summary>
    public static readonly IReadOnlyList<ReportDefinition> BuiltIn =
    [
        new("profit-and-loss", "Profit and loss", "Income and expenses over a period and the net income they leave. Compare periods, months or quarters.",
            "Financial statements", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Compare | ReportInputs.Segments, Comparisons: PeriodComparisons),
        new("profit-and-loss-by-class", "Profit and loss by class", "Income and expenses with a column for each class, lines without one, and the total.",
            "Financial statements", ReportInputs.Period | ReportInputs.Basis),
        new("profit-and-loss-by-location", "Profit and loss by location", "Income and expenses with a column for each location (or department), and the total.",
            "Financial statements", ReportInputs.Period | ReportInputs.Basis),
        new("balance-sheet", "Balance sheet", "What the business owns and owes on a date, and the owner's equity. Compare with a month or year earlier.",
            "Financial statements", ReportInputs.AsOf | ReportInputs.Basis | ReportInputs.Compare,
            Comparisons: [Comparison.None, Comparison.PreviousPeriod, Comparison.PreviousYear]),
        new("cash-flow", "Statement of cash flows", "Where cash came from and went: operating, investing and financing activities.",
            "Financial statements", ReportInputs.Period | ReportInputs.Basis),
        new("builder", "Custom transaction report", "Build your own: choose the columns, filter by account, type, name, memo or amount, and group the lines.",
            "Custom", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Segments, DatePreset.ThisMonth),
        new("kpis", "Key performance indicators", "Cash, receivables, payables, margins, ratios and net income by month, as of a date.",
            "Financial statements", ReportInputs.AsOf),
        new("cash-forecast", "Cash-flow forecast", "Cash in the bank week by week: open invoices and bills on their due dates, loan payments and a scenario.",
            "Financial statements", ReportInputs.None),
        new("trial-balance-worksheet", "Trial balance worksheet", "Balances before adjusting entries, the adjustments, and after: the accountant's worksheet.",
            "Accountant", ReportInputs.AsOf | ReportInputs.Basis),
        new("adjusting-entries", "Adjusting journal entries", "The journal entries marked as adjusting entries, with their lines.",
            "Accountant", ReportInputs.Period, DatePreset.ThisFiscalYearToDate),
        new("account-list", "Account list", "Every account with its number, type, description, tax line and balance. Detail adds inactive accounts.",
            "Accountant", ReportInputs.AsOf | ReportInputs.Detail),
        new("trial-balance", "Trial balance", "Every account's debit or credit balance on a date. Debits always equal credits.",
            "Accountant", ReportInputs.AsOf | ReportInputs.Basis),
        new("general-ledger", "General ledger", "Every transaction in each account, with opening and running balances.",
            "Accountant", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Account | ReportInputs.Segments, DatePreset.ThisMonth),
        new("journal", "Journal", "Each journal entry in a period with all its debits and credits.",
            "Accountant", ReportInputs.Period, DatePreset.ThisMonth),
        new("sales-by-customer", "Sales by customer", "Income from each customer over a period. Ties to the profit and loss.",
            "Sales and customers", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Segments),
        new("price-list", "Price list", "Each product and service's list price and what a customer or price level pays, with quantity breaks.",
            "Sales and customers", ReportInputs.AsOf | ReportInputs.PriceFor),
        new("discounts-given", "Discounts given", "What price levels, quantity breaks, rules and manual prices took off list prices, by what set the price.",
            "Sales and customers", ReportInputs.Period | ReportInputs.Detail),
        new("sales-by-item", "Sales by product or service", "Quantity sold, amount and average price of each product and service.",
            "Sales and customers", ReportInputs.Period),
        new("open-invoices", "Open invoices", "Every unpaid invoice on a date, by customer, with what's still owed.",
            "Sales and customers", ReportInputs.AsOf),
        new("customer-balances", "Customer balance summary", "What each customer owes on a date, and how much of it is past due.",
            "Sales and customers", ReportInputs.AsOf),
        new("customer-contacts", "Customer contact list", "Each customer's company, email, phone, addresses, terms and balance. Detail adds inactive customers.",
            "Sales and customers", ReportInputs.Detail),
        new("open-sales-orders", "Open sales orders", "Sales orders not fully invoiced, by customer, with what's still to invoice.",
            "Sales and customers", ReportInputs.None),
        new("estimates-by-customer", "Estimates by customer", "Estimates dated in a period, by customer, with their status: pending, accepted, declined or invoiced.",
            "Sales and customers", ReportInputs.Period),
        new("deposit-detail", "Deposit detail", "Each bank deposit with the payments and receipts in it, the way the bank shows one total.",
            "Sales and customers", ReportInputs.Period, DatePreset.ThisMonth),
        new("product-list", "Product and service list", "Every product and service with its type, prices, accounts, preferred vendor and stock. Detail adds inactive items.",
            "Sales and customers", ReportInputs.Detail),
        new("ar-aging", "A/R aging", "What each customer owes, by how long past due. Summary or invoice by invoice.",
            "Sales and customers", ReportInputs.AsOf | ReportInputs.Detail),
        new("expenses-by-vendor", "Expenses by vendor", "What you spent with each vendor over a period. Ties to the profit and loss.",
            "Expenses and vendors", ReportInputs.Period | ReportInputs.Basis | ReportInputs.Segments),
        new("unpaid-bills", "Unpaid bills", "Every unpaid bill on a date, by vendor, with what's still owed.",
            "Expenses and vendors", ReportInputs.AsOf),
        new("vendor-balances", "Vendor balance summary", "What you owe each vendor on a date, and how much of it is past due.",
            "Expenses and vendors", ReportInputs.AsOf),
        new("vendor-contacts", "Vendor contact list", "Each vendor's company, email, phone, address, your account number and terms. Detail adds inactive vendors.",
            "Expenses and vendors", ReportInputs.Detail),
        new("open-purchase-orders", "Open purchase orders", "Purchase orders not fully billed, by vendor, with units still to receive and what's still to bill.",
            "Expenses and vendors", ReportInputs.None),
        new("purchases-by-item", "Purchases by product", "Inventory bought in a period: quantity, amount and average cost per item. Detail lists each bill.",
            "Inventory", ReportInputs.Period | ReportInputs.Detail),
        new("ap-aging", "A/P aging", "What you owe each vendor, by how long past due. Summary or bill by bill.",
            "Expenses and vendors", ReportInputs.AsOf | ReportInputs.Detail),
        new("inventory-valuation", "Inventory valuation", "Quantity on hand and FIFO cost of each inventory item on a date. Summary or layer by layer.",
            "Inventory", ReportInputs.AsOf | ReportInputs.Detail | ReportInputs.Warehouse),
        new("inventory-stock", "Inventory stock status", "What's on hand against each item's reorder point, with what to reorder first.",
            "Inventory", ReportInputs.Warehouse),
        new("physical-inventory-worksheet", "Physical inventory worksheet", "A count sheet: each item's quantity on hand in a warehouse, with blank columns for what's counted.",
            "Inventory", ReportInputs.Warehouse),
        new("1099", "1099 summary", "What you paid each 1099 vendor in a year, and who needs a form.",
            "Taxes", ReportInputs.Year | ReportInputs.Threshold),
        new("1099-detail", "1099 detail", "Each payment behind the 1099 summary, by vendor.",
            "Taxes", ReportInputs.Year),
        new("sales-tax", "Sales tax liability", "Taxable, non-taxable and exempt sales and the tax charged, by agency and rate, with what you owe.",
            "Taxes", ReportInputs.Period, DatePreset.LastQuarter),
        new("tax-summary", "Tax summary", "Income and expenses totalled by the lines of your income tax return.",
            "Taxes", ReportInputs.Period | ReportInputs.Basis, DatePreset.LastFiscalYear),
        new("project-profitability", "Project profitability", "Each project's income, costs (expenses, labor, mileage) and profit, with its cost budget and what's not billed yet.",
            "Projects and time", ReportInputs.Period),
        new("time-by-customer", "Time by customer", "Hours worked for each customer and project, by whom, and whether they're billed.",
            "Projects and time", ReportInputs.Period, DatePreset.ThisMonth),
        new("mileage-log", "Mileage log", "Business trips in a year with miles driven and their value at the standard mileage rate.",
            "Projects and time", ReportInputs.Year),
        new("channel-sales", "Sales by channel", "What each online channel's posted payouts came to: sales, shipping, refunds, fees and sales tax.",
            "Sales and customers", ReportInputs.Period, DatePreset.ThisMonth),
        new("channel-sales-tax", "Sales tax by state (online orders)", "Sales, shipping and tax charged on channel orders by ship-to state: what you collected apart from what marketplaces collected.",
            "Taxes", ReportInputs.Period, DatePreset.LastQuarter),
        new("unbilled-work", "Unbilled work", "Billable time, mileage and expenses not invoiced yet, by client and project, with the oldest work waiting.",
            "Projects and time", ReportInputs.AsOf | ReportInputs.Detail),
        new("utilization", "Utilization", "Each person's billable and other hours, against the hours they're available, with what the billable time is worth.",
            "Projects and time", ReportInputs.Period, DatePreset.LastMonth),
        new("fixed-assets", "Fixed asset register", "Each fixed asset's cost, accumulated depreciation and book value on a date.",
            "Fixed assets and loans", ReportInputs.AsOf),
        new("depreciation-schedule", "Depreciation schedule", "Each asset's scheduled depreciation for a year, by quarter.",
            "Fixed assets and loans", ReportInputs.Year),
        new("loans", "Loans", "Every loan's rate, payment, next payment, interest paid and balance.",
            "Fixed assets and loans", ReportInputs.None),
        new("statement-of-activities", "Statement of activities", "Revenue, support and expenses over a period, without and with donor restrictions, and the net assets they leave.",
            "Nonprofit", ReportInputs.Period | ReportInputs.Basis),
        new("statement-of-financial-position", "Statement of financial position", "Assets, liabilities and net assets (without and with donor restrictions) on a date.",
            "Nonprofit", ReportInputs.AsOf | ReportInputs.Basis),
        new("functional-expenses", "Statement of functional expenses", "Expenses by kind and by function: each program, management and general, and fundraising.",
            "Nonprofit", ReportInputs.Period | ReportInputs.Basis),
        new("restricted-net-assets", "Restricted net assets by purpose", "What each purpose holds with donor restrictions, what's been spent on it and what's ready to release.",
            "Nonprofit", ReportInputs.AsOf),
        new("giving-by-donor", "Giving by donor", "Each donor's gifts in a period: how many, the total, the largest and the latest.",
            "Nonprofit", ReportInputs.Period | ReportInputs.Detail),
        new("lapsed-donors", "Lapsed donors", "Donors who gave last year (or earlier) but not yet this year, with their latest gift and lifetime giving.",
            "Nonprofit", ReportInputs.Year),
        new("open-pledges", "Open pledges", "Pledges not fully paid on a date: promised, paid and still to come.",
            "Nonprofit", ReportInputs.AsOf),
        new("grants", "Grants", "Every grant: awarded, earned as income, paid by the funder, spent and what's left to spend.",
            "Nonprofit", ReportInputs.AsOf),
        new("form-990-expenses", "Form 990 Part IX expenses", "Expenses by Form 990 line, split into program services, management and general, and fundraising.",
            "Nonprofit", ReportInputs.Period | ReportInputs.Basis, DatePreset.LastFiscalYear),
        new("budget-vs-actual", "Budget vs. actual", "Actual income and expenses beside a budget, with what's over or under.",
            "Budgets", ReportInputs.Budget | ReportInputs.Basis),
    ];

    public static ReportDefinition? FindBuiltIn(string? key) => BuiltIn.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every report: the built-in ones, then the add-ins'.</summary>
    public IReadOnlyList<ReportDefinition> All => [.. BuiltIn, .. plugins.Reports.Select(r => r.Definition)];

    public ReportDefinition? Find(string? key) => FindBuiltIn(key) ?? plugins.Report(key)?.Definition;

    public async Task<ReportTable> RunAsync(string key, ReportParameters p, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(p);
        var definition = Find(key) ?? throw new DomainException("report.unknown", "Unknown report.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var today = CompanyTime.Today;
        var (_, range) = p.Period(company, definition.DefaultPreset, today);
        var from = range.From ?? DateRanges.Beginning;
        var asOf = p.GetDate("asOf") ?? today;
        var basis = p.Basis(company);
        var segments = definition.Inputs.HasFlag(ReportInputs.Segments) ? p.Segments() : null;
        T Get<T>() where T : notnull => services.GetRequiredService<T>();

        return definition.Key switch
        {
            "profit-and-loss" => await Get<StatementReports>().ProfitAndLossAsync(from, range.To, basis, Compare(p, definition), segments, ct),
            "profit-and-loss-by-class" => await Get<StatementReports>().ProfitAndLossBySegmentAsync(from, range.To, Domain.Segments.SegmentKind.Class, basis, ct),
            "profit-and-loss-by-location" => await Get<StatementReports>().ProfitAndLossBySegmentAsync(from, range.To, Domain.Segments.SegmentKind.Location, basis, ct),
            "balance-sheet" => await Get<StatementReports>().BalanceSheetAsync(asOf, basis, Compare(p, definition), ct),
            "cash-flow" => await Get<StatementReports>().CashFlowTableAsync(from, range.To, basis, ct),
            "trial-balance" => TrialBalance(company, await Get<TrialBalanceQuery>().RunAsync(asOf, basis, ct)),
            "general-ledger" => GeneralLedger(company, range, await Get<FinancialReports>().GeneralLedgerAsync(range.From, range.To, basis, p.GetId("accountId"), segments, ct)),
            "journal" => await JournalAsync(company, range, ct),
            "sales-by-customer" => await Get<ActivityReports>().SalesByCustomerAsync(from, range.To, basis, segments, ct),
            "sales-by-item" => await Get<ActivityReports>().SalesByItemAsync(from, range.To, ct),
            "price-list" => await Get<PricingReports>().PriceListAsync(p.GetId("customerId"), p.GetId("levelId"), asOf, ct),
            "discounts-given" => await Get<PricingReports>().DiscountsGivenAsync(from, range.To, p.GetFlag("detail"), ct),
            "open-invoices" => await Get<ActivityReports>().OpenInvoicesAsync(asOf, ct),
            "customer-balances" => await Get<ListReports>().CustomerBalancesAsync(asOf, ct),
            "customer-contacts" => await Get<ListReports>().CustomerContactsAsync(p.GetFlag("detail"), ct),
            "open-sales-orders" => await Get<ListReports>().OpenSalesOrdersAsync(ct),
            "estimates-by-customer" => await Get<ListReports>().EstimatesByCustomerAsync(from, range.To, ct),
            "deposit-detail" => await Get<ListReports>().DepositDetailAsync(from, range.To, ct),
            "product-list" => await Get<ListReports>().ItemListAsync(p.GetFlag("detail"), ct),
            "vendor-balances" => await Get<ListReports>().VendorBalancesAsync(asOf, ct),
            "vendor-contacts" => await Get<ListReports>().VendorContactsAsync(p.GetFlag("detail"), ct),
            "open-purchase-orders" => await Get<ListReports>().OpenPurchaseOrdersAsync(ct),
            "purchases-by-item" => await Get<ListReports>().PurchasesByItemAsync(from, range.To, p.GetFlag("detail"), ct),
            "account-list" => await Get<ListReports>().AccountListAsync(asOf, p.GetFlag("detail"), ct),
            "ar-aging" => ReceivablesAging(company, await Get<ReceivablesService>().AgingAsync(asOf, ct), p.GetFlag("detail")),
            "expenses-by-vendor" => await Get<ActivityReports>().ExpensesByVendorAsync(from, range.To, basis, segments, ct),
            "unpaid-bills" => await Get<ActivityReports>().UnpaidBillsAsync(asOf, ct),
            "ap-aging" => PayablesAging(company, await Get<PayablesService>().AgingAsync(asOf, ct), p.GetFlag("detail")),
            "inventory-valuation" => await Get<InventoryReports>().ValuationAsync(asOf, p.GetFlag("detail"), p.GetId("warehouseId"), ct),
            "inventory-stock" => await Get<InventoryReports>().StockStatusAsync(p.GetId("warehouseId"), ct),
            "physical-inventory-worksheet" => await Get<InventoryReports>().CountSheetAsync(p.GetId("warehouseId"), ct),
            "1099" => Summary1099(company, await Get<PayablesService>().Report1099Async(p.GetNumber("year") ?? today.Year - 1, p.GetAmount("threshold"), ct)),
            "1099-detail" => await Get<ActivityReports>().Detail1099Async(p.GetNumber("year") ?? today.Year - 1, ct),
            "sales-tax" => SalesTax(company, await Get<SalesTaxReports>().LiabilityAsync(from, range.To, ct)),
            "tax-summary" => await Get<StatementReports>().TaxSummaryTableAsync(from, range.To, basis, ct),
            "budget-vs-actual" => await BudgetVsActualAsync(p, basis, ct),
            "builder" => await Get<ReportBuilder>().RunAsync(from, range.To, basis, segments, BuilderOptions.Parse(p), ct),
            "kpis" => await Get<KpiService>().TableAsync(asOf, ct),
            "cash-forecast" => await Get<ForecastService>().TableAsync(p.GetDate("asOf") ?? today, p.GetNumber("weeks") ?? 13, p.GetId("scenarioId"), ct),
            "trial-balance-worksheet" => await Get<AdjustingEntryService>().WorksheetTableAsync(asOf, basis, ct),
            "adjusting-entries" => await Get<AdjustingEntryService>().EntriesTableAsync(range.From, range.To, ct),
            "fixed-assets" => await Get<Assets.AssetReports>().RegisterAsync(asOf, ct),
            "depreciation-schedule" => await Get<Assets.AssetReports>().ScheduleAsync(p.GetNumber("year") ?? today.Year, ct),
            "loans" => await Get<Assets.AssetReports>().LoansAsync(ct),
            "project-profitability" => await Get<Projects.ProjectReports>().ProfitabilityTableAsync(range.From, range.To, ct),
            "time-by-customer" => await Get<Projects.ProjectReports>().TimeDetailAsync(range.From, range.To, ct),
            "mileage-log" => await Get<Projects.ProjectReports>().MileageLogAsync(p.GetNumber("year") ?? today.Year, ct),
            "channel-sales" => await Get<Channels.ChannelSalesReport>().TableAsync(from, range.To, ct),
            "statement-of-activities" => await Get<Nonprofit.NonprofitReports>().ActivitiesAsync(from, range.To, basis, ct),
            "statement-of-financial-position" => await Get<Nonprofit.NonprofitReports>().FinancialPositionAsync(asOf, basis, ct),
            "functional-expenses" => await Get<Nonprofit.NonprofitReports>().FunctionalExpensesAsync(from, range.To, basis, ct),
            "restricted-net-assets" => await Get<Nonprofit.NonprofitReports>().RestrictedNetAssetsAsync(asOf, ct),
            "giving-by-donor" => await Get<Nonprofit.GiftReports>().GivingByDonorAsync(from, range.To, p.GetFlag("detail"), ct),
            "lapsed-donors" => await Get<Nonprofit.GiftReports>().LapsedDonorsAsync(p.GetNumber("year") ?? today.Year, ct),
            "open-pledges" => await Get<Nonprofit.GiftReports>().PledgesAsync(asOf, ct),
            "grants" => await Get<Nonprofit.NonprofitReports>().GrantsAsync(asOf, ct),
            "form-990-expenses" => await Get<Nonprofit.NonprofitReports>().Form990ExpensesAsync(from, range.To, basis, ct),
            "channel-sales-tax" => await Get<Channels.ChannelOrderService>().TaxByStateAsync(from, range.To, ct),
            "unbilled-work" => await Get<Projects.ProjectReports>().UnbilledWorkAsync(asOf, p.GetFlag("detail"), ct),
            "utilization" => await Get<Projects.ProjectReports>().UtilizationAsync(from, range.To, ct),
            _ => plugins.Report(definition.Key) is { } plugin
                ? await plugin.Run(services, p, ct)
                : throw new DomainException("report.unknown", "Unknown report."),
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
            var today = CompanyTime.Today;
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
        Domain.Sales.BadDebtWriteOff.Source => "Bad debt write-off",
        Domain.Sales.Retainer.Source => "Retainer",
        Domain.Sales.Retainer.ApplicationSource => "Retainer applied",
        Domain.Sales.Retainer.RefundSource => "Retainer refund",
        Domain.Inventory.AssemblyBuild.Source => "Assembly build",
        Domain.Inventory.LandedCost.Source => "Landed cost",
        Domain.Assets.DepreciationRun.Source => "Depreciation",
        Domain.Nonprofit.RestrictionRelease.Source => "Release from restriction",
        Domain.Nonprofit.Donation.Source => "Gift",
        Domain.Nonprofit.Pledge.Source => "Pledge",
        Domain.Nonprofit.Pledge.CancelSource => "Pledge cancelled",
        Domain.Nonprofit.Grant.AwardSource => "Grant award",
        Domain.Nonprofit.Grant.ReceiptSource => "Grant payment",
        Domain.Nonprofit.Grant.RecognitionSource => "Grant income earned",
        Domain.Channels.ChannelPayout.Source => "Channel payout",
        Domain.Channels.ChannelOrderBatch.Source => "Channel orders",
        Domain.Channels.ChannelReturn.Source => "Channel return",
        Domain.Assets.FixedAsset.DisposalSource => "Asset disposal",
        Domain.Assets.Loan.Source => "Loan",
        Domain.Assets.Loan.PaymentSource => "Loan payment",
        _ => "Journal entry",
    };
}
