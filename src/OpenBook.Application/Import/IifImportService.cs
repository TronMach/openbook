using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Import;

public enum IifKind
{
    Account,
    Customer,
    Vendor,
    Item,
    Transaction,
}

public enum IifRowStatus
{
    New,

    /// <summary>Already in OpenBook (same name); left as it is.</summary>
    Exists,

    /// <summary>Something OpenBook doesn't import, such as an estimate or a non-posting account.</summary>
    Skipped,

    Error,
}

public sealed record IifRow(IifKind Kind, int Line, string Name, string? Detail, IifRowStatus Status, string? Message = null);

public sealed record IifImportResult(IReadOnlyList<IifRow> Rows, IReadOnlyList<string> Warnings)
{
    public int Count(IifKind kind, IifRowStatus status) => Rows.Count(r => r.Kind == kind && r.Status == status);
    public int NewCount => Rows.Count(r => r.Status == IifRowStatus.New);
    public int ErrorCount => Rows.Count(r => r.Status == IifRowStatus.Error);
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// QuickBooks Desktop IIF files (tab-separated, "!" header rows): the chart of accounts (ACCNT), customers (CUST),
/// vendors (VEND), products and services (INVITEM) and transactions (TRNS/SPL/ENDTRNS). Transactions come in as
/// journal entries (source "ledger.import"), one per TRNS, with positive amounts as debits; they don't become
/// invoices or bills, so A/R and A/P aging won't list them. Names that already exist are left alone. The whole
/// file imports in one database transaction, or nothing does.
/// </summary>
public sealed class IifImportService(
    AccountService accounts,
    CustomerService customers,
    VendorService vendors,
    ItemService items,
    ISalesStore sales,
    IPurchasesStore purchases,
    PostingEngine engine,
    ICompanyStore companies,
    IUnitOfWork unitOfWork)
{
    public const int MaxLines = 50_000;

    private static readonly Dictionary<string, AccountType> AccountTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BANK"] = AccountType.Bank, ["AR"] = AccountType.AccountsReceivable, ["OCASSET"] = AccountType.OtherCurrentAsset,
        ["FIXASSET"] = AccountType.FixedAsset, ["OASSET"] = AccountType.OtherAsset, ["AP"] = AccountType.AccountsPayable,
        ["CCARD"] = AccountType.CreditCard, ["OCLIAB"] = AccountType.OtherCurrentLiability, ["LTLIAB"] = AccountType.LongTermLiability,
        ["EQUITY"] = AccountType.Equity, ["INC"] = AccountType.Income, ["COGS"] = AccountType.CostOfGoodsSold,
        ["EXP"] = AccountType.Expense, ["EXINC"] = AccountType.OtherIncome, ["EXEXP"] = AccountType.OtherExpense,
    };

    private static readonly HashSet<string> NonPostingTransactions = new(StringComparer.OrdinalIgnoreCase) { "ESTIMATE", "PURCHORD", "SALESORD" };

    public async Task<IifImportResult> PreviewAsync(string iif, CancellationToken ct = default) => (await PlanAsync(iif, ct)).Result;

    public async Task<IifImportResult> ImportAsync(string iif, CancellationToken ct = default)
    {
        var plan = await PlanAsync(iif, ct);
        if (plan.Result.ErrorCount > 0)
            throw new DomainException("import.has_errors", $"{plan.Result.ErrorCount} row(s) have errors. Fix them and import again; nothing was imported.");
        if (plan.Result.NewCount == 0)
            throw new DomainException("import.nothing_new", "Everything in this file is already in OpenBook.");

        await using var transaction = await unitOfWork.BeginAsync(ct);

        // Parents before children; each new account is saved so the next can refer to it.
        foreach (var a in plan.Accounts.OrderBy(a => a.FullName.Count(c => c == Account.PathSeparator)))
        {
            var chart = await accounts.GetChartAsync(ct);
            var cut = a.FullName.LastIndexOf(Account.PathSeparator);
            var parent = cut < 0 ? null : chart.FindByFullName(a.FullName[..cut]);
            await accounts.CreateAsync(new CreateAccountCommand(a.FullName[(cut + 1)..], a.Type, a.Number, parent?.Id, a.Description), ct);
        }

        foreach (var c in plan.Customers)
            await customers.CreateAsync(c, ct);
        foreach (var v in plan.Vendors)
            await vendors.CreateAsync(v, ct);

        var final = await accounts.GetChartAsync(ct);
        foreach (var (details, incomeAccount) in plan.Items)
            await items.CreateAsync(details with { IncomeAccountId = final.FindByFullName(incomeAccount)!.Id }, ct);

        var batch = Guid.CreateVersion7();
        foreach (var t in plan.Transactions)
        {
            var lines = t.Lines.Select(l => l.Amount > 0m
                ? JournalLineDraft.Dr(final.FindByFullName(l.Account)!.Id, l.Amount, l.Memo)
                : JournalLineDraft.Cr(final.FindByFullName(l.Account)!.Id, -l.Amount, l.Memo)).ToList();
            await engine.StageAsync(new PostJournalEntryCommand(t.Date, lines, t.Memo, JournalEntry.ImportSource, batch), ct);
        }

        if (plan.Transactions.Count > 0)
            await engine.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return plan.Result;
    }

    private sealed record PlannedAccount(string FullName, AccountType Type, string? Number, string? Description);

    private sealed record PlannedLine(string Account, decimal Amount, string? Memo);

    private sealed record PlannedTransaction(DateOnly Date, string? Memo, IReadOnlyList<PlannedLine> Lines);

    private sealed record Plan(
        IifImportResult Result,
        List<PlannedAccount> Accounts,
        List<CustomerDetails> Customers,
        List<VendorDetails> Vendors,
        List<(ItemDetails Details, string IncomeAccount)> Items,
        List<PlannedTransaction> Transactions);

    private async Task<Plan> PlanAsync(string iif, CancellationToken ct)
    {
        var (records, transactions, warnings) = Parse(iif);
        var rows = new List<IifRow>();
        var chart = await accounts.GetChartAsync(ct);
        var company = await companies.GetRequiredCompanyAsync(ct);

        // Accounts: what the file adds, so later rows can refer to them.
        var plannedAccounts = new List<PlannedAccount>();
        var types = new Dictionary<string, AccountType?>(StringComparer.OrdinalIgnoreCase); // null = non-posting
        foreach (var r in records.Where(r => r.Type == "ACCNT"))
        {
            var name = r.Get("NAME") ?? string.Empty;
            var typeCode = r.Get("ACCNTTYPE") ?? string.Empty;
            if (string.Equals(typeCode, "NONPOSTING", StringComparison.OrdinalIgnoreCase))
            {
                types[name] = null;
                rows.Add(new IifRow(IifKind.Account, r.Line, name, typeCode, IifRowStatus.Skipped, "Non-posting accounts (estimates, orders) aren't part of the books."));
                continue;
            }

            var existing = chart.FindByFullName(name);
            if (existing is not null)
            {
                types[name] = existing.Type;
                rows.Add(new IifRow(IifKind.Account, r.Line, name, existing.Type.Describe(), IifRowStatus.Exists));
                continue;
            }

            var error = AccountTypes.TryGetValue(typeCode, out var type) ? ValidateAccountName(name) : $"Unknown account type '{typeCode}'.";
            var number = r.Get("ACCNUM");
            if (error is null && number is not null)
                error = Try(() => Account.NormalizeNumber(number)) ?? (chart.All.Any(a => string.Equals(a.Number, number, StringComparison.OrdinalIgnoreCase)) || plannedAccounts.Any(p => p.Number == number)
                    ? $"Account number {number} is already used." : null);
            var cut = name.LastIndexOf(Account.PathSeparator);
            if (error is null && cut >= 0)
            {
                var parentName = name[..cut];
                var parentType = chart.FindByFullName(parentName)?.Type ?? plannedAccounts.FirstOrDefault(p => string.Equals(p.FullName, parentName, StringComparison.OrdinalIgnoreCase))?.Type;
                error = parentType is null ? $"Its parent '{parentName}' isn't in the file (before it) or the chart."
                    : parentType != type ? $"A subaccount must have its parent's type ({parentType.Value.Describe()})." : null;
            }

            if (error is null && plannedAccounts.Any(p => string.Equals(p.FullName, name, StringComparison.OrdinalIgnoreCase)))
                error = "This account appears twice.";
            if (error is null)
            {
                plannedAccounts.Add(new PlannedAccount(name, type, number, r.Get("DESC")));
                types[name] = type;
            }

            rows.Add(new IifRow(IifKind.Account, r.Line, name, AccountTypes.TryGetValue(typeCode, out var t) ? t.Describe() : typeCode,
                error is null ? IifRowStatus.New : IifRowStatus.Error, error));
        }

        AccountType? TypeOf(string name) => types.TryGetValue(name, out var t) ? t : chart.FindByFullName(name)?.Type;
        bool Known(string name) => types.ContainsKey(name) || chart.FindByFullName(name) is not null;

        // Customers and vendors: new names only.
        var existingCustomers = (await sales.ListCustomersAsync(ct)).Select(c => c.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedCustomers = new List<CustomerDetails>();
        foreach (var r in records.Where(r => r.Type == "CUST"))
        {
            var name = r.Get("NAME") ?? string.Empty;
            if (existingCustomers.Contains(name) || plannedCustomers.Any(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new IifRow(IifKind.Customer, r.Line, name, null, IifRowStatus.Exists));
                continue;
            }

            var details = new CustomerDetails(name, r.Get("COMPANYNAME"), r.Get("EMAIL"), r.Get("PHONE1"), r.Address("BADDR"), r.Address("SADDR"));
            var error = Try(() => Customer.NormalizeName(name));
            if (error is null)
                plannedCustomers.Add(details);
            rows.Add(new IifRow(IifKind.Customer, r.Line, name, details.CompanyName, error is null ? IifRowStatus.New : IifRowStatus.Error, error));
        }

        var existingVendors = (await purchases.ListVendorsAsync(ct)).Select(v => v.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedVendors = new List<VendorDetails>();
        foreach (var r in records.Where(r => r.Type == "VEND"))
        {
            var name = r.Get("NAME") ?? string.Empty;
            if (existingVendors.Contains(name) || plannedVendors.Any(v => string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new IifRow(IifKind.Vendor, r.Line, name, null, IifRowStatus.Exists));
                continue;
            }

            var details = new VendorDetails(name, r.Get("COMPANYNAME"), r.Get("EMAIL"), r.Get("PHONE1"), r.Address("ADDR"),
                AccountNumber: r.Get("ACCNUM"), Track1099: string.Equals(r.Get("1099"), "Y", StringComparison.OrdinalIgnoreCase));
            var error = Try(() => Vendor.NormalizeName(name));
            if (error is null)
                plannedVendors.Add(details);
            rows.Add(new IifRow(IifKind.Vendor, r.Line, name, details.CompanyName, error is null ? IifRowStatus.New : IifRowStatus.Error, error));
        }

        // Products and services: service and non-inventory kinds; inventory parts come in as non-inventory.
        var existingItems = (await sales.ListItemsAsync(ct)).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedItems = new List<(ItemDetails, string)>();
        foreach (var r in records.Where(r => r.Type == "INVITEM"))
        {
            var name = r.Get("NAME") ?? string.Empty;
            var kind = (r.Get("INVITEMTYPE") ?? "SERV").ToUpperInvariant();
            if (existingItems.Contains(name) || plannedItems.Any(i => string.Equals(i.Item1.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new IifRow(IifKind.Item, r.Line, name, kind, IifRowStatus.Exists));
                continue;
            }

            ItemType? type = kind switch
            {
                "SERV" => ItemType.Service,
                "PART" or "OTHC" or "INVENTORY" or "INVENTORYPART" or "NONINV" => ItemType.NonInventory,
                _ => null,
            };
            if (type is null)
            {
                rows.Add(new IifRow(IifKind.Item, r.Line, name, kind, IifRowStatus.Skipped, "Discounts, subtotals, groups, payments and tax items aren't imported."));
                continue;
            }

            var income = r.Get("ACCNT") ?? string.Empty;
            var error = Try(() => Item.NormalizeName(name))
                ?? (!Known(income) ? $"Its income account '{income}' isn't in the file or the chart."
                    : TypeOf(income)?.ClassOf() != AccountClass.Income ? $"'{income}' isn't an income account." : null);
            var rate = StatementValues.TryParseAmount(r.Get("PRICE") ?? "0", out var price) ? Math.Abs(price) : 0m;
            if (error is null)
                plannedItems.Add((new ItemDetails(name, type.Value, Guid.Empty, rate, r.Get("DESC")), income));
            var note = kind.StartsWith("INVENTORY", StringComparison.Ordinal) ? "Imported as a non-inventory product; set up stock afterwards if you track it." : null;
            rows.Add(new IifRow(IifKind.Item, r.Line, name, kind, error is null ? IifRowStatus.New : IifRowStatus.Error, error ?? note));
        }

        // Transactions: one journal entry each.
        var plannedTransactions = new List<PlannedTransaction>();
        foreach (var t in transactions)
        {
            var type = t.Trns.Get("TRNSTYPE") ?? "GENERAL JOURNAL";
            var label = $"{type} {t.Trns.Get("DOCNUM")}".Trim();
            var dateText = t.Trns.Get("DATE");
            var all = t.Splits.Prepend(t.Trns).ToList();
            var amount = all.Where(x => StatementValues.TryParseAmount(x.Get("AMOUNT"), out var a) && a > 0m).Sum(x => StatementValues.TryParseAmount(x.Get("AMOUNT"), out var a) ? a : 0m);
            var detail = $"{dateText} · {amount.ToString("N2", CultureInfo.CurrentCulture)}";

            if (NonPostingTransactions.Contains(type) || all.Any(x => types.TryGetValue(x.Get("ACCNT") ?? string.Empty, out var at) && at is null))
            {
                rows.Add(new IifRow(IifKind.Transaction, t.Line, label, detail, IifRowStatus.Skipped, "Estimates and orders don't post to the books."));
                continue;
            }

            var errors = new List<string>();
            if (!TryParseDate(dateText, out var date))
                errors.Add($"the date '{dateText}' can't be read");
            else if (company.IsClosed(date))
                errors.Add($"{date:yyyy-MM-dd} is on or before the closing date");

            var lines = new List<PlannedLine>();
            foreach (var x in all)
            {
                var account = x.Get("ACCNT") ?? string.Empty;
                if (!Known(account))
                    errors.Add($"no account '{account}' in the file or the chart");
                if (!StatementValues.TryParseAmount(x.Get("AMOUNT") ?? "0", out var value))
                    errors.Add($"line {x.Line}: the amount can't be read");
                else if (value != 0m)
                    lines.Add(new PlannedLine(account, value, x.Get("MEMO") ?? x.Get("NAME")));
            }

            if (lines.Count < 2)
                errors.Add("it needs at least two lines with amounts");
            if (lines.Sum(l => l.Amount) != 0m)
                errors.Add($"its lines don't balance (off by {Math.Abs(lines.Sum(l => l.Amount)).ToString("N2", CultureInfo.CurrentCulture)})");

            var memo = string.Join(" · ", new[] { label, t.Trns.Get("NAME"), t.Trns.Get("MEMO") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (errors.Count == 0)
                plannedTransactions.Add(new PlannedTransaction(date, memo.Length > 1000 ? memo[..1000] : memo, lines));
            rows.Add(new IifRow(IifKind.Transaction, t.Line, label, detail, errors.Count == 0 ? IifRowStatus.New : IifRowStatus.Error,
                errors.Count == 0 ? null : string.Join("; ", errors.Distinct())));
        }

        if (plannedTransactions.Any(t => t.Lines.Any(l => TypeOf(l.Account) is AccountType.AccountsReceivable or AccountType.AccountsPayable)))
            warnings.Add("Some transactions post to A/R or A/P. They come in as journal entries, so they change balances but won't appear as open invoices or bills in aging and statements.");

        return new Plan(new IifImportResult(rows, warnings), plannedAccounts, plannedCustomers, plannedVendors, plannedItems, plannedTransactions);
    }

    private static string? ValidateAccountName(string fullName)
    {
        if (fullName.Length == 0)
            return "An account needs a name.";
        foreach (var part in fullName.Split(Account.PathSeparator))
        {
            if (Try(() => Account.NormalizeName(part)) is { } error)
                return error;
        }

        return fullName.Count(c => c == Account.PathSeparator) >= 5 ? "Accounts can be at most 5 levels deep." : null;
    }

    private static string? Try(Action check)
    {
        try
        {
            check();
            return null;
        }
        catch (DomainException ex)
        {
            return ex.Message;
        }
    }

    private static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), ["M/d/yyyy", "M/d/yy", "yyyy-MM-dd", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal sealed record IifRecord(int Line, string Type, IReadOnlyDictionary<string, string> Values)
    {
        public string? Get(string column) => Values.TryGetValue(column, out var v) && v.Length > 0 ? v : null;

        /// <summary>QuickBooks splits addresses over ADDR1–ADDR5 (BADDR1–5 for billing, SADDR1–5 for shipping).</summary>
        public string? Address(string prefix)
        {
            var parts = Enumerable.Range(1, 5).Select(i => Get($"{prefix}{i}")).OfType<string>().ToList();
            return parts.Count == 0 ? null : string.Join('\n', parts);
        }
    }

    internal sealed record IifTransaction(int Line, IifRecord Trns, List<IifRecord> Splits);

    /// <summary>Tab-separated; each "!TYPE" header row names the columns of later TYPE rows. Quotes around a value are removed.</summary>
    internal static (List<IifRecord> Records, List<IifTransaction> Transactions, List<string> Warnings) Parse(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > MaxLines)
            throw new DomainException("import.too_many_rows", $"IIF files can have at most {MaxLines} lines. Export in smaller parts.");
        if (!lines.Any(l => l.StartsWith('!')))
            throw new DomainException("import.not_iif", "This isn't an IIF file: it has no header rows starting with '!'. Export it from QuickBooks with File › Utilities › Export › Lists to IIF Files.");

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var records = new List<IifRecord>();
        var transactions = new List<IifTransaction>();
        var ignored = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        IifTransaction? open = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var fields = lines[i].TrimEnd('\r').Split('\t').Select(Unquote).ToArray();
            if (fields.Length == 0 || fields[0].Length == 0)
                continue;
            if (fields[0].StartsWith('!'))
            {
                headers[fields[0][1..]] = fields.Select(f => f.ToUpperInvariant()).ToArray();
                continue;
            }

            var type = fields[0].ToUpperInvariant();
            if (type == "ENDTRNS")
            {
                if (open is not null)
                    transactions.Add(open);
                open = null;
                continue;
            }

            if (!headers.TryGetValue(type, out var columns))
            {
                ignored.Add(type);
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 1; c < Math.Min(columns.Length, fields.Length); c++)
                values[columns[c]] = fields[c].Trim();
            var record = new IifRecord(i + 1, type, values);
            switch (type)
            {
                case "TRNS":
                    if (open is not null)
                        transactions.Add(open);
                    open = new IifTransaction(i + 1, record, []);
                    break;
                case "SPL" when open is not null:
                    open.Splits.Add(record);
                    break;
                case "ACCNT" or "CUST" or "VEND" or "INVITEM":
                    records.Add(record);
                    break;
                default:
                    ignored.Add(type);
                    break;
            }
        }

        if (open is not null)
            transactions.Add(open);
        var warnings = new List<string>();
        if (ignored.Count > 0)
            warnings.Add($"Not imported: {string.Join(", ", ignored)} rows (OpenBook reads ACCNT, CUST, VEND, INVITEM and TRNS/SPL).");
        return (records, transactions, warnings);
    }

    private static string Unquote(string field) =>
        field.Length >= 2 && field[0] == '"' && field[^1] == '"' ? field[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : field;
}
