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
    public const string AdjustmentSource = "nonprofit.grant_adjustment";
    public const int MaxNameLength = 100;
    public const int MaxTextLength = 2000;

    private readonly List<GrantBudgetLine> _budget = [];
    private readonly List<GrantReceipt> _receipts = [];
    private readonly List<GrantRequest> _requests = [];
    private readonly List<GrantRecognition> _recognitions = [];
    private readonly List<GrantAdjustment> _adjustments = [];

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

    /// <summary>What's been asked of the funder (reimbursement requests).</summary>
    public IReadOnlyList<GrantRequest> Requests => _requests;

    /// <summary>What's been asked for and not taken back.</summary>
    public decimal Requested => _requests.Where(r => !r.IsVoided).Sum(r => r.Amount);

    public GrantRequest AddRequest(DateOnly date, DateOnly from, DateOnly to, decimal amount)
    {
        EnsureActive();
        Common.Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new Common.DomainException("grant.request_amount", "A request is for more than zero.");
        if (to < from)
            throw new Common.DomainException("grant.request_dates", "The period ends before it starts.");
        if (Received + _requests.Where(r => !r.IsVoided && r.ReceiptLine is null).Sum(r => r.Amount) + amount > Amount)
            throw new Common.DomainException("grant.request_over_award", "That's more than is left of the award.");
        var request = new GrantRequest(_requests.Count + 1, date, from, to, amount);
        _requests.Add(request);
        return request;
    }

    /// <summary>The funder paid it: <paramref name="receiptLine"/> is the payment.</summary>
    public void MarkRequestPaid(int line, int receiptLine)
    {
        var request = _requests.FirstOrDefault(r => r.LineNumber == line) ?? throw new Common.DomainException("grant.request_unknown", "There's no such request.");
        if (request.IsVoided || request.ReceiptLine is not null)
            throw new Common.DomainException("grant.request_closed", "That request is already paid or taken back.");
        request.PaidBy(receiptLine);
    }

    public void VoidRequest(int line)
    {
        var request = _requests.FirstOrDefault(r => r.LineNumber == line) ?? throw new Common.DomainException("grant.request_unknown", "There's no such request.");
        if (request.ReceiptLine is not null)
            throw new Common.DomainException("grant.request_paid", "The funder has paid this request. Void that payment first.");
        request.Void();
    }
    public IReadOnlyList<GrantRecognition> Recognitions => _recognitions;

    public decimal Received => _receipts.Where(r => !r.IsVoided).Sum(r => r.Amount);

    /// <summary>Reductions of the award and advances paid back, each voidable.</summary>
    public IReadOnlyList<GrantAdjustment> Adjustments => _adjustments;

    /// <summary>What the award has been reduced by (the funder won't pay it).</summary>
    public decimal Reduced => _adjustments.Where(a => !a.IsVoided && a.Kind == GrantAdjustmentKind.Reduction).Sum(a => a.Amount);

    private decimal ReducedBy(DateOnly date) => _adjustments.Where(a => !a.IsVoided && a.Kind == GrantAdjustmentKind.Reduction && a.Date <= date).Sum(a => a.Amount);

    /// <summary>Advance money paid back to the funder.</summary>
    public decimal Returned => _adjustments.Where(a => !a.IsVoided && a.Kind == GrantAdjustmentKind.AdvanceReturn).Sum(a => a.Amount);

    /// <summary>The award less reductions: what the grant is now worth.</summary>
    public decimal NetAward => Amount - Reduced;

    /// <summary>What the funder had paid by the date.</summary>
    public decimal ReceivedBy(DateOnly date) => _receipts.Where(r => !r.IsVoided && r.Date <= date).Sum(r => r.Amount);

    /// <summary>Income so far on the date.</summary>
    public decimal RecognizedBy(DateOnly date) =>
        Status == GrantStatus.Voided ? 0m : Conditional ? _recognitions.Where(r => !r.IsVoided && r.Date <= date).Sum(r => r.Amount) : Amount - ReducedBy(date);

    /// <summary>Income so far: the award (unconditional) or what's been recognized (conditional).</summary>
    public decimal Recognized => Status == GrantStatus.Voided ? 0m : Conditional ? _recognitions.Where(r => !r.IsVoided).Sum(r => r.Amount) : NetAward;

    /// <summary>What the funder still owes for income recognized; negative when they've paid ahead.</summary>
    public decimal Due => Recognized - Received + Returned;

    /// <summary>Money held as a refundable advance: paid ahead and not yet earned.</summary>
    public decimal Advance =>
        _receipts.Where(r => !r.IsVoided).Sum(r => r.AdvancePart) - _recognitions.Where(r => !r.IsVoided).Sum(r => r.AdvancePart) - Returned;

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
        if (Received - Returned + amount > NetAward)
            throw new DomainException("grant.overpaid", $"That would be more than the {NetAward:N2} awarded.");
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
        if (amount <= 0m || Recognized + amount > NetAward)
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
        foreach (var request in _requests.Where(r => r.ReceiptLine == line))
            request.PaidBy(null);
    }

    /// <summary>A recognition can be voided unless the funder has since paid what it made receivable (void that payment first).</summary>
    public void VoidRecognition(int line)
    {
        var recognition = _recognitions.FirstOrDefault(r => r.LineNumber == line) ?? throw new DomainException("grant.recognition_unknown", "There's no such recognition.");
        if (!recognition.IsVoided && recognition.Amount - recognition.AdvancePart > Receivable)
            throw new DomainException("grant.recognition_paid", "The funder has paid against this income since. Void that payment first.");
        recognition.Void();
    }

    /// <summary>
    /// The most the award can come down by: on an unconditional grant what's unpaid (the receivable), on a conditional
    /// one what isn't recognized yet.
    /// </summary>
    public decimal Reducible => Math.Max(0m, Conditional ? NetAward - Recognized : Receivable);

    public GrantAdjustment AddReduction(DateOnly date, decimal amount, Guid? entryId, string? reason)
    {
        if (Status == GrantStatus.Voided)
            throw new DomainException("grant.not_active", "This grant is voided.");
        Money.EnsureCents(amount);
        if (amount <= 0m || amount > Reducible)
            throw new DomainException("grant.reduce_amount", $"The award can come down by up to {Reducible:N2}: {(Conditional ? "what isn't recognized as income yet" : "what the funder hasn't paid")}.");
        var adjustment = new GrantAdjustment(_adjustments.Count + 1, GrantAdjustmentKind.Reduction, date, amount, null, entryId, null, reason);
        _adjustments.Add(adjustment);
        return adjustment;
    }

    public GrantAdjustment AddAdvanceReturn(DateOnly date, decimal amount, Guid bankAccountId, Guid entryId, Guid? cashEntryId, string? reason)
    {
        if (Status == GrantStatus.Voided)
            throw new DomainException("grant.not_active", "This grant is voided.");
        Money.EnsureCents(amount);
        if (amount <= 0m || amount > Advance)
            throw new DomainException("grant.return_amount", $"Up to {Math.Max(0m, Advance):N2} of the advance is held to pay back.");
        var adjustment = new GrantAdjustment(_adjustments.Count + 1, GrantAdjustmentKind.AdvanceReturn, date, amount, bankAccountId, entryId, cashEntryId, reason);
        _adjustments.Add(adjustment);
        return adjustment;
    }

    /// <summary>Puts a reduction back (unless income or payments since would then pass the award) or a return (unless the advance it paid back has since been earned).</summary>
    public GrantAdjustment VoidAdjustment(int line)
    {
        var adjustment = _adjustments.FirstOrDefault(a => a.LineNumber == line) ?? throw new DomainException("grant.adjustment_unknown", "There's no such adjustment.");
        adjustment.Void();
        if (Advance < 0m)
        {
            adjustment.Unvoid();
            throw new DomainException("grant.advance_used", "The advance this paid back has been earned or returned again since.");
        }

        return adjustment;
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
        if (_receipts.Any(r => !r.IsVoided) || _recognitions.Any(r => !r.IsVoided) || _adjustments.Any(a => !a.IsVoided))
            throw new DomainException("grant.has_activity", "Void its payments, recognized income and adjustments first.");
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

/// <summary>A reimbursement request sent to the funder: posts nothing, and is paid by a <see cref="GrantReceipt"/>.</summary>
public sealed class GrantRequest
{
    private GrantRequest()
    {
    }

    internal GrantRequest(int lineNumber, DateOnly date, DateOnly from, DateOnly to, decimal amount)
    {
        LineNumber = lineNumber;
        Date = date;
        PeriodFrom = from;
        PeriodTo = to;
        Amount = amount;
    }

    public int LineNumber { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly PeriodFrom { get; private set; }
    public DateOnly PeriodTo { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The payment that settled it.</summary>
    public int? ReceiptLine { get; private set; }

    public bool IsVoided { get; private set; }

    internal void PaidBy(int? receiptLine) => ReceiptLine = receiptLine;

    internal void Void()
    {
        if (IsVoided)
            throw new Common.DomainException("grant.request_voided", "This request is already taken back.");
        IsVoided = true;
    }
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

public enum GrantAdjustmentKind
{
    /// <summary>The award comes down: the funder won't pay the rest. Unconditional grants post Dr income / Cr receivable.</summary>
    Reduction,

    /// <summary>Advance money paid back to the funder: Dr the refundable advance / Cr the bank.</summary>
    AdvanceReturn,
}

/// <summary>A change to a grant after the award: a reduction, or an advance paid back.</summary>
public sealed class GrantAdjustment
{
    private GrantAdjustment()
    {
    }

    internal GrantAdjustment(int lineNumber, GrantAdjustmentKind kind, DateOnly date, decimal amount, Guid? bankAccountId, Guid? entryId, Guid? cashEntryId, string? reason)
    {
        LineNumber = lineNumber;
        Kind = kind;
        Date = date;
        Amount = amount;
        BankAccountId = bankAccountId;
        EntryId = entryId;
        CashEntryId = cashEntryId;
        Reason = Text.Optional(reason, 500, "grant.reason_too_long", "Reason") ?? throw new DomainException("grant.reason_required", "Say why.");
    }

    public int LineNumber { get; private set; }
    public GrantAdjustmentKind Kind { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid? BankAccountId { get; private set; }

    /// <summary>Its entry; null for a conditional grant's reduction, which posts nothing.</summary>
    public Guid? EntryId { get; private set; }

    /// <summary>A return on a grant counting income on receipt: the cash-basis entry taking that income back.</summary>
    public Guid? CashEntryId { get; private set; }

    public string Reason { get; private set; } = string.Empty;
    public bool IsVoided { get; private set; }

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("grant.adjustment_voided", "This is already voided.");
        IsVoided = true;
    }

    internal void Unvoid() => IsVoided = false;
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
