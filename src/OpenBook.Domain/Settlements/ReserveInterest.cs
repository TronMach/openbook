using OpenBook.Domain.Common;

namespace OpenBook.Domain.Settlements;

/// <summary>A month's interest on one owner's reserve.</summary>
public sealed record ReserveInterestLineDraft(Guid OwnerId, int ReserveNumber, decimal Balance, decimal Amount);

public sealed class ReserveInterestLine
{
    private ReserveInterestLine() { }

    internal ReserveInterestLine(int lineNumber, ReserveInterestLineDraft draft)
    {
        LineNumber = lineNumber;
        (OwnerId, ReserveNumber, Balance) = (draft.OwnerId, draft.ReserveNumber, draft.Balance);
        Amount = draft.Amount > 0m && decimal.Round(draft.Amount, 2) == draft.Amount ? draft.Amount
            : throw new DomainException("interest.amount", "Interest is more than zero, to the cent.");
    }

    public int LineNumber { get; private set; }
    public Guid OwnerId { get; private set; }
    public int ReserveNumber { get; private set; }

    /// <summary>The reserve's balance it was worked out on.</summary>
    public decimal Balance { get; private set; }

    public decimal Amount { get; private set; }
}

/// <summary>
/// A month's interest on owner-operators' reserves: Dr the interest expense, Cr each owner's reserve account, as one
/// entry (<see cref="Source"/>) dated the month's last day. One a month while it stands; voiding it reverses its entry.
/// The interest is the owner's, added to their reserve, and isn't pay on their 1099-NEC.
/// </summary>
public sealed class ReserveInterestRun
{
    public const string Source = "settlements.reserve_interest";

    private readonly List<ReserveInterestLine> _lines = [];

    private ReserveInterestRun() { }

    public Guid Id { get; private set; }

    /// <summary>The month's last day: its date and the balances' date.</summary>
    public DateOnly Date { get; private set; }

    public Guid ExpenseAccountId { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public bool IsVoided { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<ReserveInterestLine> Lines => [.. _lines.OrderBy(l => l.LineNumber)];

    public decimal Total => _lines.Sum(l => l.Amount);

    public static ReserveInterestRun Create(Guid id, DateOnly monthEnd, Guid expenseAccountId, IReadOnlyList<ReserveInterestLineDraft> lines, Guid entryId, DateTimeOffset createdAt, string? createdBy)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (monthEnd.AddDays(1).Day != 1)
            throw new DomainException("interest.month_end", "Interest is added for a whole month, on its last day.");
        if (lines.Count == 0)
            throw new DomainException("interest.nothing", "No reserve earns interest for that month.");
        var run = new ReserveInterestRun
        {
            Id = id, Date = monthEnd, ExpenseAccountId = expenseAccountId, JournalEntryId = entryId, CreatedAt = createdAt.ToUniversalTime(), CreatedBy = createdBy,
        };
        run._lines.AddRange(lines.Select((l, i) => new ReserveInterestLine(i + 1, l)));
        return run;
    }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("interest.voided", "This interest is already voided.");
        IsVoided = true;
    }

    /// <summary>A month's interest on a balance at a yearly rate, to the cent.</summary>
    public static decimal On(decimal balance, decimal yearlyPercent) =>
        balance <= 0m ? 0m : decimal.Round(balance * yearlyPercent / 100m / 12m, 2, MidpointRounding.AwayFromZero);
}
