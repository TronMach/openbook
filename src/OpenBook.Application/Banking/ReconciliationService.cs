using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <param name="NaturalAmount">Positive adds to the balance: a deposit, or a card charge.</param>
public sealed record ReconcileItem(AccountLine Line, BankTransaction? Document, decimal NaturalAmount, bool Cleared, string Description)
{
    public string? Reference => Document?.Reference;
}

public sealed record ReconciliationWorksheet(
    Reconciliation Reconciliation,
    Account Account,
    string AccountName,
    IReadOnlyList<ReconcileItem> Items,
    decimal ClearedTotal)
{
    public decimal Difference => Reconciliation.Difference(ClearedTotal);
}

public sealed record ReconciliationReport(
    Reconciliation Reconciliation,
    Account Account,
    string AccountName,
    IReadOnlyList<ReconcileItem> Cleared,
    IReadOnlyList<ReconcileItem> Outstanding,
    decimal RegisterBalance)
{
    public decimal ClearedIncreases => Cleared.Where(i => i.NaturalAmount > 0m).Sum(i => i.NaturalAmount);
    public decimal ClearedDecreases => Cleared.Where(i => i.NaturalAmount < 0m).Sum(i => i.NaturalAmount);
    public decimal OutstandingTotal => Outstanding.Sum(i => i.NaturalAmount);
}

/// <summary>
/// Statement reconciliation for bank and credit card accounts. One reconciliation per account can be in
/// progress; ticks are saved as they're made so it can be resumed. Undoing a finished one is V1.
/// </summary>
public sealed class ReconciliationService(IBankingStore bank, AccountService accounts, IClock clock, ICurrentUser user)
{
    private const string EntityType = "reconciliation";

    public async Task<IReadOnlyList<Reconciliation>> ListAsync(Guid accountId, CancellationToken ct = default) =>
        (await bank.ListReconciliationsAsync(accountId, ct)).OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.StartedAt).ToList();

    public async Task<Reconciliation> StartAsync(Guid accountId, DateOnly statementDate, decimal statementEndingBalance, CancellationToken ct = default)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(accountId);
        BankingRules.EnsureBankingAccount(account);

        var existing = await bank.ListReconciliationsAsync(accountId, ct);
        if (existing.Any(r => r.Status == ReconciliationStatus.InProgress))
            throw new DomainException("reconcile.in_progress", "A reconciliation of this account is already in progress. Finish or cancel it first.");

        var completed = existing.Where(r => r.Status == ReconciliationStatus.Completed).ToList();
        var reconciledIds = completed.SelectMany(r => r.ClearedLineIds).ToHashSet();
        var lines = await bank.GetAccountLinesAsync(accountId, ct);
        var beginning = lines.Where(l => reconciledIds.Contains(l.JournalLineId)).Sum(l => BankingRules.Natural(account, l.Amount));

        var reconciliation = Reconciliation.Start(
            accountId,
            statementDate,
            statementEndingBalance,
            beginning,
            completed.Count == 0 ? null : completed.Max(r => r.StatementDate),
            clock.UtcNow,
            user.Actor);
        bank.Add(reconciliation);
        bank.Add(Audit(AuditAction.Created, reconciliation, before: null));
        await bank.SaveChangesAsync(ct);
        return reconciliation;
    }

    public async Task<ReconciliationWorksheet> GetWorksheetAsync(Guid reconciliationId, CancellationToken ct = default)
    {
        var (reconciliation, chart, account, name, others, lines) = await LoadAsync(reconciliationId, ct);
        var reconciledElsewhere = others.Where(r => r.Status == ReconciliationStatus.Completed).SelectMany(r => r.ClearedLineIds).ToHashSet();
        var cleared = reconciliation.ClearedLineIds.ToHashSet();
        var documents = await DocumentsAsync(lines, ct);

        var items = lines
            .Where(l => cleared.Contains(l.JournalLineId)
                || (l.Date <= reconciliation.StatementDate && !l.IsVoidPair && !reconciledElsewhere.Contains(l.JournalLineId)))
            .Select(l => Item(chart, account, l, documents, cleared.Contains(l.JournalLineId)))
            .ToList();

        return new ReconciliationWorksheet(reconciliation, account, name, items, items.Where(i => i.Cleared).Sum(i => i.NaturalAmount));
    }

    public async Task<ReconciliationWorksheet> UpdateStatementAsync(Guid reconciliationId, DateOnly statementDate, decimal statementEndingBalance, CancellationToken ct = default)
    {
        var (reconciliation, _, _, _, others, lines) = await LoadAsync(reconciliationId, ct);
        var before = Snapshot(reconciliation);
        var previous = others.Where(r => r.Status == ReconciliationStatus.Completed).Select(r => (DateOnly?)r.StatementDate).Max();
        reconciliation.UpdateStatement(statementDate, statementEndingBalance, previous);

        // Ticks on lines now after the statement date no longer belong to it.
        var byId = lines.ToDictionary(l => l.JournalLineId);
        foreach (var id in reconciliation.ClearedLineIds.ToList())
        {
            if (!byId.TryGetValue(id, out var line) || line.Date > statementDate)
                reconciliation.SetCleared(id, cleared: false);
        }

        bank.Add(Audit(AuditAction.Updated, reconciliation, before));
        await bank.SaveChangesAsync(ct);
        return await GetWorksheetAsync(reconciliationId, ct);
    }

    public async Task<ReconciliationWorksheet> SetClearedAsync(Guid reconciliationId, IReadOnlyCollection<Guid> journalLineIds, bool cleared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journalLineIds);
        var worksheet = await GetWorksheetAsync(reconciliationId, ct);
        var eligible = worksheet.Items.Select(i => i.Line.JournalLineId).ToHashSet();
        foreach (var id in journalLineIds)
        {
            if (!eligible.Contains(id))
                throw new DomainException("reconcile.line_not_eligible", "That transaction isn't part of this statement.");
            worksheet.Reconciliation.SetCleared(id, cleared);
        }

        await bank.SaveChangesAsync(ct);
        return await GetWorksheetAsync(reconciliationId, ct);
    }

    public async Task<Reconciliation> CompleteAsync(Guid reconciliationId, CancellationToken ct = default)
    {
        var worksheet = await GetWorksheetAsync(reconciliationId, ct);
        var reconciliation = worksheet.Reconciliation;
        var before = Snapshot(reconciliation);
        reconciliation.Complete(worksheet.ClearedTotal, clock.UtcNow, user.Actor);
        bank.Add(Audit(AuditAction.Updated, reconciliation, before));
        await bank.SaveChangesAsync(ct);
        return reconciliation;
    }

    public async Task CancelAsync(Guid reconciliationId, CancellationToken ct = default)
    {
        var reconciliation = await bank.GetReconciliationAsync(reconciliationId, ct)
            ?? throw new EntityNotFoundException(EntityType, reconciliationId);
        if (reconciliation.Status != ReconciliationStatus.InProgress)
            throw new DomainException("reconcile.completed", "This reconciliation is finished and can't be cancelled.");

        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, EntityType, reconciliation.Id, Snapshot(reconciliation), null));
        bank.Remove(reconciliation);
        await bank.SaveChangesAsync(ct);
    }

    /// <summary>
    /// What the statement showed and what was still outstanding when this reconciliation was finished.
    /// The register balance on the statement date equals the statement ending balance plus outstanding items.
    /// </summary>
    public async Task<ReconciliationReport> GetReportAsync(Guid reconciliationId, CancellationToken ct = default)
    {
        var (reconciliation, chart, account, name, others, lines) = await LoadAsync(reconciliationId, ct);
        if (reconciliation.Status != ReconciliationStatus.Completed)
            throw new DomainException("reconcile.not_completed", "Finish the reconciliation to see its report.");

        var completedAt = reconciliation.CompletedAt!.Value;
        var cleared = reconciliation.ClearedLineIds.ToHashSet();
        var reconciledByThen = others
            .Where(r => r.Status == ReconciliationStatus.Completed && r.CompletedAt <= completedAt)
            .SelectMany(r => r.ClearedLineIds)
            .Concat(cleared)
            .ToHashSet();
        var documents = await DocumentsAsync(lines, ct);

        // As the books stood at completion: posted by then, dated on or before the statement, void pairs left out.
        var asOfStatement = lines
            .Where(l => l.Date <= reconciliation.StatementDate && l.PostedAt <= completedAt && !l.IsVoidPair)
            .ToList();

        return new ReconciliationReport(
            reconciliation,
            account,
            name,
            lines.Where(l => cleared.Contains(l.JournalLineId)).Select(l => Item(chart, account, l, documents, true)).ToList(),
            asOfStatement.Where(l => !reconciledByThen.Contains(l.JournalLineId)).Select(l => Item(chart, account, l, documents, false)).ToList(),
            asOfStatement.Sum(l => BankingRules.Natural(account, l.Amount)));
    }

    private async Task<(Reconciliation Reconciliation, ChartOfAccounts Chart, Account Account, string Name, IReadOnlyList<Reconciliation> Others, IReadOnlyList<AccountLine> Lines)>
        LoadAsync(Guid reconciliationId, CancellationToken ct)
    {
        var reconciliation = await bank.GetReconciliationAsync(reconciliationId, ct)
            ?? throw new EntityNotFoundException(EntityType, reconciliationId);
        var chart = await accounts.GetChartAsync(ct);
        var account = chart.Get(reconciliation.AccountId);
        var others = (await bank.ListReconciliationsAsync(account.Id, ct)).Where(r => r.Id != reconciliation.Id).ToList();
        var lines = await bank.GetAccountLinesAsync(account.Id, ct);
        return (reconciliation, chart, account, chart.FullName(account.Id), others, lines);
    }

    private async Task<IReadOnlyDictionary<Guid, BankTransaction>> DocumentsAsync(IReadOnlyList<AccountLine> lines, CancellationToken ct) =>
        await bank.GetTransactionsAsync(lines.Where(l => l.SourceId is not null).Select(l => l.SourceId!.Value).Distinct().ToList(), ct);

    private static ReconcileItem Item(ChartOfAccounts chart, Account account, AccountLine line, IReadOnlyDictionary<Guid, BankTransaction> documents, bool cleared)
    {
        var document = line.SourceId is { } s ? documents.GetValueOrDefault(s) : null;
        return new(line, document, BankingRules.Natural(account, line.Amount), cleared, BankingRules.Describe(chart, account.Id, line, document));
    }

    private AuditRecord Audit(AuditAction action, Reconciliation r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(Reconciliation r) => JsonSerializer.Serialize(new
    {
        r.AccountId,
        r.StatementDate,
        r.StatementEndingBalance,
        r.BeginningBalance,
        Status = r.Status.ToString(),
        ClearedLines = r.Lines.Count,
    });
}
