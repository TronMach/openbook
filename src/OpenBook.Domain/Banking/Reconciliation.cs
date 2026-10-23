using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

public enum ReconciliationStatus
{
    InProgress,
    Completed,
}

/// <summary>
/// Matching a bank or card statement against the books. Ledger lines are ticked off ("cleared") until
/// beginning balance + cleared = statement ending balance; completing it marks them reconciled for good.
/// Balances here are in the account's natural sign: positive means money in the bank, or owed on a card.
/// The cleared lines are referenced, never copied or changed, so the ledger stays immutable.
/// </summary>
public sealed class Reconciliation
{
    private readonly List<ReconciliationLine> _lines = [];

    private Reconciliation() => StartedBy = string.Empty;

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public DateOnly StatementDate { get; private set; }
    public decimal StatementEndingBalance { get; private set; }

    /// <summary>Total of everything reconciled before this statement.</summary>
    public decimal BeginningBalance { get; private set; }

    public ReconciliationStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public string StartedBy { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? CompletedBy { get; private set; }

    public IReadOnlyCollection<ReconciliationLine> Lines => _lines;
    public IEnumerable<Guid> ClearedLineIds => _lines.Select(l => l.JournalLineId);

    /// <param name="previousStatementDate">The last completed statement's date, if any.</param>
    public static Reconciliation Start(
        Guid accountId,
        DateOnly statementDate,
        decimal statementEndingBalance,
        decimal beginningBalance,
        DateOnly? previousStatementDate,
        DateTimeOffset now,
        string actor)
    {
        var reconciliation = new Reconciliation
        {
            Id = Guid.CreateVersion7(),
            AccountId = accountId,
            BeginningBalance = beginningBalance,
            Status = ReconciliationStatus.InProgress,
            StartedAt = now.ToUniversalTime(),
            StartedBy = actor,
        };
        reconciliation.SetStatement(statementDate, statementEndingBalance, previousStatementDate);
        return reconciliation;
    }

    public void UpdateStatement(DateOnly statementDate, decimal statementEndingBalance, DateOnly? previousStatementDate)
    {
        EnsureInProgress();
        SetStatement(statementDate, statementEndingBalance, previousStatementDate);
    }

    public void SetCleared(Guid journalLineId, bool cleared)
    {
        EnsureInProgress();
        var existing = _lines.FirstOrDefault(l => l.JournalLineId == journalLineId);
        if (cleared && existing is null)
            _lines.Add(new ReconciliationLine(journalLineId));
        else if (!cleared && existing is not null)
            _lines.Remove(existing);
    }

    /// <summary>Statement ending balance minus what the books show as cleared. Zero means it reconciles.</summary>
    public decimal Difference(decimal clearedTotal) => StatementEndingBalance - (BeginningBalance + clearedTotal);

    /// <param name="clearedTotal">Natural-sign total of the cleared lines, worked out from the ledger.</param>
    public void Complete(decimal clearedTotal, DateTimeOffset now, string actor)
    {
        EnsureInProgress();
        var difference = Difference(clearedTotal);
        if (difference != 0m)
            throw new DomainException("reconcile.not_balanced", $"The statement and the cleared transactions differ by {difference:N2}.");
        Status = ReconciliationStatus.Completed;
        CompletedAt = now.ToUniversalTime();
        CompletedBy = actor;
    }

    /// <summary>
    /// Undo: takes a finished reconciliation back to in progress, with its ticks kept, so it can be corrected.
    /// Only the latest one for an account (the caller checks), since later ones start from its ending balance.
    /// </summary>
    public void Reopen()
    {
        if (Status != ReconciliationStatus.Completed)
            throw new DomainException("reconcile.not_completed", "Only a finished reconciliation can be undone.");
        Status = ReconciliationStatus.InProgress;
        CompletedAt = null;
        CompletedBy = null;
    }

    private void SetStatement(DateOnly statementDate, decimal statementEndingBalance, DateOnly? previousStatementDate)
    {
        if (statementDate == default)
            throw new DomainException("reconcile.date_required", "Enter the statement date.");
        if (previousStatementDate is { } previous && statementDate < previous)
            throw new DomainException("reconcile.date_before_previous",
                $"The statement date can't be before the last reconciled statement ({previous:yyyy-MM-dd}).");
        if (decimal.Round(statementEndingBalance, 2) != statementEndingBalance)
            throw new DomainException("money.precision", "The ending balance can have at most 2 decimal places.");

        StatementDate = statementDate;
        StatementEndingBalance = statementEndingBalance;
    }

    private void EnsureInProgress()
    {
        if (Status != ReconciliationStatus.InProgress)
            throw new DomainException("reconcile.completed", "This reconciliation is finished and can't be changed.");
    }
}

/// <summary>A ledger line ticked off in a reconciliation.</summary>
public sealed class ReconciliationLine
{
    private ReconciliationLine() { }

    internal ReconciliationLine(Guid journalLineId) => JournalLineId = journalLineId;

    public Guid JournalLineId { get; private set; }
}
