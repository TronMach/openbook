using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Channels;

/// <summary>
/// Reads paid-out payouts from a channel's API, with their transactions. Lines carry the channel's id for the payment
/// (a Stripe PaymentIntent, a Shopify order) as their reference.
/// </summary>
public interface IChannelClient
{
    Task<IReadOnlyList<PayoutData>> FetchPayoutsAsync(ChannelKind kind, string? shopDomain, string credential, DateOnly since, CancellationToken ct);

    /// <summary>Shopify orders created since the date (not cancelled), as item lines with the order's id.</summary>
    Task<IReadOnlyList<ChannelOrderDraft>> FetchOrdersAsync(string shopDomain, string token, DateOnly since, CancellationToken ct);
}

public sealed record ChannelSyncResult(bool Succeeded, int Imported, string? Error, int Orders = 0);

/// <summary>
/// Live Stripe and Shopify connections: the credential is stored protected; a sync fetches payouts paid since a little
/// before the last one fetched (else 60 days back) and saves new ones as Pending, exactly like an imported report.
/// Stripe charges for invoices paid online (already recorded as customer payments) become Cleared lines. Failures are
/// kept on the channel, not thrown.
/// </summary>
public sealed class ChannelSyncService(
    IChannelStore store, ChannelService channels, ChannelOrderService orders, IChannelClient client, ISecretProtector secrets, ISalesStore sales, ICompanyStore companies, IClock clock)
{
    public static readonly TimeSpan SyncEvery = TimeSpan.FromHours(6);
    private const int OverlapDays = 7;
    private const int FirstSyncDays = 60;

    public async Task<SalesChannel> ConnectAsync(Guid channelId, string? shopDomain, string credential, CancellationToken ct = default)
    {
        var channel = await store.GetChannelAsync(channelId, ct) ?? throw new EntityNotFoundException(ChannelService.ChannelEntity, channelId);
        var key = credential?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 500)
            throw new DomainException("channel.credential_required", channel.Kind == ChannelKind.Shopify
                ? "Enter the Admin API access token from the store's custom app."
                : "Enter a Stripe secret or restricted key.");
        if (channel.Kind == ChannelKind.Stripe && !(await companies.GetRequiredCompanyAsync(ct)).HomeCurrency.HasCents)
            throw new DomainException("online.currency_unsupported", "A Stripe connection needs a home currency with two decimals: its amounts would be read wrongly.");
        var before = ChannelService.SnapshotOf(channel);
        channel.Connect(channel.Kind == ChannelKind.Shopify ? ShopDomain(shopDomain) : null, secrets.Protect(key));
        await channels.SaveConnectionAsync(channel, before, ct);
        return channel;
    }

    public async Task<SalesChannel> DisconnectAsync(Guid channelId, CancellationToken ct = default)
    {
        var channel = await store.GetChannelAsync(channelId, ct) ?? throw new EntityNotFoundException(ChannelService.ChannelEntity, channelId);
        var before = ChannelService.SnapshotOf(channel);
        channel.Disconnect();
        await channels.SaveConnectionAsync(channel, before, ct);
        return channel;
    }

    public async Task<ChannelSyncResult> SyncAsync(Guid channelId, CancellationToken ct = default)
    {
        var channel = await store.GetChannelAsync(channelId, ct) ?? throw new EntityNotFoundException(ChannelService.ChannelEntity, channelId);
        if (!channel.IsConnected || channel.ProtectedCredential is not { } credential)
            throw new DomainException("channel.not_connected", "Connect the channel first.");
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var since = channel.SyncedThrough?.AddDays(-OverlapDays) ?? today.AddDays(-FirstSyncDays);
        try
        {
            var key = secrets.Unprotect(credential)
                ?? throw new DomainException("channel.credential_unreadable", "The saved key can't be read on this computer. Connect the channel again.");
            var payouts = await client.FetchPayoutsAsync(channel.Kind, channel.ShopDomain, key, since, ct);
            if (channel.Kind == ChannelKind.Stripe)
                payouts = await ClearInvoicePaymentsAsync(payouts, ct);

            // Shopify: the orders behind the charges split them into sales, shipping and tax, and move stock.
            IReadOnlyList<ChannelOrderDraft> fetched = [];
            string? ordersError = null;
            if (channel.Kind == ChannelKind.Shopify)
            {
                try
                {
                    fetched = await client.FetchOrdersAsync(channel.ShopDomain!, key, since.AddDays(-30), ct);
                    payouts = SplitCharges(payouts, fetched);
                }
                catch (DomainException ex)
                {
                    ordersError = $"Payouts were fetched, but not orders (give the app the read_orders scope): {ex.Message}";
                }
            }

            var result = await channels.AddAsync(channel, payouts, [], save: true, ct);
            var newOrders = fetched.Count > 0 ? (await orders.AddAsync(channel, fetched, [], ct)).Orders : 0;
            channel.RecordSync(clock.UtcNow, payouts.Count > 0 ? payouts.Max(p => p.Date) : null, ordersError);
            await store.SaveChangesAsync(ct);
            return new ChannelSyncResult(ordersError is null, result.Saved.Count, ordersError, newOrders);
        }
        catch (Exception ex) when (ex is DomainException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            channel.RecordSync(clock.UtcNow, null, ex is DomainException ? ex.Message : $"Couldn't reach {channel.Kind}: {ex.Message}");
            await store.SaveChangesAsync(ct);
            return new ChannelSyncResult(false, 0, channel.LastError);
        }
    }

    /// <summary>Connected, active channels not synced for <see cref="SyncEvery"/> (the background job).</summary>
    public async Task<IReadOnlyList<(SalesChannel Channel, ChannelSyncResult Result)>> SyncDueAsync(CancellationToken ct = default)
    {
        var results = new List<(SalesChannel, ChannelSyncResult)>();
        foreach (var channel in await store.ListChannelsAsync(ct))
        {
            if (channel.IsActive && channel.IsConnected && (channel.LastSyncAt is null || clock.UtcNow - channel.LastSyncAt >= SyncEvery))
                results.Add((channel, await SyncAsync(channel.Id, ct)));
        }

        return results;
    }

    /// <summary>"shop", "shop.myshopify.com" or "https://shop.myshopify.com/admin" all become shop.myshopify.com.</summary>
    public static string ShopDomain(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("https://", StringComparison.Ordinal))
            value = value[8..];
        value = value.Split('/')[0];
        if (!value.Contains('.', StringComparison.Ordinal))
            value += ".myshopify.com";
        if (!value.EndsWith(".myshopify.com", StringComparison.Ordinal) || value.Length > 200
            || !value[..^".myshopify.com".Length].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new DomainException("channel.shop_invalid", "Enter the store's address ending in .myshopify.com (Settings › Domains in Shopify shows it).");
        return value;
    }

    /// <summary>
    /// Splits each Shopify charge or refund ("order {id}", or the order's number in a payout file) into sales, shipping and
    /// tax in the order's proportions (a partial one takes its share), with the order's ship-to state, so tax on a refund
    /// comes back out of the liability. Those without a known order stay whole.
    /// </summary>
    public static IReadOnlyList<PayoutData> SplitCharges(IReadOnlyList<PayoutData> payouts, IReadOnlyList<ChannelOrderDraft> orders)
    {
        ArgumentNullException.ThrowIfNull(payouts);
        ArgumentNullException.ThrowIfNull(orders);
        static string Key(string reference) => (reference.StartsWith("order ", StringComparison.Ordinal) ? reference[6..] : reference).Trim().TrimStart('#');
        var byOrder = orders.GroupBy(o => Key(o.OrderId)).ToDictionary(g => g.Key, g => (Sales: g.Sum(o => o.Sales), Shipping: g.Sum(o => o.Shipping),
            Tax: g.Sum(o => o.Tax), Region: g.Select(o => o.Region).FirstOrDefault(r => r is not null)));
        return payouts.Select(p => p with
        {
            Lines = p.Lines.SelectMany(l =>
            {
                if (l.Type is not (ChannelLineType.Sale or ChannelLineType.Refund) || l.Reference is not { } reference
                    || !byOrder.TryGetValue(Key(reference), out var order) || order.Sales + order.Shipping + order.Tax is var total && total <= 0m)
                    return [l];
                var share = l.Amount / total;
                var tax = decimal.Round(order.Tax * share, 2, MidpointRounding.AwayFromZero);
                var shipping = decimal.Round(order.Shipping * share, 2, MidpointRounding.AwayFromZero);
                var parts = new List<ChannelLineDraft> { l with { Amount = l.Amount - tax - shipping, Region = order.Region } };
                if (shipping != 0m)
                    parts.Add(l with { Type = ChannelLineType.Shipping, Amount = shipping, Region = order.Region });
                if (tax != 0m)
                    parts.Add(l with { Type = ChannelLineType.Tax, Amount = tax, Region = order.Region });
                return (IEnumerable<ChannelLineDraft>)parts;
            }).ToList(),
        }).ToList();
    }

    /// <summary>Charges for invoices paid through OpenBook's payment links are already in the books: mark them Cleared.</summary>
    private async Task<IReadOnlyList<PayoutData>> ClearInvoicePaymentsAsync(IReadOnlyList<PayoutData> payouts, CancellationToken ct)
    {
        var recorded = (await sales.ListPaymentsAsync(null, ct)).Select(p => p.Reference).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (recorded.Count == 0)
            return payouts;
        return payouts.Select(p => p with
        {
            Lines = p.Lines.Select(l => l.Type == ChannelLineType.Sale && l.Reference is { } r && recorded.Contains(r) ? l with { Type = ChannelLineType.Cleared } : l).ToList(),
        }).ToList();
    }
}
