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
    }

    public int LineNumber { get; private set; }
    public SalesLineKind Kind { get; private set; }
    public Guid? ItemId { get; private set; }
    public string? Description { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Rate { get; private set; }

    public SalesLineDraft ToDraft() => new(ItemId, Description, Quantity, Rate, Kind);
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

    /// <summary>Create invoices automatically when due; otherwise wait for "Create due invoices".</summary>
    public bool AutoCreate { get; private set; }

    /// <summary>Email each invoice to the customer as it's created.</summary>
    public bool AutoEmail { get; private set; }

    public bool IsActive { get; private set; }
    public Guid? LastInvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<RecurringInvoiceLine> Lines => _lines;

    public DateOnly? NextDate => Recurrence.NextDate(Frequency, Interval, StartDate, EndDate, MaxOccurrences, OccurrencesPosted);

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
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        Recurrence.Validate(details.Interval, details.StartDate, details.EndDate, details.MaxOccurrences);
        if (details.Lines.Count == 0)
            throw new DomainException("sales.no_lines", "Add at least one line.");
        if (details.AutoEmail && !details.AutoCreate)
            throw new DomainException("recurring.email_needs_auto", "Invoices can only be emailed automatically when they're created automatically.");

        CustomerId = details.CustomerId;
        Interval = details.Interval;
        StartDate = details.StartDate;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoCreate = details.AutoCreate;
        AutoEmail = details.AutoEmail;
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

    public void Reactivate() => IsActive = true;
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
    string? Memo = null);
