using OpenBook.Domain.Common;

namespace OpenBook.Domain.Inventory;

/// <summary>
/// A place stock is kept. One warehouse is the default ("Main", created on first use): stock movements and documents
/// that name no warehouse are in it, so books from before warehouses existed need no change. Deactivated, never deleted.
/// </summary>
public sealed class Warehouse
{
    public const int MaxNameLength = 100;

    private Warehouse() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }

    /// <summary>Where stock without a warehouse is; there's exactly one. Documents and movements refer to it as null.</summary>
    public bool IsDefault { get; private set; }

    public string? Address { get; private set; }
    public bool IsActive { get; private set; }

    public static Warehouse Create(string name, string? address = null, bool isDefault = false)
    {
        var warehouse = new Warehouse { Id = Guid.CreateVersion7(), IsActive = true, IsDefault = isDefault };
        warehouse.Update(name, address);
        return warehouse;
    }

    public void Update(string name, string? address)
    {
        Name = Text.Optional(name, MaxNameLength, "warehouse.name_too_long", "Warehouse name")
            ?? throw new DomainException("warehouse.name_required", "Give the warehouse a name.");
        Address = Text.Optional(address, 500, "warehouse.address_too_long", "Address");
    }

    public void SetActive(bool active)
    {
        if (!active && IsDefault)
            throw new DomainException("warehouse.default_inactive", "The default warehouse can't be made inactive.");
        IsActive = active;
    }

    /// <summary>The id documents and movements use for this warehouse: null for the default one.</summary>
    public Guid? Key => IsDefault ? null : Id;
}

/// <summary>A shelf, aisle or zone inside a warehouse. Stock can be in a bin or just in the warehouse.</summary>
public sealed class Bin
{
    public const int MaxNameLength = 50;

    private Bin() => Name = string.Empty;

    public Guid Id { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Name { get; private set; }
    public bool IsActive { get; private set; }

    public static Bin Create(Guid warehouseId, string name)
    {
        var bin = new Bin { Id = Guid.CreateVersion7(), WarehouseId = warehouseId, IsActive = true };
        bin.Rename(name);
        return bin;
    }

    public void Rename(string name) =>
        Name = Text.Optional(name, MaxNameLength, "bin.name_too_long", "Bin name") ?? throw new DomainException("bin.name_required", "Give the bin a name.");

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>Where stock is: a warehouse (null = the default one) and optionally a bin in it.</summary>
public readonly record struct StockPlace(Guid? WarehouseId, Guid? BinId)
{
    public static readonly StockPlace Default = new(null, null);
}

/// <summary>Whether an item's units are told apart.</summary>
public enum ItemTracking
{
    None,

    /// <summary>Batches: each receipt names its lot; sales can name lots or take the oldest.</summary>
    Lot,

    /// <summary>Every unit has its own serial number; quantities are whole units.</summary>
    Serial,
}

/// <summary>
/// Lot and serial numbers as typed on a line: lots as "A-100 x 3; B-200 x 2" (a single lot without a quantity takes the
/// whole line), serials as "SN1, SN2, SN3" (commas, semicolons or new lines), one per unit.
/// </summary>
public static class LotText
{
    public const int MaxLotLength = 50;

    /// <summary>The lots and quantities on the line, checked against its quantity; empty when nothing was typed.</summary>
    public static IReadOnlyList<(string Lot, decimal Quantity)> Parse(string? text, ItemTracking tracking, decimal quantity, string itemName)
    {
        if (tracking == ItemTracking.None || string.IsNullOrWhiteSpace(text))
            return [];
        var parts = text.Split([';', '\n', '\r', tracking == ItemTracking.Serial ? ',' : ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<(string Lot, decimal Quantity)>();
        if (tracking == ItemTracking.Serial)
        {
            if (quantity != decimal.Truncate(quantity))
                throw new DomainException("lot.serial_whole", $"{itemName} is tracked by serial number, so its quantities are whole units.");
            foreach (var serial in parts)
                result.Add((Check(serial, itemName), 1m));
            if (result.Count != quantity)
                throw new DomainException("lot.serial_count", $"{itemName}: {quantity:0} unit(s) need {quantity:0} serial number(s); {result.Count} given.");
        }
        else
        {
            foreach (var part in parts)
            {
                var bits = part.Split([" x ", " X ", " × ", "*"], 2, StringSplitOptions.TrimEntries);
                if (bits.Length == 1)
                {
                    if (parts.Length > 1)
                        throw new DomainException("lot.quantity_required", $"{itemName}: give each lot a quantity, e.g. \"A-100 x 3; B-200 x 2\".");
                    result.Add((Check(bits[0], itemName), quantity));
                    continue;
                }

                if (!decimal.TryParse(bits[1], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var q)
                    || q <= 0m || decimal.Round(q, 4) != q)
                    throw new DomainException("lot.quantity_invalid", $"{itemName}: \"{bits[1]}\" isn't a quantity for lot {bits[0]}.");
                result.Add((Check(bits[0], itemName), q));
            }

            if (result.Sum(r => r.Quantity) != quantity)
                throw new DomainException("lot.quantity_mismatch", $"{itemName}: the lots add up to {result.Sum(r => r.Quantity):0.####}, not {quantity:0.####}.");
        }

        if (result.Select(r => r.Lot).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new DomainException("lot.duplicate", $"{itemName}: each {(tracking == ItemTracking.Serial ? "serial number" : "lot")} can appear only once.");
        return result;
    }

    /// <summary>The canonical text for lots used: "A-100 x 3; B-200 x 2", or serials "SN1, SN2".</summary>
    public static string? Format(IEnumerable<(string Lot, decimal Quantity)> lots, ItemTracking tracking)
    {
        ArgumentNullException.ThrowIfNull(lots);
        var merged = lots.GroupBy(l => l.Lot, StringComparer.OrdinalIgnoreCase).Select(g => (Lot: g.Key, Quantity: g.Sum(l => l.Quantity))).ToList();
        if (tracking == ItemTracking.None || merged.Count == 0)
            return null;
        var text = tracking == ItemTracking.Serial
            ? string.Join(", ", merged.Select(l => l.Lot))
            : string.Join("; ", merged.Select(l => FormattableString.Invariant($"{l.Lot} x {l.Quantity:0.####}")));
        return text.Length > 1000 ? text[..997] + "..." : text;
    }

    private static string Check(string lot, string itemName)
    {
        if (lot.Length == 0)
            throw new DomainException("lot.empty", $"{itemName}: a lot or serial number is empty.");
        if (lot.Length > MaxLotLength)
            throw new DomainException("lot.too_long", $"{itemName}: lot and serial numbers can be at most {MaxLotLength} characters.");
        return lot;
    }
}
