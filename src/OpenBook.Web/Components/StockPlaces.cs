using OpenBook.Application.Inventory;
using OpenBook.Domain.Inventory;

namespace OpenBook.Web.Components;

/// <summary>One choice in a "where" list: a warehouse, or a bin in one.</summary>
public sealed record PlaceChoice(string Value, string Label, StockPlace Place);

/// <summary>
/// Warehouses and bins as one list of choices for forms ("" is the default warehouse, "w:{id}" another warehouse,
/// "b:{id}" a bin), and whether forms need to ask at all (only once there's a second warehouse or a bin).
/// </summary>
public sealed class StockPlaces
{
    private StockPlaces(IReadOnlyList<Warehouse> warehouses, IReadOnlyList<PlaceChoice> choices)
    {
        Warehouses = warehouses;
        Choices = choices;
    }

    /// <summary>Active warehouses, default first.</summary>
    public IReadOnlyList<Warehouse> Warehouses { get; }

    /// <summary>Each active warehouse followed by its active bins.</summary>
    public IReadOnlyList<PlaceChoice> Choices { get; }

    /// <summary>Whether there's anything to choose: more than one warehouse, or any bin.</summary>
    public bool HasChoice => Choices.Count > 1;

    public bool HasWarehouses => Warehouses.Count > 1;

    public static async Task<StockPlaces> LoadAsync(WarehouseService service, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var warehouses = await service.ListAsync(ct: ct);
        var bins = await service.BinsAsync(ct: ct);
        var choices = new List<PlaceChoice>();
        foreach (var w in warehouses)
        {
            choices.Add(new PlaceChoice(Value(w), w.Name, new StockPlace(w.Key, null)));
            choices.AddRange(bins.Where(b => b.WarehouseId == w.Id).Select(b => new PlaceChoice($"b:{b.Id}", $"{w.Name} · {b.Name}", new StockPlace(w.Key, b.Id))));
        }

        return new StockPlaces(warehouses, choices);
    }

    /// <summary>The form value for a warehouse: "" for the default one.</summary>
    public static string Value(Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        return warehouse.IsDefault ? "" : $"w:{warehouse.Id}";
    }

    /// <summary>The place a form value stands for; unknown values are the default warehouse.</summary>
    public StockPlace Parse(string? value) => Choices.FirstOrDefault(c => c.Value == (value ?? "")) is { } choice ? choice.Place : StockPlace.Default;

    /// <summary>The warehouse id a document stores for a form value (null for the default one).</summary>
    public Guid? WarehouseId(string? value) => Parse(value).WarehouseId;
}
