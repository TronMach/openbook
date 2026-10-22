using System.Text.Json;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;

namespace OpenBook.Application.Accounts;

public sealed record CreateAccountCommand(string Name, AccountType Type, string? Number = null, Guid? ParentId = null);

/// <summary>Just enough chart-of-accounts support for the ledger to post. Expanded in M1.</summary>
public sealed class AccountService(ILedgerStore store, IClock clock, ICurrentUser user)
{
    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default) => store.ListAccountsAsync(ct);

    public async Task<Account> CreateAsync(CreateAccountCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = Account.Create(command.Name, command.Type, command.Number, command.ParentId);
        store.Add(account);
        store.Add(AuditRecord.Create(
            clock.UtcNow,
            user.Actor,
            AuditAction.Created,
            "account",
            account.Id,
            before: null,
            after: JsonSerializer.Serialize(new { account.Name, Type = account.Type.ToString(), account.Number, account.ParentId })));
        await store.SaveChangesAsync(ct);
        return account;
    }
}
