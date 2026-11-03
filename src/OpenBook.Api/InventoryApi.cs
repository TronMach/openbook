using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Inventory;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Api;

public sealed record WarehouseRequest(string Name, string? Address = null, bool Active = true);

public sealed record BinRequest(string Name);

public sealed record LotExpiryRequest(DateOnly? ExpiresOn);

public sealed record FinishPendingBuildRequest(DateOnly Date, string? Lots = null);

public sealed record LabelRequestItem(Guid ItemId, int Copies);

public sealed record LabelsRequest(IReadOnlyList<LabelRequestItem> Items, bool ShowPrice = true);

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

        // A physical count: one adjustment for the differences, or 204 when everything matched.
        inventory.MapPost("/count", async (InventoryCountCommand c, InventoryAdjustmentService s, CancellationToken ct) =>
            await s.CountAsync(c, ct) is { } adjustment
                ? Results.Created($"/api/inventory/adjustments/{adjustment.Id}", InventoryAdjustmentResponse.From(adjustment))
                : Results.NoContent());
        inventory.MapPost("/adjustments/{id:guid}/void", async (Guid id, VoidInventoryAdjustmentRequest r, InventoryAdjustmentService s, CancellationToken ct) =>
            InventoryAdjustmentResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        inventory.MapPut("/items/{id:guid}/components", async (Guid id, IReadOnlyList<ItemComponent> components, ItemService s, CancellationToken ct) =>
            ItemResponse.From(await s.SetComponentsAsync(id, (components ?? []).Select(c => (c.ComponentItemId, c.Quantity)).ToList(), ct)));

        inventory.MapGet("/builds", async (AssemblyBuildService s, CancellationToken ct) => await s.ListAsync(ct));
        inventory.MapGet("/builds/preview", async (Guid itemId, decimal quantity, DateOnly date, Guid? warehouseId, AssemblyBuildService s, CancellationToken ct) =>
        {
            var p = await s.PreviewAsync(itemId, quantity, date, warehouseId, ct);
            return new { p.Quantity, p.MaxBuildable, Requirements = p.Requirements.Select(r => new { ItemId = r.Component.Id, r.Component.Name, r.PerUnit, r.Needed, r.OnHand, r.IsShort }) };
        });
        inventory.MapGet("/builds/{id:guid}", async (Guid id, AssemblyBuildService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());
        inventory.MapPost("/builds", async (BuildAssemblyCommand c, AssemblyBuildService s, CancellationToken ct) =>
        {
            var build = await s.CreateAsync(c, ct);
            return Results.Created($"/api/inventory/builds/{build.Id}", build);
        });
        inventory.MapGet("/builds/pending", async (AssemblyBuildService s, CancellationToken ct) =>
            (await s.ListPendingAsync(CompanyTime.Today, ct)).Select(p => new
            {
                p.Pending,
                AssemblyName = p.Assembly.Name,
                Ready = p.Preview is { } preview && !preview.Requirements.Any(r => r.IsShort),
            }));
        inventory.MapPost("/builds/pending", async (BuildAssemblyCommand c, AssemblyBuildService s, CancellationToken ct) => await s.SavePendingAsync(c, ct));
        inventory.MapPost("/builds/partial", async (BuildAssemblyCommand c, AssemblyBuildService s, CancellationToken ct) =>
        {
            var (build, pending) = await s.BuildWhatYouCanAsync(c, ct);
            return new { Build = build, Pending = pending };
        });
        inventory.MapPost("/builds/pending/{id:guid}/build", async (Guid id, FinishPendingBuildRequest r, AssemblyBuildService s, CancellationToken ct) =>
            await s.FinishPendingAsync(id, r.Date, r.Lots, ct));
        inventory.MapPost("/builds/pending/{id:guid}/cancel", async (Guid id, AssemblyBuildService s, CancellationToken ct) => await s.CancelPendingAsync(id, ct));
        inventory.MapPost("/builds/{id:guid}/void", async (Guid id, VoidInventoryAdjustmentRequest r, AssemblyBuildService s, CancellationToken ct) =>
            await s.VoidAsync(id, r.Reason, ct));

        inventory.MapGet("/warehouses", async (WarehouseService s, CancellationToken ct) => await s.ListAsync(includeInactive: true, ct: ct));
        inventory.MapPost("/warehouses", async (WarehouseRequest r, WarehouseService s, CancellationToken ct) =>
        {
            var warehouse = await s.CreateAsync(r.Name, r.Address, ct);
            return Results.Created($"/api/inventory/warehouses/{warehouse.Id}", warehouse);
        });
        inventory.MapPut("/warehouses/{id:guid}", async (Guid id, WarehouseRequest r, WarehouseService s, CancellationToken ct) =>
            await s.UpdateAsync(id, r.Name, r.Address, r.Active, ct));
        inventory.MapGet("/bins", async (Guid? warehouseId, WarehouseService s, CancellationToken ct) => await s.BinsAsync(warehouseId, includeInactive: true, ct));
        inventory.MapPost("/warehouses/{id:guid}/bins", async (Guid id, BinRequest r, WarehouseService s, CancellationToken ct) => await s.AddBinAsync(id, r.Name, ct));
        inventory.MapPut("/bins/{id:guid}/active", async (Guid id, SetActiveRequest r, WarehouseService s, CancellationToken ct) => await s.SetBinActiveAsync(id, r.Active, ct));

        inventory.MapGet("/stock/places", async (Guid? itemId, DateOnly? asOf, InventoryService s, CancellationToken ct) =>
            (await s.StockByPlaceAsync(itemId, asOf, ct)).Select(x => new { ItemId = x.Item.Id, x.Item.Name, x.Place.WarehouseId, x.Place.BinId, x.Lot, x.Quantity, x.Value, x.Received }));
        inventory.MapGet("/lots/{lot}", async (string lot, InventoryService s, CancellationToken ct) => await s.LotHistoryAsync(lot, ct));
        inventory.MapGet("/items/barcode/{code}", async (string code, ItemService s, CancellationToken ct) =>
            await s.FindByBarcodeAsync(code, ct) is { } item ? Results.Ok(ItemResponse.From(item)) : Results.NotFound());

        inventory.MapPut("/items/{id:guid}/lots/{lot}/expiry", async (Guid id, string lot, LotExpiryRequest r, InventoryService s, CancellationToken ct) =>
            await s.SetLotExpiryAsync(id, lot, r.ExpiresOn, ct));
        inventory.MapGet("/lots/expiring", async (int? days, InventoryService s, CancellationToken ct) =>
            (await s.ExpiringAsync(CompanyTime.Today.AddDays(days ?? 30), ct)).Select(l => new { ItemId = l.Item.Id, l.Item.Name, l.Lot, l.ExpiresOn, l.Quantity }));
        inventory.MapPost("/labels", async (LabelsRequest r, LabelService s, CancellationToken ct) =>
            Results.File(await s.RenderAsync((r.Items ?? []).Select(i => (i.ItemId, i.Copies)).ToList(), r.ShowPrice, ct), "application/pdf", "Labels.pdf"));

        inventory.MapGet("/transfers", async (StockTransferService s, CancellationToken ct) => await s.ListAsync(ct));
        inventory.MapGet("/transfers/{id:guid}", async (Guid id, StockTransferService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());
        inventory.MapPost("/transfers", async (CreateStockTransferCommand c, StockTransferService s, CancellationToken ct) =>
        {
            var transfer = await s.CreateAsync(c, ct);
            return Results.Created($"/api/inventory/transfers/{transfer.Id}", transfer);
        });
        inventory.MapPost("/transfers/{id:guid}/void", async (Guid id, VoidInventoryAdjustmentRequest r, StockTransferService s, CancellationToken ct) =>
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
