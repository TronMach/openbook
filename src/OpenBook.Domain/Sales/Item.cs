using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;

namespace OpenBook.Domain.Sales;

/// <summary>What you sell.</summary>
public enum ItemType
{
    Service,
    NonInventory,

    /// <summary>Stock you buy and resell: quantity on hand and FIFO cost are tracked (see Domain.Inventory).</summary>
    Inventory,
}

/// <summary>A product or service with a default description, price and the income account its sales post to.</summary>
public sealed class Item
{
    private readonly List<QuantityBreak> _quantityBreaks = [];

    private Item() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public ItemType Type { get; private set; }

    /// <summary>Default description on sales lines.</summary>
    public string? Description { get; private set; }

    /// <summary>Default unit price, up to 4 decimal places.</summary>
    public decimal Rate { get; private set; }

    public Guid IncomeAccountId { get; private set; }

    /// <summary>Whether sales tax applies by default; each sales line can override it.</summary>
    public bool Taxable { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Inventory items: the asset account that holds the stock's cost.</summary>
    public Guid? InventoryAssetAccountId { get; private set; }

    /// <summary>Inventory items: the cost of goods sold account charged with the FIFO cost of each sale.</summary>
    public Guid? CostAccountId { get; private set; }

    /// <summary>Inventory items: reorder when the quantity on hand falls to this or below.</summary>
    public decimal? ReorderPoint { get; private set; }

    /// <summary>Default unit cost for purchases and quantity adjustments, up to 4 decimal places.</summary>
    public decimal PurchaseCost { get; private set; }

    public bool IsInventory => Type == ItemType.Inventory;

    /// <summary>Volume pricing on automatically priced lines, by the item's total quantity on the document; lowest first.</summary>
    public IReadOnlyList<QuantityBreak> QuantityBreaks => _quantityBreaks;

    public void SetQuantityBreaks(IEnumerable<(decimal FromQuantity, QuantityBreakKind Kind, decimal Value)> breaks)
    {
        var validated = Prices.Breaks(breaks);
        _quantityBreaks.Clear();
        _quantityBreaks.AddRange(validated);
    }

    public static Item Create(ItemDetails details)
    {
        var item = new Item { Id = Guid.CreateVersion7(), IsActive = true };
        item.Update(details);
        return item;
    }

    public void Update(ItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = NormalizeName(details.Name);
        Type = Enum.IsDefined(details.Type) ? details.Type : throw new DomainException("item.type_invalid", $"Unknown item type {details.Type}.");
        Description = Text.Optional(details.Description, 1000, "item.description_too_long", "Description");
        Rate = SalesLine.ValidRate(details.Rate);
        Taxable = details.Taxable;
        IncomeAccountId = details.IncomeAccountId == Guid.Empty
            ? throw new DomainException("item.income_account_required", "Choose the income account sales of this item post to.")
            : details.IncomeAccountId;
        PurchaseCost = details.PurchaseCost is >= 0m && decimal.Round(details.PurchaseCost, SalesLine.Precision) == details.PurchaseCost
            ? details.PurchaseCost
            : throw new DomainException("item.cost_invalid", "The purchase cost must be zero or more, with up to 4 decimal places.");

        if (Type != ItemType.Inventory)
        {
            (InventoryAssetAccountId, CostAccountId, ReorderPoint) = (null, null, null);
            return;
        }

        InventoryAssetAccountId = details.InventoryAssetAccountId is { } asset && asset != Guid.Empty
            ? asset
            : throw new DomainException("item.asset_account_required", "Choose the inventory asset account for this item.");
        CostAccountId = details.CostAccountId is { } cost && cost != Guid.Empty
            ? cost
            : throw new DomainException("item.cost_account_required", "Choose the cost of goods sold account for this item.");
        ReorderPoint = details.ReorderPoint switch
        {
            null => null,
            < 0m => throw new DomainException("item.reorder_point_invalid", "The reorder point can't be negative."),
            { } r when decimal.Round(r, SalesLine.Precision) != r => throw new DomainException("item.reorder_point_invalid", "The reorder point can have up to 4 decimal places."),
            { } r => r,
        };
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>Sales post to income accounts (income or other income).</summary>
    public static void EnsureIncomeAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Type.ClassOf() != AccountClass.Income)
            throw new DomainException("item.income_account_type", $"'{account.Name}' isn't an income account.");
        if (!account.IsActive)
            throw new DomainException("item.income_account_inactive", $"'{account.Name}' is inactive.");
    }

    /// <summary>Inventory is held in an asset account (other current asset, usually "Inventory Asset").</summary>
    public static void EnsureInventoryAssetAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Type != AccountType.OtherCurrentAsset)
            throw new DomainException("item.asset_account_type", $"'{account.Name}' isn't an other current asset account.");
        if (!account.IsActive)
            throw new DomainException("item.asset_account_inactive", $"'{account.Name}' is inactive.");
    }

    /// <summary>The cost of sales goes to a cost of goods sold (or expense) account.</summary>
    public static void EnsureCostAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Type.ClassOf() != AccountClass.Expense)
            throw new DomainException("item.cost_account_type", $"'{account.Name}' isn't a cost of goods sold or expense account.");
        if (!account.IsActive)
            throw new DomainException("item.cost_account_inactive", $"'{account.Name}' is inactive.");
    }

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 100, "item.name_too_long", "Item name")
        ?? throw new DomainException("item.name_required", "An item needs a name.");
}

/// <param name="InventoryAssetAccountId">Inventory items only, like <paramref name="CostAccountId"/> and <paramref name="ReorderPoint"/>.</param>
public sealed record ItemDetails(
    string Name,
    ItemType Type,
    Guid IncomeAccountId,
    decimal Rate = 0m,
    string? Description = null,
    bool Taxable = true,
    Guid? InventoryAssetAccountId = null,
    Guid? CostAccountId = null,
    decimal? ReorderPoint = null,
    decimal PurchaseCost = 0m);
