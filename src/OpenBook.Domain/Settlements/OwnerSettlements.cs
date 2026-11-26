using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Settlements;

/// <summary>What a line of an owner's settlement is. The sign of its amount follows: positive is money to the owner.</summary>
public enum OwnerLineKind
{
    /// <summary>Pay for a load or work: counts on the owner's 1099.</summary>
    Earning,

    /// <summary>Money given back for a cost the owner paid (tolls, scales): not on the 1099.</summary>
    Reimbursement,

    /// <summary>Backup withholding, held back from what's reportable before anything else.</summary>
    Withholding,

    /// <summary>A regular deduction: insurance, a truck payment, a fee.</summary>
    Deduction,

    /// <summary>An open charge or advance taken back.</summary>
    Charge,

    /// <summary>Money put into one of the owner's reserves.</summary>
    ReserveAdded,

    /// <summary>Money paid out of one of the owner's reserves.</summary>
    ReserveReleased,

    /// <summary>What a deduction couldn't take this time, left as an open charge for the next settlement.</summary>
    Left,
}

public static class OwnerLineKinds
{
    public static string Label(OwnerLineKind kind) => kind switch
    {
        OwnerLineKind.Earning => "Earnings",
        OwnerLineKind.Reimbursement => "Reimbursements",
        OwnerLineKind.Withholding => "Backup withholding",
        OwnerLineKind.Deduction => "Deductions",
        OwnerLineKind.Charge => "Charges taken back",
        OwnerLineKind.ReserveAdded => "Into reserves",
        OwnerLineKind.ReserveReleased => "Out of reserves",
        _ => "Left for next time",
    };

    /// <summary>Whether a line of the kind takes money from the owner, so its amount is negative.</summary>
    public static bool Takes(OwnerLineKind kind) => kind is OwnerLineKind.Withholding or OwnerLineKind.Deduction or OwnerLineKind.Charge or OwnerLineKind.ReserveAdded;
}

/// <param name="Amount">Signed: positive is money to the owner. A line that takes (<see cref="OwnerLineKinds.Takes"/>) is negative.</param>
/// <param name="Reportable">Whether it counts toward the owner's 1099 (earnings, unless said otherwise).</param>
/// <param name="ChargeId">The open charge it takes back, or leaves.</param>
/// <param name="ReserveNumber">The reserve it adds to or pays out of.</param>
/// <param name="DeductionNumber">The owner's regular deduction it takes.</param>
/// <param name="LoadId">The load it pays for, with <paramref name="DispatchNumber"/>.</param>
public sealed record OwnerLineDraft(
    OwnerLineKind Kind, string Description, decimal Amount, Guid AccountId, Guid? ClassId = null, string? Load = null, Guid? TruckId = null,
    decimal? LoadedMiles = null, decimal? EmptyMiles = null, bool Reportable = false, Guid? ChargeId = null, int? ReserveNumber = null, int? DeductionNumber = null,
    Guid? LoadId = null, int? DispatchNumber = null, decimal? Quantity = null, decimal? Rate = null);

public sealed class OwnerSettlementLine
{
    private OwnerSettlementLine() => Description = string.Empty;

    internal OwnerSettlementLine(int lineNumber, OwnerLineDraft draft)
    {
        LineNumber = lineNumber;
        Kind = Enum.IsDefined(draft.Kind) ? draft.Kind : throw new DomainException("owner_settlement.kind_invalid", "Unknown kind of settlement line.");
        Description = Text.Optional(draft.Description, 200, "owner_settlement.description_too_long", "Description")
            ?? throw new DomainException("owner_settlement.description_required", $"Line {lineNumber} needs a description.");
        Amount = decimal.Round(draft.Amount, 2) != draft.Amount ? throw new DomainException("owner_settlement.cents", $"Line {lineNumber}: amounts are to the cent.")
            : Math.Abs(draft.Amount) > Money.MaxAmount ? throw new DomainException("owner_settlement.amount_too_large", $"Line {lineNumber}: that amount is too large.")
            : draft.Amount;
        if (Amount != 0m && (Amount < 0m) != OwnerLineKinds.Takes(Kind) && Kind is not (OwnerLineKind.Earning or OwnerLineKind.Reimbursement))
            throw new DomainException("owner_settlement.sign", $"Line {lineNumber}: {OwnerLineKinds.Label(Kind).ToLowerInvariant()} go the other way.");
        AccountId = draft.AccountId != Guid.Empty ? draft.AccountId : throw new DomainException("owner_settlement.account_required", $"Line {lineNumber} needs an account.");
        ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId;
        Load = Text.Optional(draft.Load, 40, "owner_settlement.load_too_long", "Load");
        TruckId = draft.TruckId == Guid.Empty ? null : draft.TruckId;
        LoadedMiles = MilesOf(draft.LoadedMiles, lineNumber);
        EmptyMiles = MilesOf(draft.EmptyMiles, lineNumber);
        Reportable = draft.Reportable && Kind == OwnerLineKind.Earning;
        (ChargeId, ReserveNumber, DeductionNumber, LoadId, DispatchNumber) = (draft.ChargeId, draft.ReserveNumber, draft.DeductionNumber, draft.LoadId, draft.DispatchNumber);
        (Quantity, Rate) = (draft.Quantity, draft.Rate);
    }

    public int LineNumber { get; private set; }
    public OwnerLineKind Kind { get; private set; }
    public string Description { get; private set; }

    /// <summary>Signed: positive is money to the owner.</summary>
    public decimal Amount { get; private set; }

    public Guid AccountId { get; private set; }
    public Guid? ClassId { get; private set; }
    public string? Load { get; private set; }
    public Guid? TruckId { get; private set; }
    public decimal? LoadedMiles { get; private set; }
    public decimal? EmptyMiles { get; private set; }
    public bool Reportable { get; private set; }
    public Guid? ChargeId { get; private set; }
    public int? ReserveNumber { get; private set; }
    public int? DeductionNumber { get; private set; }
    public Guid? LoadId { get; private set; }
    public int? DispatchNumber { get; private set; }

    /// <summary>What a pay rule worked it out from: miles, a percent's base, hours.</summary>
    public decimal? Quantity { get; private set; }

    public decimal? Rate { get; private set; }

    public decimal Miles => (LoadedMiles ?? 0m) + (EmptyMiles ?? 0m);

    /// <summary>Reclassifying its bill or entry moves its class, so miles follow.</summary>
    public void Reclassify(Guid? classId) => ClassId = classId;

    private static decimal? MilesOf(decimal? value, int lineNumber) =>
        value is not { } v ? null
        : v is < 0m or > 100_000m ? throw new DomainException("owner_settlement.miles_range", $"Line {lineNumber}: miles must be between 0 and 100,000.")
        : decimal.Round(v, 1, MidpointRounding.AwayFromZero);
}

public enum OwnerSettlementStatus
{
    /// <summary>Its bill (or, at zero, its entry) stands.</summary>
    Made,
    Voided,
}

/// <summary>
/// What the company pays an owner-operator for a week: their earnings, less withholding, deductions, charges taken back
/// and reserves, as lines. A net above zero is a bill to the owner's vendor record (<see cref="BillId"/>), paid like any
/// other; a net of exactly zero is an entry of its own (<see cref="Source"/>), since a bill must be above zero. Never
/// below zero: what doesn't fit stays open for the next one. Only the owner's latest can be voided, since balances run
/// forward from one to the next.
/// </summary>
public sealed class OwnerSettlement
{
    /// <summary>The entry of a settlement that nets to zero.</summary>
    public const string Source = "settlements.owner";

    public const int MaxLines = 500;

    private readonly List<OwnerSettlementLine> _lines = [];

    private OwnerSettlement() { }

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public Guid OwnerId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly? PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public string? Memo { get; private set; }
    public OwnerSettlementStatus Status { get; private set; }
    public Guid? BillId { get; private set; }
    public Guid? JournalEntryId { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? VoidReason { get; private set; }

    public IReadOnlyList<OwnerSettlementLine> Lines => [.. _lines.OrderBy(l => l.LineNumber)];

    public bool Stands => Status == OwnerSettlementStatus.Made;

    public decimal Net => _lines.Sum(l => l.Amount);

    /// <summary>What it earned, reportable or not.</summary>
    public decimal Earned => _lines.Where(l => l.Kind == OwnerLineKind.Earning).Sum(l => l.Amount);

    /// <summary>What counts toward the owner's 1099: earnings, before anything is taken out.</summary>
    public decimal ReportableGross => _lines.Where(l => l.Reportable).Sum(l => l.Amount);

    public decimal Withheld => -_lines.Where(l => l.Kind == OwnerLineKind.Withholding).Sum(l => l.Amount);

    public decimal Total(OwnerLineKind kind) => _lines.Where(l => l.Kind == kind).Sum(l => l.Amount);

    public decimal LoadedMiles => _lines.Sum(l => l.LoadedMiles ?? 0m);
    public decimal EmptyMiles => _lines.Sum(l => l.EmptyMiles ?? 0m);

    public static OwnerSettlement Create(
        int number, Guid ownerId, DateOnly date, DateOnly? periodStart, DateOnly periodEnd, IReadOnlyList<OwnerLineDraft> lines, string? memo, DateTimeOffset createdAt, string? createdBy)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (periodStart is { } from && from > periodEnd)
            throw new DomainException("owner_settlement.period_invalid", "The period's first day is after its last.");
        if (lines.Count == 0)
            throw new DomainException("owner_settlement.empty", "There's nothing to settle.");
        if (lines.Count > MaxLines)
            throw new DomainException("owner_settlement.too_many_lines", $"A settlement takes at most {MaxLines} lines.");
        var settlement = new OwnerSettlement
        {
            Id = Guid.CreateVersion7(),
            Number = number > 0 ? number : throw new DomainException("owner_settlement.number", "Settlements are numbered from 1."),
            OwnerId = ownerId,
            Date = date,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Memo = Text.Optional(memo, 500, "owner_settlement.memo_too_long", "Memo"),
            CreatedAt = createdAt.ToUniversalTime(),
            CreatedBy = createdBy,
        };
        settlement._lines.AddRange(lines.Select((l, i) => new OwnerSettlementLine(i + 1, l)));
        if (settlement.Net < 0m)
            throw new DomainException("owner_settlement.negative", "A settlement can't pay less than nothing: take less this time, and leave the rest open for the next one.");
        return settlement;
    }

    /// <summary>
    /// The lines as a bill has them: each account and class's total, Dr what it pays (or releases), Cr what it takes. An
    /// account and class whose lines cancel out gets none.
    /// </summary>
    public IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount, string Memo)> Postings() =>
        [.. _lines.GroupBy(l => (l.AccountId, l.ClassId)).Select(g => (g.Key.AccountId, g.Key.ClassId, g.Sum(l => l.Amount),
            JournalEntry.Fit(string.Join(", ", g.Select(l => OwnerLineKinds.Label(l.Kind)).Distinct(StringComparer.Ordinal)))))
            .Where(p => p.Item3 != 0m)];

    public void Billed(Guid billId) => BillId = billId;

    public void Posted(Guid entryId) => JournalEntryId = entryId;

    public void Void(string reason)
    {
        if (Status == OwnerSettlementStatus.Voided)
            throw new DomainException("owner_settlement.voided", "This settlement is already voided.");
        Status = OwnerSettlementStatus.Voided;
        VoidReason = Text.Optional(reason, 200, "owner_settlement.reason_too_long", "Reason");
    }
}
