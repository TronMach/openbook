using System.Text.Json;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;

namespace OpenBook.Application.Accounts;

public sealed record CreateAccountCommand(string Name, AccountType Type, string? Number = null, Guid? ParentId = null, string? Description = null);

public sealed record UpdateAccountCommand(Guid Id, string Name, AccountType Type, string? Number, Guid? ParentId, string? Description);

/// <summary>Chart-of-accounts maintenance. Accounts are never deleted; they're made inactive.</summary>
public sealed class AccountService(ILedgerStore store, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "account";

    public async Task<ChartOfAccounts> GetChartAsync(CancellationToken ct = default) =>
        new(await store.ListAccountsAsync(ct));

    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default) =>
        (await GetChartAsync(ct)).Ordered().Select(x => x.Account).ToList();

    public async Task<Account> CreateAsync(CreateAccountCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var chart = await GetChartAsync(ct);
        chart.EnsureCanAdd(command.Name, command.Type, command.Number, command.ParentId);
        var account = Account.Create(command.Name, command.Type, command.Number, command.ParentId, command.Description);

        store.Add(account);
        store.Add(Audit(AuditAction.Created, account, before: null));
        await store.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> UpdateAsync(UpdateAccountCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var chart = await GetChartAsync(ct);
        var account = chart.Get(command.Id);
        var hasPostings = command.Type != account.Type && await store.HasPostingsAsync(account.Id, ct);
        chart.EnsureCanUpdate(account, command.Name, command.Number, command.Type, command.ParentId, hasPostings);

        var before = Snapshot(account);
        account.Update(command.Name, command.Number, command.Description);
        account.ChangeType(command.Type);
        account.MoveTo(command.ParentId);

        store.Add(Audit(AuditAction.Updated, account, before));
        await store.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var chart = await GetChartAsync(ct);
        var account = chart.Get(id);
        chart.EnsureCanDeactivate(account);
        return await SetActiveAsync(account, active: false, ct);
    }

    public async Task<Account> ReactivateAsync(Guid id, CancellationToken ct = default)
    {
        var chart = await GetChartAsync(ct);
        var account = chart.Get(id);
        chart.EnsureCanReactivate(account);
        return await SetActiveAsync(account, active: true, ct);
    }

    internal AuditRecord Audit(AuditAction action, Account account, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, account.Id, before, Snapshot(account));

    private async Task<Account> SetActiveAsync(Account account, bool active, CancellationToken ct)
    {
        if (account.IsActive == active)
            return account;

        var before = Snapshot(account);
        if (active)
            account.Reactivate();
        else
            account.Deactivate();

        store.Add(Audit(AuditAction.Updated, account, before));
        await store.SaveChangesAsync(ct);
        return account;
    }

    private static string Snapshot(Account a) => JsonSerializer.Serialize(new
    {
        a.Name,
        a.Number,
        Type = a.Type.ToString(),
        a.ParentId,
        a.Description,
        a.IsActive,
    });
}
