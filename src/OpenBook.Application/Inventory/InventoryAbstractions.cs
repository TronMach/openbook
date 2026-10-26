using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Inventory;

namespace OpenBook.Application.Inventory;

/// <summary>Stock movements and adjustments. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface IInventoryStore
{
    /// <summary>Untracked; every item when <paramref name="itemId"/> is null. Includes movements added but not yet saved.</summary>
    Task<IReadOnlyList<InventoryMovement>> ListMovementsAsync(Guid? itemId, CancellationToken ct);

    Task<IReadOnlyList<InventoryMovement>> ListMovementsForSourceAsync(string sourceType, Guid sourceId, CancellationToken ct);

    /// <summary>Whether the item has ever moved stock.</summary>
    Task<bool> HasMovementsAsync(Guid itemId, CancellationToken ct);

    Task<IReadOnlyList<InventoryAdjustment>> ListAdjustmentsAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<InventoryAdjustment?> GetAdjustmentAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<AssemblyBuild>> ListBuildsAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<AssemblyBuild?> GetBuildAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<LandedCost>> ListLandedCostsAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<LandedCost?> GetLandedCostAsync(Guid id, CancellationToken ct);

    void Add(InventoryMovement movement);
    void Add(InventoryAdjustment adjustment);
    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<Warehouse>> ListWarehousesAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<Bin>> ListBinsAsync(CancellationToken ct);

    Task<IReadOnlyList<StockTransfer>> ListTransfersAsync(CancellationToken ct);

    /// <summary>Tracked, with lines.</summary>
    Task<StockTransfer?> GetTransferAsync(Guid id, CancellationToken ct);

    void Add(Warehouse warehouse);
    void Add(Bin bin);
    void Add(StockTransfer transfer);
    void Add(AssemblyBuild build);
    void Add(LandedCost landedCost);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>The default accounts inventory posts to, created (and saved) on first use like A/R and A/P.</summary>
public sealed class InventoryAccounts(AccountService accounts)
{
    public const string AssetName = "Inventory Asset";
    public const string CostName = "Cost of Goods Sold";
    public const string ShrinkageName = "Inventory Shrinkage";
    public const string LandedCostName = "Landed Cost Clearing";

    public Task<Account> GetOrCreateAssetAsync(CancellationToken ct = default) =>
        GetOrCreateAsync(AssetName, AccountType.OtherCurrentAsset, "The cost of inventory on hand.", ct);

    public Task<Account> GetOrCreateCostAsync(CancellationToken ct = default) =>
        GetOrCreateAsync(CostName, AccountType.CostOfGoodsSold, "The FIFO cost of inventory sold.", ct);

    public Task<Account> GetOrCreateShrinkageAsync(CancellationToken ct = default) =>
        GetOrCreateAsync(ShrinkageName, AccountType.CostOfGoodsSold, "Inventory lost, damaged or found in counts.", ct);

    public Task<Account> GetOrCreateLandedCostAsync(CancellationToken ct = default) =>
        GetOrCreateAsync(LandedCostName, AccountType.OtherCurrentAsset, "Freight, duty and other costs of bringing stock in, until they're spread over the stock.", ct);

    /// <summary>The first active account of the type with the name; else the first of the type for the asset and cost accounts; else a new one.</summary>
    private async Task<Account> GetOrCreateAsync(string name, AccountType type, string description, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var active = chart.Ordered(includeInactive: false).Select(x => x.Account).Where(a => a.Type == type).ToList();
        var existing = active.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (name is ShrinkageName or LandedCostName ? null : active.FirstOrDefault(a => name != AssetName || a.Name.Contains("inventory", StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
            return existing;
        var free = chart.FindByFullName(name) is null ? name : $"{name} ({type.Describe()})";
        return await accounts.CreateAsync(new CreateAccountCommand(free, type, Description: description), ct);
    }
}
