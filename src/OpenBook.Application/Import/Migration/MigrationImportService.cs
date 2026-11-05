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

namespace OpenBook.Application.Import.Migration;

public enum MigrationKind
{
    Account,
    Customer,
    Vendor,
    Item,
    Transaction,
}

public enum MigrationRowStatus
{
    New,

    /// <summary>Already in OpenBook (same name, or a transaction imported before); left as it is.</summary>
    Exists,

    /// <summary>Something OpenBook doesn't import, such as an estimate or a non-posting account.</summary>
    Skipped,

    Error,
}

public sealed record MigrationRow(MigrationKind Kind, int Line, string Name, string? Detail, MigrationRowStatus Status, string? Message = null);

public sealed record MigrationResult(IReadOnlyList<MigrationRow> Rows, IReadOnlyList<string> Warnings)
{
    public int Count(MigrationKind kind, MigrationRowStatus status) => Rows.Count(r => r.Kind == kind && r.Status == status);
    public int NewCount => Rows.Count(r => r.Status == MigrationRowStatus.New);
    public int ErrorCount => Rows.Count(r => r.Status == MigrationRowStatus.Error);
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// Moving in from another program: a reader (<see cref="MigrationReaders"/>) turns the export into <see cref="MigrationData"/>,
/// which this checks against the books and imports. Accounts, customers, vendors and products already in OpenBook (same
/// name, ignoring case) are left alone, and so are transactions imported before (<see cref="ImportedBefore"/>), so
/// importing a file again adds nothing. Transactions become journal entries (source "ledger.import", one batch per file,
/// positive amounts debits); they don't become invoices or bills, so A/R and A/P aging won't list them. Everything imports
/// in one database transaction, or nothing does.
/// </summary>
public sealed class MigrationImportService(
    AccountService accounts,
    CustomerService customers,
    VendorService vendors,
    ItemService items,
    ISalesStore sales,
    IPurchasesStore purchases,
    PostingEngine engine,
    ICompanyStore companies,
    IUnitOfWork unitOfWork,
    Plugins.PluginRegistry plugins)
{
    /// <summary>Largest export accepted (decompressed).</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    public async Task<MigrationResult> PreviewAsync(MigrationSource source, byte[] content, CancellationToken ct = default) =>
        (await PlanAsync(MigrationReaders.Read(source, content, await HomeCurrencyAsync(ct)), ct)).Result;

    public async Task<MigrationResult> ImportAsync(MigrationSource source, byte[] content, CancellationToken ct = default) =>
        await ImportAsync(MigrationReaders.Read(source, content, await HomeCurrencyAsync(ct)), ct);

    /// <summary>The formats to move in from: the built-in ones (by <see cref="MigrationSource"/> name), then add-ins' (by key).</summary>
    public IReadOnlyList<(string Key, string Label)> Sources() =>
        [.. Enum.GetValues<MigrationSource>().Select(s => (s.ToString(), MigrationReaders.Describe(s))), .. plugins.Importers.Select(i => (i.Key, i.Label))];

    /// <summary>A built-in source by its name, or an add-in's import format by its key.</summary>
    public async Task<MigrationResult> PreviewAsync(string source, byte[] content, CancellationToken ct = default) =>
        (await PlanAsync(await ReadAsync(source, content, ct), ct)).Result;

    public async Task<MigrationResult> ImportAsync(string source, byte[] content, CancellationToken ct = default) =>
        await ImportAsync(await ReadAsync(source, content, ct), ct);

    private async Task<MigrationData> ReadAsync(string source, byte[] content, CancellationToken ct) => Read(source, content, await HomeCurrencyAsync(ct));

    /// <summary>What an export holds, read without the books (company setup reads the file before there's a company).</summary>
    public MigrationData Read(string source, byte[] content, string currency)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Enum.TryParse<MigrationSource>(source, ignoreCase: true, out var builtIn) && Enum.IsDefined(builtIn))
            return MigrationReaders.Read(builtIn, content, currency);
        var importer = plugins.Importer(source)
            ?? throw new DomainException("import.source_unknown", $"Unknown source '{source}'. Use one of: {string.Join(", ", Sources().Select(s => s.Key))}.");
        if (content.Length == 0)
            throw new DomainException("import.empty", "The file is empty.");
        if (content.Length > MaxBytes)
            throw new DomainException("import.too_large", "The file is larger than 20 MB. Export in smaller parts.");
        try
        {
            return importer.Read(content, currency);
        }
        catch (Exception ex) when (ex is not DomainException and not OutOfMemoryException)
        {
            throw new DomainException("import.plugin_failed", $"The {importer.Label} add-in couldn't read the file: {ex.Message}");
        }
    }

    public async Task<MigrationResult> PreviewAsync(MigrationData data, CancellationToken ct = default) => (await PlanAsync(data, ct)).Result;

    public async Task<MigrationResult> ImportAsync(MigrationData data, CancellationToken ct = default)
    {
        var plan = await PlanAsync(data, ct);
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

    private async Task<string> HomeCurrencyAsync(CancellationToken ct) => (await companies.GetRequiredCompanyAsync(ct)).HomeCurrency.Code;

    private sealed record PlannedAccount(string FullName, AccountType Type, string? Number, string? Description);

    private sealed record PlannedTransaction(DateOnly Date, string? Memo, IReadOnlyList<SourceLine> Lines);

    private sealed record Plan(
        MigrationResult Result,
        List<PlannedAccount> Accounts,
        List<CustomerDetails> Customers,
        List<VendorDetails> Vendors,
        List<(ItemDetails Details, string IncomeAccount)> Items,
        List<PlannedTransaction> Transactions);

    private async Task<Plan> PlanAsync(MigrationData data, CancellationToken ct)
    {
        var rows = new List<MigrationRow>();
        var warnings = data.Warnings.ToList();
        var chart = await accounts.GetChartAsync(ct);
        var company = await companies.GetRequiredCompanyAsync(ct);

        // Accounts: what the file adds, so later rows can refer to them.
        var plannedAccounts = new List<PlannedAccount>();
        var types = new Dictionary<string, AccountType?>(StringComparer.OrdinalIgnoreCase); // null = not part of the books
        foreach (var a in data.Accounts)
        {
            var name = a.FullName;
            if (a.SkipReason is not null)
            {
                types[name] = null;
                rows.Add(new MigrationRow(MigrationKind.Account, a.Line, name, a.TypeLabel, MigrationRowStatus.Skipped, a.SkipReason));
                continue;
            }

            var existing = chart.FindByFullName(name);
            if (existing is not null)
            {
                types[name] = existing.Type;
                rows.Add(new MigrationRow(MigrationKind.Account, a.Line, name, existing.Type.Describe(), MigrationRowStatus.Exists));
                continue;
            }

            var error = a.Type is null ? $"Unknown account type '{a.TypeLabel}'." : ValidateAccountName(name);
            var number = a.Number;
            if (error is null && number is not null)
                error = Try(() => Account.NormalizeNumber(number)) ?? (chart.All.Any(x => string.Equals(x.Number, number, StringComparison.OrdinalIgnoreCase)) || plannedAccounts.Any(p => p.Number == number)
                    ? $"Account number {number} is already used." : null);
            var cut = name.LastIndexOf(Account.PathSeparator);
            if (error is null && cut >= 0)
            {
                var parentName = name[..cut];
                var parentType = chart.FindByFullName(parentName)?.Type ?? plannedAccounts.FirstOrDefault(p => string.Equals(p.FullName, parentName, StringComparison.OrdinalIgnoreCase))?.Type;
                error = parentType is null ? $"Its parent '{parentName}' isn't in the file (before it) or the chart."
                    : parentType != a.Type ? $"A subaccount must have its parent's type ({parentType.Value.Describe()})." : null;
            }

            if (error is null && plannedAccounts.Any(p => string.Equals(p.FullName, name, StringComparison.OrdinalIgnoreCase)))
                error = "This account appears twice.";
            if (error is null)
            {
                plannedAccounts.Add(new PlannedAccount(name, a.Type!.Value, number, a.Description));
                types[name] = a.Type;
            }

            rows.Add(new MigrationRow(MigrationKind.Account, a.Line, name, a.Type?.Describe() ?? a.TypeLabel,
                error is null ? MigrationRowStatus.New : MigrationRowStatus.Error, error ?? a.Note));
        }

        // Some sources name accounts by number ("#4000"): the chart's account with that number, or one the file adds.
        string Resolve(string name) => !name.StartsWith('#') ? name
            : chart.All.FirstOrDefault(a => string.Equals(a.Number, name[1..], StringComparison.OrdinalIgnoreCase)) is { } numbered ? chart.FullName(numbered.Id)
            : plannedAccounts.FirstOrDefault(p => string.Equals(p.Number, name[1..], StringComparison.OrdinalIgnoreCase))?.FullName ?? name;
        AccountType? TypeOf(string name) => types.TryGetValue(name, out var t) ? t : chart.FindByFullName(name)?.Type;
        bool Known(string name) => types.ContainsKey(name) || chart.FindByFullName(name) is not null;

        // Customers and vendors: new names only.
        var existingCustomers = (await sales.ListCustomersAsync(ct)).Select(c => c.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedCustomers = new List<CustomerDetails>();
        foreach (var c in data.Customers)
        {
            var name = c.Details.DisplayName;
            if (c.SkipReason is not null)
            {
                rows.Add(new MigrationRow(MigrationKind.Customer, c.Line, name, c.Details.CompanyName, MigrationRowStatus.Skipped, c.SkipReason));
                continue;
            }

            if (existingCustomers.Contains(name.Trim()) || plannedCustomers.Any(p => string.Equals(p.DisplayName, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new MigrationRow(MigrationKind.Customer, c.Line, name, null, MigrationRowStatus.Exists));
                continue;
            }

            var error = Try(() => Customer.Create(c.Details));
            if (error is null)
                plannedCustomers.Add(c.Details with { DisplayName = name.Trim() });
            rows.Add(new MigrationRow(MigrationKind.Customer, c.Line, name, c.Details.CompanyName, error is null ? MigrationRowStatus.New : MigrationRowStatus.Error, error));
        }

        var existingVendors = (await purchases.ListVendorsAsync(ct)).Select(v => v.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedVendors = new List<VendorDetails>();
        foreach (var v in data.Vendors)
        {
            var name = v.Details.DisplayName;
            if (v.SkipReason is not null)
            {
                rows.Add(new MigrationRow(MigrationKind.Vendor, v.Line, name, v.Details.CompanyName, MigrationRowStatus.Skipped, v.SkipReason));
                continue;
            }

            if (existingVendors.Contains(name.Trim()) || plannedVendors.Any(p => string.Equals(p.DisplayName, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new MigrationRow(MigrationKind.Vendor, v.Line, name, null, MigrationRowStatus.Exists));
                continue;
            }

            var error = Try(() => Vendor.NormalizeName(name));
            if (error is null)
                plannedVendors.Add(v.Details with { DisplayName = name.Trim() });
            rows.Add(new MigrationRow(MigrationKind.Vendor, v.Line, name, v.Details.CompanyName, error is null ? MigrationRowStatus.New : MigrationRowStatus.Error, error));
        }

        // Products and services.
        var existingItems = (await sales.ListItemsAsync(ct)).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedItems = new List<(ItemDetails, string)>();
        foreach (var i in data.Items)
        {
            var name = i.Name;
            if (existingItems.Contains(name) || plannedItems.Any(p => string.Equals(p.Item1.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new MigrationRow(MigrationKind.Item, i.Line, name, i.KindLabel, MigrationRowStatus.Exists));
                continue;
            }

            if (i.Type is null)
            {
                rows.Add(new MigrationRow(MigrationKind.Item, i.Line, name, i.KindLabel, MigrationRowStatus.Skipped, i.Note));
                continue;
            }

            // No transaction depends on an item, so one OpenBook can't use is left out rather than stopping the move-in.
            var income = Resolve(i.IncomeAccount ?? string.Empty);
            if (string.IsNullOrWhiteSpace(i.IncomeAccount) || (Known(income) && TypeOf(income)?.ClassOf() != AccountClass.Income))
            {
                rows.Add(new MigrationRow(MigrationKind.Item, i.Line, name, i.KindLabel, MigrationRowStatus.Skipped,
                    string.IsNullOrWhiteSpace(i.IncomeAccount) ? "It has no income account. Add it by hand if you sell it." : $"'{income}' isn't an income account. Add it by hand if you sell it."));
                continue;
            }

            var error = Try(() => Item.NormalizeName(name))
                ?? (!Known(income) ? $"Its income account '{income}' isn't in the file or the chart." : null)
                ?? Try(() => SalesLine.ValidRate(i.Price));
            if (error is null)
                plannedItems.Add((new ItemDetails(name, i.Type.Value, Guid.Empty, i.Price, i.Description), income));
            rows.Add(new MigrationRow(MigrationKind.Item, i.Line, name, i.KindLabel, error is null ? MigrationRowStatus.New : MigrationRowStatus.Error, error ?? i.Note));
        }

        // Transactions: one journal entry each, unless an earlier import brought it in.
        var plannedTransactions = new List<PlannedTransaction>();
        var dated = data.Transactions.Where(t => t.Date is not null).Select(t => t.Date!.Value).ToList();
        var before = dated.Count == 0 ? null : await ImportedBefore.LoadAsync(engine, dated.Min(), dated.Max(), ct);
        var repeats = 0;
        foreach (var source in data.Transactions)
        {
            var t = source with { Lines = source.Lines.Select(l => l with { Account = Resolve(l.Account) }).ToList() };
            var amount = t.Lines.Where(l => l.Amount > 0m).Sum(l => l.Amount);
            var detail = $"{t.DateText ?? t.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · {amount.ToString("N2", CultureInfo.CurrentCulture)}";
            if (t.SkipReason is not null || t.Lines.Any(l => types.TryGetValue(l.Account, out var at) && at is null))
            {
                rows.Add(new MigrationRow(MigrationKind.Transaction, t.Line, t.Label, detail, MigrationRowStatus.Skipped,
                    t.SkipReason ?? "It uses an account that isn't part of the books (such as a non-posting account)."));
                continue;
            }

            var errors = (t.Errors ?? []).ToList();
            if (t.Date is not { } date)
            {
                if (errors.Count == 0)
                    errors.Add($"the date '{t.DateText}' can't be read");
                date = default;
            }
            else if (company.IsClosed(date))
                errors.Add($"{date:yyyy-MM-dd} is on or before the closing date");

            var lines = t.Lines.Where(l => l.Amount != 0m).ToList();
            foreach (var l in lines.Where(l => !Known(l.Account)))
                errors.Add($"no account '{l.Account}' in the file or the chart");
            if (lines.Any(l => decimal.Round(l.Amount, 2) != l.Amount))
                errors.Add("amounts can have at most 2 decimal places");
            if (lines.Count < 2)
                errors.Add("it needs at least two lines with amounts");
            if (lines.Sum(l => l.Amount) != 0m)
                errors.Add($"its lines don't balance (off by {Math.Abs(lines.Sum(l => l.Amount)).ToString("N2", CultureInfo.CurrentCulture)})");

            var memo = t.Memo is { Length: > 1000 } full ? full[..1000] : t.Memo;
            if (errors.Count == 0 && before is not null && lines.All(l => chart.FindByFullName(l.Account) is not null)
                && before.Take(date, memo, lines.Select(l => (chart.FindByFullName(l.Account)!.Id, l.Amount))))
            {
                repeats++;
                rows.Add(new MigrationRow(MigrationKind.Transaction, t.Line, t.Label, detail, MigrationRowStatus.Exists, "Imported before: the same date, memo, accounts and amounts."));
                continue;
            }

            if (errors.Count == 0)
                plannedTransactions.Add(new PlannedTransaction(date, memo, lines));
            rows.Add(new MigrationRow(MigrationKind.Transaction, t.Line, t.Label, detail, errors.Count == 0 ? MigrationRowStatus.New : MigrationRowStatus.Error,
                errors.Count == 0 ? null : string.Join("; ", errors.Distinct())));
        }

        if (repeats > 0)
            warnings.Add($"{repeats} transaction{(repeats == 1 ? " matches an entry" : "s match entries")} from an earlier import (same date, memo, accounts and amounts), so {(repeats == 1 ? "it's" : "they're")} left out. If one is a genuine repeat, enter it by hand.");
        if (plannedTransactions.Any(t => t.Lines.Any(l => TypeOf(l.Account) is AccountType.AccountsReceivable or AccountType.AccountsPayable)))
            warnings.Add("Some transactions post to A/R or A/P. They come in as journal entries, so they change balances but won't appear as open invoices or bills in aging and statements.");

        return new Plan(new MigrationResult(rows, warnings), plannedAccounts, plannedCustomers, plannedVendors, plannedItems, plannedTransactions);
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

    internal static string? Try(Action check)
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
}
