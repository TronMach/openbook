using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

/// <summary>
/// A petty cash box run on the imprest system: a bank-type account holding a fixed float. Spending is recorded as
/// ordinary spends from the account; a count records what's really in the box (posting any over or short), and a
/// replenishment transfers enough from a bank account to bring the box back to the float.
/// </summary>
public sealed class PettyCashFund
{
    private PettyCashFund() { }

    public Guid Id { get; private set; }

    /// <summary>The Bank-type account that is the box.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>What the box holds when full.</summary>
    public decimal FloatAmount { get; private set; }

    /// <summary>Who looks after the box.</summary>
    public string? Custodian { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PettyCashFund Create(Guid accountId, decimal floatAmount, string? custodian, DateTimeOffset now)
    {
        var fund = new PettyCashFund { Id = Guid.CreateVersion7(), AccountId = accountId, IsActive = true, CreatedAt = now };
        fund.Update(floatAmount, custodian);
        return fund;
    }

    public void Update(decimal floatAmount, string? custodian)
    {
        if (floatAmount <= 0m)
            throw new DomainException("petty_cash.float_invalid", "The float must be more than zero.");
        if (decimal.Round(floatAmount, 2) != floatAmount)
            throw new DomainException("petty_cash.float_cents", "Enter the float in whole cents.");
        FloatAmount = floatAmount;
        Custodian = Text.Optional(custodian, 100, "petty_cash.custodian_too_long", "Custodian");
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>
/// A count of the cash in a petty cash box. The book balance is the account's balance on the date; a difference is
/// posted as an over or short (a bank transaction against the over/short account), so the box then agrees.
/// </summary>
public sealed class CashCount
{
    private CashCount() => CountedBy = string.Empty;

    public Guid Id { get; private set; }
    public Guid FundId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Counted { get; private set; }
    public decimal BookBalance { get; private set; }
    public decimal Difference => Counted - BookBalance;

    /// <summary>The over/short transaction posted for a difference, if any.</summary>
    public Guid? AdjustmentTransactionId { get; private set; }

    /// <summary>Notes and coins counted, as "value x count" pairs (e.g. "20x5; 1x3; 0.25x8"), when entered.</summary>
    public string? Breakdown { get; private set; }

    public string? Note { get; private set; }
    public string CountedBy { get; private set; }
    public DateTimeOffset CountedAt { get; private set; }

    public static CashCount Create(
        Guid fundId, DateOnly date, decimal counted, decimal bookBalance, Guid? adjustmentTransactionId, string? breakdown, string? note,
        string countedBy, DateTimeOffset now)
    {
        if (counted < 0m)
            throw new DomainException("petty_cash.count_negative", "The amount counted can't be negative.");
        if (decimal.Round(counted, 2) != counted)
            throw new DomainException("petty_cash.count_cents", "Enter the amount counted in whole cents.");
        return new CashCount
        {
            Id = Guid.CreateVersion7(),
            FundId = fundId,
            Date = date,
            Counted = counted,
            BookBalance = bookBalance,
            AdjustmentTransactionId = adjustmentTransactionId,
            Breakdown = Text.Optional(breakdown, 1000, "petty_cash.breakdown_too_long", "Breakdown"),
            Note = Text.Optional(note, 500, "petty_cash.note_too_long", "Note"),
            CountedBy = countedBy,
            CountedAt = now,
        };
    }
}
