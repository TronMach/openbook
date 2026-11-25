using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>When a sales rep's commission on an invoice is earned. Sales receipts are paid on the spot either way.</summary>
public enum CommissionBasis
{
    /// <summary>As the invoice is paid: each payment earns its share.</summary>
    Paid,

    /// <summary>On the invoice date, paid or not.</summary>
    Invoiced,
}

/// <summary>The document a commission is worked out on.</summary>
public enum CommissionSource
{
    Invoice,
    SalesReceipt,
    CreditMemo,
}

/// <summary>What earned (or took back) a commission.</summary>
public enum CommissionEvent
{
    /// <summary>The document itself.</summary>
    Sale,

    /// <summary>A customer payment on an invoice.</summary>
    Payment,

    /// <summary>The bank took that payment back.</summary>
    Returned,

    /// <summary>A retainer applied to an invoice.</summary>
    Retainer,

    /// <summary>A credit memo, itself taken back from its sales rep, applied to an invoice.</summary>
    Credit,
}

/// <summary>
/// What one commission is for: a rep, a document and what settled it. <see cref="SettlementId"/> is the payment, retainer
/// application or credit memo; <see cref="Guid.Empty"/> for the document itself.
/// </summary>
public readonly record struct CommissionKey(Guid WorkerId, CommissionSource Source, Guid DocumentId, CommissionEvent Kind, Guid SettlementId);

/// <summary>When commission is earned, and the account commission bills go to. One row per company.</summary>
public sealed class CommissionSettings
{
    /// <summary>Always the same, so two first saves at once clash instead of making two rows.</summary>
    public static readonly Guid SingleId = new("c0331550-0000-7000-8000-000000000001");

    private CommissionSettings() { }

    public Guid Id { get; private set; }
    public CommissionBasis Basis { get; private set; }

    /// <summary>The account the last run was billed to; null before the first (then "Commissions").</summary>
    public Guid? ExpenseAccountId { get; private set; }

    public static CommissionSettings Create() => new() { Id = SingleId };

    public void SetBasis(CommissionBasis basis) =>
        Basis = Enum.IsDefined(basis) ? basis : throw new DomainException("commission.basis_invalid", "Commission is earned when an invoice is paid or when it's invoiced.");

    public void SetExpenseAccount(Guid accountId) => ExpenseAccountId = accountId;
}

/// <param name="Category">Only items in this category (matched ignoring case); null for everything without its own rate.</param>
/// <param name="StartsOn">For documents dated from this day; null from the beginning.</param>
public sealed record CommissionRateDetails(Guid WorkerId, decimal Percent, string? Category = null, DateOnly? StartsOn = null);

/// <summary>
/// A sales rep's commission rate: a percent of what they sell, or of one item category, from a date. A rise is another
/// rate from its own date, so documents dated before it keep theirs.
/// </summary>
public sealed class CommissionRate
{
    private CommissionRate() { }

    public Guid Id { get; private set; }
    public Guid WorkerId { get; private set; }
    public string? Category { get; private set; }
    public decimal Percent { get; private set; }
    public DateOnly? StartsOn { get; private set; }

    public static CommissionRate Create(CommissionRateDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Percent is < 0m or > 100m || decimal.Round(details.Percent, 4) != details.Percent)
            throw new DomainException("commission.percent_invalid", "A commission rate is from 0 to 100 percent, with at most 4 decimals.");
        return new CommissionRate
        {
            Id = Guid.CreateVersion7(),
            WorkerId = details.WorkerId,
            Category = Text.Optional(details.Category, 50, "commission.category_too_long", "Category"),
            Percent = details.Percent,
            StartsOn = details.StartsOn,
        };
    }

    public bool SameSlot(CommissionRate other) =>
        other is not null && WorkerId == other.WorkerId && StartsOn == other.StartsOn && string.Equals(Category, other.Category, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The percent a rep earns on an item category on a document's date: the category's own rate, else the rep's general
    /// one, each the latest started by then; 0 without one.
    /// </summary>
    public static decimal For(IEnumerable<CommissionRate> rates, Guid workerId, string? category, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var started = rates.Where(r => r.WorkerId == workerId && (r.StartsOn is null || r.StartsOn <= date))
            .OrderByDescending(r => r.StartsOn ?? DateOnly.MinValue).ToList();
        return (started.FirstOrDefault(r => r.Category is not null && string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
            ?? started.FirstOrDefault(r => r.Category is null))?.Percent ?? 0m;
    }
}

/// <param name="Document">The document as it was numbered when the run was made ("Invoice 1001").</param>
/// <param name="Counted">The amount counted, before tax and after discounts; negative when it takes one back.</param>
public sealed record CommissionRunLineDraft(
    CommissionSource Source, Guid DocumentId, CommissionEvent Kind, Guid SettlementId, DateOnly Date, string Document, decimal Counted, decimal Commission);

public sealed class CommissionRunLine
{
    public const int MaxDocumentLength = 50;

    private CommissionRunLine() => Document = string.Empty;

    internal CommissionRunLine(int lineNumber, CommissionRunLineDraft draft)
    {
        Money.EnsureCents(draft.Counted);
        Money.EnsureCents(draft.Commission);
        LineNumber = lineNumber;
        Source = draft.Source;
        DocumentId = draft.DocumentId;
        Kind = draft.Kind;
        SettlementId = draft.SettlementId;
        Date = draft.Date;
        Document = TextCleaning.Cut(draft.Document, MaxDocumentLength);
        Counted = draft.Counted;
        Percent = CommissionRun.Rate(draft.Counted, draft.Commission);
        Commission = draft.Commission;
    }

    public int LineNumber { get; private set; }
    public CommissionSource Source { get; private set; }
    public Guid DocumentId { get; private set; }
    public CommissionEvent Kind { get; private set; }
    public Guid SettlementId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Document { get; private set; }
    public decimal Counted { get; private set; }

    /// <summary>The rate it came to (item categories can have their own), kept as it was.</summary>
    public decimal? Percent { get; private set; }

    public decimal Commission { get; private set; }
}

/// <summary>
/// One rep's commissions made into a bill to their vendor record. It posts nothing itself: voiding the bill frees its
/// lines for the next run.
/// </summary>
public sealed class CommissionRun
{
    private readonly List<CommissionRunLine> _lines = [];

    private CommissionRun() { }

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public Guid WorkerId { get; private set; }
    public DateOnly Through { get; private set; }
    public Guid BillId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<CommissionRunLine> Lines => _lines;
    public decimal Total => _lines.Sum(l => l.Commission);

    public static CommissionRun Create(int number, Guid workerId, DateOnly through, Guid billId, IReadOnlyList<CommissionRunLineDraft> lines, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var run = new CommissionRun { Id = Guid.CreateVersion7(), Number = number, WorkerId = workerId, Through = through, BillId = billId, CreatedAt = createdAt.ToUniversalTime() };
        run._lines.AddRange(lines.Select((l, i) => new CommissionRunLine(i + 1, l)));
        return run.Total > 0m ? run : throw new DomainException("commission.nothing_to_pay", "There's no commission to pay: what's taken back is as much as what's earned.");
    }

    public CommissionKey KeyOf(CommissionRunLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new CommissionKey(WorkerId, line.Source, line.DocumentId, line.Kind, line.SettlementId);
    }

    /// <summary>The percent a commission comes to of the amount counted, to 4 decimals; null when nothing was counted or it isn't a rate.</summary>
    public static decimal? Rate(decimal counted, decimal commission)
    {
        if (counted == 0m)
            return null;
        var percent = decimal.Round(commission * 100m / counted, 4, MidpointRounding.AwayFromZero);
        return Math.Abs(percent) <= 1000m ? percent : null;
    }
}
