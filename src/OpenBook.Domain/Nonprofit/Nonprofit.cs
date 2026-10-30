using OpenBook.Domain.Common;
using OpenBook.Domain.Segments;

namespace OpenBook.Domain.Nonprofit;

/// <summary>
/// Net assets released from donor restrictions: the purpose was met (the money was spent on it) or the time passed.
/// Posts one entry moving the amount from the "with donor restrictions" release account to the "without" one, both
/// on the purpose's class, so the statement of activities shows it leaving one column and arriving in the other
/// while total net assets don't change. Voided, never edited.
/// </summary>
public sealed class RestrictionRelease
{
    public const string Source = "nonprofit.release";
    public const int MaxMemoLength = 200;

    private RestrictionRelease() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>The purpose (a class) the restriction was for; null for a time restriction or one without a class.</summary>
    public Guid? ClassId { get; private set; }

    public decimal Amount { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static RestrictionRelease Create(Guid id, DateOnly date, Guid? classId, decimal amount, string? memo, Guid journalEntryId, DateTimeOffset createdAt)
    {
        if (amount <= 0m)
            throw new DomainException("release.amount", "Enter an amount above zero to release.");
        if (decimal.Round(amount, 2) != amount)
            throw new DomainException("release.amount_cents", "Amounts are in cents.");
        return new RestrictionRelease
        {
            Id = id,
            Date = date,
            ClassId = classId == Guid.Empty ? null : classId,
            Amount = amount,
            Memo = Text.Optional(memo, MaxMemoLength, "release.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("release.already_voided", "This release is already voided.");
        Status = DocumentStatus.Voided;
    }
}

public static class FunctionalAreas
{
    public static string Label(FunctionalArea? area) => area switch
    {
        FunctionalArea.Program => "Program services",
        FunctionalArea.ManagementAndGeneral => "Management and general",
        FunctionalArea.Fundraising => "Fundraising",
        _ => "Not assigned",
    };

    /// <summary>The class itself if it has an area, else the nearest parent that has one; null when none does.</summary>
    public static Segment? Deciding(Guid? classId, IReadOnlyDictionary<Guid, Segment> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        for (var depth = 0; classId is { } id && classes.TryGetValue(id, out var segment) && depth <= Segment.MaxDepth; depth++)
        {
            if (segment.FunctionalArea is not null)
                return segment;
            classId = segment.ParentId;
        }

        return null;
    }
}
