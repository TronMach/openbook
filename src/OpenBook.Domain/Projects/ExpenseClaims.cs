using OpenBook.Domain.Common;

namespace OpenBook.Domain.Projects;

public enum ExpenseClaimStatus
{
    Submitted,
    Approved,
    Returned,
}

/// <param name="AccountId">The expense (or other category) account it's for.</param>
/// <param name="CustomerId">A customer to bill it on to, if it's billable.</param>
public sealed record ExpenseClaimLineDraft(DateOnly Date, Guid AccountId, string? Description, decimal Amount, Guid? CustomerId = null);

/// <summary>One thing a person paid for themselves.</summary>
public sealed class ExpenseClaimLine
{
    public const int MaxDescriptionLength = 500;

    private ExpenseClaimLine()
    {
    }

    internal ExpenseClaimLine(int lineNumber, ExpenseClaimLineDraft draft)
    {
        LineNumber = lineNumber;
        Date = draft.Date;
        AccountId = draft.AccountId == Guid.Empty ? throw new DomainException("expense_claim.account_required", $"Line {lineNumber}: choose what it was for.") : draft.AccountId;
        Description = Text.Optional(draft.Description, MaxDescriptionLength, "expense_claim.description_too_long", $"Line {lineNumber}'s description")
            ?? throw new DomainException("expense_claim.description_required", $"Line {lineNumber}: say what was bought.");
        Amount = draft.Amount > 0m && draft.Amount <= Money.MaxAmount && decimal.Round(draft.Amount, 2) == draft.Amount
            ? draft.Amount
            : throw new DomainException("expense_claim.amount", $"Line {lineNumber}: the amount is above zero, in cents.");
        CustomerId = draft.CustomerId == Guid.Empty ? null : draft.CustomerId;
    }

    public int LineNumber { get; private set; }
    public DateOnly Date { get; private set; }
    public Guid AccountId { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public Guid? CustomerId { get; private set; }
}

/// <summary>
/// Expenses a person paid out of their own pocket, handed in to be paid back (C345): submitted, then approved (which
/// makes a bill to the person's vendor record, paid like any bill) or returned with a note to be fixed and submitted
/// again. A claim whose bill is voided can be changed and approved again. It posts nothing itself.
/// </summary>
public sealed class ExpenseClaim
{
    public const int MaxLines = 100;
    public const int MaxNoteLength = 500;

    private readonly List<ExpenseClaimLine> _lines = [];

    private ExpenseClaim() => SubmittedBy = string.Empty;

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public Guid WorkerId { get; private set; }
    public ExpenseClaimStatus Status { get; private set; }
    public string? Memo { get; private set; }
    public IReadOnlyList<ExpenseClaimLine> Lines => _lines;
    public decimal Total => _lines.Sum(l => l.Amount);

    public DateTimeOffset SubmittedAt { get; private set; }
    public string SubmittedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? ReviewedBy { get; private set; }

    /// <summary>Why it was returned (or a remark on approving).</summary>
    public string? Note { get; private set; }

    /// <summary>The bill paying it back, once approved.</summary>
    public Guid? BillId { get; private set; }

    public static ExpenseClaim Submit(int number, Guid workerId, string? memo, IReadOnlyList<ExpenseClaimLineDraft> lines, DateTimeOffset at, string by)
    {
        var claim = new ExpenseClaim { Id = Guid.CreateVersion7(), Number = number, WorkerId = workerId };
        claim.Hand(memo, lines, at, by);
        return claim;
    }

    /// <summary>
    /// Changes a claim and hands it in again: one waiting for review, a returned one, or an approved one whose bill
    /// was voided (<paramref name="billVoided"/>).
    /// </summary>
    public void Change(string? memo, IReadOnlyList<ExpenseClaimLineDraft> lines, DateTimeOffset at, string by, bool billVoided = false)
    {
        if (Status == ExpenseClaimStatus.Approved && !billVoided)
            throw new DomainException("expense_claim.approved", "This claim is approved and billed. Void its bill to change it.");
        Hand(memo, lines, at, by);
    }

    public void Approve(Guid billId, DateTimeOffset at, string by, string? note, bool billVoided = false)
    {
        if (Status == ExpenseClaimStatus.Returned || (Status == ExpenseClaimStatus.Approved && !billVoided))
            throw new DomainException("expense_claim.not_waiting", Status == ExpenseClaimStatus.Returned
                ? "This claim was returned. It can be approved once it's handed in again."
                : "This claim is approved already.");
        BillId = billId;
        Review(ExpenseClaimStatus.Approved, at, by, note);
    }

    public void Return(DateTimeOffset at, string by, string note, bool billVoided = false)
    {
        if (Status == ExpenseClaimStatus.Returned)
            throw new DomainException("expense_claim.not_waiting", "This claim has been returned already.");
        if (Status == ExpenseClaimStatus.Approved && !billVoided)
            throw new DomainException("expense_claim.approved", "This claim is approved and billed. Void its bill to return it.");
        if (string.IsNullOrWhiteSpace(note))
            throw new DomainException("expense_claim.note_required", "Say what needs fixing.");
        BillId = null;
        Review(ExpenseClaimStatus.Returned, at, by, note);
    }

    private void Hand(string? memo, IReadOnlyList<ExpenseClaimLineDraft> lines, DateTimeOffset at, string by)
    {
        if (lines is null || lines.Count == 0)
            throw new DomainException("expense_claim.no_lines", "Add at least one expense.");
        if (lines.Count > MaxLines)
            throw new DomainException("expense_claim.too_many_lines", $"A claim can have at most {MaxLines} lines.");
        var built = lines.Select((d, i) => new ExpenseClaimLine(i + 1, d)).ToList();
        if (built.Sum(l => l.Amount) > Money.MaxAmount)
            throw new DomainException("expense_claim.amount", "The claim's total is too large.");
        Memo = Text.Optional(memo, MaxNoteLength, "expense_claim.memo_too_long", "Memo");
        _lines.Clear();
        _lines.AddRange(built);
        Status = ExpenseClaimStatus.Submitted;
        SubmittedAt = at.ToUniversalTime();
        SubmittedBy = Text.Optional(by, 200, "expense_claim.actor_too_long", "Submitted by") ?? "system";
        (ReviewedAt, ReviewedBy, Note, BillId) = (null, null, null, null);
    }

    private void Review(ExpenseClaimStatus status, DateTimeOffset at, string by, string? note)
    {
        Status = status;
        ReviewedAt = at.ToUniversalTime();
        ReviewedBy = Text.Optional(by, 200, "expense_claim.actor_too_long", "Reviewed by");
        Note = Text.Optional(note, MaxNoteLength, "expense_claim.note_too_long", "Note");
    }
}
