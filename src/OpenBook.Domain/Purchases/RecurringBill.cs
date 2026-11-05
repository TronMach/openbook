using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Purchases;

public sealed class RecurringBillLine
{
    private RecurringBillLine() { }

    internal RecurringBillLine(int lineNumber, BillLine line)
    {
        LineNumber = lineNumber;
        AccountId = line.AccountId;
        Amount = line.Amount;
        Description = line.Description;
        ClassId = line.ClassId;
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Description { get; private set; }
    public Guid? ClassId { get; private set; }

    public BillLineDraft ToDraft() => new(AccountId, Amount, Description, ClassId: ClassId);
}

/// <summary>A bill entered on a schedule, such as rent or a subscription.</summary>
public sealed class RecurringBill
{
    private readonly List<RecurringBillLine> _lines = [];

    private RecurringBill() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public Guid VendorId { get; private set; }
    public string? Memo { get; private set; }
    public RecurrenceFrequency Frequency { get; private set; }
    public int Interval { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? MaxOccurrences { get; private set; }
    public int OccurrencesPosted { get; private set; }

    /// <summary>Added to the count to find the place in the schedule: a schedule change or a pause moves it (<see cref="Recurrence.Rebase"/>).</summary>
    public int IndexOffset { get; private set; }

    /// <summary>Enter bills automatically when due; otherwise wait for "Enter due bills".</summary>
    public bool AutoCreate { get; private set; }

    public bool IsActive { get; private set; }
    public Guid? LastBillId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The location every document or entry made from this template gets.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Tag ids, comma separated: a template sits outside the ledger, so it keeps them as text.</summary>
    public string? Tags { get; private set; }

    public IReadOnlyList<Guid> TagIds => Recurrence.ParseTags(Tags);

    public IReadOnlyList<RecurringBillLine> Lines => _lines;

    public DateOnly? NextDate => Recurrence.NextDate(Frequency, Interval, StartDate, EndDate, MaxOccurrences, OccurrencesPosted, IndexOffset);

    public bool IsDue(DateOnly today) => IsActive && NextDate is { } next && next <= today;

    public static RecurringBill Create(RecurringBillDetails details, DateTimeOffset createdAt)
    {
        var template = new RecurringBill { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        template.Update(details);
        return template;
    }

    public void Update(RecurringBillDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "recurring.name_too_long", "Name")
            ?? throw new DomainException("recurring.name_required", "Give the recurring bill a name.");
        Memo = Text.Optional(details.Memo, 1000, "bill.memo_too_long", "Memo");
        var schedule = Recurrence.Rebase(Frequency, Interval, StartDate, IndexOffset, OccurrencesPosted, details.Frequency, details.Interval, details.StartDate);
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        Recurrence.Validate(details.Interval, details.StartDate, details.EndDate, details.MaxOccurrences);
        if ((details.Lines ?? []).Any(l => l.ItemId is not null))
            throw new DomainException("recurringbill.no_items", "Recurring bills are for categories only; enter inventory purchases on a bill.");
        var lines = Bill.LinesFrom(details.Lines ?? []);

        VendorId = details.VendorId;
        Interval = details.Interval;
        (StartDate, IndexOffset) = schedule;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoCreate = details.AutoCreate;
        LocationId = details.LocationId == Guid.Empty ? null : details.LocationId;
        Tags = Recurrence.JoinTags(details.TagIds);
        _lines.Clear();
        _lines.AddRange(lines.Select((l, i) => new RecurringBillLine(i + 1, l)));
    }

    public DateOnly OccurrenceDate(int index) => Recurrence.OccurrenceDate(Frequency, Interval, StartDate, index);

    public void RecordCreated(Guid billId)
    {
        OccurrencesPosted++;
        LastBillId = billId;
    }

    public void Deactivate() => IsActive = false;

    /// <summary>Turns it back on; dates before <paramref name="skipBefore"/>, if given, are passed over rather than made.</summary>
    public void Reactivate(DateOnly? skipBefore = null)
    {
        if (!IsActive && skipBefore is { } today)
            IndexOffset += Recurrence.Missed(Frequency, Interval, StartDate, OccurrencesPosted + IndexOffset, today);
        IsActive = true;
    }
}

public sealed record RecurringBillDetails(
    string Name,
    Guid VendorId,
    IReadOnlyList<BillLineDraft> Lines,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    int Interval = 1,
    DateOnly? EndDate = null,
    int? MaxOccurrences = null,
    bool AutoCreate = false,
    string? Memo = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);
