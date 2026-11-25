using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.SalesTax;

/// <summary>What use tax is recorded on.</summary>
public enum UseTaxDocument
{
    Bill,
    Expense,
    VendorCredit,
}

/// <summary>One rate's share of a document's use tax, at the percentage charged.</summary>
public sealed class UseTaxLine
{
    private UseTaxLine() => RateName = string.Empty;

    internal UseTaxLine(int position, TaxComponent rate, decimal amount)
    {
        Position = position;
        TaxRateId = rate.TaxRateId;
        RateName = rate.Name;
        Percent = rate.Percent;
        LiabilityAccountId = rate.LiabilityAccountId;
        Amount = amount;
    }

    public int Position { get; private set; }
    public Guid TaxRateId { get; private set; }
    public string RateName { get; private set; }
    public decimal Percent { get; private set; }

    /// <summary>The agency's account, credited (debited on a vendor credit).</summary>
    public Guid LiabilityAccountId { get; private set; }

    public decimal Amount { get; private set; }

    public TaxComponent ToComponent() => new(TaxRateId, RateName, Percent, LiabilityAccountId);
}

/// <summary>
/// Use tax: what the business owes its own state on something bought without sales tax. It belongs to a bill, an expense
/// or a vendor credit and posts at the end of that document's entry: the expense debited in proportion to the document's
/// lines, each rate's agency account credited (a vendor credit is the mirror). Nothing of it is owed to the vendor.
/// </summary>
public sealed class UseTax
{
    /// <summary>The memo on its lines of the entry.</summary>
    public const string Memo = "Use tax";

    private readonly List<UseTaxLine> _lines = [];

    private UseTax() { }

    public Guid Id { get; private set; }
    public UseTaxDocument Kind { get; private set; }

    /// <summary>The bill, bank transaction or vendor credit; each has at most one.</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>The document's date, when it's owed, whatever basis the agency files on.</summary>
    public DateOnly Date { get; private set; }

    public Guid TaxCodeId { get; private set; }

    /// <summary>What it's worked out on: the document's expense and fixed asset lines.</summary>
    public decimal Taxable { get; private set; }

    /// <summary>The document's entry now, and the first of its lines that is use tax (they come last).</summary>
    public Guid JournalEntryId { get; private set; }

    public int FirstLine { get; private set; }

    /// <summary>The document was voided.</summary>
    public bool IsVoided { get; private set; }

    public IReadOnlyList<UseTaxLine> Lines => _lines;

    public decimal Total => _lines.Sum(l => l.Amount);

    /// <summary>1, or -1 on a vendor credit, which takes it back.</summary>
    public decimal Sign => Kind == UseTaxDocument.VendorCredit ? -1m : 1m;

    /// <summary>Whether a line of the document's entry is its use tax rather than what was bought.</summary>
    public bool Covers(Guid entryId, int lineNumber) => entryId == JournalEntryId && lineNumber >= FirstLine;

    /// <summary>The total was typed rather than worked out from the rates.</summary>
    public bool IsTyped => Total != TaxCalculator.TaxOn(Taxable, _lines.Sum(l => l.Percent));

    /// <summary>
    /// Expenses and fixed assets carry it. Stock doesn't: what's bought to resell isn't taxed when it's bought, and its
    /// cost has to stay what the vendor charged. Nor do balance sheet accounts other records keep.
    /// </summary>
    public static bool CanCarry(AccountType type) => type.ClassOf() == AccountClass.Expense || type == AccountType.FixedAsset;

    public static UseTax Create(UseTaxDocument kind, Guid documentId) =>
        new() { Id = Guid.CreateVersion7(), Kind = kind, DocumentId = documentId };

    /// <summary>Takes the document's date and entry and what's charged; again on a correction.</summary>
    public void Set(DateOnly date, Guid taxCodeId, decimal taxable, IReadOnlyList<(TaxComponent Rate, decimal Amount)> rates, Guid journalEntryId, int firstLine)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (IsVoided)
            throw new DomainException("usetax.voided", "The document this use tax is on is voided.");
        Date = date;
        TaxCodeId = taxCodeId;
        Taxable = taxable;
        JournalEntryId = journalEntryId;
        FirstLine = firstLine;
        _lines.Clear();
        _lines.AddRange(rates.Select((r, i) => new UseTaxLine(i + 1, r.Rate, r.Amount)));
    }

    public void Void() => IsVoided = true;

    /// <summary>
    /// The total spread over accounts (and classes) in proportion to the lines, to the cent, the cents left over going to
    /// the largest remainders. A negative line (a discount) takes its share back.
    /// </summary>
    public static IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> Spread(decimal total, IReadOnlyCollection<(Guid AccountId, Guid? ClassId, decimal Amount)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var groups = lines.GroupBy(l => (l.AccountId, l.ClassId)).Select(g => (g.Key.AccountId, g.Key.ClassId, Weight: g.Sum(l => l.Amount))).ToList();
        var sum = groups.Sum(g => g.Weight);
        if (sum <= 0m)
            return [];
        var exact = groups.Select(g => total * (g.Weight / sum)).ToList();
        var shares = exact.Select(e => Math.Floor(e * 100m) / 100m).ToList();
        var cents = (int)((total - shares.Sum()) * 100m);
        foreach (var i in Enumerable.Range(0, shares.Count).OrderByDescending(i => exact[i] - shares[i]).ThenBy(i => i).Take(cents))
            shares[i] += 0.01m;
        return groups.Select((g, i) => (g.AccountId, g.ClassId, Amount: shares[i])).Where(s => s.Amount != 0m).ToList();
    }

    /// <summary>Its journal lines: what was bought debited, the agencies credited; the other way round on a vendor credit.</summary>
    public static IReadOnlyList<JournalLineDraft> Postings(
        UseTaxDocument kind, IReadOnlyCollection<(Guid AccountId, Guid? ClassId, decimal Amount)> lines, IReadOnlyList<(TaxComponent Rate, decimal Amount)> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var debits = Spread(rates.Sum(r => r.Amount), lines).Select(s => (s.AccountId, s.ClassId, s.Amount, Memo))
            .Concat(rates.Where(r => r.Amount != 0m).Select(r => (r.Rate.LiabilityAccountId, (Guid?)null, -r.Amount, $"{Memo} · {TaxCalculator.Describe(r.Rate.Name, r.Rate.Percent)}")));
        var sign = kind == UseTaxDocument.VendorCredit ? -1m : 1m;
        return debits.Select(d => (d.Item1, d.Item2, Amount: sign * d.Item3, d.Item4))
            .Select(d => d.Amount > 0m ? JournalLineDraft.Dr(d.Item1, d.Amount, d.Item4, d.Item2) : JournalLineDraft.Cr(d.Item1, -d.Amount, d.Item4, d.Item2))
            .ToList();
    }
}
