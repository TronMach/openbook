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
}

public sealed record ChannelSyncResult(bool Succeeded, int Imported, string? Error);

/// <summary>
/// Live Stripe and Shopify connections: the credential is stored protected; a sync fetches payouts paid since a little
/// before the last one fetched (else 60 days back) and saves new ones as Pending, exactly like an imported report.
/// Stripe charges for invoices paid online (already recorded as customer payments) become Cleared lines. Failures are
/// kept on the channel, not thrown.
/// </summary>
public sealed class ChannelSyncService(IChannelStore store, ChannelService channels, IChannelClient client, ISecretProtector secrets, ISalesStore sales, IClock clock)
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
            var result = await channels.AddAsync(channel, payouts, [], save: true, ct);
            channel.RecordSync(clock.UtcNow, payouts.Count > 0 ? payouts.Max(p => p.Date) : null, null);
            await store.SaveChangesAsync(ct);
            return new ChannelSyncResult(true, result.Saved.Count, null);
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
