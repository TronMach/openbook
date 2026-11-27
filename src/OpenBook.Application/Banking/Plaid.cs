using System.Globalization;
using System.Text.Json;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <summary>The company's Plaid keys, unprotected for a call; null while they aren't set.</summary>
public sealed record PlaidKeys(string ClientId, string Secret, PlaidEnvironment Environment);

/// <summary>Reads the company's Plaid keys (scoped: they're in the company file).</summary>
public interface IPlaidKeys
{
    Task<PlaidKeys?> GetAsync(CancellationToken ct);
}

/// <summary>Plaid Link's side of connecting: the short-lived token the browser opens Link with.</summary>
public interface IPlaidLink
{
    /// <param name="clientUserId">Plaid's id for whoever connects (OpenBook sends the company's id: one Plaid user per company file).</param>
    /// <param name="accessToken">
    /// A connected bank login's access token, for Link's update mode: the person signs in to that bank again, and the
    /// login, its accounts and the access token stay as they were. Null connects a new bank.
    /// </param>
    Task<string> CreateLinkTokenAsync(string clientUserId, string? accessToken, CancellationToken ct);
}

/// <summary>
/// Plaid's API (plaid.com/docs/api), JSON over HTTPS with the client ID and secret in each body. Link gives the browser a
/// one-time <c>public_token</c>; <c>/item/public_token/exchange</c> swaps it for the bank login's long-lived
/// <c>access_token</c>. <c>/accounts/get</c> lists accounts and balances; <c>/transactions/get</c> pages through a date
/// range. Plaid's amounts are positive for money leaving the account, so they're turned round here; a credit or loan
/// balance is what's owed, so it's turned round too (OpenBook's remote balances are from the holder's view).
/// </summary>
public static class Plaid
{
    /// <summary>Transactions per <c>/transactions/get</c> page (Plaid's largest).</summary>
    public const int PageSize = 500;

    /// <summary>History Link asks the bank for (Plaid's longest): an account's start date can go back this far.</summary>
    public const int DaysRequested = 730;

    /// <summary>The refusal code for a bank that wants the person to sign in to it again.</summary>
    public const string LoginRequired = "plaid.login_required";

    public static Uri BaseUrl(PlaidEnvironment environment) => environment switch
    {
        PlaidEnvironment.Production => new Uri("https://production.plaid.com/"),
        _ => new Uri("https://sandbox.plaid.com/"),
    };

    public static string LinkToken(string json) => Read(json, root =>
        String(root, "link_token") ?? throw Unreadable("no link token"));

    public static string AccessToken(string json) => Read(json, root =>
        String(root, "access_token") ?? throw Unreadable("no access token"));

    /// <summary>The accounts of an <c>/accounts/get</c> or <c>/transactions/get</c> answer.</summary>
    public static IReadOnlyList<RemoteAccount> Accounts(string json) => Read(json, AccountsOf);

    /// <summary>
    /// One <c>/transactions/get</c> page: its accounts, the transactions it could read by account id, how many it held
    /// (read or not: the next page starts after them all), and how many the range holds in all.
    /// </summary>
    public static (IReadOnlyList<RemoteAccount> Accounts, IReadOnlyList<(string AccountId, RemoteTransaction Transaction)> Transactions, int Received, int Total) TransactionsPage(string json) =>
        Read(json, root =>
        {
            var lines = new List<(string, RemoteTransaction)>();
            var received = 0;
            if (root.TryGetProperty("transactions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in list.EnumerateArray())
                {
                    received++;
                    if (t.ValueKind != JsonValueKind.Object)
                        continue;
                    var id = String(t, "transaction_id");
                    var account = String(t, "account_id");
                    var amount = Amount(t, "amount");
                    var date = Date(t, "date");
                    if (id is null || account is null || amount is null || date is null)
                        continue;
                    var pending = t.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True;
                    // The bank's own text is "name"; "merchant_name" is Plaid's cleaned-up merchant, when it knows one.
                    lines.Add((account, new RemoteTransaction(id, date.Value, -amount.Value, String(t, "merchant_name"), String(t, "name"), null, pending)));
                }
            }

            var total = root.TryGetProperty("total_transactions", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) && count >= 0
                ? count
                : received;
            return ((IReadOnlyList<RemoteAccount>)AccountsOf(root), (IReadOnlyList<(string, RemoteTransaction)>)lines, received, total);
        });

    /// <summary>What Plaid's error answer means for the person; <paramref name="json"/> may be anything (or nothing).</summary>
    public static DomainException Error(string? json, int status)
    {
        string? code = null, type = null, message = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                code = String(document.RootElement, "error_code");
                type = String(document.RootElement, "error_type");
                message = String(document.RootElement, "display_message") ?? String(document.RootElement, "error_message");
            }
        }
        catch (JsonException)
        {
        }

        return code switch
        {
            "ITEM_LOGIN_REQUIRED" or "PENDING_EXPIRATION" or "ITEM_LOCKED" or "INVALID_CREDENTIALS" =>
                new DomainException(LoginRequired, "The bank wants you to sign in to it again. On this connection's page, select Sign in to the bank again."),
            "PRODUCT_NOT_READY" =>
                new DomainException("plaid.not_ready", "Plaid is still gathering this bank's transactions. Try Sync now again in a few minutes."),
            "INVALID_API_KEYS" or "INVALID_CLIENT_ID" or "INVALID_SECRET" =>
                new DomainException("plaid.keys_invalid", "Plaid refused the keys. Check the client ID, the secret and the environment under Settings, Plaid."),
            "INVALID_ACCESS_TOKEN" or "ITEM_NOT_FOUND" =>
                new DomainException("plaid.access_invalid", "Plaid no longer knows this bank connection. Disconnect it and connect the bank again."),
            "INVALID_PUBLIC_TOKEN" =>
                new DomainException("plaid.public_token_invalid", "Plaid didn't accept that bank sign-in; it may have expired. Choose your bank again."),
            "RATE_LIMIT_EXCEEDED" or "ADDITION_LIMIT" or "PRODUCTS_NOT_SUPPORTED" or "INSTITUTION_DOWN" or "INSTITUTION_NOT_RESPONDING" =>
                new DomainException("plaid.unavailable", Clip(message) ?? "Plaid or the bank can't answer just now. Try again later."),
            _ => new DomainException("plaid.failed", Clip(message) is { } m
                ? $"Plaid answered: {m}"
                : $"Plaid answered HTTP {status}{(code is null ? "" : $" ({code}{(type is null ? "" : $", {type}")})")}."),
        };
    }

    private static List<RemoteAccount> AccountsOf(JsonElement root)
    {
        var accounts = new List<RemoteAccount>();
        if (!root.TryGetProperty("accounts", out var list) || list.ValueKind != JsonValueKind.Array)
            return accounts;
        foreach (var a in list.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || String(a, "account_id") is not { } id)
                continue;
            var name = String(a, "name") ?? String(a, "official_name") ?? id;
            if (String(a, "mask") is { } mask)
                name = $"{name} ••{mask}";
            decimal? balance = null;
            string? currency = null;
            if (a.TryGetProperty("balances", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                balance = Amount(b, "current") ?? Amount(b, "available");
                currency = String(b, "iso_currency_code") ?? String(b, "unofficial_currency_code");
            }

            // A card's or loan's balance is what's owed: negative from the holder's view.
            if (String(a, "type") is "credit" or "loan" && balance is { } owed)
                balance = -owed;
            accounts.Add(new RemoteAccount(id, name, null, currency, balance, null));
        }

        return accounts;
    }

    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException)
        {
            throw Unreadable("not JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Unreadable("not an object");
            return read(document.RootElement);
        }
    }

    private static DomainException Unreadable(string what) =>
        new("plaid.response_invalid", $"Plaid sent an answer OpenBook couldn't read ({what}).");

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.Text()?.Trim() is { Length: > 0 } s ? s : null;

    private static decimal? Amount(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var n))
            return null;
        var rounded = Math.Round(n, 2, MidpointRounding.AwayFromZero);
        return Math.Abs(rounded) > Money.MaxAmount ? null : rounded;
    }

    private static DateOnly? Date(JsonElement e, string name) =>
        String(e, name) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string? Clip(string? text) => text is null ? null : text.Length > 300 ? text[..300] : text;
}
