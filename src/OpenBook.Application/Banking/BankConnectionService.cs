using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <param name="Queued">New lines waiting for review.</param>
/// <param name="AutoAdded">Lines bank rules added straight away.</param>
/// <param name="Skipped">Lines fetched before.</param>
/// <param name="Error">Why the sync failed; null when it worked.</param>
public sealed record ConnectionSyncResult(int Queued, int AutoAdded, int Skipped, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Live bank feeds: a connection to a bank data service (SimpleFIN Bridge) fetches posted transactions for each linked
/// account into the bank feed's review queue, exactly as if a statement file had been imported. Lines are keyed by the
/// service's transaction id, so overlapping fetches add nothing twice. Pending transactions wait until they post.
/// </summary>
public sealed class BankConnectionService(
    IBankingStore bank,
    BankFeedService feed,
    AccountService accounts,
    IEnumerable<IBankConnectionClient> clients,
    ISecretProtector protector,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "bank_connection";

    /// <summary>How often the background service fetches; SimpleFIN allows about 24 requests a day.</summary>
    public static readonly TimeSpan SyncEvery = TimeSpan.FromHours(6);

    /// <summary>A newly linked account without a start date fetches this far back.</summary>
    public const int DefaultHistoryDays = 30;

    /// <summary>Each fetch starts this many days before the last posted date seen, for lines the bank posts late.</summary>
    public const int OverlapDays = 10;

    public async Task<IReadOnlyList<BankConnection>> ListAsync(CancellationToken ct = default) =>
        (await bank.ListConnectionsAsync(ct)).OrderByDescending(c => c.IsActive).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<BankConnection?> GetAsync(Guid id, CancellationToken ct = default) => bank.GetConnectionAsync(id, ct);

    /// <summary>Claims the setup token, stores the access credential encrypted, and lists the accounts it can see.</summary>
    public async Task<BankConnection> ConnectAsync(BankConnectionProvider provider, string setupToken, string? name, TimeZoneInfo zone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var client = Client(provider);
        string access;
        try
        {
            access = await client.ConnectAsync(setupToken, ct);
        }
        catch (Exception ex) when (IsServiceFailure(ex, ct))
        {
            throw new DomainException("connection.failed", $"Couldn't connect: {Describe(ex)}");
        }

        var now = clock.UtcNow;
        var connection = BankConnection.Create(provider, string.IsNullOrWhiteSpace(name) ? Default(provider) : name, protector.Protect(access), now);
        try
        {
            var snapshot = await client.FetchAsync(access, null, Today(zone), zone, ct);
            Refresh(connection, snapshot);
            connection.SyncSucceeded(now, Messages(snapshot));
        }
        catch (Exception ex) when (IsServiceFailure(ex, ct))
        {
            connection.SyncFailed(now, Describe(ex));
        }

        bank.Add(connection);
        bank.Add(AuditRecord.Create(now, user.Actor, AuditAction.Created, EntityType, connection.Id, null, Snapshot(connection)));
        await bank.SaveChangesAsync(ct);
        return connection;
    }

    public async Task<BankConnection> RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var connection = await RequiredAsync(id, ct);
        var before = Snapshot(connection);
        connection.Rename(name);
        await AuditAsync(connection, before, ct);
        return connection;
    }

    /// <summary>Sends a remote account's lines to a bank or card account (or stops fetching it when null).</summary>
    public async Task<BankConnection> LinkAsync(Guid id, string remoteId, Guid? accountId, DateOnly? importFrom, CancellationToken ct = default)
    {
        var connection = await RequiredAsync(id, ct);
        if (accountId is { } target)
        {
            var account = (await accounts.GetChartAsync(ct)).Get(target);
            BankingRules.EnsureBankingAccount(account);
            if (!account.IsActive)
                throw new DomainException("connection.account_inactive", "That account is inactive.");
            if ((await bank.ListConnectionsAsync(ct)).Any(c => c.Id != id && c.IsActive && c.Accounts.Any(a => a.AccountId == target)))
                throw new DomainException("connection.account_linked", "Another bank connection already fetches into that account.");
        }

        var before = Snapshot(connection);
        connection.Link(remoteId, accountId, importFrom);
        await AuditAsync(connection, before, ct);
        return connection;
    }

    /// <summary>Forgets the credential and unlinks the accounts. Lines already fetched stay in the bank feed.</summary>
    public async Task DisconnectAsync(Guid id, CancellationToken ct = default)
    {
        var connection = await RequiredAsync(id, ct);
        var before = Snapshot(connection);
        connection.Disconnect();
        await AuditAsync(connection, before, ct);
    }

    /// <summary>Fetches every linked account's new posted lines into its bank feed. A failure is kept on the connection, not thrown.</summary>
    public async Task<ConnectionSyncResult> SyncAsync(Guid id, TimeZoneInfo zone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var connection = await RequiredAsync(id, ct);
        if (!connection.IsActive)
            throw new DomainException("connection.disconnected", "This connection is disconnected.");

        var now = clock.UtcNow;
        var today = Today(zone);
        int queued = 0, autoAdded = 0, skipped = 0;
        try
        {
            var access = protector.Unprotect(connection.ProtectedAccess)
                ?? throw new DomainException("connection.credential_unreadable",
                    "The saved credential can't be read here (the company file may have moved to another computer). Disconnect and connect again.");
            var linked = connection.Accounts.Where(a => a.AccountId is not null).ToList();
            DateOnly? from = linked.Count == 0 ? null : linked.Min(a => Start(a, today));
            var snapshot = await Client(connection.Provider).FetchAsync(access, from, today, zone, ct);
            Refresh(connection, snapshot);

            var chart = await accounts.GetChartAsync(ct);
            var problems = new List<string>(Messages(snapshot) is { } m ? [m] : []);
            foreach (var remote in linked)
            {
                var target = chart.Find(remote.AccountId!.Value);
                if (target is not { IsActive: true })
                {
                    problems.Add($"{remote.Name}: the linked account is inactive, so nothing was fetched.");
                    continue;
                }

                var start = Start(remote, today);
                var posted = snapshot.Transactions.GetValueOrDefault(remote.RemoteId, [])
                    .Where(t => !t.Pending && t.Date >= start && t.Date <= today && t.Amount != 0m)
                    .OrderBy(t => t.Date)
                    .ToList();
                foreach (var chunk in posted.Chunk(BankImportService.MaxRows))
                {
                    var result = await feed.QueueAsync(target.Id, chunk.Select(Line).ToList(), ct);
                    queued += result.Queued;
                    autoAdded += result.AutoAdded;
                    skipped += result.Skipped;
                }

                if (posted.Count > 0)
                    remote.Fetched(posted[^1].Date);
            }

            connection.SyncSucceeded(now, problems.Count == 0 ? null : string.Join(" ", problems));
            await bank.SaveChangesAsync(ct);
            return new ConnectionSyncResult(queued, autoAdded, skipped, null);
        }
        catch (Exception ex) when (IsServiceFailure(ex, ct) || ex is DomainException)
        {
            var error = Describe(ex);
            connection.SyncFailed(now, error);
            await bank.SaveChangesAsync(ct);
            return new ConnectionSyncResult(queued, autoAdded, skipped, error);
        }
    }

    /// <summary>Syncs each active connection with linked accounts that hasn't been fetched for <see cref="SyncEvery"/>.</summary>
    public async Task<IReadOnlyList<(BankConnection Connection, ConnectionSyncResult Result)>> SyncDueAsync(TimeZoneInfo zone, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = (await bank.ListConnectionsAsync(ct))
            .Where(c => c.IsActive && c.Accounts.Any(a => a.AccountId is not null) && (c.LastSyncAt is null || now - c.LastSyncAt >= SyncEvery))
            .ToList();
        var results = new List<(BankConnection, ConnectionSyncResult)>();
        foreach (var connection in due)
            results.Add((connection, await SyncAsync(connection.Id, zone, ct)));
        return results;
    }

    /// <summary>The first date a sync fetches for the account.</summary>
    public static DateOnly Start(BankConnectionAccount account, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(account);
        var start = account.FetchedThrough is { } through ? through.AddDays(-OverlapDays) : today.AddDays(-DefaultHistoryDays);
        return account.ImportFrom is { } floor && floor > start ? floor : start;
    }

    private static FeedLine Line(RemoteTransaction t)
    {
        // SimpleFIN's description is the bank's text; payee, when given, is a cleaned-up name.
        var payee = t.Payee ?? t.Description;
        var memo = t.Memo ?? (t.Payee is not null && t.Description is not null && t.Description != t.Payee ? t.Description : null);
        return new FeedLine(t.Date, t.Amount, Clip(payee, 200), null, Clip(memo, 1000), Clip(t.Id, 240));
    }

    private static void Refresh(BankConnection connection, RemoteSnapshot snapshot)
    {
        foreach (var a in snapshot.Accounts)
            connection.Refresh(a.Id, a.Name, a.Institution, a.Currency, a.Balance, a.BalanceDate);
    }

    private static string? Messages(RemoteSnapshot snapshot) => snapshot.Messages.Count == 0 ? null : string.Join(" ", snapshot.Messages);

    private IBankConnectionClient Client(BankConnectionProvider provider) =>
        clients.FirstOrDefault(c => c.Provider == provider)
        ?? throw new DomainException("connection.provider_unavailable", $"{Default(provider)} connections aren't available on this server.");

    private async Task<BankConnection> RequiredAsync(Guid id, CancellationToken ct) =>
        await bank.GetConnectionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private async Task AuditAsync(BankConnection connection, string before, CancellationToken ct)
    {
        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, connection.Id, before, Snapshot(connection)));
        await bank.SaveChangesAsync(ct);
    }

    private DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);

    private static string Default(BankConnectionProvider provider) => provider switch
    {
        BankConnectionProvider.SimpleFin => "SimpleFIN",
        _ => provider.ToString(),
    };

    /// <summary>Network trouble, timeouts and odd responses: reported to the user, not crashes.</summary>
    private static bool IsServiceFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException
        || (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        || (ex is DomainException d && d.Code.StartsWith("simplefin.", StringComparison.Ordinal));

    private static string Describe(Exception ex) => ex switch
    {
        DomainException d => d.Message,
        TaskCanceledException or OperationCanceledException => "The bank data service didn't answer in time.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } =>
            "The bank data service refused the credential. The token may have been used already or revoked; make a new one and connect again.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => "The SimpleFIN subscription needs renewing.",
        HttpRequestException h => h.StatusCode is { } code ? $"The bank data service answered HTTP {(int)code}." : $"Couldn't reach the bank data service ({h.Message}).",
        _ => "The bank data service sent a response OpenBook couldn't read.",
    };

    private static string? Clip(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;

    /// <summary>No credential in here: audit records feed webhooks.</summary>
    private static string Snapshot(BankConnection c) => JsonSerializer.Serialize(new
    {
        Provider = c.Provider.ToString(),
        c.Name,
        c.IsActive,
        Accounts = c.Accounts.Select(a => new { a.RemoteId, a.Name, a.Institution, a.AccountId, a.ImportFrom }),
    });
}
