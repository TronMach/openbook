using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Domain.Settlements;

public enum SettlementStatus
{
    /// <summary>Entered or imported; can be changed until it's posted.</summary>
    Waiting,
    Posted,

    /// <summary>Left out on purpose (a duplicate); posts nothing.</summary>
    Dismissed,
}

/// <param name="Amount">Signed: positive is money to the owner, negative money taken.</param>
/// <param name="Code">The carrier's code; without one, the description stands in for it.</param>
/// <param name="AccountId">This line's own account, instead of the code's.</param>
/// <param name="ClassId">This line's own class, instead of the code's or the carrier's.</param>
public sealed record SettlementLineDraft(
    SettlementSection Section, string? Code, decimal Amount, DateOnly? Date = null, string? Load = null, string? Dispatch = null, string? Description = null,
    Guid? AccountId = null, Guid? ClassId = null, decimal? LoadedMiles = null, decimal? EmptyMiles = null, decimal? Gallons = null);

/// <summary>What the statement prints as a section's total, signed as it counts, to check the lines against.</summary>
public sealed record SettlementTotalDraft(SettlementSection Section, decimal Amount);

/// <summary>A balance the statement prints (a reserve, an advance still owed), in the account's own sign, to check the books against.</summary>
public sealed record SettlementBalanceDraft(Guid AccountId, decimal Amount);

/// <summary>Part of the net, paid into an account of its own.</summary>
public sealed record SettlementDepositDraft(Guid AccountId, decimal Amount);

/// <param name="Number">The statement's number or date, unique for the carrier.</param>
/// <param name="Date">The day it's paid: the posting date.</param>
/// <param name="NetPaid">What reaches the owner's account; never negative.</param>
/// <param name="CarriedForward">A balance left for the next settlement: negative is owed to the carrier, positive is money it's holding.</param>
/// <param name="Deposits">Null or empty pays it all into the carrier's deposit account.</param>
public sealed record SettlementDetails(
    string Number, DateOnly Date, decimal NetPaid, IReadOnlyList<SettlementLineDraft> Lines, DateOnly? PeriodStart = null, DateOnly? PeriodEnd = null,
    decimal CarriedForward = 0m, IReadOnlyList<SettlementDepositDraft>? Deposits = null, string? Memo = null,
    IReadOnlyList<SettlementTotalDraft>? StatedTotals = null, IReadOnlyList<SettlementBalanceDraft>? StatedBalances = null);

public sealed class SettlementLine
{
    private SettlementLine() => Code = string.Empty;

    internal SettlementLine(int lineNumber, SettlementLineDraft draft)
    {
        LineNumber = lineNumber;
        Section = Enum.IsDefined(draft.Section) ? draft.Section : throw new DomainException("settlement.section_invalid", $"Line {lineNumber}: unknown part of a settlement.");
        Description = SettlementText.Fit(draft.Description, 300);
        Code = CarrierCode.Normalize(draft.Code) ?? CarrierCode.Normalize(Description)
            ?? throw new DomainException("settlement.line_code", $"Line {lineNumber} needs a code or a description.");
        Amount = Settlement.Cents(draft.Amount, $"Line {lineNumber}");
        Date = draft.Date;
        Load = SettlementText.Fit(draft.Load, 40);
        Dispatch = SettlementText.Fit(draft.Dispatch, 20);
        AccountId = draft.AccountId == Guid.Empty ? null : draft.AccountId;
        ClassId = draft.ClassId == Guid.Empty ? null : draft.ClassId;
        LoadedMiles = Quantity(draft.LoadedMiles, lineNumber, "miles", 1);
        EmptyMiles = Quantity(draft.EmptyMiles, lineNumber, "miles", 1);
        Gallons = Quantity(draft.Gallons, lineNumber, "gallons", 3);
    }

    public int LineNumber { get; private set; }
    public SettlementSection Section { get; private set; }
    public string Code { get; private set; }
    public DateOnly? Date { get; private set; }
    public string? Load { get; private set; }
    public string? Dispatch { get; private set; }
    public string? Description { get; private set; }

    /// <summary>Signed: positive is money to the owner.</summary>
    public decimal Amount { get; private set; }

    /// <summary>The line's own account, when it isn't the code's.</summary>
    public Guid? AccountId { get; private set; }

    public Guid? ClassId { get; private set; }
    public decimal? LoadedMiles { get; private set; }
    public decimal? EmptyMiles { get; private set; }
    public decimal? Gallons { get; private set; }

    /// <summary>Where it went when the settlement posted; null while it waits.</summary>
    public Guid? PostedAccountId { get; private set; }

    /// <summary>The class it carried when the settlement posted, as reclassified since: what reports by class count its miles under.</summary>
    public Guid? PostedClassId { get; private set; }

    public decimal Miles => (LoadedMiles ?? 0m) + (EmptyMiles ?? 0m);

    internal void Post(Guid accountId, Guid? classId) => (PostedAccountId, PostedClassId) = (accountId, classId);

    internal void Unpost() => (PostedAccountId, PostedClassId) = (null, null);

    public void Reclassify(Guid? classId) => PostedClassId = classId;

    private static decimal? Quantity(decimal? value, int lineNumber, string what, int places) =>
        value is not { } v ? null
        : v is < 0m or > 1_000_000m ? throw new DomainException("settlement.quantity_range", $"Line {lineNumber}: {what} must be between 0 and 1,000,000.")
        : decimal.Round(v, places, MidpointRounding.AwayFromZero);
}

public sealed class SettlementDeposit
{
    private SettlementDeposit() { }

    internal SettlementDeposit(int lineNumber, SettlementDepositDraft draft)
    {
        LineNumber = lineNumber;
        AccountId = draft.AccountId != Guid.Empty ? draft.AccountId : throw new DomainException("settlement.deposit_required", "Choose the account each deposit went into.");
        Amount = Settlement.Cents(draft.Amount, "Each deposit");
        if (Amount <= 0m)
            throw new DomainException("settlement.deposit_amount", "Each deposit must be more than zero.");
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
}

public sealed class SettlementStatedTotal
{
    private SettlementStatedTotal() { }

    internal SettlementStatedTotal(SettlementTotalDraft draft)
    {
        Section = Enum.IsDefined(draft.Section) ? draft.Section : throw new DomainException("settlement.section_invalid", "Unknown part of a settlement.");
        Amount = Settlement.Cents(draft.Amount, "A printed total");
    }

    public SettlementSection Section { get; private set; }
    public decimal Amount { get; private set; }
}

public sealed class SettlementStatedBalance
{
    private SettlementStatedBalance() { }

    internal SettlementStatedBalance(int lineNumber, SettlementBalanceDraft draft)
    {
        LineNumber = lineNumber;
        AccountId = draft.AccountId != Guid.Empty ? draft.AccountId : throw new DomainException("settlement.balance_account", "Choose the account each balance is of.");
        Amount = Settlement.Cents(draft.Amount, "A printed balance");
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
}

/// <summary>
/// A carrier's settlement statement: what a week's loads earned, what was taken out, and the net paid. It waits, and can
/// be changed, until it's posted as one journal entry (Dr the deposit, each line to its code's account); voiding the entry
/// puts it back to waiting. Miles and gallons stay here, beside the ledger, for the reports per mile.
/// </summary>
public sealed class Settlement
{
    public const string Source = "settlements.settlement";
    public const int MaxLines = 500;
    public const int MaxDeposits = 5;
    public const int MaxBalances = 20;

    private readonly List<SettlementLine> _lines = [];
    private readonly List<SettlementDeposit> _deposits = [];
    private readonly List<SettlementStatedTotal> _statedTotals = [];
    private readonly List<SettlementStatedBalance> _statedBalances = [];

    private Settlement() => Number = string.Empty;

    public Guid Id { get; private set; }
    public Guid CarrierId { get; private set; }
    public string Number { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly? PeriodStart { get; private set; }
    public DateOnly? PeriodEnd { get; private set; }
    public decimal NetPaid { get; private set; }
    public decimal CarriedForward { get; private set; }
    public string? Memo { get; private set; }
    public SettlementStatus Status { get; private set; }
    public Guid? JournalEntryId { get; private set; }

    /// <summary>The location its entry carries, kept when it posts.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>SHA-256 (hex) of the file its lines came from; null when they were typed.</summary>
    public string? FileHash { get; private set; }

    public void SetFileHash(string hash) => FileHash = hash;

    public string? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SettlementLine> Lines => [.. _lines.OrderBy(l => l.LineNumber)];

    public IReadOnlyList<SettlementDeposit> Deposits => [.. _deposits.OrderBy(d => d.LineNumber)];

    /// <summary>The section totals the statement prints, by section.</summary>
    public IReadOnlyList<SettlementStatedTotal> StatedTotals => [.. _statedTotals.OrderBy(t => t.Section)];

    public IReadOnlyList<SettlementStatedBalance> StatedBalances => [.. _statedBalances.OrderBy(b => b.LineNumber)];

    public decimal Total => _lines.Sum(l => l.Amount);

    public decimal SectionTotal(SettlementSection section) => _lines.Where(l => l.Section == section).Sum(l => l.Amount);

    /// <summary>What the statement says it comes to, less the lines: zero when they add up.</summary>
    public decimal Difference => NetPaid + CarriedForward - Total;

    public decimal LoadedMiles => _lines.Sum(l => l.LoadedMiles ?? 0m);
    public decimal EmptyMiles => _lines.Sum(l => l.EmptyMiles ?? 0m);
    public decimal Gallons => _lines.Sum(l => l.Gallons ?? 0m);

    public static Settlement Create(Guid carrierId, SettlementDetails details, DateTimeOffset createdAt, string? createdBy)
    {
        var settlement = new Settlement { Id = Guid.CreateVersion7(), CarrierId = carrierId, CreatedAt = createdAt.ToUniversalTime(), CreatedBy = createdBy };
        settlement.Set(details);
        return settlement;
    }

    /// <summary>Accounts and classes are checked by the service first.</summary>
    public void Change(SettlementDetails details)
    {
        if (Status != SettlementStatus.Waiting)
            throw new DomainException("settlement.not_waiting", Status == SettlementStatus.Posted
                ? "This settlement is posted. Void its entry to change it."
                : "This settlement was dismissed. Restore it to change it.");
        Set(details);
    }

    private void Set(SettlementDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Number = Text.Optional(details.Number, 60, "settlement.number_too_long", "Number")
            ?? throw new DomainException("settlement.number_required", "Enter the statement's number, or its date when it has none.");
        if (details.PeriodStart is { } from && details.PeriodEnd is { } to && to < from)
            throw new DomainException("settlement.period_invalid", "The period's first day is after its last.");
        (Date, PeriodStart, PeriodEnd) = (details.Date, details.PeriodStart, details.PeriodEnd);
        NetPaid = Cents(details.NetPaid, "The net paid");
        if (NetPaid < 0m)
            throw new DomainException("settlement.net_negative", "A settlement can't pay less than nothing. Enter 0 as the net paid, and what you still owe the carrier as carried forward, with a minus.");
        CarriedForward = Cents(details.CarriedForward, "The amount carried forward");
        Memo = Text.Optional(details.Memo, 1000, "settlement.memo_too_long", "Memo");

        var lines = details.Lines ?? [];
        if (lines.Count == 0)
            throw new DomainException("settlement.empty", "A settlement needs at least one line.");
        if (lines.Count > MaxLines)
            throw new DomainException("settlement.too_many_lines", $"A settlement takes at most {MaxLines} lines.");
        var deposits = details.Deposits ?? [];
        if (deposits.Count > MaxDeposits)
            throw new DomainException("settlement.too_many_deposits", $"A settlement can be paid into at most {MaxDeposits} accounts.");
        var newLines = lines.Select((l, i) => new SettlementLine(i + 1, l)).ToList();
        var newDeposits = deposits.Select((d, i) => new SettlementDeposit(i + 1, d)).ToList();
        if (newDeposits.Count > 0 && newDeposits.Sum(d => d.Amount) != NetPaid)
            throw new DomainException("settlement.deposits_unequal", $"The deposits add up to {newDeposits.Sum(d => d.Amount):N2}, but the net paid is {NetPaid:N2}.");

        var totals = (details.StatedTotals ?? []).Select(t => new SettlementStatedTotal(t)).ToList();
        if (totals.Select(t => t.Section).Distinct().Count() != totals.Count)
            throw new DomainException("settlement.total_twice", "Each part of the statement has one printed total.");
        var balances = (details.StatedBalances ?? []).Select((b, i) => new SettlementStatedBalance(i + 1, b)).ToList();
        if (balances.Count > MaxBalances)
            throw new DomainException("settlement.too_many_balances", $"A settlement takes at most {MaxBalances} balances.");
        if (balances.Select(b => b.AccountId).Distinct().Count() != balances.Count)
            throw new DomainException("settlement.balance_twice", "Each account has one printed balance.");

        _lines.Clear();
        _lines.AddRange(newLines);
        _deposits.Clear();
        _deposits.AddRange(newDeposits);
        _statedTotals.Clear();
        _statedTotals.AddRange(totals);
        _statedBalances.Clear();
        _statedBalances.AddRange(balances);
    }

    /// <summary>The line's own account, else what the carrier's code stands for; null while it has neither.</summary>
    public static Guid? AccountFor(Carrier carrier, SettlementLine line)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(line);
        return line.AccountId ?? carrier.CodeFor(line.Section, line.Code)?.AccountId;
    }

    public static Guid? ClassFor(Carrier carrier, SettlementLine line)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(line);
        return line.ClassId ?? carrier.CodeFor(line.Section, line.Code)?.ClassId ?? carrier.ClassId;
    }

    /// <summary>Lines with no account yet.</summary>
    public IReadOnlyList<SettlementLine> Unassigned(Carrier carrier) => [.. Lines.Where(l => AccountFor(carrier, l) is null)];

    /// <summary>
    /// The journal lines: the net to the deposit accounts, what's carried forward to <paramref name="dueAccountId"/>, and
    /// each account and class's total the other way. An account and class whose lines cancel out gets none.
    /// </summary>
    public IReadOnlyList<JournalLineDraft> Postings(Carrier carrier, Guid? dueAccountId)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        var result = new List<JournalLineDraft>();
        if (_deposits.Count > 0)
            result.AddRange(Deposits.Select(d => JournalLineDraft.Dr(d.AccountId, d.Amount, "Settlement")));
        else if (NetPaid > 0m)
            result.Add(JournalLineDraft.Dr(carrier.DepositAccountId, NetPaid, "Settlement"));
        if (CarriedForward != 0m)
        {
            const string carried = "Carried to the next settlement";
            var due = dueAccountId ?? throw new DomainException("settlement.due_account", "Choose the account a balance carried forward is kept in.");
            result.Add(CarriedForward > 0m ? JournalLineDraft.Dr(due, CarriedForward, carried) : JournalLineDraft.Cr(due, -CarriedForward, carried));
        }

        foreach (var group in Lines.GroupBy(l => (Account: AccountFor(carrier, l) ?? throw new DomainException("settlement.unassigned", $"Choose an account for {l.Code}."), Class: ClassFor(carrier, l))))
        {
            var amount = group.Sum(l => l.Amount);
            if (amount == 0m)
                continue;
            var memo = JournalEntry.Fit(string.Join(", ", group.Select(l => l.Description ?? l.Code).Distinct(StringComparer.OrdinalIgnoreCase)));
            result.Add(amount > 0m ? JournalLineDraft.Cr(group.Key.Account, amount, memo, group.Key.Class) : JournalLineDraft.Dr(group.Key.Account, -amount, memo, group.Key.Class));
        }

        return result;
    }

    public void Posted(Guid entryId, Carrier carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        EnsureCanPost(carrier);
        foreach (var line in _lines)
            line.Post(AccountFor(carrier, line)!.Value, ClassFor(carrier, line));
        LocationId = carrier.LocationId;
        Status = SettlementStatus.Posted;
        JournalEntryId = entryId;
    }

    /// <summary>Refuses what can't post: it isn't waiting, its lines don't add up, or a line has no account.</summary>
    public void EnsureCanPost(Carrier carrier)
    {
        if (Status != SettlementStatus.Waiting)
            throw new DomainException("settlement.not_waiting", "Only a settlement waiting to be posted can be posted.");
        if (Difference != 0m)
            throw new DomainException("settlement.unbalanced",
                $"The lines add up to {Total:N2}, but the statement says {NetPaid + CarriedForward:N2} (net paid and carried forward). Check the lines against the statement before posting.");
        var missing = Unassigned(carrier).Select(l => l.Code).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new DomainException("settlement.unassigned", $"Choose an account for {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? "…" : "")} before posting.");
    }

    /// <summary>After its entry is voided: waiting again.</summary>
    public void Unpost()
    {
        foreach (var line in _lines)
            line.Unpost();
        (Status, JournalEntryId, LocationId) = (SettlementStatus.Waiting, null, null);
    }

    /// <summary>Reclassifying its entry moves the location of the whole settlement.</summary>
    public void SetLocation(Guid? locationId) => LocationId = locationId;

    public void Dismiss()
    {
        if (Status == SettlementStatus.Posted)
            throw new DomainException("settlement.posted", "Void the settlement's entry before dismissing it.");
        Status = SettlementStatus.Dismissed;
    }

    public void Restore()
    {
        if (Status == SettlementStatus.Dismissed)
            Status = SettlementStatus.Waiting;
    }

    internal static decimal Cents(decimal amount, string what) =>
        decimal.Round(amount, 2) != amount ? throw new DomainException("settlement.cents", $"{what}: amounts are to the cent.")
        : Math.Abs(amount) > Money.MaxAmount ? throw new DomainException("settlement.amount_too_large", $"{what}: that amount is too large.")
        : amount;
}
