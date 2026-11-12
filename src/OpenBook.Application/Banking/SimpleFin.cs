using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <summary>An account as a bank data service reports it. <see cref="Balance"/> is from the holder's view.</summary>
public sealed record RemoteAccount(string Id, string Name, string? Institution, string? Currency, decimal? Balance, DateOnly? BalanceDate);

/// <summary>A transaction as a bank data service reports it. Positive is money in.</summary>
public sealed record RemoteTransaction(string Id, DateOnly Date, decimal Amount, string? Payee, string? Description, string? Memo, bool Pending);

/// <param name="Messages">Warnings the service passed on, e.g. a bank that needs signing in to again.</param>
public sealed record RemoteSnapshot(
    IReadOnlyList<RemoteAccount> Accounts, IReadOnlyDictionary<string, IReadOnlyList<RemoteTransaction>> Transactions, IReadOnlyList<string> Messages);

/// <summary>Talks to one bank data service. Implemented in Infrastructure (HTTP).</summary>
public interface IBankConnectionClient
{
    BankConnectionProvider Provider { get; }

    /// <summary>Turns what the user pastes (a SimpleFIN setup token) into the long-lived access credential.</summary>
    Task<string> ConnectAsync(string setupToken, CancellationToken ct);

    /// <summary>Accounts and their posted transactions from <paramref name="from"/> through <paramref name="through"/>; balances only when <paramref name="from"/> is null.</summary>
    Task<RemoteSnapshot> FetchAsync(string access, DateOnly? from, DateOnly through, TimeZoneInfo zone, CancellationToken ct);
}

/// <summary>
/// The SimpleFIN protocol (simplefin.org/protocol.html): a setup token is the Base64 of a claim URL; POSTing to it
/// once returns the access URL (https://user:password@host/path). GET {access}/accounts returns accounts with
/// transactions; amounts are decimal strings (positive = money in) and dates are Unix timestamps.
/// </summary>
public static class SimpleFin
{
    /// <summary>The claim URL inside a setup token. Only https URLs are accepted.</summary>
    public static Uri ClaimUrl(string setupToken)
    {
        var token = (setupToken ?? string.Empty).Trim();
        if (token.Length == 0)
            throw new DomainException("simplefin.token_required", "Paste the setup token from SimpleFIN Bridge.");
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            throw new DomainException("simplefin.token_invalid", "That isn't a SimpleFIN setup token. Copy the whole token from SimpleFIN Bridge.");
        }

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : throw new DomainException("simplefin.token_invalid", "That isn't a SimpleFIN setup token. Copy the whole token from SimpleFIN Bridge.");
    }

    /// <summary>Splits an access URL into the request base (without credentials) and the Basic auth user and password.</summary>
    public static (Uri BaseUrl, string User, string Password) ParseAccess(string access)
    {
        if (!Uri.TryCreate((access ?? string.Empty).Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(url.UserInfo))
            throw new DomainException("simplefin.access_invalid", "SimpleFIN didn't return a usable access URL. Make a new setup token and connect again.");
        var parts = url.UserInfo.Split(':', 2);
        var builder = new UriBuilder(url) { UserName = string.Empty, Password = string.Empty };
        return (builder.Uri, Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    /// <summary>The accounts URL for a date range (end exclusive in the protocol, so one day is added), or balances only.</summary>
    public static Uri AccountsUrl(Uri baseUrl, DateOnly? from, DateOnly to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var path = baseUrl.AbsoluteUri.TrimEnd('/') + "/accounts";
        var query = from is { } start
            ? FormattableString.Invariant($"?start-date={Unix(start, zone)}&end-date={Unix(to.AddDays(1), zone)}")
            : "?balances-only=1";
        return new Uri(path + query);
    }

    /// <summary>Reads an accounts response.</summary>
    public static RemoteSnapshot Parse(string json, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DomainException("simplefin.response_invalid", "SimpleFIN sent something that isn't an accounts list.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DomainException("simplefin.response_invalid", "SimpleFIN sent something that isn't an accounts list.");

            var messages = new List<string>();
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                messages.AddRange(errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()).OfType<string>()
                    .Where(m => m.Length > 0));

            var accounts = new List<RemoteAccount>();
            var transactions = new Dictionary<string, IReadOnlyList<RemoteTransaction>>(StringComparer.Ordinal);
            if (root.TryGetProperty("accounts", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var id = String(a, "id");
                    if (id is null)
                        continue;
                    var institution = a.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object
                        ? String(org, "name") ?? String(org, "domain")
                        : null;
                    accounts.Add(new RemoteAccount(id, String(a, "name") ?? id, institution, String(a, "currency"), Amount(a, "balance"),
                        Date(a, "balance-date", zone)));

                    var lines = new List<RemoteTransaction>();
                    if (a.TryGetProperty("transactions", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in items.EnumerateArray())
                        {
                            var transactionId = String(t, "id");
                            var amount = Amount(t, "amount");
                            var pending = t.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True;
                            // Pending lines have no posted date yet ("posted": 0); they come back once posted.
                            var date = Date(t, "posted", zone) ?? (pending ? Date(t, "transacted_at", zone) : null);
                            if (transactionId is null || amount is null || date is null)
                                continue;
                            lines.Add(new RemoteTransaction(transactionId, date.Value, amount.Value, String(t, "payee"), String(t, "description"),
                                String(t, "memo"), pending));
                        }
                    }

                    transactions[id] = lines;
                }
            }

            return new RemoteSnapshot(accounts, transactions, messages);
        }
    }

    private static long Unix(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;

    private static decimal? Amount(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n))
            return Math.Round(n, 2, MidpointRounding.AwayFromZero);
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? Math.Round(d, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static DateOnly? Date(JsonElement e, string name, TimeZoneInfo zone)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        long seconds;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            seconds = n;
        else if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            seconds = s;
        else
            return null;
        // A time no date can hold (found by fuzzing: FromUnixTimeSeconds throws past year 9999, and a zone's offset can
        // carry the year 9999 past its end) is unreadable, like any other.
        return seconds <= 0 || seconds > LatestSeconds ? null : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), zone).DateTime);
    }

    private static readonly long LatestSeconds = new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
}
