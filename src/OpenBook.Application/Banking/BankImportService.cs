using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

public enum AmountSign
{
    PositiveIsMoneyIn,
    PositiveIsMoneyOut,
}

/// <summary>Overrides for what <see cref="BankImportService"/> detects. Null means auto.</summary>
public sealed record BankImportOptions(DateOrder? DateOrder = null, AmountSign AmountSign = AmountSign.PositiveIsMoneyIn);

/// <param name="Amount">Positive is money in (deposit, card credit); negative is money out.</param>
/// <param name="PossibleDuplicate">A transaction with the same date, amount and payee is already on the account.</param>
public sealed record BankImportRow(
    int Line,
    DateOnly? Date,
    decimal? Amount,
    string? Payee,
    string? Reference,
    string? Memo,
    bool PossibleDuplicate,
    string? Error)
{
    public bool IsValid => Error is null;
}

public sealed record BankImportPreview(
    IReadOnlyList<BankImportRow> Rows,
    DateOrder DateOrder,
    bool DateOrderAmbiguous,
    bool HasDebitCreditColumns,
    IReadOnlyList<string> ColumnsUsed);

/// <param name="Amount">Positive is money in, negative money out.</param>
public sealed record ImportBankRowCommand(DateOnly Date, decimal Amount, Guid CategoryAccountId, string? Payee, string? Reference, string? Memo);

/// <summary>
/// Imports a bank or card statement CSV. Each chosen row becomes a categorized spend or receive in one save.
/// Columns are found by header: a date, then either one signed amount or separate money-out/money-in
/// columns, plus optional payee/description, reference and memo. Rules and a review queue arrive in V1.
/// </summary>
public sealed class BankImportService(IBankingStore bank, AccountService accounts, BankingService banking)
{
    public const int MaxRows = 5000;

    private static readonly string[] DateHeaders = ["date", "transactiondate", "posteddate", "postingdate", "transdate", "bookingdate", "valuedate"];
    private static readonly string[] PayeeHeaders = ["payee", "name", "merchant", "counterparty", "payeename"];
    private static readonly string[] DescriptionHeaders = ["description", "details", "transactiondescription", "narrative", "particulars"];
    private static readonly string[] MemoHeaders = ["memo", "notes", "note"];
    private static readonly string[] AmountHeaders = ["amount", "transactionamount", "value"];
    private static readonly string[] OutHeaders = ["debit", "debitamount", "withdrawal", "withdrawals", "withdrawalamount", "moneyout", "paidout", "charge", "charges"];
    private static readonly string[] InHeaders = ["credit", "creditamount", "deposit", "deposits", "depositamount", "moneyin", "paidin", "payment", "payments"];
    private static readonly string[] ReferenceHeaders = ["checknumber", "check", "checkno", "chequenumber", "cheque", "reference", "ref", "refnumber", "referencenumber", "number", "num"];

    public async Task<BankImportPreview> PreviewAsync(Guid accountId, string csv, BankImportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        BankingRules.EnsureBankingAccount((await accounts.GetChartAsync(ct)).Get(accountId));

        var records = Csv.Parse(csv);
        if (records.Count < 2)
            throw new DomainException("import.empty", "The file needs a header row and at least one transaction.");
        if (records.Count - 1 > MaxRows)
            throw new DomainException("import.too_many_rows", $"Import at most {MaxRows} transactions at a time.");

        var header = records[0].Fields.Select(Csv.Normalize).ToList();
        int? Col(string[] names) => header.FindIndex(names.Contains) is var i and >= 0 ? i : null;

        var dateCol = Col(DateHeaders) ?? throw new DomainException("import.no_date_column", "The file needs a date column (\"Date\").");
        var amountCol = Col(AmountHeaders);
        var outCol = Col(OutHeaders);
        var inCol = Col(InHeaders);
        if (amountCol is null && (outCol is null || inCol is null))
            throw new DomainException("import.no_amount_column",
                "The file needs an \"Amount\" column, or both money-out and money-in columns (e.g. \"Debit\" and \"Credit\").");
        var useSplitColumns = amountCol is null;
        var payeeCol = Col(PayeeHeaders);
        var descriptionCol = Col(DescriptionHeaders);
        var memoCol = Col(MemoHeaders);
        var referenceCol = Col(ReferenceHeaders);

        // With both a payee and a description column, the description becomes the memo.
        var payeeSource = payeeCol ?? descriptionCol;
        var memoSource = memoCol ?? (payeeCol is not null ? descriptionCol : null);

        static string? Field(CsvRecord r, int? column) =>
            column is { } c && c < r.Fields.Count && r.Fields[c].Length > 0 ? r.Fields[c] : null;

        var body = records.Skip(1).ToList();
        var rawDates = body.Select(r => Field(r, dateCol)).OfType<string>().ToList();
        var ambiguous = false;
        var order = options.DateOrder ?? StatementValues.DetectDateOrder(rawDates, CultureInfo.CurrentCulture, out ambiguous);

        var rows = new List<BankImportRow>(body.Count);
        foreach (var record in body)
        {
            var payee = Field(record, payeeSource);
            var reference = Field(record, referenceCol);
            var memo = Field(record, memoSource);
            var dateText = Field(record, dateCol);
            DateOnly? date = StatementValues.TryParseDate(dateText, order, out var d) ? d : null;

            decimal? amount = null;
            string? amountError = null;
            if (useSplitColumns)
            {
                var outText = Field(record, outCol);
                var inText = Field(record, inCol);
                var outOk = StatementValues.TryParseAmount(outText, out var outAmount) || outText is null;
                var inOk = StatementValues.TryParseAmount(inText, out var inAmount) || inText is null;
                if (!outOk || !inOk)
                    amountError = $"Couldn't read the amount '{(outOk ? inText : outText)}'.";
                else
                    amount = Math.Abs(inAmount) - Math.Abs(outAmount);
            }
            else
            {
                var text = Field(record, amountCol);
                if (StatementValues.TryParseAmount(text, out var value))
                    amount = options.AmountSign == AmountSign.PositiveIsMoneyIn ? value : -value;
                else
                    amountError = text is null ? "The amount is missing." : $"Couldn't read the amount '{text}'.";
            }

            var error = date is null ? (dateText is null ? "The date is missing." : $"Couldn't read the date '{dateText}'.")
                : amountError
                ?? (amount == 0m ? "The amount is zero." : null);
            rows.Add(new BankImportRow(record.Line, date, amount, payee, reference, memo, PossibleDuplicate: false, error));
        }

        rows = await FlagDuplicatesAsync(accountId, rows, ct);

        var used = new List<string> { $"Date: {records[0].Fields[dateCol]}" };
        if (useSplitColumns)
            used.Add($"Money out: {records[0].Fields[outCol!.Value]}, money in: {records[0].Fields[inCol!.Value]}");
        else
            used.Add($"Amount: {records[0].Fields[amountCol!.Value]}");
        if (payeeSource is { } p)
            used.Add($"Payee: {records[0].Fields[p]}");
        if (referenceCol is { } rc)
            used.Add($"Reference: {records[0].Fields[rc]}");
        if (memoSource is { } m)
            used.Add($"Memo: {records[0].Fields[m]}");

        return new BankImportPreview(rows, order, ambiguous && options.DateOrder is null, useSplitColumns, used);
    }

    /// <summary>Records every row as a spend or receive, all in one save, or none if any row is invalid.</summary>
    /// <returns>How many transactions were created.</returns>
    public async Task<int> ImportAsync(Guid accountId, IReadOnlyCollection<ImportBankRowCommand> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new DomainException("import.nothing_selected", "Choose at least one transaction to import.");
        if (rows.Count > MaxRows)
            throw new DomainException("import.too_many_rows", $"Import at most {MaxRows} transactions at a time.");

        var batchId = Guid.CreateVersion7();
        foreach (var row in rows)
        {
            if (row.Amount == 0m)
                throw new DomainException("bank.split_zero", "Amounts can't be zero.");
            var kind = row.Amount > 0m ? BankTransactionKind.Receive : BankTransactionKind.Spend;
            await banking.StageAsync(
                new RecordBankTransactionCommand(kind, accountId, row.Date, [new BankSplit(row.CategoryAccountId, Math.Abs(row.Amount))], row.Payee, row.Reference, row.Memo),
                batchId,
                ct);
        }

        await bank.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<List<BankImportRow>> FlagDuplicatesAsync(Guid accountId, List<BankImportRow> rows, CancellationToken ct)
    {
        var dated = rows.Where(r => r.Date is not null).ToList();
        if (dated.Count == 0)
            return rows;

        var existing = (await bank.ListTransactionsAsync(accountId, dated.Min(r => r.Date!.Value), dated.Max(r => r.Date!.Value), ct))
            .Where(t => t.Status == BankTransactionStatus.Posted)
            .Select(t => (t.Date, t.SignedAmount, Payee: Key(t.Payee)))
            .ToHashSet();

        return rows
            .Select(r => r.Date is { } date && r.Amount is { } amount && existing.Contains((date, amount, Key(r.Payee)))
                ? r with { PossibleDuplicate = true }
                : r)
            .ToList();
    }

    private static string Key(string? payee) => payee?.Trim().ToUpperInvariant() ?? string.Empty;
}
