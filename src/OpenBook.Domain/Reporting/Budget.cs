using OpenBook.Domain.Common;

namespace OpenBook.Domain.Reporting;

/// <param name="Period">1 for the fiscal year's first month, up to 12.</param>
/// <param name="Amount">In the account's natural sign: income and expenses both positive.</param>
public sealed record BudgetAmount(Guid AccountId, int Period, decimal Amount);

/// <summary>One account's budgeted amount for one month of the budget's fiscal year.</summary>
public sealed class BudgetLine
{
    private BudgetLine()
    {
    }

    public Guid AccountId { get; private set; }
    public int Period { get; private set; }
    public decimal Amount { get; private set; }

    internal static BudgetLine Create(BudgetAmount amount) =>
        new() { AccountId = amount.AccountId, Period = amount.Period, Amount = amount.Amount };
}

/// <summary>
/// Monthly amounts for profit and loss accounts over one fiscal year, compared with actual results by the
/// budget vs. actual report. A planning record: it posts nothing, and can be deleted.
/// </summary>
public sealed class Budget
{
    public const int Periods = 12;
    private const decimal MaxAmount = 1_000_000_000_000m;

    private readonly List<BudgetLine> _lines = [];

    private Budget() => Name = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; private set; }

    /// <summary>The first day of the fiscal year the budget covers.</summary>
    public DateOnly FiscalYearStart { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<BudgetLine> Lines => _lines;

    public DateOnly FiscalYearEnd => FiscalYearStart.AddYears(1).AddDays(-1);

    public static Budget Create(string name, DateOnly fiscalYearStart, DateTimeOffset createdAt)
    {
        if (fiscalYearStart.Day != 1)
            throw new DomainException("budget.start_invalid", "A budget's year starts on the first day of a month.");
        return new Budget
        {
            Id = Guid.CreateVersion7(),
            Name = NormalizeName(name),
            FiscalYearStart = fiscalYearStart,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public void Rename(string name) => Name = NormalizeName(name);

    /// <summary>A budget for one class (and its subclasses); null for the whole company.</summary>
    public Guid? ClassId { get; private set; }

    /// <summary>A budget for one location (and those under it); null for every location.</summary>
    public Guid? LocationId { get; private set; }

    public void SetSegments(Guid? classId, Guid? locationId)
    {
        ClassId = classId == Guid.Empty ? null : classId;
        LocationId = locationId == Guid.Empty ? null : locationId;
    }

    /// <summary>The first day of month <paramref name="period"/> (1–12).</summary>
    public DateOnly PeriodStart(int period) => FiscalYearStart.AddMonths(period - 1);

    public decimal AmountFor(Guid accountId, int period) =>
        _lines.Where(l => l.AccountId == accountId && l.Period == period).Sum(l => l.Amount);

    /// <summary>Replaces every amount. Zeros are dropped; amounts are to the cent.</summary>
    public void SetAmounts(IEnumerable<BudgetAmount> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var lines = new List<BudgetLine>();
        var seen = new HashSet<(Guid, int)>();
        foreach (var amount in amounts)
        {
            if (amount.Period is < 1 or > Periods)
                throw new DomainException("budget.period_invalid", "Budget months run from 1 to 12.");
            if (amount.AccountId == Guid.Empty)
                throw new DomainException("budget.account_required", "Each budget amount needs an account.");
            if (decimal.Round(amount.Amount, 2) != amount.Amount)
                throw new DomainException("budget.amount_precision", "Budget amounts are to the cent.");
            if (Math.Abs(amount.Amount) >= MaxAmount)
                throw new DomainException("budget.amount_too_large", "That budget amount is too large.");
            if (!seen.Add((amount.AccountId, amount.Period)))
                throw new DomainException("budget.duplicate", "An account has two amounts for the same month.");
            if (amount.Amount != 0m)
                lines.Add(BudgetLine.Create(amount));
        }

        _lines.Clear();
        _lines.AddRange(lines);
    }

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 100, "budget.name_too_long", "Budget name")
        ?? throw new DomainException("budget.name_required", "A budget needs a name.");
}
