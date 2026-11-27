using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Stands in for Plaid's API behind the real <c>PlaidClient</c>: checks the keys in each body, hands out a link token,
/// exchanges <see cref="PublicToken"/> for <see cref="AccessToken"/>, lists accounts and pages through transactions as
/// Plaid does (positive amounts are money leaving the account).
/// </summary>
public sealed class FakePlaid : HttpMessageHandler
{
    public const string ClientId = "testclient1";
    public const string Secret = "test-secret";
    public const string PublicToken = "public-sandbox-ok";
    public const string AccessToken = "access-sandbox-1";
    public const string LinkToken = "link-sandbox-1";

    public List<Account> Accounts { get; } = [];
    public List<(string Path, JsonObject Body)> Requests { get; } = [];
    public List<string> Removed { get; } = [];

    /// <summary>Set to make every accounts and transactions request fail with this Plaid error code.</summary>
    public string? FailWith { get; set; }

    /// <summary>Plaid's largest page is 500; tests make it small to see paging.</summary>
    public int? MaxPage { get; set; }

    public sealed record Account(string Id, string Name, string Mask, string Type, decimal Current, List<Transaction> Transactions);

    /// <param name="Amount">Plaid's sign: positive is money out.</param>
    public sealed record Transaction(string Id, DateOnly Date, decimal Amount, string Name, string? Merchant = null, bool Pending = false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        Requests.Add((path, body));
        if (request.RequestUri.Host != "sandbox.plaid.com" && request.RequestUri.Host != "production.plaid.com")
            return Error("INVALID_REQUEST", "unknown host");
        if ((string?)body["client_id"] != ClientId || (string?)body["secret"] != Secret)
            return Error("INVALID_API_KEYS", "invalid client_id or secret provided", "INVALID_INPUT");

        switch (path)
        {
            case "link/token/create":
                // Update mode names a login it knows. Real Plaid ignores products there; OpenBook leaves them out, which this holds it to.
                if (body["access_token"] is { } access && ((string?)access != AccessToken || body["products"] is not null))
                    return Error("INVALID_FIELD", "products must not be provided in update mode, and the access token must be valid", "INVALID_REQUEST");
                return Ok(new JsonObject { ["link_token"] = LinkToken, ["expiration"] = "2026-11-20T00:00:00Z" });
            case "item/public_token/exchange":
                return (string?)body["public_token"] == PublicToken
                    ? Ok(new JsonObject { ["access_token"] = AccessToken, ["item_id"] = "item-1" })
                    : Error("INVALID_PUBLIC_TOKEN", "provided public token is in an invalid format");
            case "item/remove":
                Removed.Add((string?)body["access_token"] ?? "");
                return Ok(new JsonObject { ["request_id"] = "r" });
        }

        if ((string?)body["access_token"] != AccessToken)
            return Error("INVALID_ACCESS_TOKEN", "provided access token is in an invalid format");
        if (FailWith is { } code)
            return Error(code, "the bank says no", code == "ITEM_LOGIN_REQUIRED" ? "ITEM_ERROR" : "API_ERROR");

        if (path == "accounts/get")
            return Ok(new JsonObject { ["accounts"] = AccountsJson() });
        if (path != "transactions/get")
            return Error("INVALID_REQUEST", "unknown path");

        var from = DateOnly.Parse((string)body["start_date"]!, CultureInfo.InvariantCulture);
        var to = DateOnly.Parse((string)body["end_date"]!, CultureInfo.InvariantCulture);
        var options = body["options"]?.AsObject();
        var count = Math.Min((int?)options?["count"] ?? 100, MaxPage ?? 500);
        var offset = (int?)options?["offset"] ?? 0;
        var all = Accounts.SelectMany(a => a.Transactions.Select(t => (Account: a, Line: t)))
            .Where(x => x.Line.Date >= from && x.Line.Date <= to)
            .OrderByDescending(x => x.Line.Date).ThenBy(x => x.Line.Id, StringComparer.Ordinal)
            .ToList();
        var page = new JsonArray(all.Skip(offset).Take(count).Select(x => (JsonNode)new JsonObject
        {
            ["transaction_id"] = x.Line.Id,
            ["account_id"] = x.Account.Id,
            ["amount"] = x.Line.Amount,
            ["date"] = x.Line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["name"] = x.Line.Name,
            ["merchant_name"] = x.Line.Merchant,
            ["pending"] = x.Line.Pending,
            ["iso_currency_code"] = "USD",
        }).ToArray());
        return Ok(new JsonObject { ["accounts"] = AccountsJson(), ["transactions"] = page, ["total_transactions"] = all.Count });
    }

    private JsonArray AccountsJson() => new(Accounts.Select(a => (JsonNode)new JsonObject
    {
        ["account_id"] = a.Id,
        ["name"] = a.Name,
        ["mask"] = a.Mask,
        ["type"] = a.Type,
        ["subtype"] = a.Type == "credit" ? "credit card" : "checking",
        ["balances"] = new JsonObject { ["current"] = a.Current, ["available"] = null, ["iso_currency_code"] = "USD" },
    }).ToArray());

    private static HttpResponseMessage Ok(JsonObject body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string code, string message, string type = "INVALID_INPUT") =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error_type = type, error_code = code, error_message = message, display_message = (string?)null }),
                Encoding.UTF8, "application/json"),
        };
}
