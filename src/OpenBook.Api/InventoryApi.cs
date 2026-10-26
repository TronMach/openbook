using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Inventory;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

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

        inventory.MapPut("/items/{id:guid}/components", async (Guid id, IReadOnlyList<ItemComponent> components, ItemService s, CancellationToken ct) =>
            ItemResponse.From(await s.SetComponentsAsync(id, (components ?? []).Select(c => (c.ComponentItemId, c.Quantity)).ToList(), ct)));

        inventory.MapGet("/builds", async (AssemblyBuildService s, CancellationToken ct) => await s.ListAsync(ct));
        inventory.MapGet("/builds/preview", async (Guid itemId, decimal quantity, DateOnly date, AssemblyBuildService s, CancellationToken ct) =>
        {
            var p = await s.PreviewAsync(itemId, quantity, date, ct);
            return new { p.Quantity, p.MaxBuildable, Requirements = p.Requirements.Select(r => new { ItemId = r.Component.Id, r.Component.Name, r.PerUnit, r.Needed, r.OnHand, r.IsShort }) };
        });
        inventory.MapGet("/builds/{id:guid}", async (Guid id, AssemblyBuildService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());
        inventory.MapPost("/builds", async (BuildAssemblyCommand c, AssemblyBuildService s, CancellationToken ct) =>
        {
            var build = await s.CreateAsync(c, ct);
            return Results.Created($"/api/inventory/builds/{build.Id}", build);
        });
        inventory.MapPost("/builds/{id:guid}/void", async (Guid id, VoidInventoryAdjustmentRequest r, AssemblyBuildService s, CancellationToken ct) =>
            await s.VoidAsync(id, r.Reason, ct));

        inventory.MapGet("/landed-costs", async (LandedCostService s, CancellationToken ct) => await s.ListAsync(ct));
        inventory.MapGet("/landed-costs/candidates", async (LandedCostService s, CancellationToken ct) =>
            (await s.CandidatesAsync(ct)).Select(c => new { BillId = c.Bill.Id, c.Bill.Date, c.Bill.Reference, c.VendorName, c.StockQuantity, c.StockValue }));
        inventory.MapGet("/landed-costs/{id:guid}", async (Guid id, LandedCostService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } l ? Results.Ok(l) : Results.NotFound());
        inventory.MapPost("/landed-costs", async (CreateLandedCostCommand c, LandedCostService s, CancellationToken ct) =>
        {
            var landed = await s.CreateAsync(c, ct);
            return Results.Created($"/api/inventory/landed-costs/{landed.Id}", landed);
        });
        inventory.MapPost("/landed-costs/{id:guid}/void", async (Guid id, VoidInventoryAdjustmentRequest r, LandedCostService s, CancellationToken ct) =>
            await s.VoidAsync(id, r.Reason, ct));
    }
}
