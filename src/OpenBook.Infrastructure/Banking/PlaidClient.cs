using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Banking;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Banking;

/// <summary>The one HTTP client the Plaid calls share (the keys differ per company file, so the client itself is scoped).</summary>
public sealed class PlaidTransport : IDisposable
{
    public PlaidTransport()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    /// <summary>For tests: a handler standing in for Plaid.</summary>
    public PlaidTransport(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    internal HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Plaid over HTTPS (see <see cref="Plaid"/>), with the company's own keys read for each call. The addresses are Plaid's
/// own (sandbox or production), never typed in, so the calls needn't be kept off private networks.
/// </summary>
public sealed class PlaidClient(IPlaidKeys keys, PlaidTransport transport) : IBankConnectionClient, IPlaidLink
{
    private static readonly string[] Products = ["transactions"];
    private static readonly string[] Countries = ["US", "CA"];

    public BankConnectionProvider Provider => BankConnectionProvider.Plaid;

    public async Task<string> CreateLinkTokenAsync(string clientUserId, CancellationToken ct)
    {
        var json = await PostAsync("link/token/create", new Dictionary<string, object?>
        {
            ["client_name"] = "OpenBook",
            ["user"] = new Dictionary<string, object?> { ["client_user_id"] = clientUserId },
            ["products"] = Products,
            ["transactions"] = new Dictionary<string, object?> { ["days_requested"] = Plaid.DaysRequested },
            ["country_codes"] = Countries,
            ["language"] = "en",
        }, ct);
        return Plaid.LinkToken(json);
    }

    public async Task<string> ConnectAsync(string setupToken, CancellationToken ct)
    {
        var publicToken = (setupToken ?? string.Empty).Trim();
        if (publicToken.Length is 0 or > 500)
            throw new DomainException("plaid.public_token_invalid", "Plaid didn't hand back a bank sign-in. Choose your bank again.");
        return Plaid.AccessToken(await PostAsync("item/public_token/exchange", new Dictionary<string, object?> { ["public_token"] = publicToken }, ct));
    }

    public async Task<RemoteSnapshot> FetchAsync(string access, DateOnly? from, DateOnly through, TimeZoneInfo zone, CancellationToken ct)
    {
        if (from is not { } start)
        {
            var accounts = Plaid.Accounts(await PostAsync("accounts/get", new Dictionary<string, object?> { ["access_token"] = access }, ct));
            return new RemoteSnapshot(accounts, new Dictionary<string, IReadOnlyList<RemoteTransaction>>(StringComparer.Ordinal), []);
        }

        IReadOnlyList<RemoteAccount> latest = [];
        var lines = new Dictionary<string, Dictionary<string, RemoteTransaction>>(StringComparer.Ordinal);
        for (var offset = 0; ;)
        {
            var page = Plaid.TransactionsPage(await PostAsync("transactions/get", new Dictionary<string, object?>
            {
                ["access_token"] = access,
                ["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["end_date"] = through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["options"] = new Dictionary<string, object?> { ["count"] = Plaid.PageSize, ["offset"] = offset },
            }, ct));
            latest = page.Accounts;
            foreach (var (accountId, line) in page.Transactions)
            {
                var known = lines.TryGetValue(accountId, out var existing) ? existing : lines[accountId] = new(StringComparer.Ordinal);
                known[line.Id] = line;
            }

            // The next page starts after every line this one held. An empty page ends it too, so an answer that
            // miscounts can't loop for ever.
            offset += page.Received;
            if (page.Received == 0 || offset >= page.Total)
                break;
        }

        return new RemoteSnapshot(latest, lines.ToDictionary(l => l.Key, l => (IReadOnlyList<RemoteTransaction>)l.Value.Values.ToList(), StringComparer.Ordinal), []);
    }

    public async Task RemoveAsync(string access, CancellationToken ct) =>
        await PostAsync("item/remove", new Dictionary<string, object?> { ["access_token"] = access }, ct);

    private async Task<string> PostAsync(string path, Dictionary<string, object?> body, CancellationToken ct)
    {
        var plaid = await keys.GetAsync(ct)
            ?? throw new DomainException("plaid.not_set_up", "Plaid isn't set up: an admin enters the company's Plaid keys under Settings, Plaid.");
        body["client_id"] = plaid.ClientId;
        body["secret"] = plaid.Secret;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Plaid.BaseUrl(plaid.Environment), path))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var response = await transport.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw Plaid.Error(text, (int)response.StatusCode);
        return text;
    }
}
