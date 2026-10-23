using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Banking;

/// <summary>One ledger line on an account, with its entry's details. <see cref="Amount"/> is debit-positive.</summary>
public sealed record AccountLine(
    Guid JournalLineId,
    Guid JournalEntryId,
    long EntryNumber,
    DateOnly Date,
    decimal Amount,
    string? LineMemo,
    string? EntryMemo,
    string Source,
    Guid? SourceId,
    JournalEntryStatus EntryStatus,
    JournalEntryKind EntryKind,
    DateTimeOffset PostedAt)
{
    /// <summary>Voided entries and their reversals cancel out and never reach the bank, so reconciliation skips them.</summary>
    public bool IsVoidPair => EntryStatus == JournalEntryStatus.Voided || EntryKind == JournalEntryKind.Reversal;
}

public interface IBankingStore
{
    /// <summary>Every sealed ledger line on the account, oldest first (date, entry number, line).</summary>
    Task<IReadOnlyList<AccountLine>> GetAccountLinesAsync(Guid accountId, CancellationToken ct);

    /// <summary>For each entry touching the account, the other accounts it posts to.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetCounterpartAccountsAsync(Guid accountId, CancellationToken ct);

    Task<BankTransaction?> GetTransactionAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, BankTransaction>> GetTransactionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<BankTransaction>> ListTransactionsAsync(Guid accountId, DateOnly fromDate, DateOnly toDate, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSpendReferencesAsync(Guid accountId, CancellationToken ct);

    /// <summary>All reconciliations for the account with their lines, tracked.</summary>
    Task<IReadOnlyList<Reconciliation>> ListReconciliationsAsync(Guid accountId, CancellationToken ct);

    Task<Reconciliation?> GetReconciliationAsync(Guid id, CancellationToken ct);

    void Add(BankTransaction transaction);
    void Add(Reconciliation reconciliation);
    void Remove(Reconciliation reconciliation);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

public static class BankingRules
{
    /// <summary>Positive means more money in the bank, or more owed on a card.</summary>
    public static decimal Natural(Account account, decimal debitPositiveAmount) =>
        account.Type.IsDebitNormal() ? debitPositiveAmount : -debitPositiveAmount;

    /// <summary>How a line reads in a register or on a statement, from <paramref name="accountId"/>'s side.</summary>
    public static string Describe(ChartOfAccounts chart, Guid accountId, AccountLine line, BankTransaction? document) => document switch
    {
        { Kind: BankTransactionKind.Transfer, TransferAccountId: { } to } t => t.AccountId == accountId
            ? $"Transfer to {chart.FullName(to)}"
            : $"Transfer from {chart.FullName(t.AccountId)}",
        _ => document?.Payee ?? line.EntryMemo ?? line.LineMemo ?? $"Journal entry {line.EntryNumber}",
    };

    public static void EnsureBankingAccount(Account account)
    {
        if (!BankTransaction.IsBankingAccount(account.Type))
            throw new Domain.Common.DomainException("bank.not_banking_account", $"'{account.Name}' isn't a bank or credit card account.");
    }
}
