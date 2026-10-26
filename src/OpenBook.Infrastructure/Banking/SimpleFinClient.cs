using System.Net.Http.Headers;
using System.Text;
using OpenBook.Application.Banking;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Banking;

/// <summary>
/// SimpleFIN over HTTPS (see <see cref="SimpleFin"/>): claims setup tokens and fetches accounts. Long ranges are
/// fetched in 60-day pieces because the bridge limits how much history one request returns.
/// </summary>
public sealed class SimpleFinClient : IBankConnectionClient, IDisposable
{
    private const int MaxDaysPerRequest = 60;
    private readonly HttpClient _client;

    public SimpleFinClient()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for the bridge.</summary>
    public SimpleFinClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public BankConnectionProvider Provider => BankConnectionProvider.SimpleFin;

    public async Task<string> ConnectAsync(string setupToken, CancellationToken ct)
    {
        var claim = SimpleFin.ClaimUrl(setupToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, claim) { Content = new ByteArrayContent([]) };
        using var response = await _client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new DomainException("simplefin.token_used", "That setup token has already been used or was revoked. Make a new one in SimpleFIN Bridge.");
        response.EnsureSuccessStatusCode();
        var access = (await response.Content.ReadAsStringAsync(ct)).Trim();
        SimpleFin.ParseAccess(access);
        return access;
    }

    public async Task<RemoteSnapshot> FetchAsync(string access, DateOnly? from, DateOnly through, TimeZoneInfo zone, CancellationToken ct)
    {
        var (baseUrl, user, password) = SimpleFin.ParseAccess(access);
        if (from is not { } start)
            return SimpleFin.Parse(await GetAsync(SimpleFin.AccountsUrl(baseUrl, null, through, zone), user, password, ct), zone);

        // Accounts come from the latest piece; transactions are gathered from every piece, keyed by id.
        var accounts = new Dictionary<string, RemoteAccount>(StringComparer.Ordinal);
        var transactions = new Dictionary<string, Dictionary<string, RemoteTransaction>>(StringComparer.Ordinal);
        var messages = new List<string>();
        for (var pieceStart = start; pieceStart <= through; pieceStart = pieceStart.AddDays(MaxDaysPerRequest))
        {
            var pieceEnd = pieceStart.AddDays(MaxDaysPerRequest - 1) < through ? pieceStart.AddDays(MaxDaysPerRequest - 1) : through;
            var snapshot = SimpleFin.Parse(await GetAsync(SimpleFin.AccountsUrl(baseUrl, pieceStart, pieceEnd, zone), user, password, ct), zone);
            foreach (var account in snapshot.Accounts)
                accounts[account.Id] = account;
            foreach (var (accountId, lines) in snapshot.Transactions)
            {
                var known = transactions.TryGetValue(accountId, out var existing) ? existing : transactions[accountId] = new(StringComparer.Ordinal);
                foreach (var line in lines)
                    known[line.Id] = line;
            }

            messages.AddRange(snapshot.Messages.Where(m => !messages.Contains(m)));
        }

        return new RemoteSnapshot(
            accounts.Values.ToList(),
            transactions.ToDictionary(t => t.Key, t => (IReadOnlyList<RemoteTransaction>)t.Value.Values.ToList(), StringComparer.Ordinal),
            messages);
    }

    private async Task<string> GetAsync(Uri url, string user, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _client.Dispose();
}
