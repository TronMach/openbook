using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

public enum BankConnectionProvider
{
    /// <summary>SimpleFIN Bridge (simplefin.org): the user buys access and pastes a setup token; no developer account needed.</summary>
    SimpleFin,
}

/// <summary>
/// A live link to a bank data service that fetches statement lines into the bank feed, in place of downloading
/// files. The access credential (for SimpleFIN, the access URL with its user and password) is stored encrypted by
/// the host and never shown again. Each remote account it sees can be linked to one bank or card account.
/// </summary>
public sealed class BankConnection
{
    public const int MaxNameLength = 100;
    private readonly List<BankConnectionAccount> _accounts = [];

    private BankConnection()
    {
        Name = string.Empty;
        ProtectedAccess = string.Empty;
    }

    public Guid Id { get; private set; }
    public BankConnectionProvider Provider { get; private set; }
    public string Name { get; private set; }

    /// <summary>Encrypted by the host (ASP.NET Data Protection); empty once disconnected.</summary>
    public string ProtectedAccess { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSyncAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }

    /// <summary>Whether the latest sync failed (<see cref="LastError"/> says why); warnings alone don't count.</summary>
    public bool LastSyncFailed { get; private set; }

    /// <summary>Why the last sync failed, or warnings the service sent back (e.g. "re-authenticate with your bank").</summary>
    public string? LastError { get; private set; }

    public IReadOnlyList<BankConnectionAccount> Accounts => _accounts;

    public static BankConnection Create(BankConnectionProvider provider, string name, string protectedAccess, DateTimeOffset now)
    {
        if (!Enum.IsDefined(provider))
            throw new DomainException("connection.provider_invalid", "Unknown bank connection provider.");
        if (string.IsNullOrWhiteSpace(protectedAccess))
            throw new DomainException("connection.access_required", "The connection has no access credential.");
        var connection = new BankConnection
        {
            Id = Guid.CreateVersion7(),
            Provider = provider,
            ProtectedAccess = protectedAccess,
            IsActive = true,
            CreatedAt = now,
        };
        connection.Rename(name);
        return connection;
    }

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("connection.name_required", "Give the connection a name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException("connection.name_too_long", $"Keep the name to {MaxNameLength} characters.");
        Name = trimmed;
    }

    /// <summary>Forgets the credential and unlinks every account. Lines already fetched stay in the feed.</summary>
    public void Disconnect()
    {
        IsActive = false;
        ProtectedAccess = string.Empty;
        foreach (var account in _accounts)
            account.Link(null, null);
    }

    /// <summary>Adds the remote account, or refreshes its name and balance.</summary>
    public BankConnectionAccount Refresh(string remoteId, string name, string? institution, string? currency, decimal? balance, DateOnly? balanceDate)
    {
        if (string.IsNullOrWhiteSpace(remoteId))
            throw new DomainException("connection.account_id_required", "The service sent an account without an id.");
        var account = Find(remoteId);
        if (account is null)
        {
            account = new BankConnectionAccount(Id, remoteId.Trim());
            _accounts.Add(account);
        }

        account.Update(name, institution, currency, balance, balanceDate);
        return account;
    }

    public BankConnectionAccount? Find(string remoteId) => _accounts.FirstOrDefault(a => a.RemoteId == remoteId);

    /// <param name="accountId">The bank or card account its lines go to; null stops fetching it.</param>
    /// <param name="importFrom">The first date to fetch; earlier lines are left alone (e.g. already entered by hand).</param>
    public void Link(string remoteId, Guid? accountId, DateOnly? importFrom)
    {
        if (!IsActive)
            throw new DomainException("connection.disconnected", "This connection is disconnected.");
        var account = Find(remoteId) ?? throw new DomainException("connection.account_not_found", "That account isn't on this connection.");
        if (accountId is { } id && _accounts.Any(a => a != account && a.AccountId == id))
            throw new DomainException("connection.account_linked", "Another account on this connection already goes to that account.");
        account.Link(accountId, importFrom);
    }

    public void SyncSucceeded(DateTimeOffset now, string? warnings)
    {
        LastSyncAt = now;
        LastSuccessAt = now;
        LastSyncFailed = false;
        LastError = Clip(warnings);
    }

    public void SyncFailed(DateTimeOffset now, string error)
    {
        LastSyncAt = now;
        LastSyncFailed = true;
        LastError = Clip(error) ?? "The sync failed.";
    }

    private static string? Clip(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 1000 ? trimmed[..1000] : trimmed;
    }
}

/// <summary>An account the bank data service reports, and where (if anywhere) its lines go.</summary>
public sealed class BankConnectionAccount
{
    private BankConnectionAccount()
    {
        RemoteId = string.Empty;
        Name = string.Empty;
    }

    internal BankConnectionAccount(Guid connectionId, string remoteId)
    {
        ConnectionId = connectionId;
        RemoteId = remoteId;
        Name = string.Empty;
    }

    public Guid ConnectionId { get; private set; }

    /// <summary>The service's id for the account.</summary>
    public string RemoteId { get; private set; }

    public string Name { get; private set; }
    public string? Institution { get; private set; }
    public string? Currency { get; private set; }

    /// <summary>The balance the bank reports, from the holder's view (negative on a card means owed).</summary>
    public decimal? Balance { get; private set; }

    public DateOnly? BalanceDate { get; private set; }

    /// <summary>The bank or card account its lines are fetched into.</summary>
    public Guid? AccountId { get; private set; }

    public DateOnly? ImportFrom { get; private set; }

    /// <summary>The latest posted date fetched so far; the next sync starts a little before it.</summary>
    public DateOnly? FetchedThrough { get; private set; }

    internal void Update(string name, string? institution, string? currency, decimal? balance, DateOnly? balanceDate)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        Name = trimmed.Length == 0 ? RemoteId : trimmed.Length > 200 ? trimmed[..200] : trimmed;
        Institution = Clip(institution, 200);
        Currency = Clip(currency, 10);
        Balance = balance is { } b ? Math.Round(b, 2, MidpointRounding.AwayFromZero) : null;
        BalanceDate = balanceDate;
    }

    internal void Link(Guid? accountId, DateOnly? importFrom)
    {
        if (accountId != AccountId)
            FetchedThrough = null;
        AccountId = accountId;
        ImportFrom = accountId is null ? null : importFrom;
    }

    public void Fetched(DateOnly through)
    {
        if (FetchedThrough is null || through > FetchedThrough)
            FetchedThrough = through;
    }

    private static string? Clip(string? text, int max)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
