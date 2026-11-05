using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Banking;

public sealed class RecurringExpenseLine
{
    private RecurringExpenseLine() { }

    internal RecurringExpenseLine(int lineNumber, BankSplit split)
    {
        LineNumber = lineNumber;
        AccountId = split.AccountId;
        Amount = split.Amount;
        Description = split.Memo;
        ClassId = split.ClassId;
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Description { get; private set; }
    public Guid? ClassId { get; private set; }

    public BankSplit ToSplit() => new(AccountId, Amount, Description, ClassId);
}

/// <summary>
/// An expense paid on a schedule straight from a bank or card account, with no bill: a card subscription, an
/// autopay loan or insurance payment. Each occurrence is an ordinary spend.
/// </summary>
public sealed class RecurringExpense
{
    private readonly List<RecurringExpenseLine> _lines = [];

    private RecurringExpense() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }

    /// <summary>The bank or credit card account it's paid from.</summary>
    public Guid AccountId { get; private set; }

    public Guid? VendorId { get; private set; }

    /// <summary>Who's paid, when there's no vendor (or to print otherwise than the vendor's name).</summary>
    public string? Payee { get; private set; }

    public string? Memo { get; private set; }
    public RecurrenceFrequency Frequency { get; private set; }
    public int Interval { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? MaxOccurrences { get; private set; }
    public int OccurrencesPosted { get; private set; }

    /// <summary>Added to the count to find the place in the schedule: a schedule change or a pause moves it (<see cref="Recurrence.Rebase"/>).</summary>
    public int IndexOffset { get; private set; }

    /// <summary>Record expenses automatically when due; otherwise wait for "Record due expenses".</summary>
    public bool AutoCreate { get; private set; }

    public bool IsActive { get; private set; }
    public Guid? LastTransactionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? LocationId { get; private set; }

    /// <summary>Tag ids, comma separated.</summary>
    public string? Tags { get; private set; }

    public IReadOnlyList<Guid> TagIds => Recurrence.ParseTags(Tags);

    public IReadOnlyList<RecurringExpenseLine> Lines => _lines;

    public decimal Total => _lines.Sum(l => l.Amount);

    public DateOnly? NextDate => Recurrence.NextDate(Frequency, Interval, StartDate, EndDate, MaxOccurrences, OccurrencesPosted, IndexOffset);

    public bool IsDue(DateOnly today) => IsActive && NextDate is { } next && next <= today;

    public static RecurringExpense Create(RecurringExpenseDetails details, DateTimeOffset createdAt)
    {
        var template = new RecurringExpense { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        template.Update(details);
        return template;
    }

    public void Update(RecurringExpenseDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "recurring.name_too_long", "Name")
            ?? throw new DomainException("recurring.name_required", "Give the recurring expense a name.");
        Memo = Text.Optional(details.Memo, 1000, "banking.memo_too_long", "Memo");
        Payee = Text.Optional(details.Payee, 100, "banking.payee_too_long", "Payee");
        var schedule = Recurrence.Rebase(Frequency, Interval, StartDate, IndexOffset, OccurrencesPosted, details.Frequency, details.Interval, details.StartDate);
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        Recurrence.Validate(details.Interval, details.StartDate, details.EndDate, details.MaxOccurrences);
        var splits = (details.Lines ?? []).Where(l => l.Amount != 0m).ToList();
        if (splits.Count == 0)
            throw new DomainException("recurringexpense.no_lines", "Add at least one category with an amount.");
        if (splits.Any(l => decimal.Round(l.Amount, 2) != l.Amount))
            throw new DomainException("banking.amount_invalid", "Amounts can have at most 2 decimal places.");
        if (splits.Sum(l => l.Amount) <= 0m)
            throw new DomainException("recurringexpense.total_not_positive", "The expense's total must be more than zero.");
        if (splits.Any(l => l.AccountId == details.AccountId))
            throw new DomainException("banking.split_is_account", "A category can't be the account it's paid from.");

        AccountId = details.AccountId;
        VendorId = details.VendorId == Guid.Empty ? null : details.VendorId;
        Interval = details.Interval;
        (StartDate, IndexOffset) = schedule;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoCreate = details.AutoCreate;
        LocationId = details.LocationId == Guid.Empty ? null : details.LocationId;
        Tags = Recurrence.JoinTags(details.TagIds);
        _lines.Clear();
        _lines.AddRange(splits.Select((l, i) => new RecurringExpenseLine(i + 1, l with { Memo = Text.Optional(l.Memo, 1000, "banking.memo_too_long", "Description") })));
    }

    public void RecordCreated(Guid transactionId)
    {
        OccurrencesPosted++;
        LastTransactionId = transactionId;
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

/// <param name="AccountId">The bank or credit card account it's paid from.</param>
public sealed record RecurringExpenseDetails(
    string Name,
    Guid AccountId,
    IReadOnlyList<BankSplit> Lines,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    int Interval = 1,
    DateOnly? EndDate = null,
    int? MaxOccurrences = null,
    bool AutoCreate = false,
    Guid? VendorId = null,
    string? Payee = null,
    string? Memo = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);
