using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Tests;

public sealed class InventoryCostingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Landed_costs_split_by_value_or_quantity_with_the_last_line_taking_the_rounding()
    {
        var received = new List<(decimal, decimal)> { (1m, 10m), (1m, 10m), (1m, 10m) };
        Assert.Equal([33.33m, 33.33m, 33.34m], LandedCost.Allocate(100m, received, LandedCostMethod.ByValue));
        Assert.Equal([75m, 25m], LandedCost.Allocate(100m, [(3m, 0m), (1m, 50m)], LandedCostMethod.ByQuantity));
        Assert.Equal("landed_cost.no_basis", Assert.Throws<DomainException>(() => LandedCost.Allocate(10m, [(3m, 0m)], LandedCostMethod.ByValue)).Code);
    }

    [Fact]
    public void Average_cost_takes_the_average_on_hand_on_the_date()
    {
        var item = Item.Create(new ItemDetails("Bolt", ItemType.Inventory, Guid.NewGuid(), InventoryAssetAccountId: Guid.NewGuid(),
            CostAccountId: Guid.NewGuid(), CostMethod: InventoryCostMethod.Average));
        var source = Guid.NewGuid();
        var movements = new List<InventoryMovement>
        {
            InventoryMovement.Receipt(item.Id, new DateOnly(2026, 1, 1), 1m, 1m, "bill", source, Now),
            InventoryMovement.Receipt(item.Id, new DateOnly(2026, 1, 2), 2m, 3m, "bill", source, Now),
            InventoryMovement.Receipt(item.Id, new DateOnly(2026, 2, 1), 10m, 100m, "bill", source, Now),
        };

        // On Jan 3: 3 on hand worth 4.00; two cost 2.67, taken from both January layers.
        var takes = Costing.Take(item, movements, 2m, new DateOnly(2026, 1, 3));
        Assert.Equal(2.67m, takes.Sum(t => t.Cost));
        Assert.Equal(2, takes.Count);
        Assert.Equal(1.33m, Costing.AverageCost(movements, new DateOnly(2026, 1, 3))!.Value, 2);
    }

    [Fact]
    public void Components_belong_to_bundles_and_assemblies_once_each_with_positive_quantities()
    {
        var bundle = Item.Create(new ItemDetails("Set", ItemType.Bundle, Guid.NewGuid()));
        var part = Guid.NewGuid();
        Assert.Equal("item.component_duplicate", Assert.Throws<DomainException>(() => bundle.SetComponents([(part, 1m), (part, 2m)])).Code);
        Assert.Equal("item.component_quantity_invalid", Assert.Throws<DomainException>(() => bundle.SetComponents([(part, 0m)])).Code);
        Assert.Equal("item.component_self", Assert.Throws<DomainException>(() => bundle.SetComponents([(bundle.Id, 1m)])).Code);
        bundle.SetComponents([(part, 2.5m)]);
        Assert.Equal(2.5m, Assert.Single(bundle.Components).Quantity);
        Assert.False(bundle.IsInventory);

        // Changing to a type without components drops them.
        bundle.Update(new ItemDetails("Set", ItemType.Service, bundle.IncomeAccountId));
        Assert.Empty(bundle.Components);
        Assert.True(Item.IsTracked(ItemType.Assembly));
    }
}
