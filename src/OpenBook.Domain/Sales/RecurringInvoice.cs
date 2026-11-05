using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Sales;

/// <summary>A line on a recurring invoice template, kept as typed so each invoice uses the item's current account.</summary>
public sealed class RecurringInvoiceLine
{
    private RecurringInvoiceLine() { }

    internal RecurringInvoiceLine(int lineNumber, SalesLineDraft draft)
    {
        LineNumber = lineNumber;
        Kind = draft.Kind;
        ItemId = draft.ItemId;
        Description = Text.Optional(draft.Description, 1000, "sales.line_description_too_long", "Line description");
        Quantity = SalesLine.ValidQuantity(draft.Quantity);
        Rate = SalesLine.ValidRate(draft.Rate);
        ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId;
    }

    public int LineNumber { get; private set; }
    public SalesLineKind Kind { get; private set; }
    public Guid? ItemId { get; private set; }
    public string? Description { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Rate { get; private set; }
    public Guid? ClassId { get; private set; }

    public SalesLineDraft ToDraft() => new(ItemId, Description, Quantity, Rate, Kind, ClassId: ClassId);
}

/// <summary>An invoice created on a schedule, such as a monthly retainer.</summary>
public sealed class RecurringInvoice
{
    private readonly List<RecurringInvoiceLine> _lines = [];

    private RecurringInvoice() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>Message shown to the customer on each invoice.</summary>
    public string? Memo { get; private set; }

    public RecurrenceFrequency Frequency { get; private set; }
    public int Interval { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? MaxOccurrences { get; private set; }
    public int OccurrencesPosted { get; private set; }

    /// <summary>Added to the count to find the place in the schedule: a schedule change or a pause moves it (<see cref="Recurrence.Rebase"/>).</summary>
    public int IndexOffset { get; private set; }

    /// <summary>Create invoices automatically when due; otherwise wait for "Create due invoices".</summary>
    public bool AutoCreate { get; private set; }

    /// <summary>Email each invoice to the customer as it's created.</summary>
    public bool AutoEmail { get; private set; }

    public bool IsActive { get; private set; }
    public Guid? LastInvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The location every document or entry made from this template gets.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Tag ids, comma separated: a template sits outside the ledger, so it keeps them as text.</summary>
    public string? Tags { get; private set; }

    public IReadOnlyList<Guid> TagIds => Recurrence.ParseTags(Tags);

    public IReadOnlyList<RecurringInvoiceLine> Lines => _lines;

    public DateOnly? NextDate => Recurrence.NextDate(Frequency, Interval, StartDate, EndDate, MaxOccurrences, OccurrencesPosted, IndexOffset);

    public bool IsDue(DateOnly today) => IsActive && NextDate is { } next && next <= today;

    public static RecurringInvoice Create(RecurringInvoiceDetails details, DateTimeOffset createdAt)
    {
        var template = new RecurringInvoice { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        template.Update(details);
        return template;
    }

    /// <summary>Lines are checked against items when each invoice is created; here only their shape.</summary>
    public void Update(RecurringInvoiceDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(details.Lines);
        Name = Text.Optional(details.Name, 100, "recurring.name_too_long", "Name")
            ?? throw new DomainException("recurring.name_required", "Give the recurring invoice a name.");
        Memo = Text.Optional(details.Memo, 1000, "invoice.memo_too_long", "Message");
        var schedule = Recurrence.Rebase(Frequency, Interval, StartDate, IndexOffset, OccurrencesPosted, details.Frequency, details.Interval, details.StartDate);
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        Recurrence.Validate(details.Interval, details.StartDate, details.EndDate, details.MaxOccurrences);
        if (details.Lines.Count == 0)
            throw new DomainException("sales.no_lines", "Add at least one line.");
        if (details.AutoEmail && !details.AutoCreate)
            throw new DomainException("recurring.email_needs_auto", "Invoices can only be emailed automatically when they're created automatically.");

        CustomerId = details.CustomerId;
        Interval = details.Interval;
        (StartDate, IndexOffset) = schedule;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoCreate = details.AutoCreate;
        AutoEmail = details.AutoEmail;
        LocationId = details.LocationId == Guid.Empty ? null : details.LocationId;
        Tags = Recurrence.JoinTags(details.TagIds);
        _lines.Clear();
        _lines.AddRange(details.Lines.Select((l, i) => new RecurringInvoiceLine(i + 1, l)));
    }

    public DateOnly OccurrenceDate(int index) => Recurrence.OccurrenceDate(Frequency, Interval, StartDate, index);

    public void RecordCreated(Guid invoiceId)
    {
        OccurrencesPosted++;
        LastInvoiceId = invoiceId;
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

public sealed record RecurringInvoiceDetails(
    string Name,
    Guid CustomerId,
    IReadOnlyList<SalesLineDraft> Lines,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    int Interval = 1,
    DateOnly? EndDate = null,
    int? MaxOccurrences = null,
    bool AutoCreate = false,
    bool AutoEmail = false,
    string? Memo = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);
