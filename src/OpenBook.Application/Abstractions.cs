using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ICurrentUser
{
    /// <summary>Who to record in the audit log.</summary>
    string Actor { get; }
}

public sealed record LedgerOptions(Currency HomeCurrency);

/// <summary>Net ledger balance of one account, positive for a debit balance.</summary>
public sealed record AccountBalance(Guid AccountId, decimal Balance);

/// <summary>Persistence for the ledger. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface ILedgerStore
{
    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, Account>> GetAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<JournalEntry?> GetEntryAsync(Guid id, CancellationToken ct);
    Task<long> NextEntryNumberAsync(CancellationToken ct);
    Task<IReadOnlyList<AccountBalance>> GetBalancesAsync(DateOnly asOf, CancellationToken ct);

    void Add(Account account);
    void Add(JournalEntry entry);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}
