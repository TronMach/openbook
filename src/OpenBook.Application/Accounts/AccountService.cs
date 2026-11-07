using OpenBook.Application.Companies;
using System.Text.Json;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Accounts;

/// <param name="DonorRestricted">Nonprofits: the account holds amounts with donor restrictions (income, other income and equity only).</param>
/// <param name="Excluded1099">Payments to 1099 vendors for this account aren't reportable (materials, merchandise).</param>
public sealed record CreateAccountCommand(string Name, AccountType Type, string? Number = null, Guid? ParentId = null, string? Description = null, bool DonorRestricted = false,
    bool Excluded1099 = false);

/// <param name="DonorRestricted">Null keeps the current setting.</param>
/// <param name="Excluded1099">Null keeps the current setting.</param>
public sealed record UpdateAccountCommand(Guid Id, string Name, AccountType Type, string? Number, Guid? ParentId, string? Description, bool? DonorRestricted = null,
    bool? Excluded1099 = null);

/// <summary>Chart-of-accounts maintenance. Accounts are never deleted; they're made inactive.</summary>
/// <summary>Something outside the chart that an account is needed for, so it stays active: asked before an account is made inactive.</summary>
public interface IAccountInUse
{
    /// <summary>Why the account can't go, in words that finish the sentence; null when nothing here needs it.</summary>
    Task<string?> WhyNeededAsync(Account account, CancellationToken ct);
}

public sealed class AccountService(ILedgerStore store, ICompanyStore companies, Ledger.PostingEngine engine, IEnumerable<IAccountInUse> uses, IClock clock, ICurrentUser user)
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
        account.SetExcluded1099(command.Excluded1099);

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
        // Invoices and bills name the account they're kept in: anything else would stop the agings tying to it.
        if (hasPostings && (account.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable || command.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable))
            throw new Domain.Common.DomainException("account.type_receivable", "This account has transactions, so it can't change to or from Accounts Receivable or Accounts Payable.");
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
        if (command.Excluded1099 is { } excluded)
            account.SetExcluded1099(excluded);

        store.Add(Audit(AuditAction.Updated, account, before));
        await store.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var chart = await GetChartAsync(ct);
        var account = chart.Get(id);
        chart.EnsureCanDeactivate(account);
        foreach (var use in uses)
        {
            if (await use.WhyNeededAsync(account, ct) is { } reason)
                throw new DomainException("account.in_use", $"{account.Name} can't be made inactive: {reason}");
        }

        return await SetActiveAsync(account, active: false, ct);
    }

    /// <summary>
    /// Makes an account OpenBook needs and doesn't have. When its name is held already, by an inactive account or one of
    /// another type, says so: the plain refusal, that the name is taken, doesn't tell anyone what to do.
    /// </summary>
    public async Task<Account> CreateUsualAsync(CreateAccountCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var chart = await GetChartAsync(ct);
        if (chart.Ordered(includeInactive: true).Select(x => x.Account).FirstOrDefault(a => a.ParentId is null && string.Equals(a.Name, command.Name, StringComparison.OrdinalIgnoreCase)) is { } held)
        {
            throw new DomainException("account.usual_name_held", held.IsActive
                ? $"OpenBook needs an account named \"{held.Name}\" for this, and yours isn't the right type. Change its type on the chart of accounts, or rename it."
                : $"The account \"{held.Name}\" is inactive. Make it active again on the chart of accounts.");
        }

        return await CreateAsync(command, ct);
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
        a.Excluded1099,
    });
}
