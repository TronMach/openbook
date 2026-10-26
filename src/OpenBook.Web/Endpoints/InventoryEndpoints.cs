using System.Globalization;
using OpenBook.Application.Inventory;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>Grids and small forms on the item and warehouse pages (antiforgery-checked, form-bound).</summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: component{n} (item id) and quantity{n}; blank rows are skipped.
        app.MapPost("/items/{id:guid}/components", async (Guid id, IFormCollection form, ItemService items, CancellationToken ct) =>
        {
            var components = new List<(Guid, decimal)>();
            for (var n = 0; n < 60; n++)
            {
                var item = form[$"component{n}"].ToString().Trim();
                var quantity = form[$"quantity{n}"].ToString().Trim();
                if (item.Length == 0 && quantity.Length == 0)
                    continue;
                if (!Guid.TryParse(item, out var itemId)
                    || !decimal.TryParse(quantity.Length == 0 ? "1" : quantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                    return Back(id, "Choose an item and a quantity for each component.");
                components.Add((itemId, amount));
            }

            try
            {
                await items.SetComponentsAsync(id, components, ct);
                return Results.Redirect($"/items/{id}?components=saved#components");
            }
            catch (DomainException ex)
            {
                return Back(id, ex.Message);
            }
        }).RequireAuthorization();

        app.MapPost("/inventory/warehouses", async (IFormCollection form, WarehouseService warehouses, CancellationToken ct) =>
            await Warehouses(() => warehouses.CreateAsync(form["name"].ToString(), form["address"], ct))).RequireAuthorization();

        app.MapPost("/inventory/warehouses/{id:guid}", async (Guid id, IFormCollection form, WarehouseService warehouses, CancellationToken ct) =>
            await Warehouses(() => warehouses.UpdateAsync(id, form["name"].ToString(), form["address"], form["active"] == "true", ct))).RequireAuthorization();

        app.MapPost("/inventory/warehouses/{id:guid}/bins", async (Guid id, IFormCollection form, WarehouseService warehouses, CancellationToken ct) =>
            await Warehouses(() => warehouses.AddBinAsync(id, form["name"].ToString(), ct))).RequireAuthorization();

        app.MapPost("/inventory/bins/{id:guid}/active", async (Guid id, IFormCollection form, WarehouseService warehouses, CancellationToken ct) =>
            await Warehouses(() => warehouses.SetBinActiveAsync(id, form["active"] == "true", ct))).RequireAuthorization();

        return app;
    }

    private static IResult Back(Guid id, string error) => Results.Redirect($"/items/{id}?componentError={Uri.EscapeDataString(error)}#components");

    private static async Task<IResult> Warehouses<T>(Func<Task<T>> change)
    {
        try
        {
            await change();
            return Results.Redirect("/inventory/warehouses?done=saved");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/inventory/warehouses?error={Uri.EscapeDataString(ex.Message)}");
        }
    }
}
