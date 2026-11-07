using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Nonprofit;

/// <summary>One gift in a file: what it will become, or why it can't.</summary>
/// <param name="NewDonor">The name isn't on the customer list, so a record will be made.</param>
/// <param name="Purpose">The class named in the file.</param>
public sealed record GiftImportRow(int Line, DateOnly? Date, string? Donor, bool NewDonor, decimal Amount, string? Reference, string? Purpose, ImportRowStatus Status, string? Message);

/// <param name="Warnings">What the reader had to guess, such as the order of ambiguous dates.</param>
public sealed record GiftImportResult(IReadOnlyList<GiftImportRow> Rows, int NewCount, int ExistingCount, int ErrorCount, decimal Total, int NewDonors, IReadOnlyList<string> Warnings)
{
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>What a batch of gifts shares.</summary>
/// <param name="DebitAccountId">The bank account or Undeposited Funds they go into.</param>
/// <param name="IncomeAccountId">Null: "Contributions".</param>
/// <param name="ClassId">The purpose, unless a row names its own.</param>
public sealed record GiftBatchDefaults(Guid DebitAccountId, Guid? IncomeAccountId = null, Guid? ClassId = null, PaymentMethod? Method = null);

/// <summary>
/// Gifts from a CSV file, all or nothing: date, donor, amount, and optionally a check or reference number, a memo and
/// the purpose (a class, by full name). A donor who isn't on the list is added. A gift already in the books with the same
/// donor, date, amount and reference is skipped, one for one, so a file can be imported again.
/// </summary>
public sealed class GiftImportService(ISalesStore sales, INonprofitStore store, GiftService gifts, CustomerService customers, SegmentService segments, IUnitOfWork unitOfWork)
{
    public const int MaxRows = 5000;

    public Task<GiftImportResult> PreviewAsync(string csv, CancellationToken ct = default) => PlanAsync(csv, ct);

    public async Task<GiftImportResult> ImportAsync(string csv, GiftBatchDefaults defaults, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var plan = await PlanAsync(csv, ct);
        SalesImport.EnsureImportable(plan.ErrorCount, plan.NewCount);
        var classes = await ClassNamesAsync(ct);
        var donors = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.DisplayName, StringComparer.OrdinalIgnoreCase);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        foreach (var row in plan.Rows.Where(r => r.Status == ImportRowStatus.New))
        {
            if (!donors.TryGetValue(row.Donor!, out var donor))
                donors[row.Donor!] = donor = await customers.CreateAsync(new CustomerDetails(row.Donor!), ct);
            try
            {
                await gifts.RecordAsync(new RecordGiftCommand(donor.Id, row.Date!.Value, row.Amount, defaults.DebitAccountId, IncomeAccountId: defaults.IncomeAccountId,
                    ClassId: row.Purpose is { } purpose ? classes[purpose] : defaults.ClassId, Reference: row.Reference, Method: defaults.Method), ct);
            }
            catch (DomainException ex) when (ex is not ConcurrencyConflictException)
            {
                throw new DomainException(ex.Code, $"Line {row.Line}: {ex.Message}");
            }
        }

        await transaction.CommitAsync(ct);
        return plan;
    }

    private async Task<IReadOnlyDictionary<string, Guid>> ClassNamesAsync(CancellationToken ct) =>
        (await segments.ListAsync(SegmentKind.Class, includeInactive: false, ct))
            .ToDictionary(c => c.FullName, c => c.Segment.Id, StringComparer.OrdinalIgnoreCase);

    private async Task<GiftImportResult> PlanAsync(string csv, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var dateColumn = cols.Find("date", "giftdate", "donationdate", "received", "datereceived")
            ?? throw new DomainException("import.no_date_column", "The file needs a date column.");
        var donorColumn = cols.Find("donor", "name", "customer", "donorname", "fullname", "from")
            ?? throw new DomainException("import.no_name_column", "The file needs a donor column (\"Donor\" or \"Name\").");
        var amountColumn = cols.Find("amount", "gift", "giftamount", "donation", "total")
            ?? throw new DomainException("import.no_amount_column", "The file needs an amount column.");
        var referenceColumn = cols.Find("reference", "check", "checknumber", "checkno", "ref", "transactionid");
        var memoColumn = cols.Find("memo", "note", "notes");
        var purposeColumn = cols.Find("purpose", "fund", "class", "designation", "program");

        var warnings = new List<string>();
        var dates = records.Skip(1).Select(r => CsvColumns.Field(r, dateColumn)).OfType<string>().ToList();
        var order = StatementValues.DetectDateOrder(dates, CultureInfo.CurrentCulture, out var ambiguous);
        if (ambiguous)
            warnings.Add($"Every date could be read either day-first or month-first; they were read {(order == DateOrder.DayFirst ? "day-first" : "month-first")}. Check them.");

        var classes = await ClassNamesAsync(ct);
        var known = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.DisplayName, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var standing = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted)
            .GroupBy(d => (d.DonorId, d.Date, d.Amount, d.Reference)).ToDictionary(g => g.Key, g => g.Count());
        var newDonors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<GiftImportRow>();
        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c);
            var donor = F(donorColumn)?.Trim();
            var reference = F(referenceColumn)?.Trim();
            var purpose = F(purposeColumn)?.Trim();
            var hasDate = StatementValues.TryParseDate(F(dateColumn), order, out var date);
            var hasAmount = StatementValues.TryParseAmount(F(amountColumn), out var amount);
            var message = !hasDate ? "The date can't be read."
                : string.IsNullOrEmpty(donor) ? "There's no donor."
                : !hasAmount ? "The amount can't be read."
                : amount <= 0m ? "A gift's amount is above zero."
                : purpose is not null && !classes.ContainsKey(purpose) ? $"There's no class named \"{purpose}\"."
                : null;
            if (message is not null)
            {
                rows.Add(new GiftImportRow(r.Line, hasDate ? date : null, donor, false, hasAmount ? amount : 0m, reference, purpose, ImportRowStatus.Error, message));
                continue;
            }

            var isNew = !known.TryGetValue(donor!, out var donorId);
            if (!isNew && standing.TryGetValue((donorId, date, amount, reference), out var held) && held > 0)
            {
                standing[(donorId, date, amount, reference)] = held - 1;
                rows.Add(new GiftImportRow(r.Line, date, donor, false, amount, reference, purpose, ImportRowStatus.Exists, "Already in the books."));
                continue;
            }

            if (isNew)
                newDonors.Add(donor!);
            rows.Add(new GiftImportRow(r.Line, date, donor, isNew, amount, reference, purpose, ImportRowStatus.New, null));
        }

        var added = rows.Where(r => r.Status == ImportRowStatus.New).ToList();
        return new GiftImportResult(rows, added.Count, rows.Count(r => r.Status == ImportRowStatus.Exists), rows.Count(r => r.Status == ImportRowStatus.Error),
            added.Sum(r => r.Amount), newDonors.Count, warnings);
    }
}
