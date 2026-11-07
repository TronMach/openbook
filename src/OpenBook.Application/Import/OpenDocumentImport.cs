using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import;

public enum OpenDocumentKind
{
    Invoices,
    Bills,
}

/// <summary>An unpaid invoice or bill in a file.</summary>
public sealed record OpenDocumentRow(int Line, string? Party, bool NewParty, string? Number, DateOnly? Date, DateOnly? DueDate, decimal Amount, ImportRowStatus Status, string? Message);

public sealed record OpenDocumentPlan(IReadOnlyList<OpenDocumentRow> Rows, int NewCount, int ExistingCount, int ErrorCount, decimal Total, int NewParties, decimal BalanceInBooks, IReadOnlyList<string> Warnings)
{
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// What customers and vendors still owe and are owed when starting fresh, from a CSV, all or nothing: real invoices and
/// bills for the unpaid amounts, so the agings list them and payments apply. Each is one line for what's unpaid,
/// to an income account or category chosen for the file. A name that isn't on the list is added; a number that's
/// taken is skipped. The accounts receivable and payable accounts get their balances this way, not from opening balances.
/// </summary>
public sealed class OpenDocumentImportService(
    ISalesStore sales,
    IPurchasesStore purchases,
    CustomerService customers,
    VendorService vendors,
    ItemService items,
    InvoiceService invoices,
    BillService bills,
    AccountService accounts,
    Reporting.ReportLedger ledger,
    IUnitOfWork unitOfWork)
{
    public const int MaxRows = 5000;
    public const string OpeningItemName = "Opening balance";

    public Task<OpenDocumentPlan> PreviewAsync(OpenDocumentKind kind, string csv, CancellationToken ct = default) => PlanAsync(kind, csv, ct);

    /// <param name="kind">Unpaid invoices or unpaid bills.</param>
    /// <param name="csv">The file.</param>
    /// <param name="accountId">The income account for invoices, the category for bills.</param>
    /// <param name="ct">Cancels the import.</param>
    public async Task<OpenDocumentPlan> ImportAsync(OpenDocumentKind kind, string csv, Guid accountId, CancellationToken ct = default)
    {
        var plan = await PlanAsync(kind, csv, ct);
        SalesImport.EnsureImportable(plan.ErrorCount, plan.NewCount);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var rows = plan.Rows.Where(r => r.Status == ImportRowStatus.New).ToList();
        if (kind == OpenDocumentKind.Invoices)
        {
            var known = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.DisplayName, StringComparer.OrdinalIgnoreCase);
            var item = (await sales.ListItemsAsync(ct)).FirstOrDefault(i => string.Equals(i.Name, OpeningItemName, StringComparison.OrdinalIgnoreCase))
                ?? await items.CreateAsync(new ItemDetails(OpeningItemName, ItemType.Service, accountId, Taxable: false), ct);
            foreach (var row in rows)
            {
                if (!known.TryGetValue(row.Party!, out var customer))
                    known[row.Party!] = customer = await customers.CreateAsync(new CustomerDetails(row.Party!), ct);
                await Named(row, () => invoices.CreateAsync(new CreateInvoiceCommand(customer.Id, row.Date!.Value,
                    [new SalesLineDraft(item.Id, "Unpaid when moving in", 1m, row.Amount)], row.DueDate, row.Number, TaxCodeId: Guid.Empty), ct));
            }
        }
        else
        {
            var known = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!known.TryGetValue(row.Party!, out var vendor))
                    known[row.Party!] = vendor = await vendors.CreateAsync(new VendorDetails(row.Party!), ct);
                await Named(row, () => bills.CreateAsync(new CreateBillCommand(vendor.Id, row.Date!.Value,
                    [new BillLineDraft(accountId, row.Amount, "Unpaid when moving in")], row.DueDate, row.Number), ct));
            }
        }

        await transaction.CommitAsync(ct);
        return plan;
    }

    private static async Task Named<T>(OpenDocumentRow row, Func<Task<T>> make)
    {
        try
        {
            await make();
        }
        catch (DomainException ex) when (ex is not ConcurrencyConflictException)
        {
            throw new DomainException(ex.Code, $"Line {row.Line}: {ex.Message}");
        }
    }

    private async Task<OpenDocumentPlan> PlanAsync(OpenDocumentKind kind, string csv, CancellationToken ct)
    {
        var isInvoice = kind == OpenDocumentKind.Invoices;
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var partyColumn = (isInvoice ? cols.Find("customer", "name", "customername", "client") : cols.Find("vendor", "name", "vendorname", "supplier"))
            ?? throw new DomainException("import.no_name_column", $"The file needs a {(isInvoice ? "Customer" : "Vendor")} column.");
        var dateColumn = cols.Find("date", "invoicedate", "billdate", "txndate") ?? throw new DomainException("import.no_date_column", "The file needs a date column.");
        var amountColumn = cols.Find("openbalance", "balance", "amountdue", "due", "amount", "total", "unpaid")
            ?? throw new DomainException("import.no_amount_column", "The file needs an amount column (\"Open balance\" or \"Amount\").");
        var numberColumn = cols.Find("number", "num", "no", "invoicenumber", "invoiceno", "billnumber", "billno", "reference", "refno");
        var dueColumn = cols.Find("duedate", "due date");

        var warnings = new List<string>();
        var dateTexts = records.Skip(1).SelectMany(r => new[] { CsvColumns.Field(r, dateColumn), CsvColumns.Field(r, dueColumn) }).OfType<string>().ToList();
        var order = StatementValues.DetectDateOrder(dateTexts, CultureInfo.CurrentCulture, out var ambiguous);
        if (ambiguous)
            warnings.Add($"Every date could be read either day-first or month-first; they were read {(order == DateOrder.DayFirst ? "day-first" : "month-first")}. Check them.");

        var parties = isInvoice
            ? (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.DisplayName, c => c.Id, StringComparer.OrdinalIgnoreCase)
            : (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.DisplayName, v => v.Id, StringComparer.OrdinalIgnoreCase);
        var taken = isInvoice
            ? (await sales.ListInvoiceNumbersAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : (await purchases.ListBillsAsync(null, ct)).Where(b => b.Status == DocumentStatus.Posted && b.Reference is not null).Select(b => $"{b.VendorId}|{b.Reference}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newParties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<OpenDocumentRow>();
        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c);
            var party = F(partyColumn)?.Trim();
            var number = F(numberColumn)?.Trim();
            var hasDate = StatementValues.TryParseDate(F(dateColumn), order, out var date);
            var hasAmount = StatementValues.TryParseAmount(F(amountColumn), out var amount);
            DateOnly? due = StatementValues.TryParseDate(F(dueColumn), order, out var dueOn) ? dueOn : null;
            var message = !hasDate ? "The date can't be read."
                : string.IsNullOrEmpty(party) ? $"There's no {(isInvoice ? "customer" : "vendor")}."
                : !hasAmount ? "The amount can't be read."
                : amount < 0m ? "Enter credits on their own: this imports what's owed."
                : isInvoice && string.IsNullOrEmpty(number) ? "An invoice needs a number."
                : null;
            if (message is not null)
            {
                rows.Add(new OpenDocumentRow(r.Line, party, false, number, hasDate ? date : null, due, 0m, ImportRowStatus.Error, message));
                continue;
            }

            var isNew = !parties.TryGetValue(party!, out var partyId);
            var key = isInvoice ? number! : $"{partyId}|{number}";
            if (amount == 0m || (number is not null && !isNew && taken.Contains(key)) || (isInvoice && taken.Contains(key)))
            {
                rows.Add(new OpenDocumentRow(r.Line, party, false, number, date, due, amount, ImportRowStatus.Exists, amount == 0m ? "Nothing owed." : "That number is already in the books."));
                continue;
            }

            if (number is not null)
                taken.Add(key);
            if (isNew)
                newParties.Add(party!);
            rows.Add(new OpenDocumentRow(r.Line, party, isNew, number, date, due, amount, ImportRowStatus.New, null));
        }

        // What's already in the account would be doubled.
        var type = isInvoice ? AccountType.AccountsReceivable : AccountType.AccountsPayable;
        var chart = await accounts.GetChartAsync(ct);
        var held = chart.Ordered(includeInactive: true).Where(x => x.Account.Type == type).Select(x => x.Account.Id).ToHashSet();
        var today = CompanyTime.Today;
        var balance = (await ledger.TotalsAsync(Domain.Ledger.ReportBasis.Accrual, today, today, ct)).Where(l => held.Contains(l.AccountId)).Sum(l => l.Amount);
        var inDocuments = isInvoice
            ? (await sales.ListOpenInvoicesAsync(null, ct)).Sum(i => i.Balance)
            : -(await purchases.ListOpenBillsAsync(null, ct)).Sum(b => b.Balance);
        if (balance != inDocuments)
            warnings.Add($"The {(isInvoice ? "receivables" : "payables")} accounts hold {Math.Abs(balance - inDocuments):N2} that no {(isInvoice ? "invoice" : "bill")} explains, from imported history or a journal entry. Importing these adds to that, so check it first.");
        var added = rows.Where(r => r.Status == ImportRowStatus.New).ToList();
        return new OpenDocumentPlan(rows, added.Count, rows.Count(r => r.Status == ImportRowStatus.Exists), rows.Count(r => r.Status == ImportRowStatus.Error),
            added.Sum(r => r.Amount), newParties.Count, balance, warnings);
    }
}
