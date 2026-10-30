using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Nonprofit;

/// <summary>
/// A donor's regular gift (a monthly card gift, a standing transfer): each occurrence is recorded as an ordinary money
/// gift on its date, by hand or by the hourly job when <see cref="AutoCreate"/> is on. Naming a pledge makes each gift
/// a payment on it. Dates come from the start date and the occurrence index, as for other recurring templates.
/// Deactivated, never deleted.
/// </summary>
public sealed class RecurringGift
{
    private RecurringGift()
    {
    }

    public Guid Id { get; private set; }
    public Guid DonorId { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The bank account, or Undeposited Funds, each gift goes to.</summary>
    public Guid DepositAccountId { get; private set; }

    public Guid? IncomeAccountId { get; private set; }
    public Guid? ClassId { get; private set; }
    public Guid? PledgeId { get; private set; }
    public string? Memo { get; private set; }
    public RecurrenceFrequency Frequency { get; private set; }
    public int Interval { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? MaxOccurrences { get; private set; }
    public int OccurrencesPosted { get; private set; }
    public bool AutoCreate { get; private set; }
    public bool IsActive { get; private set; }
    public Guid? LastGiftId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public DateOnly? NextDate => Recurrence.NextDate(Frequency, Interval, StartDate, EndDate, MaxOccurrences, OccurrencesPosted);

    public bool IsDue(DateOnly today) => IsActive && NextDate is { } next && next <= today;

    public static RecurringGift Create(RecurringGiftDetails details, DateTimeOffset createdAt)
    {
        var template = new RecurringGift { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        template.Update(details);
        return template;
    }

    public void Update(RecurringGiftDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Amount <= 0m || decimal.Round(details.Amount, 2) != details.Amount)
            throw new DomainException("donation.amount", "Enter an amount above zero, in cents.");
        Frequency = Enum.IsDefined(details.Frequency) ? details.Frequency : throw new DomainException("recurring.frequency_invalid", "Unknown frequency.");
        Recurrence.Validate(details.Interval, details.StartDate, details.EndDate, details.MaxOccurrences);
        DonorId = details.DonorId;
        Amount = details.Amount;
        DepositAccountId = details.DepositAccountId;
        IncomeAccountId = details.IncomeAccountId == Guid.Empty ? null : details.IncomeAccountId;
        ClassId = details.ClassId == Guid.Empty ? null : details.ClassId;
        PledgeId = details.PledgeId == Guid.Empty ? null : details.PledgeId;
        Memo = Text.Optional(details.Memo, Donation.MaxTextLength, "donation.memo_too_long", "Memo");
        Interval = details.Interval;
        StartDate = details.StartDate;
        EndDate = details.EndDate;
        MaxOccurrences = details.MaxOccurrences;
        AutoCreate = details.AutoCreate;
    }

    public void RecordCreated(Guid giftId)
    {
        OccurrencesPosted++;
        LastGiftId = giftId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public sealed record RecurringGiftDetails(
    Guid DonorId,
    decimal Amount,
    Guid DepositAccountId,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    int Interval = 1,
    DateOnly? EndDate = null,
    int? MaxOccurrences = null,
    bool AutoCreate = false,
    Guid? IncomeAccountId = null,
    Guid? ClassId = null,
    Guid? PledgeId = null,
    string? Memo = null);
