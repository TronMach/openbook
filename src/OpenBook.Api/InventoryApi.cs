using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Inventory;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;

namespace OpenBook.Api;

public sealed record StockLevelResponse(Guid ItemId, string Name, decimal Quantity, decimal Value, decimal? AverageCost, decimal? ReorderPoint, bool NeedsReorder)
{
    public static StockLevelResponse From(StockLevel s) =>
        new(s.Item.Id, s.Item.Name, s.Quantity, s.Value, s.AverageCost, s.Item.ReorderPoint, s.NeedsReorder);
}

public sealed record InventoryAdjustmentResponse(
    Guid Id, DateOnly Date, string? Reference, Guid AdjustmentAccountId, string? Memo, DocumentStatus Status, Guid JournalEntryId,
    decimal ValueChange, IReadOnlyList<InventoryAdjustmentLine> Lines)
{
    public static InventoryAdjustmentResponse From(InventoryAdjustment a) =>
        new(a.Id, a.Date, a.Reference, a.AdjustmentAccountId, a.Memo, a.Status, a.JournalEntryId, a.ValueChange, a.Lines);
}

public sealed record VoidInventoryAdjustmentRequest(string Reason);

internal static class InventoryApi
{
    public static void MapInventoryApi(this RouteGroupBuilder api)
    {
        var inventory = api.MapGroup("/inventory");

        inventory.MapGet("/stock", async (DateOnly? asOf, InventoryService s, CancellationToken ct) =>
            (await s.StockAsync(asOf, ct)).Select(StockLevelResponse.From));

        inventory.MapGet("/items/{id:guid}/movements", async (Guid id, InventoryService s, CancellationToken ct) => await s.MovementsAsync(id, ct));

        inventory.MapGet("/items/{id:guid}/layers", async (Guid id, DateOnly? asOf, InventoryService s, CancellationToken ct) => await s.LayersAsync(id, asOf, ct));

        inventory.MapGet("/adjustments", async (InventoryAdjustmentService s, CancellationToken ct) =>
            (await s.ListAsync(ct)).Select(InventoryAdjustmentResponse.From));

        inventory.MapGet("/adjustments/{id:guid}", async (Guid id, InventoryAdjustmentService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } a ? Results.Ok(InventoryAdjustmentResponse.From(a)) : Results.NotFound());

        inventory.MapPost("/adjustments", async (CreateInventoryAdjustmentCommand c, InventoryAdjustmentService s, CancellationToken ct) =>
        {
            var adjustment = await s.CreateAsync(c, ct);
            return Results.Created($"/api/inventory/adjustments/{adjustment.Id}", InventoryAdjustmentResponse.From(adjustment));
        });

        inventory.MapPost("/adjustments/{id:guid}/void", async (Guid id, VoidInventoryAdjustmentRequest r, InventoryAdjustmentService s, CancellationToken ct) =>
            InventoryAdjustmentResponse.From(await s.VoidAsync(id, r.Reason, ct)));
    }
}
