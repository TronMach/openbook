using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Import;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Application.Nonprofit;

/// <param name="Skipped">Rows there already, in the history or recorded here as a gift.</param>
public sealed record GiftHistoryImport(int Added, int Skipped);

/// <summary>
/// Gifts from before the move (C080): imported from the old system's gift list as a CSV, kept per donor for their
/// history and giving statements, never posted. All or nothing; a row whose donor isn't a customer stops the file. A gift
/// already there with the same donor, date, amount and reference answers for one row, so a file can be imported again
/// without doubling anyone's statement.
/// </summary>
public sealed class GiftHistoryService(INonprofitStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public const string EntityType = "gift_history";
    public const int MaxRows = 20000;

    public async Task<IReadOnlyList<GiftHistory>> ListAsync(Guid donorId, CancellationToken ct = default) =>
        [.. (await store.ListGiftHistoryAsync(donorId, ct)).OrderByDescending(g => g.Date)];

    public async Task<int> ImportCsvAsync(string csv, CancellationToken ct = default) => (await ImportAsync(csv, ct)).Added;

    public async Task<GiftHistoryImport> ImportAsync(string csv, CancellationToken ct = default)
    {
        var donors = (await sales.ListCustomersAsync(ct)).GroupBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var read = Read(csv, donors, clock.UtcNow);
        var (first, last) = (read.Min(g => g.Date), read.Max(g => g.Date));
        // Given in kind, a gift is known by what it was: the history keeps no value for it.
        var standing = (await store.ListGiftHistoryAsync(null, ct)).Select(Key)
            .Concat((await store.FindDonationsAsync(d => d.Status == DocumentStatus.Posted && d.ReturnedOn == null && d.Date >= first && d.Date <= last, ct))
                .Select(d => (d.DonorId, d.Date, d.Kind, d.Kind == GiftKind.InKind ? 0m : d.Amount, d.Reference, d.Kind == GiftKind.InKind ? d.Description : null)))
            .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var gifts = new List<GiftHistory>();
        foreach (var gift in read)
        {
            if (standing.TryGetValue(Key(gift), out var held) && held > 0)
            {
                standing[Key(gift)] = held - 1;
                continue;
            }

            gifts.Add(gift);
            store.Add(gift);
        }

        if (gifts.Count == 0)
            return new GiftHistoryImport(0, read.Count);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, Guid.Empty, null,
            JsonSerializer.Serialize(new { Imported = gifts.Count, Total = gifts.Sum(g => g.Amount) })));
        await store.SaveChangesAsync(ct);
        return new GiftHistoryImport(gifts.Count, read.Count - gifts.Count);
    }

    private static (Guid DonorId, DateOnly Date, GiftKind Kind, decimal Amount, string? Reference, string? Description) Key(GiftHistory g) =>
        (g.DonorId, g.Date, g.Kind, g.Amount, g.Reference, g.Kind == GiftKind.InKind ? g.Description : null);

    /// <summary>
    /// The CSV reader on its own (fuzzed): a Donor (or Name) column, Date and Amount, and if there are, Description
    /// (Memo, Purpose, Fund), Reference (Check number), In kind (Yes/No, or a Type column saying "In kind" or "Non-cash"),
    /// Goods value and Goods description.
    /// </summary>
    public static IReadOnlyList<GiftHistory> Read(string csv, IReadOnlyDictionary<string, Guid> donors, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(donors);
        var records = Csv.Parse(csv ?? "");
        if (records.Count < 2)
            throw new DomainException("import.empty", "The file needs a header row and at least one gift.");
        if (records.Count - 1 > MaxRows)
            throw new DomainException("import.too_many_rows", $"A gift history file can have at most {MaxRows} rows.");
        var cols = new CsvColumns(records[0]);
        var donorCol = cols.Find("donor", "name", "donorname", "customer", "constituent") ?? throw new DomainException("import.no_donor_column", "The file needs a Donor column.");
        var dateCol = cols.Find("date", "giftdate", "datereceived") ?? throw new DomainException("import.no_date_column", "The file needs a Date column.");
        var amountCol = cols.Find("amount", "giftamount", "total") ?? throw new DomainException("import.no_amount_column", "The file needs an Amount column.");
        var descriptionCol = cols.Find("description", "memo", "purpose", "fund", "campaign");
        var referenceCol = cols.Find("reference", "checknumber", "checkno", "ref");
        var kindCol = cols.Find("inkind", "type", "gifttype", "kind");
        var goodsValueCol = cols.Find("goodsvalue", "valuereceived", "fairmarketvalue");
        var goodsCol = cols.Find("goodsdescription", "goodsreceived", "received");
        var rows = records.Skip(1).Where(r => r.Fields.Any(f => f.Trim().Length > 0)).ToList();
        var order = StatementValues.DetectDateOrder([.. rows.Select(r => CsvColumns.Field(r, dateCol)).OfType<string>().Take(500)], CultureInfo.CurrentCulture, out _);
        var gifts = new List<GiftHistory>();
        var line = 1;
        foreach (var r in rows)
        {
            line++;
            string? F(int? c) => CsvColumns.Field(r, c)?.Trim();
            var name = F(donorCol) ?? throw new DomainException("import.no_donor", $"Line {line}: the donor is missing.");
            if (!donors.TryGetValue(name, out var donorId))
                throw new DomainException("import.unknown_donor", $"Line {line}: there's no customer named \"{Clip(name)}\". Import the donors first.");
            if (!StatementValues.TryParseDate(F(dateCol), order, out var date))
                throw new DomainException("import.bad_date", $"Line {line}: \"{Clip(F(dateCol) ?? "")}\" isn't a date.");
            var kindText = Csv.Normalize(F(kindCol) ?? "");
            var inKind = kindText is "yes" or "y" or "true" or "goods"
                || kindText.StartsWith("inkind", StringComparison.Ordinal) || kindText.StartsWith("noncash", StringComparison.Ordinal);
            decimal amount = 0m;
            if (F(amountCol) is { } amountText && !StatementValues.TryParseAmount(amountText, out amount))
                throw new DomainException("import.bad_amount", $"Line {line}: \"{Clip(amountText)}\" isn't an amount.");
            var goodsValue = 0m;
            if (F(goodsValueCol) is { } goodsText && !StatementValues.TryParseAmount(goodsText, out goodsValue))
                throw new DomainException("import.bad_amount", $"Line {line}: \"{Clip(goodsText)}\" isn't an amount.");
            try
            {
                gifts.Add(GiftHistory.Create(donorId, date, inKind ? 0m : amount, inKind ? GiftKind.InKind : GiftKind.Cash, F(descriptionCol), F(referenceCol),
                    goodsValue, F(goodsCol), now));
            }
            catch (DomainException ex)
            {
                throw new DomainException(ex.Code, $"Line {line}: {ex.Message}");
            }
        }

        return gifts.Count > 0 ? gifts : throw new DomainException("import.empty", "The file has no gifts.");
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var gift = await store.GetGiftHistoryAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, gift.Id,
            JsonSerializer.Serialize(new { gift.DonorId, gift.Date, gift.Amount, Kind = gift.Kind.ToString(), gift.Description, gift.Reference }), null));
        store.Remove(gift);
        await store.SaveChangesAsync(ct);
    }

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
