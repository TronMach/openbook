namespace OpenBook.Domain.Ledger;

/// <summary>What a caller asks to post: exactly one of <see cref="Debit"/> or <see cref="Credit"/> is positive.</summary>
/// <param name="ClassId">The line's class (Domain.Segments), kept beside the ledger; null for none.</param>
public sealed record JournalLineDraft(Guid AccountId, decimal Debit, decimal Credit, string? Memo = null, Guid? ClassId = null)
{
    public static JournalLineDraft Dr(Guid accountId, decimal amount, string? memo = null, Guid? classId = null) => new(accountId, amount, 0m, memo, classId);

    public static JournalLineDraft Cr(Guid accountId, decimal amount, string? memo = null, Guid? classId = null) => new(accountId, 0m, amount, memo, classId);
}

/// <summary>
/// One posted line. <see cref="Amount"/> is signed: positive is a debit, negative a credit,
/// so a balanced entry's lines always sum to zero.
/// </summary>
public sealed class JournalLine
{
    private JournalLine() { }

    internal JournalLine(int lineNumber, Guid accountId, decimal amount, string? memo)
    {
        Id = Guid.CreateVersion7();
        LineNumber = lineNumber;
        AccountId = accountId;
        Amount = amount;
        Memo = memo;
    }

    public Guid Id { get; private set; }
    public int LineNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Memo { get; private set; }

    public decimal Debit => Amount > 0m ? Amount : 0m;
    public decimal Credit => Amount < 0m ? -Amount : 0m;
}
