using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Channels;
using OpenBook.Domain.Channels;

namespace OpenBook.Api;

public sealed record PayoutResponse(
    Guid Id, Guid ChannelId, string ExternalId, DateOnly Date, DateOnly? PeriodStart, DateOnly? PeriodEnd, PayoutStatus Status, decimal Net, decimal? ReportedNet,
    Guid? JournalEntryId, IReadOnlyDictionary<ChannelLineType, decimal> Totals, IReadOnlyList<PayoutLineResponse>? Lines)
{
    public static PayoutResponse From(ChannelPayout p, bool lines = false) => new(
        p.Id, p.ChannelId, p.ExternalId, p.Date, p.PeriodStart, p.PeriodEnd, p.Status, p.Net, p.ReportedNet, p.JournalEntryId,
        p.Lines.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount)),
        lines ? p.Lines.Select(l => new PayoutLineResponse(l.Date, l.Type, l.Amount, l.Reference, l.Description, l.Region)).ToList() : null);
}

public sealed record PayoutLineResponse(DateOnly Date, ChannelLineType Type, decimal Amount, string? Reference, string? Description, string? Region);

public sealed record PayoutImportResponse(IReadOnlyList<string> New, IReadOnlyList<string> AlreadyImported, IReadOnlyList<string> Warnings, IReadOnlyList<PayoutResponse> Saved);

public sealed record VoidPayoutRequest(string? Reason);

/// <summary>A channel's settings; leave an account out to use (and create if needed) the usual one.</summary>
public sealed record ChannelRequest(
    string Name, ChannelKind Kind, Guid DepositAccountId, Guid? SalesAccountId = null, Guid? ShippingAccountId = null, Guid? RefundsAccountId = null,
    Guid? FeesAccountId = null, Guid? TaxAccountId = null, Guid? AdjustmentsAccountId = null, Guid? ClearingAccountId = null, bool? MarketplaceCollectsTax = null,
    Guid? ClassId = null, Guid? LocationId = null, Guid? WarehouseId = null)
{
    public ChannelDetails ToDetails() => new(
        Name, Kind, DepositAccountId, SalesAccountId ?? Guid.Empty, ShippingAccountId ?? Guid.Empty, RefundsAccountId ?? Guid.Empty, FeesAccountId ?? Guid.Empty,
        TaxAccountId ?? Guid.Empty, AdjustmentsAccountId ?? Guid.Empty, ClearingAccountId, MarketplaceCollectsTax, ClassId, LocationId, WarehouseId);
}

public sealed record ConnectChannelRequest(string Credential, string? ShopDomain = null);

/// <param name="ItemId">The item, or null for "not stocked".</param>
public sealed record SkuMapRequest(string Sku, Guid? ItemId);

/// <summary>A channel without its credential (only whether it's connected).</summary>
public sealed record ChannelResponse(
    Guid Id, string Name, ChannelKind Kind, Guid DepositAccountId, Guid SalesAccountId, Guid ShippingAccountId, Guid RefundsAccountId, Guid FeesAccountId,
    Guid TaxAccountId, Guid AdjustmentsAccountId, Guid? ClearingAccountId, bool IsActive, bool IsConnected, string? ShopDomain, DateTimeOffset? LastSyncAt,
    bool LastSyncFailed, string? LastError, DateOnly? SyncedThrough)
{
    public static ChannelResponse From(SalesChannel c) => new(
        c.Id, c.Name, c.Kind, c.DepositAccountId, c.SalesAccountId, c.ShippingAccountId, c.RefundsAccountId, c.FeesAccountId, c.TaxAccountId,
        c.AdjustmentsAccountId, c.ClearingAccountId, c.IsActive, c.IsConnected, c.ShopDomain, c.LastSyncAt, c.LastSyncFailed, c.LastError, c.SyncedThrough);
}

public static class ChannelsApi
{
    public static void MapChannelsApi(this RouteGroupBuilder api)
    {
        var channels = api.MapGroup("/channels");
        channels.MapGet("", async (ChannelService s, CancellationToken ct) => (await s.ListAsync(includeInactive: true, ct)).Select(ChannelResponse.From));
        channels.MapPost("", async (ChannelRequest r, ChannelService s, CancellationToken ct) =>
        {
            var channel = await s.CreateAsync(r.ToDetails(), ct);
            return Results.Created($"/api/channels/{channel.Id}", ChannelResponse.From(channel));
        });
        channels.MapGet("/{id:guid}/rules", async (Guid id, ChannelService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } c ? Results.Ok(c.Rules.Select(r => new ChannelRuleDraft(r.Text, r.Type, r.AccountId))) : Results.NotFound());
        channels.MapPut("/{id:guid}/rules", async (Guid id, IReadOnlyList<ChannelRuleDraft> rules, ChannelService s, CancellationToken ct) =>
            (await s.SetRulesAsync(id, rules, ct)).Rules.Select(r => new ChannelRuleDraft(r.Text, r.Type, r.AccountId)));
        channels.MapPut("/{id:guid}", async (Guid id, ChannelRequest r, ChannelService s, CancellationToken ct) => ChannelResponse.From(await s.UpdateAsync(id, r.ToDetails(), ct)));
        channels.MapGet("/{id:guid}/payouts", async (Guid id, ChannelService s, CancellationToken ct) =>
            (await s.PayoutsAsync(id, ct)).Select(p => PayoutResponse.From(p)));

        // The report file as the body; ?fileName= for the record, ?dryRun=true to see what's new without saving.
        channels.MapPost("/{id:guid}/import", async (Guid id, HttpRequest request, string? fileName, bool? dryRun, ChannelService s, CancellationToken ct) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, ct);
            var result = await s.ImportAsync(id, fileName ?? "report.csv", memory.ToArray(), save: dryRun != true, ct);
            return new PayoutImportResponse(
                result.New.Select(p => p.ExternalId).ToList(), result.AlreadyImported.Select(p => p.ExternalId).ToList(), result.Warnings,
                result.Saved.Select(p => PayoutResponse.From(p)).ToList());
        });

        channels.MapPut("/connections/{id:guid}", async (Guid id, ConnectChannelRequest r, ChannelSyncService s, CancellationToken ct) =>
        {
            await s.ConnectAsync(id, r.ShopDomain, r.Credential, ct);
            return Results.NoContent();
        });
        channels.MapDelete("/connections/{id:guid}", async (Guid id, ChannelSyncService s, CancellationToken ct) =>
        {
            await s.DisconnectAsync(id, ct);
            return Results.NoContent();
        });
        channels.MapPost("/connections/{id:guid}/sync", async (Guid id, ChannelSyncService s, CancellationToken ct) => await s.SyncAsync(id, ct));

        // An orders report as the body.
        channels.MapPost("/{id:guid}/orders", async (Guid id, HttpRequest request, ChannelOrderService s, CancellationToken ct) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, ct);
            var result = await s.ImportAsync(id, memory.ToArray(), ct);
            return new { result.Orders, result.AlreadyImported, Batches = result.Saved.Select(b => b.Id), result.Warnings };
        });
        // A returns report as the body; ?dryRun=true shows what each row would do.
        channels.MapPost("/{id:guid}/returns/import", async (Guid id, bool? dryRun, HttpRequest request, ChannelOrderService s, CancellationToken ct) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, ct);
            return await s.ImportReturnsAsync(id, memory.ToArray(), dryRun ?? false, ct);
        });
        channels.MapGet("/{id:guid}/orders", async (Guid id, ChannelOrderService s, CancellationToken ct) =>
            (await s.BatchesAsync(id, ct)).Select(b => new { b.Id, b.Date, b.Status, b.Orders, b.JournalEntryId }));
        channels.MapGet("/{id:guid}/skus", async (Guid id, ChannelOrderService s, CancellationToken ct) => await s.SkusAsync(id, ct));
        channels.MapPut("/{id:guid}/skus", async (Guid id, SkuMapRequest r, ChannelOrderService s, CancellationToken ct) =>
        {
            await s.MapSkuAsync(id, r.Sku, r.ItemId, ct);
            return Results.NoContent();
        });
        channels.MapPost("/orders/{id:guid}/post", async (Guid id, ChannelOrderService s, CancellationToken ct) => (await s.PostAsync(id, ct)).Status);
        channels.MapPost("/orders/{id:guid}/void", async (Guid id, VoidPayoutRequest r, ChannelOrderService s, CancellationToken ct) =>
            (await s.VoidAsync(id, r.Reason ?? "Orders voided", ct)).Status);
        // A batch with its lines (the line numbers returns refer to).
        channels.MapGet("/orders/{id:guid}", async (Guid id, ChannelOrderService s, CancellationToken ct) =>
            await s.GetBatchAsync(id, ct) is { } b
                ? Results.Ok(new
                {
                    b.Id, b.ChannelId, b.Date, b.Status, b.Orders, b.JournalEntryId,
                    Lines = b.Lines.Select(l => new { Line = l.LineNumber, l.OrderId, l.Date, l.Sku, l.Title, l.Quantity, l.Sales, l.Shipping, l.Tax, l.Region }),
                })
                : Results.NotFound());
        channels.MapPost("/orders/{id:guid}/dismiss", async (Guid id, ChannelOrderService s, CancellationToken ct) => (await s.SetDismissedAsync(id, true, ct)).Status);
        channels.MapPost("/orders/{id:guid}/restore", async (Guid id, ChannelOrderService s, CancellationToken ct) => (await s.SetDismissedAsync(id, false, ct)).Status);
        channels.MapPost("/orders/{id:guid}/returns", async (Guid id, ChannelReturnRequest r, ChannelOrderService s, CancellationToken ct) =>
        {
            var made = await s.ReturnAsync(id, r.Date ?? OpenBook.Application.CompanyTime.Today, [.. (r.Lines ?? []).Select(l => (l.Line, l.Quantity))], r.Memo,
                (r.Lines ?? []).Where(l => l.NotRestocked).Select(l => l.Line).ToHashSet(), ct);
            return Results.Created($"/api/channels/returns/{made.Id}", ReturnResponse(made));
        });
        channels.MapGet("/returns/{id:guid}", async (Guid id, ChannelOrderService s, CancellationToken ct) =>
            await s.GetReturnAsync(id, ct) is { } r ? Results.Ok(ReturnResponse(r)) : Results.NotFound());
        channels.MapPost("/returns/{id:guid}/void", async (Guid id, VoidPayoutRequest r, ChannelOrderService s, CancellationToken ct) =>
            ReturnResponse(await s.VoidReturnAsync(id, r.Reason ?? "Return voided", ct)));

        channels.MapGet("/payouts/{id:guid}", async (Guid id, ChannelService s, CancellationToken ct) =>
            await s.GetPayoutAsync(id, ct) is { } p ? Results.Ok(PayoutResponse.From(p, lines: true)) : Results.NotFound());
        channels.MapPost("/payouts/{id:guid}/post", async (Guid id, ChannelService s, CancellationToken ct) => PayoutResponse.From(await s.PostAsync(id, ct)));
        channels.MapPost("/payouts/{id:guid}/void", async (Guid id, VoidPayoutRequest r, ChannelService s, CancellationToken ct) =>
            PayoutResponse.From(await s.VoidAsync(id, r.Reason ?? "Payout voided", ct)));
        channels.MapPost("/payouts/{id:guid}/dismiss", async (Guid id, ChannelService s, CancellationToken ct) => PayoutResponse.From(await s.SetDismissedAsync(id, true, ct)));
        channels.MapPost("/payouts/{id:guid}/restore", async (Guid id, ChannelService s, CancellationToken ct) => PayoutResponse.From(await s.SetDismissedAsync(id, false, ct)));
    }

    private static object ReturnResponse(OpenBook.Domain.Channels.ChannelReturn r) => new
    {
        r.Id, r.ChannelId, r.BatchId, r.Date, r.Memo, r.Status, r.JournalEntryId,
        Lines = r.Lines.Select(l => new { l.OrderLine, l.Quantity }),
    };
}

/// <summary>What came back, by the batch's line numbers (<c>GET /api/channels/orders/{id}</c>); date defaults to today.</summary>
public sealed record ChannelReturnRequest(DateOnly? Date, IReadOnlyList<ChannelReturnLineRequest>? Lines, string? Memo = null);

public sealed record ChannelReturnLineRequest(int Line, decimal Quantity, bool NotRestocked = false);
