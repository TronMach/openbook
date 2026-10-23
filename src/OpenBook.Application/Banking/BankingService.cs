using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Banking;

public sealed record RecordBankTransactionCommand(
    BankTransactionKind Kind,
    Guid AccountId,
    DateOnly Date,
    IReadOnlyCollection<BankSplit> Splits,
    string? Payee = null,
    string? Reference = null,
    string? Memo = null,
    Guid? VendorId = null);

public sealed record TransferCommand(Guid FromAccountId, Guid ToAccountId, DateOnly Date, decimal Amount, string? Memo = null);

public sealed record BankAccountSummary(
    Account Account,
    string FullName,
    decimal Balance,
    decimal ReconciledBalance,
    DateOnly? LastReconciledDate,
    Guid? ReconciliationInProgressId);

public enum LineClearing
{
    None,
    Cleared,
    Reconciled,
}

/// <param name="NaturalAmount">Positive adds to the account's balance (deposit; card charge).</param>
/// <param name="Balance">Running balance after this line, natural sign.</param>
/// <param name="OtherAccountIds">Where the money went or came from.</param>
public sealed record RegisterLine(
    AccountLine Line,
    BankTransaction? Document,
    decimal NaturalAmount,
    decimal Balance,
    LineClearing Clearing,
    IReadOnlyList<Guid> OtherAccountIds);

public sealed record AccountRegister(Account Account, string FullName, decimal Balance, IReadOnlyList<RegisterLine> Lines, int TotalLines);

/// <summary>Checks, deposits, card charges and transfers: each a document that posts one journal entry.</summary>
public sealed class BankingService(
    IBankingStore bank,
    Purchases.IPurchasesStore purchases,
    ILedgerStore ledger,
    AccountService accounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "bank_transaction";

    public async Task<IReadOnlyList<BankAccountSummary>> ListAccountsAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var result = new List<BankAccountSummary>();
        foreach (var (account, _) in chart.Ordered(includeInactive: false).Where(x => BankTransaction.IsBankingAccount(x.Account.Type)))
        {
            var lines = await bank.GetAccountLinesAsync(account.Id, ct);
            var reconciliations = await bank.ListReconciliationsAsync(account.Id, ct);
            var completed = reconciliations.Where(r => r.Status == ReconciliationStatus.Completed).ToList();
            var reconciled = completed.SelectMany(r => r.ClearedLineIds).ToHashSet();

            result.Add(new BankAccountSummary(
                account,
                chart.FullName(account.Id),
                lines.Sum(l => BankingRules.Natural(account, l.Amount)),
                lines.Where(l => reconciled.Contains(l.JournalLineId)).Sum(l => BankingRules.Natural(account, l.Amount)),
                completed.Count == 0 ? null : completed.Max(r => r.StatementDate),
                reconciliations.FirstOrDefault(r => r.Status == ReconciliationStatus.InProgress)?.Id));
        }

        return result;
    }

    /// <param name="take">Newest lines to return; balances are still computed over the whole history.</param>
    public async Task<AccountRegister> GetRegisterAsync(Guid accountId, int take = 500, CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var account = chart.Get(accountId);
        var lines = await bank.GetAccountLinesAsync(accountId, ct);
        var counterparts = await bank.GetCounterpartAccountsAsync(accountId, ct);
        var documents = await bank.GetTransactionsAsync(lines.Where(l => l.SourceId is not null).Select(l => l.SourceId!.Value).Distinct().ToList(), ct);
        var reconciliations = await bank.ListReconciliationsAsync(accountId, ct);
        var reconciled = reconciliations.Where(r => r.Status == ReconciliationStatus.Completed).SelectMany(r => r.ClearedLineIds).ToHashSet();
        var cleared = reconciliations.Where(r => r.Status == ReconciliationStatus.InProgress).SelectMany(r => r.ClearedLineIds).ToHashSet();

        var balance = 0m;
        var register = new List<RegisterLine>(lines.Count);
        foreach (var line in lines)
        {
            var natural = BankingRules.Natural(account, line.Amount);
            balance += natural;
            register.Add(new RegisterLine(
                line,
                line.SourceId is { } sourceId ? documents.GetValueOrDefault(sourceId) : null,
                natural,
                balance,
                reconciled.Contains(line.JournalLineId) ? LineClearing.Reconciled
                    : cleared.Contains(line.JournalLineId) ? LineClearing.Cleared
                    : LineClearing.None,
                counterparts.GetValueOrDefault(line.JournalEntryId) ?? []));
        }

        register.Reverse();
        return new AccountRegister(account, chart.FullName(accountId), balance, register.Take(take).ToList(), register.Count);
    }

    public Task<BankTransaction?> GetAsync(Guid id, CancellationToken ct = default) => bank.GetTransactionAsync(id, ct);

    public async Task<BankTransaction> RecordAsync(RecordBankTransactionCommand command, CancellationToken ct = default)
    {
        var transaction = await StageAsync(command, importBatchId: null, ct);
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task<BankTransaction> TransferAsync(TransferCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var chart = await accounts.GetChartAsync(ct);
        var from = chart.Get(command.FromAccountId);
        var to = chart.Get(command.ToAccountId);
        BankingRules.EnsureBankingAccount(from);
        BankingRules.EnsureBankingAccount(to);

        var id = Guid.CreateVersion7();
        var memo = string.IsNullOrWhiteSpace(command.Memo) ? $"Transfer from {chart.FullName(from.Id)} to {chart.FullName(to.Id)}" : command.Memo;
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            BankTransaction.TransferPostings(from.Id, to.Id, command.Amount),
            memo,
            BankTransaction.TransferSource,
            id), ct);

        var transaction = BankTransaction.Create(
            id, BankTransactionKind.Transfer, from.Id, to.Id, command.Date, command.Amount, entry.Id, clock.UtcNow, memo: command.Memo);
        bank.Add(transaction);
        bank.Add(Audit(AuditAction.Created, transaction, before: null));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task<BankTransaction> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var transaction = await bank.GetTransactionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(transaction);
        // Entry first: it refuses reconciled transactions, and nothing should change in memory before that.
        await engine.StageVoidAsync(transaction.JournalEntryId, reason, fromDocument: true, ct);
        transaction.MarkVoided();
        bank.Add(Audit(AuditAction.Voided, transaction, before));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    /// <summary>One more than the highest numeric check number used on the account, or null if none.</summary>
    public async Task<string?> SuggestCheckNumberAsync(Guid accountId, CancellationToken ct = default)
    {
        var numbers = (await bank.ListSpendReferencesAsync(accountId, ct))
            .Select(r => long.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (long?)null)
            .Where(n => n is not null)
            .ToList();
        return numbers.Count == 0 ? null : (numbers.Max()!.Value + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Adds the document and its entry without saving; imports stage many and save once.</summary>
    internal async Task<BankTransaction> StageAsync(RecordBankTransactionCommand command, Guid? importBatchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind == BankTransactionKind.Transfer)
            throw new DomainException("bank.kind_invalid", "Record transfers with TransferAsync.");

        var account = (await ledger.GetAccountsAsync([command.AccountId], ct)).GetValueOrDefault(command.AccountId)
            ?? throw new EntityNotFoundException("account", command.AccountId);
        BankingRules.EnsureBankingAccount(account);

        // An expense can name a vendor; the payee defaults to their name.
        var payee = command.Payee;
        if (command.VendorId is { } vendorId)
        {
            var vendor = await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
            if (!vendor.IsActive)
                throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
            if (string.IsNullOrWhiteSpace(payee))
                payee = vendor.DisplayName;
        }

        var postings = BankTransaction.SplitPostings(command.Kind, account.Id, command.Splits);
        var id = Guid.CreateVersion7();
        var entryMemo = string.Join(" · ", new[] { payee, command.Memo }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            postings,
            entryMemo.Length == 0 ? null : entryMemo,
            BankTransaction.SourceFor(command.Kind),
            id), ct);

        var transaction = BankTransaction.Create(
            id,
            command.Kind,
            account.Id,
            transferAccountId: null,
            command.Date,
            command.Splits.Sum(s => s.Amount),
            entry.Id,
            clock.UtcNow,
            payee,
            command.Reference,
            command.Memo,
            importBatchId,
            command.VendorId);
        bank.Add(transaction);
        bank.Add(Audit(AuditAction.Created, transaction, before: null));
        return transaction;
    }

    private AuditRecord Audit(AuditAction action, BankTransaction t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    private static string Snapshot(BankTransaction t) => JsonSerializer.Serialize(new
    {
        Kind = t.Kind.ToString(),
        t.AccountId,
        t.TransferAccountId,
        t.Date,
        t.Payee,
        t.Reference,
        t.Memo,
        t.Amount,
        Status = t.Status.ToString(),
        t.JournalEntryId,
        t.ImportBatchId,
        t.VendorId,
    });
}
