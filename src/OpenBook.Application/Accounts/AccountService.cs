using OpenBook.Application.Companies;
using System.Text.Json;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;

namespace OpenBook.Application.Accounts;

/// <param name="DonorRestricted">Nonprofits: the account holds amounts with donor restrictions (income, other income and equity only).</param>
public sealed record CreateAccountCommand(string Name, AccountType Type, string? Number = null, Guid? ParentId = null, string? Description = null, bool DonorRestricted = false);

/// <param name="DonorRestricted">Null keeps the current setting.</param>
public sealed record UpdateAccountCommand(Guid Id, string Name, AccountType Type, string? Number, Guid? ParentId, string? Description, bool? DonorRestricted = null);

/// <summary>Chart-of-accounts maintenance. Accounts are never deleted; they're made inactive.</summary>
public sealed class AccountService(ILedgerStore store, ICompanyStore companies, Ledger.PostingEngine engine, IClock clock, ICurrentUser user)
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
        account.SetDonorRestricted(command.DonorRestricted);

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
        if (command.Type != account.Type && Domain.Banking.BankTransaction.IsBankingAccount(account.Type)
            && await store.HasBankingRecordsAsync(account.Id, ct))
            throw new Domain.Common.DomainException("account.type_banking",
                "This account has bank transactions or reconciliations, so its type can't change.");

        var hasPostings = command.Type != account.Type && await store.HasPostingsAsync(account.Id, ct);
        chart.EnsureCanUpdate(account, command.Name, command.Number, command.Type, command.ParentId, hasPostings);

        // Net assets read the flag as it is now, so changing it restates every past statement.
        if (command.DonorRestricted is { } flag && flag != account.DonorRestricted && await store.FirstPostingDateAsync(account.Id, ct) is { } first)
            engine.EnsureOpen(await companies.GetRequiredCompanyAsync(ct), first, $"changing whether {account.Name} holds donor-restricted amounts");

        var before = Snapshot(account);
        account.Update(command.Name, command.Number, command.Description);
        account.ChangeType(command.Type);
        account.MoveTo(command.ParentId);
        if (command.DonorRestricted is { } restricted)
            account.SetDonorRestricted(restricted);

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

    /// <summary>
    /// Sets the tax line of each listed account (null or empty clears it) in one save. Accounts not listed are
    /// left alone; only changed ones are audited.
    /// </summary>
    public async Task<int> MapTaxLinesAsync(IReadOnlyDictionary<Guid, string?> taxLines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taxLines);
        var chart = await GetChartAsync(ct);
        var changed = 0;
        foreach (var (id, key) in taxLines)
        {
            var account = chart.Get(id);
            var before = Snapshot(account);
            account.SetTaxLine(key);
            if (Snapshot(account) == before)
                continue;
            store.Add(Audit(AuditAction.Updated, account, before));
            changed++;
        }

        if (changed > 0)
            await store.SaveChangesAsync(ct);
        return changed;
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
        TaxLine = a.TaxLineKey,
        a.DonorRestricted,
    });
}
