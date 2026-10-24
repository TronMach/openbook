using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Import;

public sealed record ImportedLine(string Account, Guid? AccountId, decimal Debit, decimal Credit, string? Memo);

/// <param name="Key">The file's entry number, or "#n" when entries were grouped by balancing.</param>
public sealed record ImportedEntry(int Line, string Key, DateOnly? Date, string? Memo, IReadOnlyList<ImportedLine> Lines, string? Error)
{
    public decimal Total => Lines.Sum(l => l.Debit);
    public bool IsValid => Error is null;
}

public sealed record JournalImportResult(IReadOnlyList<ImportedEntry> Entries, IReadOnlyList<string> Warnings)
{
    public int ErrorCount => Entries.Count(e => !e.IsValid);
    public bool CanImport => Entries.Count > 0 && ErrorCount == 0;
}

/// <param name="Balance">In the account's natural direction: positive is money in the bank, owed on a card, value of an asset.</param>
public sealed record ImportedBalance(int Line, string Account, Guid? AccountId, decimal Balance, string? Error);

public sealed record OpeningBalanceImportResult(IReadOnlyList<ImportedBalance> Rows)
{
    public int ErrorCount => Rows.Count(r => r.Error is not null);
    public bool CanImport => Rows.Count > 0 && ErrorCount == 0;
}

/// <summary>
/// Journal entries and opening balances from CSV, with a preview, all-or-nothing. Accounts are matched by full
/// name ("Parent:Child", any case) or number. Dates in any common order (year first, month first or day first,
/// detected from the whole column). Amounts as debit/credit columns, or one signed amount (positive = debit).
/// Without an entry number column, consecutive lines form an entry until they balance.
/// </summary>
public sealed class JournalImportService(AccountService accounts, PostingEngine engine, OpeningBalanceService openingBalances, ICompanyStore companies)
{
    public const int MaxRows = 5000;

    private static readonly string[] DateHeaders = ["date", "transactiondate", "txndate", "entrydate", "postingdate"];
    private static readonly string[] EntryHeaders = ["entry", "entryno", "entrynumber", "journal", "journalno", "journalnumber", "transaction", "transactionid", "trans", "no", "number", "num"];
    private static readonly string[] AccountHeaders = ["account", "accountname", "fullname", "accountnumber", "accnt", "acct"];
    private static readonly string[] DebitHeaders = ["debit", "debits", "dr"];
    private static readonly string[] CreditHeaders = ["credit", "credits", "cr"];
    private static readonly string[] AmountHeaders = ["amount", "value"];
    private static readonly string[] MemoHeaders = ["memo", "description", "linememo", "details", "narration"];
    private static readonly string[] BalanceHeaders = ["balance", "openingbalance", "amount"];

    public async Task<JournalImportResult> PreviewEntriesAsync(string csv, CancellationToken ct = default) => await PlanEntriesAsync(csv, ct);

    /// <summary>Posts every entry in one save (source "ledger.import"), or nothing.</summary>
    public async Task<JournalImportResult> ImportEntriesAsync(string csv, CancellationToken ct = default)
    {
        var result = await PlanEntriesAsync(csv, ct);
        if (!result.CanImport)
            throw new DomainException("import.has_errors", $"{result.ErrorCount} entr{(result.ErrorCount == 1 ? "y has" : "ies have")} errors. Fix them and import again; nothing was imported.");

        var batch = Guid.CreateVersion7();
        foreach (var entry in result.Entries)
        {
            var lines = entry.Lines.Select(l => new JournalLineDraft(l.AccountId!.Value, l.Debit, l.Credit, l.Memo)).ToList();
            await engine.StageAsync(new PostJournalEntryCommand(entry.Date!.Value, lines, entry.Memo, JournalEntry.ImportSource, batch), ct);
        }

        await engine.SaveAsync(ct);
        return result;
    }

    public async Task<OpeningBalanceImportResult> PreviewBalancesAsync(string csv, CancellationToken ct = default) => await PlanBalancesAsync(csv, ct);

    /// <summary>One opening balance entry on <paramref name="asOf"/>, the difference going to Opening Balance Equity.</summary>
    public async Task<JournalEntry> ImportBalancesAsync(string csv, DateOnly asOf, CancellationToken ct = default)
    {
        var result = await PlanBalancesAsync(csv, ct);
        if (!result.CanImport)
            throw new DomainException("import.has_errors", $"{result.ErrorCount} row(s) have errors. Fix them and import again; nothing was imported.");
        return await openingBalances.PostAsync(asOf, result.Rows.Select(r => new OpeningBalance(r.AccountId!.Value, r.Balance)).ToList(), ct);
    }

    private async Task<JournalImportResult> PlanEntriesAsync(string csv, CancellationToken ct)
    {
        var (header, rows) = Read(csv, "journal lines");
        var columns = new CsvColumns(header);
        var dateColumn = columns.Find(DateHeaders) ?? throw new DomainException("import.no_date_column", "The file needs a date column.");
        var accountColumn = columns.Find(AccountHeaders) ?? throw new DomainException("import.no_account_column", "The file needs an account column.");
        var debitColumn = columns.Find(DebitHeaders);
        var creditColumn = columns.Find(CreditHeaders);
        var amountColumn = debitColumn is null && creditColumn is null
            ? columns.Find(AmountHeaders) ?? throw new DomainException("import.no_amount_column", "The file needs debit and credit columns, or one amount column (positive = debit).")
            : (int?)null;
        var entryColumn = columns.Find(EntryHeaders);
        var memoColumn = columns.Find(MemoHeaders);

        var warnings = new List<string>();
        var order = DateOrderOf(rows.Select(r => CsvColumns.Field(r, dateColumn)), warnings);
        var chart = await accounts.GetChartAsync(ct);
        var company = await companies.GetRequiredCompanyAsync(ct);

        // Group lines into entries: by the entry column, or until the running total balances.
        var groups = new List<(string Key, List<CsvRecord> Rows)>();
        var running = 0m;
        foreach (var row in rows)
        {
            var key = CsvColumns.Field(row, entryColumn);
            if (entryColumn is not null)
            {
                key ??= $"line {row.Line}";
                var existing = groups.FindIndex(g => g.Key == key);
                if (existing >= 0)
                    groups[existing].Rows.Add(row);
                else
                    groups.Add((key, [row]));
                continue;
            }

            if (groups.Count == 0 || running == 0m)
                groups.Add(($"#{groups.Count + 1}", []));
            groups[^1].Rows.Add(row);
            running += LineAmount(row, debitColumn, creditColumn, amountColumn) ?? 0m;
        }

        var entries = groups.Select(g => PlanEntry(g.Key, g.Rows, chart, company, order, dateColumn, accountColumn, debitColumn, creditColumn, amountColumn, memoColumn)).ToList();
        return new JournalImportResult(entries, warnings);
    }

    private static ImportedEntry PlanEntry(
        string key, List<CsvRecord> rows, ChartOfAccounts chart, Domain.Companies.Company company, DateOrder order,
        int dateColumn, int accountColumn, int? debitColumn, int? creditColumn, int? amountColumn, int? memoColumn)
    {
        var errors = new List<string>();
        var lines = new List<ImportedLine>();
        DateOnly? date = null;
        foreach (var row in rows)
        {
            if (!StatementValues.TryParseDate(CsvColumns.Field(row, dateColumn), order, out var d))
                errors.Add($"line {row.Line}: no date or one that can't be read");
            else if (date is null)
                date = d;
            else if (date != d)
                errors.Add($"line {row.Line}: a different date from the rest of the entry");

            var name = CsvColumns.Field(row, accountColumn) ?? string.Empty;
            var account = FindAccount(chart, name);
            if (account is null)
                errors.Add($"line {row.Line}: no account '{name}'");
            else if (!account.IsActive)
                errors.Add($"line {row.Line}: '{name}' is inactive");

            var amount = LineAmount(row, debitColumn, creditColumn, amountColumn);
            if (amount is null)
                errors.Add($"line {row.Line}: the amount can't be read (or both debit and credit are filled in)");
            lines.Add(new ImportedLine(name, account?.Id, amount is > 0m ? amount.Value : 0m, amount is < 0m ? -amount.Value : 0m, CsvColumns.Field(row, memoColumn)));
        }

        if (lines.Count < 2)
            errors.Add("an entry needs at least two lines");
        var difference = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        if (difference != 0m)
            errors.Add($"debits and credits differ by {Math.Abs(difference).ToString("N2", CultureInfo.CurrentCulture)}");
        if (date is { } on && company.IsClosed(on))
            errors.Add($"{on:yyyy-MM-dd} is on or before the closing date");

        return new ImportedEntry(rows[0].Line, key, date, lines.Select(l => l.Memo).FirstOrDefault(m => m is not null), lines,
            errors.Count == 0 ? null : string.Join("; ", errors.Distinct()));
    }

    private async Task<OpeningBalanceImportResult> PlanBalancesAsync(string csv, CancellationToken ct)
    {
        var (header, rows) = Read(csv, "balances");
        var columns = new CsvColumns(header);
        var accountColumn = columns.Find(AccountHeaders) ?? throw new DomainException("import.no_account_column", "The file needs an account column.");
        var debitColumn = columns.Find(DebitHeaders);
        var creditColumn = columns.Find(CreditHeaders);
        var balanceColumn = debitColumn is null && creditColumn is null
            ? columns.Find(BalanceHeaders) ?? throw new DomainException("import.no_amount_column", "The file needs a balance column, or debit and credit columns.")
            : (int?)null;
        var chart = await accounts.GetChartAsync(ct);

        var result = new List<ImportedBalance>();
        foreach (var row in rows)
        {
            var name = CsvColumns.Field(row, accountColumn) ?? string.Empty;
            var account = FindAccount(chart, name);
            var amount = LineAmount(row, debitColumn, creditColumn, balanceColumn);
            string? error = null;
            decimal balance = 0m;
            if (account is null)
                error = $"There's no account '{name}'.";
            else if (!OpeningBalanceService.IsEligible(account))
                error = account.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable
                    ? "Enter open invoices or bills instead of an A/R or A/P balance."
                    : "Only balance sheet accounts (other than Opening Balance Equity) take opening balances.";
            else if (amount is null)
                error = "The amount can't be read.";
            else if (result.Any(r => r.AccountId == account.Id))
                error = "This account appears twice.";
            else
                // A balance column is already in the natural direction; debit/credit columns become it here.
                balance = balanceColumn is not null ? amount.Value : (account.Type.IsDebitNormal() ? 1m : -1m) * amount.Value;
            result.Add(new ImportedBalance(row.Line, name, account?.Id, balance, error));
        }

        return new OpeningBalanceImportResult(result);
    }

    private static (CsvRecord Header, IReadOnlyList<CsvRecord> Rows) Read(string csv, string what)
    {
        var records = Csv.Parse(csv ?? string.Empty);
        if (records.Count < 2)
            throw new DomainException("import.empty", $"The file needs a header row and at least one row of {what}.");
        if (records.Count - 1 > MaxRows)
            throw new DomainException("import.too_many_rows", $"Files can have at most {MaxRows} rows. Split it into smaller files.");
        return (records[0], records.Skip(1).Where(r => r.Fields.Any(f => f.Length > 0)).ToList());
    }

    /// <summary>Debit-positive; null when it can't be read or both debit and credit are filled.</summary>
    private static decimal? LineAmount(CsvRecord row, int? debitColumn, int? creditColumn, int? amountColumn)
    {
        if (amountColumn is not null)
            return StatementValues.TryParseAmount(CsvColumns.Field(row, amountColumn), out var a) ? a : null;
        var debitText = CsvColumns.Field(row, debitColumn);
        var creditText = CsvColumns.Field(row, creditColumn);
        decimal debit = 0m, credit = 0m;
        if (debitText is not null && !StatementValues.TryParseAmount(debitText, out debit))
            return null;
        if (creditText is not null && !StatementValues.TryParseAmount(creditText, out credit))
            return null;
        if (debit != 0m && credit != 0m)
            return null;
        return debit - credit;
    }

    internal static Account? FindAccount(ChartOfAccounts chart, string nameOrNumber)
    {
        var text = nameOrNumber.Trim();
        if (text.Length == 0)
            return null;
        return chart.FindByFullName(text)
            ?? chart.All.FirstOrDefault(a => string.Equals(a.Number, text, StringComparison.OrdinalIgnoreCase))
            ?? chart.All.FirstOrDefault(a => a.ParentId is null && string.Equals(a.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    private static DateOrder DateOrderOf(IEnumerable<string?> values, List<string> warnings)
    {
        var dates = values.OfType<string>().ToList();
        var order = StatementValues.DetectDateOrder(dates, CultureInfo.CurrentCulture, out var ambiguous);
        if (ambiguous)
            warnings.Add($"Every date could be read either day-first or month-first; they were read {(order == DateOrder.DayFirst ? "day-first" : "month-first")}. Check the dates below, or use yyyy-mm-dd.");
        return order;
    }
}
