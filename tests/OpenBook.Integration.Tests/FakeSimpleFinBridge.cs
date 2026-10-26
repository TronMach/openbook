using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenBook.Integration.Tests;

/// <summary>
/// A SimpleFIN Bridge in memory: setup tokens claim once (then 403), the access URL's Basic credentials are checked,
/// and /accounts filters transactions by start-date (inclusive) and end-date (exclusive), as the protocol says.
/// </summary>
public sealed class FakeSimpleFinBridge : HttpMessageHandler
{
    public const string ClaimUrl = "https://bridge.test/simplefin/claim/demo";
    public const string AccessUrl = "https://user1:secret%2F1@bridge.test/simplefin";
    public static readonly string SetupToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClaimUrl));

    private bool _claimed;

    public List<Account> Accounts { get; } = [];
    public List<string> Errors { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Set to make every accounts request fail with this status.</summary>
    public HttpStatusCode? FailWith { get; set; }

    public sealed record Account(string Id, string Name, string Org, decimal Balance, List<Transaction> Transactions);

    public sealed record Transaction(string Id, DateOnly Posted, decimal Amount, string Description, string? Payee = null, bool Pending = false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!;
        if (request.Method == HttpMethod.Post && url.AbsoluteUri == ClaimUrl)
        {
            if (_claimed)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            _claimed = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccessUrl) };
        }

        if (request.Method == HttpMethod.Get && url.AbsolutePath == "/simplefin/accounts")
        {
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("user1:secret/1"));
            if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected || !string.IsNullOrEmpty(url.UserInfo))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (FailWith is { } status)
                return new HttpResponseMessage(status);

            var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
            var balancesOnly = query.ContainsKey("balances-only");
            long? start = query.TryGetValue("start-date", out var s) ? long.Parse(s, CultureInfo.InvariantCulture) : null;
            long? end = query.TryGetValue("end-date", out var e) ? long.Parse(e, CultureInfo.InvariantCulture) : null;
            var json = JsonSerializer.Serialize(new
            {
                errors = Errors,
                accounts = Accounts.Select(a => new Dictionary<string, object?>
                {
                    ["org"] = new { domain = "bank.test", name = a.Org },
                    ["id"] = a.Id,
                    ["name"] = a.Name,
                    ["currency"] = "USD",
                    ["balance"] = a.Balance.ToString("0.00", CultureInfo.InvariantCulture),
                    ["balance-date"] = 1767225600,
                    ["transactions"] = balancesOnly
                        ? Array.Empty<object>()
                        : a.Transactions
                            .Where(t => (start is null || Unix(t.Posted) >= start) && (end is null || Unix(t.Posted) < end))
                            .Select(t => (object)new Dictionary<string, object?>
                            {
                                ["id"] = t.Id,
                                ["posted"] = t.Pending ? 0 : Unix(t.Posted) + 43200,
                                ["amount"] = t.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                                ["description"] = t.Description,
                                ["payee"] = t.Payee,
                                ["pending"] = t.Pending,
                                ["transacted_at"] = Unix(t.Posted),
                            }).ToArray(),
                }),
            });
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Midnight UTC; tests run the sync in UTC.</summary>
    public static long Unix(DateOnly date) => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
}
