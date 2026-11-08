using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;

namespace OpenBook.Domain.Sales;

/// <summary>
/// Whether sales may take more of a tracked item than is on hand (the count goes below zero until stock comes in):
/// as the company's setting says, always, or never. Items with lot or serial numbers never do.
/// </summary>
public enum ShortStock
{
    Company,
    Allow,
    Never,
}

/// <summary>What you sell.</summary>
public enum ItemType
{
    Service,
    NonInventory,

    /// <summary>Stock you buy and resell: quantity on hand and FIFO cost are tracked (see Domain.Inventory).</summary>
    Inventory,

    /// <summary>A kit: several items sold together as one line at one price; its inventory components leave stock.</summary>
    Bundle,

    /// <summary>Stock you make from inventory components (a bill of materials) with assembly builds; tracked like inventory.</summary>
    Assembly,
}

/// <summary>How a tracked item's cost of goods sold is worked out.</summary>
public enum InventoryCostMethod
{
    /// <summary>First in, first out: sales use the oldest layers' costs.</summary>
    Fifo,

    /// <summary>Weighted average: sales use the average cost of everything on hand on the sale date.</summary>
    Average,
}

/// <summary>One line of a bundle or an assembly's bill of materials: how much of another item goes into one unit.</summary>
public sealed record ItemComponent(Guid ComponentItemId, decimal Quantity);

/// <summary>A product or service with a default description, price and the income account its sales post to.</summary>
public sealed class Item
{
    public const int MaxComponents = 50;
    private readonly List<QuantityBreak> _quantityBreaks = [];
    private readonly List<ItemComponent> _components = [];

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

    /// <summary>Tracked items (inventory products and assemblies): how sales are costed. Locked once stock has moved.</summary>
    public InventoryCostMethod CostMethod { get; private set; }

    /// <summary>Tracked items: whether units carry lot or serial numbers. Locked once stock has moved.</summary>
    public Inventory.ItemTracking Tracking { get; private set; }

    /// <summary>
    /// Assemblies without lot or serial tracking: a sale short of finished units builds the shortfall from components
    /// in the same entry, so pre-packed kits can be assembled to order.
    /// </summary>
    public bool AutoBuild { get; private set; }

    /// <summary>Tracked items: whether sales may take more than is on hand (<see cref="CanSellShort"/>).</summary>
    public ShortStock SellShort { get; private set; }

    /// <summary>
    /// Whether a sale (or a build) may take more of this item than is on hand: the extra units are costed at an estimate
    /// until stock comes in, which then fills the shortfall and corrects the cost. Never for lot or serial items.
    /// </summary>
    public bool CanSellShort(bool companyAllows) =>
        IsInventory && Tracking == Inventory.ItemTracking.None && (SellShort == ShortStock.Allow || SellShort == ShortStock.Company && companyAllows);

    /// <summary>What a scanner reads off the product (UPC, EAN, or your own code); unique among items.</summary>
    public string? Barcode { get; private set; }

    /// <summary>A product line or group ("Mugs", "Consulting") for reports by category; null for none.</summary>
    public string? Category { get; private set; }

    /// <summary>Who you usually buy it from: reordering groups items into one purchase order per preferred vendor.</summary>
    public Guid? PreferredVendorId { get; private set; }

    /// <summary>
    /// Inventory products: how many to order once stock (with what's on order) falls to the reorder point; null orders
    /// enough to bring it back above the reorder point.
    /// </summary>
    public decimal? ReorderQuantity { get; private set; }

    /// <summary>Days from ordering to having it in stock; with what it sells, reordering starts early enough not to run out.</summary>
    public int? LeadTimeDays { get; private set; }

    /// <summary>Whether stock on hand and its cost are tracked: inventory products and assemblies.</summary>
    public bool IsInventory => IsTracked(Type);

    public bool IsBundle => Type == ItemType.Bundle;
    public bool IsAssembly => Type == ItemType.Assembly;

    /// <summary>Bundles and assemblies: what goes into one unit. Empty for other items.</summary>
    public IReadOnlyList<ItemComponent> Components => _components;

    public static bool IsTracked(ItemType type) => type is ItemType.Inventory or ItemType.Assembly;

    public static bool HasComponents(ItemType type) => type is ItemType.Bundle or ItemType.Assembly;

    /// <summary>Replaces the components. The service checks what they are (no bundles in bundles, assemblies take tracked items, no cycles).</summary>
    public void SetComponents(IEnumerable<(Guid ItemId, decimal Quantity)> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var list = components.ToList();
        if (!HasComponents(Type))
        {
            if (list.Count > 0)
                throw new DomainException("item.components_not_allowed", "Only bundles and assemblies have components.");
            _components.Clear();
            return;
        }

        if (list.Count > MaxComponents)
            throw new DomainException("item.components_too_many", $"Use at most {MaxComponents} components.");
        foreach (var (itemId, quantity) in list)
        {
            if (itemId == Id)
                throw new DomainException("item.component_self", $"{Name} can't be one of its own components.");
            if (quantity <= 0m || decimal.Round(quantity, SalesLine.Precision) != quantity)
                throw new DomainException("item.component_quantity_invalid", "Component quantities must be more than zero, with up to 4 decimal places.");
        }

        if (list.Select(c => c.ItemId).Distinct().Count() != list.Count)
            throw new DomainException("item.component_duplicate", "List each component once.");
        _components.Clear();
        _components.AddRange(list.Select(c => new ItemComponent(c.ItemId, c.Quantity)));
    }

    /// <summary>Volume pricing on automatically priced lines, by the item's total quantity on the document; lowest first.</summary>
    public IReadOnlyList<QuantityBreak> QuantityBreaks => _quantityBreaks.OrderBy(b => b.FromQuantity).ToList();

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
        Barcode = NormalizeBarcode(details.Barcode);
        if (details.Category is not null)
            Category = Text.Optional(details.Category, 50, "item.category_too_long", "Category");
        Taxable = details.Taxable;
        PreferredVendorId = details.PreferredVendorId is { } vendor && vendor != Guid.Empty ? vendor : null;
        IncomeAccountId = details.IncomeAccountId == Guid.Empty
            ? throw new DomainException("item.income_account_required", "Choose the income account this item's sales are counted in.")
            : details.IncomeAccountId;
        PurchaseCost = details.PurchaseCost is >= 0m && decimal.Round(details.PurchaseCost, SalesLine.Precision) == details.PurchaseCost
            ? details.PurchaseCost
            : throw new DomainException("item.cost_invalid", "The purchase cost must be zero or more, with up to 4 decimal places.");

        if (!HasComponents(Type))
            _components.Clear();
        if (!IsInventory)
        {
            (InventoryAssetAccountId, CostAccountId, ReorderPoint, ReorderQuantity, LeadTimeDays) = (null, null, null, null, null);
            CostMethod = InventoryCostMethod.Fifo;
            Tracking = Inventory.ItemTracking.None;
            AutoBuild = false;
            SellShort = ShortStock.Company;
            return;
        }

        Tracking = Enum.IsDefined(details.Tracking) ? details.Tracking : throw new DomainException("item.tracking_invalid", "Unknown lot or serial tracking.");
        AutoBuild = details.AutoBuild && Type == ItemType.Assembly
            ? Tracking == Inventory.ItemTracking.None
                ? true
                : throw new DomainException("item.auto_build_tracked", "Automatic builds need an assembly without lot or serial numbers: there'd be no numbers to give the units built.")
            : false;

        CostMethod = Enum.IsDefined(details.CostMethod) ? details.CostMethod : throw new DomainException("item.cost_method_invalid", "Unknown costing method.");
        SellShort = !Enum.IsDefined(details.SellShort)
            ? throw new DomainException("item.short_stock_invalid", "Unknown setting for selling more than is on hand.")
            : details.SellShort == ShortStock.Allow && Tracking != Inventory.ItemTracking.None
                ? throw new DomainException("item.short_stock_tracked", "Items with lot or serial numbers can't be sold beyond what's on hand: every unit sold needs its number.")
                : details.SellShort;

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
        ReorderQuantity = details.ReorderQuantity switch
        {
            null => null,
            <= 0m => throw new DomainException("item.reorder_quantity_invalid", "The quantity to reorder must be more than zero (leave it blank to order back up to the reorder point)."),
            { } q when decimal.Round(q, SalesLine.Precision) != q => throw new DomainException("item.reorder_quantity_invalid", "The quantity to reorder can have up to 4 decimal places."),
            { } q => q,
        };
        LeadTimeDays = details.LeadTimeDays switch
        {
            null => null,
            < 0 or > 365 => throw new DomainException("item.lead_time_invalid", "The lead time is from 0 to 365 days."),
            { } d => d,
        };
    }

    /// <summary>Only the name (a merged duplicate's "(merged)"); other details go through Update.</summary>
    public void Rename(string name) => Name = NormalizeName(name);

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>
    /// Sales post to income accounts (income or other income). A service or non-stock item may instead post to an other
    /// current liability: money taken now for something given later, such as gift cards, which a negative line on a later
    /// sale redeems.
    /// </summary>
    public static void EnsureIncomeAccount(Account account, ItemType? type = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var liabilityAllowed = type is ItemType.Service or ItemType.NonInventory && account.Type == AccountType.OtherCurrentLiability;
        if (account.Type.ClassOf() != AccountClass.Income && !liabilityAllowed)
            throw new DomainException("item.income_account_type", type is ItemType.Service or ItemType.NonInventory
                ? $"'{account.Name}' isn't an income account (or an other current liability, for gift cards and the like)."
                : $"'{account.Name}' isn't an income account.");
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

    /// <summary>Trimmed; printable characters only, up to 50; null when blank.</summary>
    public static string? NormalizeBarcode(string? barcode)
    {
        var code = barcode?.Trim();
        if (string.IsNullOrEmpty(code))
            return null;
        if (code.Length > 50 || code.Any(c => c < ' ' || c > '~'))
            throw new DomainException("item.barcode_invalid", "A barcode is up to 50 plain characters (letters, digits and symbols).");
        return code;
    }

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 100, "item.name_too_long", "Item name")
        ?? throw new DomainException("item.name_required", "An item needs a name.");
}

/// <param name="InventoryAssetAccountId">Tracked items only, like <paramref name="CostAccountId"/>, <paramref name="ReorderPoint"/> and <paramref name="CostMethod"/>.</param>
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
    decimal PurchaseCost = 0m,
    InventoryCostMethod CostMethod = InventoryCostMethod.Fifo,
    Inventory.ItemTracking Tracking = Inventory.ItemTracking.None,
    string? Barcode = null,
    bool AutoBuild = false,
    ShortStock SellShort = ShortStock.Company,
    Guid? PreferredVendorId = null,
    decimal? ReorderQuantity = null,
    int? LeadTimeDays = null,
    string? Category = null);
