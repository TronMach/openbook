using OpenBook.Domain.Common;

namespace OpenBook.Domain.Nonprofit;

public enum GrantStatus
{
    Active,

    /// <summary>Finished: kept for reports, nothing more is expected. Can be reopened.</summary>
    Closed,

    /// <summary>Recorded by mistake; its award entry is reversed.</summary>
    Voided,
}

/// <summary>
/// A grant awarded by a funder (a customer record). Its class is the grant: spending on that class (and its subclasses)
/// within the grant period counts against it and its budget. An unconditional grant is income when awarded (Dr the
/// receivable / Cr the grant income account on the class). A conditional grant, such as cost reimbursement, is income
/// only as its condition is met: each recognition posts what's been spent and not yet recognized, up to the award.
/// Payments from the funder reduce the receivable; on a conditional grant, what's paid beyond the income recognized so
/// far is a refundable advance (a liability), which later recognitions draw down before the receivable. Recognitions
/// and receipts are voided, never edited.
/// </summary>
public sealed class Grant
{
    public const string AwardSource = "nonprofit.grant";
    public const string ReceiptSource = "nonprofit.grant_receipt";
    public const string RecognitionSource = "nonprofit.grant_recognition";
    public const int MaxNameLength = 100;
    public const int MaxTextLength = 2000;

    private readonly List<GrantBudgetLine> _budget = [];
    private readonly List<GrantReceipt> _receipts = [];
    private readonly List<GrantRecognition> _recognitions = [];

    private Grant()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public Guid FunderId { get; private set; }

    /// <summary>The funder's award or grant number.</summary>
    public string? Reference { get; private set; }

    public decimal Amount { get; private set; }
    public DateOnly AwardedOn { get; private set; }
    public DateOnly StartsOn { get; private set; }
    public DateOnly EndsOn { get; private set; }
    public Guid ClassId { get; private set; }
    public Guid IncomeAccountId { get; private set; }
    public Guid ReceivableAccountId { get; private set; }
    public bool Conditional { get; private set; }

    /// <summary>Conditions, reporting dates, allowed costs: whatever the award letter says.</summary>
    public string? Terms { get; private set; }

    public GrantStatus Status { get; private set; }

    /// <summary>The award entry of an unconditional grant.</summary>
    public Guid? AwardEntryId { get; private set; }

    /// <summary>Conditional grants: the liability for money paid ahead of the income ("Refundable Advances"), set when first needed.</summary>
    public Guid? AdvanceAccountId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<GrantBudgetLine> Budget => _budget;
    public IReadOnlyList<GrantReceipt> Receipts => _receipts;
    public IReadOnlyList<GrantRecognition> Recognitions => _recognitions;

    public decimal Received => _receipts.Where(r => !r.IsVoided).Sum(r => r.Amount);

    /// <summary>Income so far: the award (unconditional) or what's been recognized (conditional).</summary>
    public decimal Recognized => Status == GrantStatus.Voided ? 0m : Conditional ? _recognitions.Where(r => !r.IsVoided).Sum(r => r.Amount) : Amount;

    /// <summary>What the funder still owes for income recognized; negative when they've paid ahead.</summary>
    public decimal Due => Recognized - Received;

    /// <summary>Money held as a refundable advance: paid ahead and not yet earned.</summary>
    public decimal Advance =>
        _receipts.Where(r => !r.IsVoided).Sum(r => r.AdvancePart) - _recognitions.Where(r => !r.IsVoided).Sum(r => r.AdvancePart);

    /// <summary>The receivable's balance for this grant: income recognized (or awarded) and not yet paid.</summary>
    public decimal Receivable => (Conditional ? _recognitions.Where(r => !r.IsVoided).Sum(r => r.Amount - r.AdvancePart) : Recognized)
                                 - _receipts.Where(r => !r.IsVoided).Sum(r => r.Amount - r.AdvancePart);

    /// <summary>How much of a payment would be a refundable advance: what's beyond the receivable (conditional grants only).</summary>
    public decimal AdvancePartOf(decimal amount) => Conditional ? Math.Max(0m, amount - Math.Max(0m, Receivable)) : 0m;

    /// <summary>How much of a recognition would come out of the advance before the receivable.</summary>
    public decimal AdvanceUsedBy(decimal amount) => Math.Min(amount, Math.Max(0m, Advance));

    public void UseAdvanceAccount(Guid accountId) => AdvanceAccountId ??= accountId;

    /// <summary>
    /// Income on the cash basis comes with the money: the award and recognitions are accrual only, and each receipt has a
    /// cash-only entry. False on grants entered before this, whose posted entries count on both bases and stay as they are.
    /// </summary>
    public bool CashOnReceipt { get; private set; }

    public static Grant Create(
        Guid id, string name, Guid funderId, string? reference, decimal amount, DateOnly awardedOn, DateOnly startsOn, DateOnly endsOn,
        Guid classId, Guid incomeAccountId, Guid receivableAccountId, bool conditional, string? terms, Guid? awardEntryId, DateTimeOffset createdAt)
    {
        Check(name, amount, startsOn, endsOn);
        return new Grant
        {
            Id = id,
            CashOnReceipt = true,
            Name = name.Trim(),
            FunderId = funderId,
            Reference = Text.Optional(reference, 50, "grant.reference_too_long", "Reference"),
            Amount = amount,
            AwardedOn = awardedOn,
            StartsOn = startsOn,
            EndsOn = endsOn,
            ClassId = classId,
            IncomeAccountId = incomeAccountId,
            ReceivableAccountId = receivableAccountId,
            Conditional = conditional,
            Terms = Text.Optional(terms, MaxTextLength, "grant.terms_too_long", "Terms"),
            Status = GrantStatus.Active,
            AwardEntryId = awardEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public static void Check(string? name, decimal amount, DateOnly startsOn, DateOnly endsOn)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("grant.name_required", "Name the grant.");
        if (name.Trim().Length > MaxNameLength)
            throw new DomainException("grant.name_too_long", $"Names can be at most {MaxNameLength} characters.");
        if (amount <= 0m)
            throw new DomainException("grant.amount", "Enter the amount awarded.");
        if (decimal.Round(amount, 2) != amount)
            throw new DomainException("grant.amount_cents", "Amounts are in cents.");
        if (endsOn < startsOn)
            throw new DomainException("grant.period", "The grant can't end before it starts.");
    }

    /// <summary>What can change after the award: the name, reference, period and terms (amounts and accounts are posted).</summary>
    public void Update(string name, string? reference, DateOnly startsOn, DateOnly endsOn, string? terms)
    {
        Check(name, Amount, startsOn, endsOn);
        Name = name.Trim();
        Reference = Text.Optional(reference, 50, "grant.reference_too_long", "Reference");
        StartsOn = startsOn;
        EndsOn = endsOn;
        Terms = Text.Optional(terms, MaxTextLength, "grant.terms_too_long", "Terms");
    }

    /// <summary>Replaces the budget: one amount per expense account (zero lines dropped).</summary>
    public void SetBudget(IReadOnlyList<(Guid AccountId, decimal Amount)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Any(l => l.Amount < 0m || decimal.Round(l.Amount, 2) != l.Amount))
            throw new DomainException("grant.budget_amount", "Budget amounts are zero or more, in cents.");
        if (lines.Where(l => l.Amount != 0m).GroupBy(l => l.AccountId).Any(g => g.Count() > 1))
            throw new DomainException("grant.budget_duplicate", "Each account can appear once in the budget.");
        _budget.Clear();
        _budget.AddRange(lines.Where(l => l.Amount != 0m).Select((l, i) => new GrantBudgetLine(i + 1, l.AccountId, l.Amount)));
    }

    /// <summary>Refuses a payment that isn't whole cents above zero or would pay more than the award.</summary>
    public void CheckReceipt(decimal amount)
    {
        EnsureActive();
        if (amount <= 0m || decimal.Round(amount, 2) != amount)
            throw new DomainException("grant.receipt_amount", "Enter an amount above zero, in cents.");
        if (Received + amount > Amount)
            throw new DomainException("grant.overpaid", $"That would be more than the {Amount:N2} awarded.");
    }

    public GrantReceipt AddReceipt(DateOnly date, decimal amount, Guid bankAccountId, Guid entryId, Guid? cashEntryId = null)
    {
        CheckReceipt(amount);
        var receipt = new GrantReceipt(_receipts.Count + 1, date, amount, bankAccountId, entryId, AdvancePartOf(amount)) { CashEntryId = cashEntryId };
        _receipts.Add(receipt);
        return receipt;
    }

    public GrantRecognition AddRecognition(DateOnly date, decimal amount, Guid entryId)
    {
        EnsureActive();
        if (!Conditional)
            throw new DomainException("grant.unconditional", "An unconditional grant was income when awarded.");
        if (amount <= 0m || Recognized + amount > Amount)
            throw new DomainException("grant.recognize_amount", "Nothing more to recognize, or more than the award.");
        var recognition = new GrantRecognition(_recognitions.Count + 1, date, amount, entryId, AdvanceUsedBy(amount));
        _recognitions.Add(recognition);
        return recognition;
    }

    /// <summary>A payment can be voided unless its advance has since been earned (void that recognition first).</summary>
    public void VoidReceipt(int line)
    {
        var receipt = _receipts.FirstOrDefault(r => r.LineNumber == line) ?? throw new DomainException("grant.receipt_unknown", "There's no such payment.");
        if (!receipt.IsVoided && receipt.AdvancePart > Advance)
            throw new DomainException("grant.advance_used", "Income has been recognized from this payment since. Void that first.");
        receipt.Void();
    }

    /// <summary>A recognition can be voided unless the funder has since paid what it made receivable (void that payment first).</summary>
    public void VoidRecognition(int line)
    {
        var recognition = _recognitions.FirstOrDefault(r => r.LineNumber == line) ?? throw new DomainException("grant.recognition_unknown", "There's no such recognition.");
        if (!recognition.IsVoided && recognition.Amount - recognition.AdvancePart > Receivable)
            throw new DomainException("grant.recognition_paid", "The funder has paid against this income since. Void that payment first.");
        recognition.Void();
    }

    public void Close()
    {
        EnsureActive();
        Status = GrantStatus.Closed;
    }

    public void Reopen()
    {
        if (Status != GrantStatus.Closed)
            throw new DomainException("grant.not_closed", "Only a closed grant can be reopened.");
        Status = GrantStatus.Active;
    }

    public void Void()
    {
        if (Status == GrantStatus.Voided)
            throw new DomainException("grant.already_voided", "This grant is already voided.");
        if (_receipts.Any(r => !r.IsVoided) || _recognitions.Any(r => !r.IsVoided))
            throw new DomainException("grant.has_activity", "Void its payments and recognized income first.");
        Status = GrantStatus.Voided;
    }

    private void EnsureActive()
    {
        if (Status != GrantStatus.Active)
            throw new DomainException("grant.not_active", Status == GrantStatus.Closed ? "This grant is closed. Reopen it first." : "This grant is voided.");
    }
}

public sealed class GrantBudgetLine
{
    private GrantBudgetLine()
    {
    }

    internal GrantBudgetLine(int lineNumber, Guid accountId, decimal amount)
    {
        LineNumber = lineNumber;
        AccountId = accountId;
        Amount = amount;
    }

    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
}

/// <summary>Money from the funder: Dr the bank / Cr the grant's receivable, and the refundable advance for anything paid ahead.</summary>
public sealed class GrantReceipt
{
    private GrantReceipt()
    {
    }

    internal GrantReceipt(int lineNumber, DateOnly date, decimal amount, Guid bankAccountId, Guid entryId, decimal advancePart)
    {
        LineNumber = lineNumber;
        Date = date;
        Amount = amount;
        BankAccountId = bankAccountId;
        EntryId = entryId;
        AdvancePart = advancePart;
    }

    public int LineNumber { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid BankAccountId { get; private set; }
    public Guid EntryId { get; private set; }

    /// <summary>The cash-only entry putting the money into income on the cash basis (<see cref="Grant.CashOnReceipt"/>).</summary>
    public Guid? CashEntryId { get; internal set; }

    /// <summary>The part paid ahead of the income, credited to the refundable advance rather than the receivable.</summary>
    public decimal AdvancePart { get; private set; }

    public bool IsVoided { get; private set; }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("grant.receipt_voided", "This payment is already voided.");
        IsVoided = true;
    }
}

/// <summary>Income recognized on a conditional grant as its condition was met: Dr the advance (as far as it goes) and the receivable / Cr the grant income.</summary>
public sealed class GrantRecognition
{
    private GrantRecognition()
    {
    }

    internal GrantRecognition(int lineNumber, DateOnly date, decimal amount, Guid entryId, decimal advancePart)
    {
        LineNumber = lineNumber;
        Date = date;
        Amount = amount;
        EntryId = entryId;
        AdvancePart = advancePart;
    }

    public int LineNumber { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid EntryId { get; private set; }

    /// <summary>The part taken out of the refundable advance rather than added to the receivable.</summary>
    public decimal AdvancePart { get; private set; }
    public bool IsVoided { get; private set; }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("grant.recognition_voided", "This is already voided.");
        IsVoided = true;
    }
}
