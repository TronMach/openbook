using OpenBook.Domain.Common;

namespace OpenBook.Domain.Projects;

public enum PreBillStatus
{
    Draft,
    Posted,
    Discarded,
}

public enum PreBillLineKind
{
    Time,
    Mileage,
    Expense,
}

/// <summary>
/// A draft bill: a client's unbilled work through a date, gathered for review before anything posts. Lines can be taken
/// out or, for time, billed at another amount with a reason. Posting makes the invoice; nothing reaches the books before.
/// </summary>
public sealed class PreBill
{
    public const int MaxMemoLength = 1000;

    private readonly List<PreBillLine> _lines = [];

    private PreBill() => CreatedBy = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>Who's billed.</summary>
    public Guid CustomerId { get; private set; }

    public DateOnly Through { get; private set; }
    public PreBillStatus Status { get; private set; }
    public string? Memo { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; }
    public IReadOnlyList<PreBillLine> Lines => _lines;

    public decimal Worked => _lines.Sum(l => l.Worked);
    public decimal Billed => _lines.Sum(l => l.Billed);

    public static PreBill Create(Guid customerId, DateOnly through, IEnumerable<PreBillLineDraft> lines, DateTimeOffset createdAt, string createdBy)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var bill = new PreBill { Id = Guid.CreateVersion7(), CustomerId = customerId, Through = through, Status = PreBillStatus.Draft, CreatedAt = createdAt.ToUniversalTime(), CreatedBy = createdBy };
        bill._lines.AddRange(lines.Select((l, i) => new PreBillLine(i + 1, l)));
        if (bill._lines.Count == 0)
            throw new DomainException("prebill.nothing", "There's nothing unbilled for this client through that date.");
        return bill;
    }

    private void EnsureDraft()
    {
        if (Status != PreBillStatus.Draft)
            throw new DomainException("prebill.not_draft", "This draft has already been posted or discarded.");
    }

    public void SetMemo(string? memo)
    {
        EnsureDraft();
        Memo = Text.Optional(memo, MaxMemoLength, "prebill.memo_too_long", "Memo");
    }

    /// <summary>Time can go out at another amount, with a reason; other lines go at what they are.</summary>
    public void Bill(int lineNumber, decimal billed, string? reason)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.LineNumber == lineNumber) ?? throw new DomainException("prebill.line_unknown", "That line isn't on this draft.");
        line.Set(billed, reason);
    }

    /// <summary>Takes a line off: the work stays unbilled, for another time.</summary>
    public void Remove(int lineNumber)
    {
        EnsureDraft();
        _lines.RemoveAll(l => l.LineNumber == lineNumber);
    }

    public void Post(Guid? invoiceId)
    {
        EnsureDraft();
        Status = PreBillStatus.Posted;
        InvoiceId = invoiceId;
    }

    public void Discard()
    {
        EnsureDraft();
        Status = PreBillStatus.Discarded;
    }
}

public sealed record PreBillLineDraft(PreBillLineKind Kind, Guid SourceId, DateOnly Date, string Description, decimal Hours, decimal Worked);

/// <summary>One piece of work on a draft bill. <see cref="Worked"/> is its value as recorded, <see cref="Billed"/> what goes on the invoice.</summary>
public sealed class PreBillLine
{
    public const int MaxReasonLength = 200;

    private PreBillLine() => Description = string.Empty;

    internal PreBillLine(int lineNumber, PreBillLineDraft draft)
    {
        LineNumber = lineNumber;
        Kind = draft.Kind;
        SourceId = draft.SourceId;
        Date = draft.Date;
        Description = TextCleaning.Cut(draft.Description, 500);
        Hours = draft.Hours;
        Worked = draft.Worked;
        Billed = draft.Worked;
    }

    public int LineNumber { get; private set; }
    public PreBillLineKind Kind { get; private set; }
    public Guid SourceId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Description { get; private set; }
    public decimal Hours { get; private set; }
    public decimal Worked { get; private set; }
    public decimal Billed { get; private set; }

    /// <summary>Why it's billed at something other than its value.</summary>
    public string? Reason { get; private set; }

    internal void Set(decimal billed, string? reason)
    {
        Money.EnsureCents(billed);
        if (billed < 0m)
            throw new DomainException("prebill.amount", "A line can't be billed below zero.");
        if (billed == Billed && reason == Reason)
            return;
        var said = Text.Optional(reason, MaxReasonLength, "prebill.reason_too_long", "Reason");
        if (billed != Worked && said is null)
            throw new DomainException("prebill.reason_required", "Say why this line is billed at a different amount.");
        (Billed, Reason) = (billed, billed == Worked ? null : said);
    }
}
