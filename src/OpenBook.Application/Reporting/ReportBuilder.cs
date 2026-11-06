using OpenBook.Domain.CustomFields;
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

public enum BuilderColumn
{
    Date,
    Number,
    Type,
    Name,
    Account,
    Memo,
    Class,
    Location,
    Debit,
    Credit,
    Amount,
    Balance,
}

public enum BuilderGrouping
{
    None,
    Account,
    AccountType,
    Name,
    Type,
    Month,
    Class,
    Location,
}

/// <summary>
/// A custom transaction report's settings, read from the query string so it can be saved, exported and emailed
/// like any report: <c>columns</c> (comma list of <see cref="BuilderColumn"/>), <c>groupBy</c>, <c>accounts</c>
/// (comma list of ids, subaccounts included), <c>accountClass</c>, <c>types</c> (comma list of document types),
/// <c>name</c> and <c>memo</c> (contains), <c>min</c>/<c>max</c> (absolute amount), <c>title</c>, <c>fields</c> (comma list
/// of custom field ids: customer, vendor or document fields of each line's document, shown before the amounts) and
/// <c>fieldId</c>/<c>fieldValue</c> (only lines whose document or name has that field containing the text).
/// </summary>
public sealed record BuilderOptions(
    IReadOnlyList<BuilderColumn> Columns,
    BuilderGrouping GroupBy = BuilderGrouping.None,
    IReadOnlyList<Guid>? AccountIds = null,
    AccountClass? AccountClass = null,
    IReadOnlyList<string>? Types = null,
    string? NameContains = null,
    string? MemoContains = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null,
    string? Title = null,
    IReadOnlyList<Guid>? FieldIds = null,
    Guid? FieldFilterId = null,
    string? FieldFilterText = null)
{
    public static readonly IReadOnlyList<BuilderColumn> DefaultColumns =
        [BuilderColumn.Date, BuilderColumn.Type, BuilderColumn.Number, BuilderColumn.Name, BuilderColumn.Account, BuilderColumn.Memo, BuilderColumn.Amount];

    public static BuilderOptions Parse(ReportParameters p)
    {
        ArgumentNullException.ThrowIfNull(p);
        static IEnumerable<string> List(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var columns = List(p.Get("columns"))
            .Select(c => Enum.TryParse<BuilderColumn>(c, ignoreCase: true, out var col) && Enum.IsDefined(col) ? col : (BuilderColumn?)null)
            .OfType<BuilderColumn>().Distinct().ToList();
        return new BuilderOptions(
            columns.Count > 0 ? columns : DefaultColumns,
            p.GetEnum<BuilderGrouping>("groupBy") ?? BuilderGrouping.None,
            List(p.Get("accounts")).Select(a => Guid.TryParse(a, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList(),
            p.GetEnum<AccountClass>("accountClass"),
            List(p.Get("types")).ToList(),
            p.Get("name"),
            p.Get("memo"),
            p.GetAmount("min"),
            p.GetAmount("max"),
            p.Get("title"),
            List(p.Get("fields")).Select(f => Guid.TryParse(f, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList(),
            Guid.TryParse(p.Get("fieldId"), out var fieldId) ? fieldId : null,
            p.Get("fieldValue"));
    }
}

/// <summary>
/// The custom report builder: ledger lines (on the chosen basis, through <see cref="ReportLedger"/>, so it ties to
/// the other reports) with the columns, filters and grouping the user picks.
/// </summary>
public sealed class ReportBuilder(
    ReportLedger ledger, AccountService accounts, ISalesStore sales, IPurchasesStore purchases, Segments.SegmentService segments, ICompanyStore companies,
    CustomFields.CustomFieldService customFields, Nonprofit.INonprofitStore nonprofit)
{
    public const int MaxRows = 5000;

    /// <summary>The document types lines can be filtered by, as shown in the Type column.</summary>
    public static IReadOnlyList<string> TypeNames { get; } =
    [
        "Invoice", "Payment", "Sales receipt", "Credit memo", "Refund", "Deposit", "Bill", "Bill payment", "Vendor credit", "Vendor refund",
        "Expense", "Transfer", "Sales tax payment", "Inventory adjustment", "Goods received", "Assembly build", "Landed cost", "Bad debt write-off",
        "Retainer", "Retainer applied", "Retainer refund", "Depreciation", "Asset disposal", "Loan", "Loan payment", "Gift", "Pledge", "Pledge cancelled",
        "Grant award", "Grant payment", "Grant income earned", "Release from restriction", "Channel payout", "Channel orders", "Channel return", "Journal entry",
    ];

    public static string Header(BuilderColumn column) => column switch
    {
        BuilderColumn.Number => "No.",
        BuilderColumn.Name => "Name",
        _ => column.ToString(),
    };

    public async Task<ReportTable> RunAsync(DateOnly from, DateOnly to, ReportBasis basis, SegmentFilter? filter, BuilderOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var names = await NamesAsync(ct);
        var classes = (await segments.ListAsync(Domain.Segments.SegmentKind.Class, ct: ct)).ToDictionary(s => s.Segment.Id, s => s.FullName);
        var locations = (await segments.ListAsync(Domain.Segments.SegmentKind.Location, ct: ct)).ToDictionary(s => s.Segment.Id, s => s.FullName);

        // Custom fields of each line's customer or vendor, or of its document.
        var reportable = (await customFields.ListAsync(ct: ct)).Where(f => f.Target != CustomFieldTarget.Item).ToDictionary(f => f.Id);
        var fieldColumns = (options.FieldIds ?? []).Where(reportable.ContainsKey).Select(id => reportable[id]).ToList();
        var filterField = options.FieldFilterId is { } filterId ? reportable.GetValueOrDefault(filterId) : null;
        var fieldValues = fieldColumns.Count > 0 || filterField is not null
            ? await customFields.ValuesByRecordAsync([.. CustomFields.CustomFieldService.EntityTypes.Keys.Where(k => k != "item")], ct)
            : new Dictionary<Guid, IReadOnlyList<CustomFields.CustomFieldEntry>>();
        string? FieldValue(CustomField field, LedgerLine l)
        {
            Guid? owner = field.Target == CustomFieldTarget.Document ? l.SourceId
                : l.SourceId is { } id && names.TryGetValue((l.Source, id), out var n) ? n.PartyId : null;
            return owner is { } o && fieldValues.TryGetValue(o, out var entries) ? entries.FirstOrDefault(e => e.Field.Id == field.Id)?.Display : null;
        }

        IEnumerable<Guid> Subtree(Guid id) => chart.ChildrenOf(id).SelectMany(c => Subtree(c.Id)).Prepend(id);
        var accountSet = options.AccountIds is { Count: > 0 } ids
            ? ids.Where(id => chart.Find(id) is not null).SelectMany(Subtree).ToHashSet()
            : null;
        var types = options.Types is { Count: > 0 } t ? t.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var lines = (await ledger.LinesAsync(basis, from, to, filter, ct))
            .Where(l => accountSet is null || accountSet.Contains(l.AccountId))
            .Where(l => options.AccountClass is not { } c || chart.Find(l.AccountId)?.Type.ClassOf() == c)
            .Where(l => types is null || types.Contains(ReportCatalog.DocumentType(l.Source)))
            .Where(l => options.NameContains is not { } n || Name(l, names)?.Contains(n, StringComparison.OrdinalIgnoreCase) == true)
            .Where(l => options.MemoContains is not { } m || (l.LineMemo ?? l.EntryMemo)?.Contains(m, StringComparison.OrdinalIgnoreCase) == true)
            .Where(l => options.MinAmount is not { } min || Math.Abs(l.Amount) >= min)
            .Where(l => options.MaxAmount is not { } max || Math.Abs(l.Amount) <= max)
            .Where(l => filterField is null || FieldValue(filterField, l)?.Contains(options.FieldFilterText ?? "", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(l => l.Date).ThenBy(l => l.EntryNumber)
            .ToList();
        var truncated = lines.Count > MaxRows;
        // Totals count every line found, shown or not, or they'd look like the whole answer and fall short.
        var everyLine = lines;
        if (truncated)
            lines = lines.Take(MaxRows).ToList();

        string AccountName(Guid id) => chart.Find(id) is { } a
            ? (company.UseAccountNumbers && a.Number is { } n ? $"{n} {chart.FullName(id)}" : chart.FullName(id))
            : "";
        string Group(LedgerLine l) => options.GroupBy switch
        {
            BuilderGrouping.Account => AccountName(l.AccountId),
            BuilderGrouping.AccountType => chart.Find(l.AccountId)?.Type.ToString() ?? "",
            BuilderGrouping.Name => Name(l, names) ?? "No name",
            BuilderGrouping.Type => ReportCatalog.DocumentType(l.Source),
            BuilderGrouping.Month => l.Date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            BuilderGrouping.Class => l.ClassId is { } c ? classes.GetValueOrDefault(c, "") : "Not specified",
            BuilderGrouping.Location => l.LocationId is { } loc ? locations.GetValueOrDefault(loc, "") : "Not specified",
            _ => "",
        };

        // Custom field columns go before the first amount column.
        var standard = options.Columns;
        var firstMoney = standard.ToList().FindIndex(c => c is BuilderColumn.Debit or BuilderColumn.Credit or BuilderColumn.Amount or BuilderColumn.Balance);
        var at = firstMoney < 0 ? standard.Count : firstMoney;
        List<(BuilderColumn? Column, CustomField? Field)> columns =
            [.. standard.Take(at).Select(c => ((BuilderColumn?)c, (CustomField?)null)), .. fieldColumns.Select(f => ((BuilderColumn?)null, (CustomField?)f)),
             .. standard.Skip(at).Select(c => ((BuilderColumn?)c, (CustomField?)null))];
        ReportCell Any((BuilderColumn? Column, CustomField? Field) column, LedgerLine l, decimal balance) =>
            column.Field is { } field ? FieldValue(field, l) : Cell(column.Column!.Value, l, balance);
        ReportCell Cell(BuilderColumn column, LedgerLine l, decimal balance) => column switch
        {
            BuilderColumn.Date => new ReportCell(l.Date),
            BuilderColumn.Number => new ReportCell((int)l.EntryNumber, new ReportLink("entry", l.JournalEntryId) { Source = l.Source, SourceId = l.SourceId }),
            BuilderColumn.Type => new ReportCell(ReportCatalog.DocumentType(l.Source), new ReportLink("entry", l.JournalEntryId) { Source = l.Source, SourceId = l.SourceId }),
            BuilderColumn.Name => Name(l, names),
            BuilderColumn.Account => new ReportCell(AccountName(l.AccountId), new ReportLink("account", l.AccountId) { From = from, To = to }),
            BuilderColumn.Memo => l.LineMemo ?? l.EntryMemo,
            BuilderColumn.Class => l.ClassId is { } c ? classes.GetValueOrDefault(c) : null,
            BuilderColumn.Location => l.LocationId is { } loc ? locations.GetValueOrDefault(loc) : null,
            BuilderColumn.Debit => l.Amount > 0m ? l.Amount : ReportCell.Empty,
            BuilderColumn.Credit => l.Amount < 0m ? -l.Amount : ReportCell.Empty,
            BuilderColumn.Amount => l.Amount,
            _ => balance,
        };
        ReportCell? Total((BuilderColumn? Column, CustomField? Field) column, IReadOnlyList<LedgerLine> group, string label, int index) => column.Column switch
        {
            // The first column carries the label, unless it's a figure: the total matters more, and the section heading names it.
            _ when index == 0 && column.Column is not (BuilderColumn.Debit or BuilderColumn.Credit or BuilderColumn.Amount or BuilderColumn.Balance) => label,
            null => null,
            BuilderColumn.Debit => group.Where(l => l.Amount > 0m).Sum(l => l.Amount),
            BuilderColumn.Credit => -group.Where(l => l.Amount < 0m).Sum(l => l.Amount),
            BuilderColumn.Amount or BuilderColumn.Balance => group.Sum(l => l.Amount),
            _ => null,
        };

        var rows = new List<ReportTableRow>();
        void AddLines(IReadOnlyList<LedgerLine> group, int depth)
        {
            var balance = 0m;
            foreach (var l in group)
            {
                balance += l.Amount;
                rows.Add(new ReportTableRow(columns.Select(c => (ReportCell?)Any(c, l, balance)).ToList(), depth));
            }
        }

        if (options.GroupBy == BuilderGrouping.None)
        {
            AddLines(lines, 0);
        }
        else
        {
            foreach (var group in lines.GroupBy(Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var list = group.ToList();
                var label = group.Key.Length == 0 ? "(none)" : group.Key;
                rows.Add(new ReportTableRow([label, .. Enumerable.Repeat<ReportCell?>(null, columns.Count - 1)], 0, ReportRowStyle.Section));
                AddLines(list, 1);
                var whole = truncated ? everyLine.Where(l => Group(l) == group.Key).ToList() : list;
                rows.Add(new ReportTableRow(columns.Select((c, i) => Total(c, whole, $"Total for {label}", i)).ToList(), 0, ReportRowStyle.Subtotal));
            }
        }

        rows.Add(new ReportTableRow(columns.Select((c, i) => Total(c, everyLine, "Total", i)).ToList(), 0, ReportRowStyle.Total));
        var subtitle = $"{(from == DateRanges.Beginning ? $"All dates to {to:MMM d, yyyy}" : Comparisons.Describe(from, to))} · {StatementReports.Describe(basis)}";
        return new ReportTable(
            options.Title is { Length: > 0 } title ? title : "Transaction report",
            [company.Name, subtitle],
            columns.Select(c => c.Field is { } field ? new ReportColumn(field.Name, ColumnFormat.Text) : new ReportColumn(Header(c.Column!.Value), c.Column switch
            {
                BuilderColumn.Date => ColumnFormat.Date,
                BuilderColumn.Number => ColumnFormat.Number,
                BuilderColumn.Debit or BuilderColumn.Credit or BuilderColumn.Amount or BuilderColumn.Balance => ColumnFormat.Money,
                _ => ColumnFormat.Text,
            })).ToList(),
            rows,
            basis,
            truncated ? $"Only the first {MaxRows:N0} of {everyLine.Count:N0} lines are shown (the oldest), though the totals count them all; narrow the dates or filters." : "Amounts are debits positive, credits negative.");
    }

    private static string? Name(LedgerLine line, IReadOnlyDictionary<(string, Guid), (string Name, Guid? PartyId)> names) =>
        line.SourceId is { } id && names.TryGetValue((line.Source, id), out var name) ? name.Name : null;

    /// <summary>The customer or vendor of each document (name and id; an expense without a vendor has its payee), keyed by source and document id.</summary>
    private async Task<IReadOnlyDictionary<(string, Guid), (string Name, Guid? PartyId)>> NamesAsync(CancellationToken ct)
    {
        var customers = CustomerTree.FullNames(await sales.ListCustomersAsync(ct));
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var names = new Dictionary<(string, Guid), (string Name, Guid? PartyId)>();
        void Customer(string source, Guid id, Guid? customerId)
        {
            if (customerId is { } c && customers.TryGetValue(c, out var n))
                names[(source, id)] = (n, c);
        }

        void Vendor(string source, Guid id, Guid? vendorId)
        {
            if (vendorId is { } v && vendors.TryGetValue(v, out var n))
                names[(source, id)] = (n, v);
        }

        foreach (var i in await sales.ListInvoicesAsync(null, ct))
            Customer(Invoice.Source, i.Id, i.CustomerId);
        foreach (var r in await sales.ListReceiptsAsync(ct))
            Customer(SalesReceipt.Source, r.Id, r.CustomerId);
        foreach (var c in await sales.ListCreditMemosAsync(null, ct))
            Customer(CreditMemo.Source, c.Id, c.CustomerId);
        foreach (var p in await sales.ListPaymentsAsync(null, ct))
            Customer(CustomerPayment.Source, p.Id, p.CustomerId);
        foreach (var r in await sales.ListRefundsAsync(null, ct))
            Customer(CustomerRefund.Source, r.Id, r.CustomerId);
        foreach (var w in await sales.ListWriteOffsAsync(null, ct))
            Customer(BadDebtWriteOff.Source, w.Id, w.CustomerId);
        foreach (var r in await sales.ListRetainersAsync(null, ct))
        {
            Customer(Retainer.Source, r.Id, r.CustomerId);
            Customer(Retainer.ApplicationSource, r.Id, r.CustomerId);
            Customer(Retainer.RefundSource, r.Id, r.CustomerId);
        }

        foreach (var d in await nonprofit.ListDonationsAsync(ct))
            Customer(Domain.Nonprofit.Donation.Source, d.Id, d.DonorId);
        foreach (var p in await nonprofit.ListPledgesAsync(ct))
        {
            Customer(Domain.Nonprofit.Pledge.Source, p.Id, p.DonorId);
            Customer(Domain.Nonprofit.Pledge.CancelSource, p.Id, p.DonorId);
        }

        foreach (var g in await nonprofit.ListGrantsAsync(ct))
        {
            Customer(Domain.Nonprofit.Grant.AwardSource, g.Id, g.FunderId);
            Customer(Domain.Nonprofit.Grant.ReceiptSource, g.Id, g.FunderId);
            Customer(Domain.Nonprofit.Grant.RecognitionSource, g.Id, g.FunderId);
        }

        foreach (var b in await purchases.ListBillsAsync(null, ct))
            Vendor(Bill.Source, b.Id, b.VendorId);
        foreach (var c in await purchases.ListVendorCreditsAsync(null, ct))
            Vendor(VendorCredit.Source, c.Id, c.VendorId);
        foreach (var p in await purchases.ListPaymentsAsync(null, ct))
            Vendor(BillPayment.Source, p.Id, p.VendorId);
        foreach (var r in await purchases.ListVendorRefundsAsync(null, ct))
            Vendor(VendorRefund.Source, r.Id, r.VendorId);
        foreach (var e in await purchases.ListExpensesAsync(null, ct))
        {
            if (e.VendorId is not null)
                Vendor(BankTransaction.SpendSource, e.Id, e.VendorId);
            else if (e.Payee is { } payee)
                names[(BankTransaction.SpendSource, e.Id)] = (payee, null);
        }

        return names;
    }
}
