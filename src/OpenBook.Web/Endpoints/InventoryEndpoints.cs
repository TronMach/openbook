using OpenBook.Web.Security;
using OpenBook.Application.Ledger;
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

        app.MapPost("/items/{id:guid}/lots/expiry", async (Guid id, IFormCollection form, InventoryService inventory, CancellationToken ct) =>
        {
            var date = DateOnly.TryParseExact(form["expiresOn"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
            try
            {
                await inventory.SetLotExpiryAsync(id, form["lot"].ToString(), date, ct);
                return Results.Redirect($"/items/{id}?lots=saved#lots");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/items/{id}?lotError={Uri.EscapeDataString(ex.Message)}#lots");
            }
        }).RequireAuthorization();

        // Fields: copies:{itemId} (how many labels of each) and showPrice. The PDF opens in a new tab.
        app.MapPost("/inventory/labels", async (IFormCollection form, HttpContext http, LabelService labels, CancellationToken ct) =>
        {
            var requests = new List<(Guid, int)>();
            foreach (var (field, values) in form)
            {
                if (field.StartsWith("copies:", StringComparison.Ordinal) && Guid.TryParse(field[7..], out var itemId)
                    && int.TryParse(values.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var copies) && copies > 0)
                    requests.Add((itemId, copies));
            }

            try
            {
                var pdf = await labels.RenderAsync(requests, form["showPrice"] == "true", ct);
                http.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileNameStar = "Labels.pdf" }.ToString();
                return Results.File(pdf, "application/pdf");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/inventory/labels?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Fields: count:{itemId} (blank = not counted), date, warehouseId, accountId, reference.
        app.MapPost("/inventory/count", async (IFormCollection form, InventoryAdjustmentService adjustments, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } password ? password : null;
            var warehouse = Guid.TryParse(form["warehouseId"], out var w) ? w : (Guid?)null;
            var back = "/inventory/count" + (warehouse is { } wid ? $"?warehouseId={wid}&" : "?");
            try
            {
                var counts = new List<CountedItem>();
                foreach (var (field, values) in form)
                {
                    var text = values.ToString().Trim();
                    if (!field.StartsWith("count:", StringComparison.Ordinal) || text.Length == 0 || !Guid.TryParse(field[6..], out var itemId))
                        continue;
                    counts.Add(decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var counted)
                        ? new CountedItem(itemId, counted)
                        : throw new DomainException("count.quantity_invalid", $"'{text}' isn't a number."));
                }

                var date = DateOnly.TryParseExact(form["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Application.CompanyTime.Today;
                var reference = form["reference"].ToString().Trim();
                var made = await adjustments.CountAsync(new InventoryCountCommand(date, counts, warehouse,
                    Guid.TryParse(form["accountId"], out var account) ? account : null, reference.Length == 0 ? null : reference), ct);
                return Results.Redirect($"{back}done={(made is null ? "matched" : made.Id.ToString())}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}error={Uri.EscapeDataString(ex.Message)}{(ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "")}");
            }
        }).RequireAuthorization();

        app.MapPost("/inventory/pending-builds/{id:guid}/cancel", async (Guid id, IFormCollection form, AssemblyBuildService builds, CancellationToken ct) =>
        {
            try
            {
                await builds.CancelPendingAsync(id, ct);
                return Results.Redirect("/inventory?pending=cancelled#pending");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/inventory?pendingError={Uri.EscapeDataString(ex.Message)}#pending");
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
