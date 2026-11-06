using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Inventory;

/// <summary>One component an assembly build used: how much, and what it cost.</summary>
public sealed class AssemblyBuildLine
{
    private AssemblyBuildLine() { }

    internal AssemblyBuildLine(int lineNumber, Guid itemId, decimal quantity, decimal cost, string? lots)
    {
        Lots = lots;
        LineNumber = lineNumber;
        ItemId = itemId;
        Quantity = quantity;
        Cost = cost;
    }

    public int LineNumber { get; private set; }
    public Guid ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Cost { get; private set; }

    /// <summary>The component lots or serials used.</summary>
    public string? Lots { get; private set; }
}

/// <summary>
/// Making assemblies: takes the bill of materials times the quantity out of stock (at each component's own cost)
/// and puts the finished assemblies on hand as one layer at the components' total cost. The entry moves that cost
/// from the components' asset accounts to the assembly's. Voided (an "unbuild"), never edited.
/// </summary>
public sealed class AssemblyBuild
{
    public const string Source = "inventory.build";

    private readonly List<AssemblyBuildLine> _lines = [];

    private AssemblyBuild() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public Guid ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public string? Reference { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Where the components came from and the assemblies went; null for the default warehouse.</summary>
    public Guid? WarehouseId { get; private set; }

    /// <summary>The lots or serials given to the built assemblies.</summary>
    public string? Lots { get; private set; }

    public IReadOnlyList<AssemblyBuildLine> Lines => _lines;

    /// <summary>What the built assemblies cost: the sum of the components used.</summary>
    public decimal Cost => _lines.Sum(l => l.Cost);

    public static void ValidateQuantity(decimal quantity)
    {
        if (quantity <= 0m || decimal.Round(quantity, SalesLine.Precision) != quantity)
            throw new DomainException("build.quantity_invalid", "Build a quantity more than zero, with up to 4 decimal places.");
    }

    public static AssemblyBuild Create(
        Guid id, DateOnly date, Guid itemId, decimal quantity, IReadOnlyList<(Guid ItemId, decimal Quantity, decimal Cost, string? Lots)> lines,
        Guid journalEntryId, DateTimeOffset createdAt, string? reference = null, string? memo = null, Guid? warehouseId = null, string? lots = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ValidateQuantity(quantity);
        var build = new AssemblyBuild
        {
            Id = id,
            Date = date,
            ItemId = itemId,
            Quantity = quantity,
            Reference = Text.Optional(reference, 30, "build.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "build.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
            WarehouseId = warehouseId,
            Lots = lots,
        };
        build._lines.AddRange(lines.Select((l, i) => new AssemblyBuildLine(i + 1, l.ItemId, l.Quantity, l.Cost, l.Lots)));
        return build;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("build.voided", "This build is already voided.");
        Status = DocumentStatus.Voided;
    }
}

public enum LandedCostMethod
{
    /// <summary>In proportion to each line's purchase cost.</summary>
    ByValue,

    /// <summary>In proportion to each line's quantity.</summary>
    ByQuantity,
}

/// <summary>What one received line (a cost layer) got of a landed cost, split between stock on hand and stock already sold.</summary>
public sealed class LandedCostLine
{
    private LandedCostLine() { }

    internal LandedCostLine(int lineNumber, Guid billId, Guid layerId, Guid itemId, decimal amount, decimal toStock)
    {
        LineNumber = lineNumber;
        BillId = billId;
        LayerId = layerId;
        ItemId = itemId;
        Amount = amount;
        ToStock = toStock;
    }

    public int LineNumber { get; private set; }

    /// <summary>The bill that brought the stock in.</summary>
    public Guid BillId { get; private set; }

    /// <summary>The receipt movement (the bill line's layer) that got the cost.</summary>
    public Guid LayerId { get; private set; }

    public Guid ItemId { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>The part added to stock still on hand; the rest went to cost of goods sold.</summary>
    public decimal ToStock { get; private set; }

    public decimal ToCostOfSales => Amount - ToStock;
}

/// <summary>
/// Freight, duty and other costs of getting stock in, spread over the inventory received on chosen bills (by value
/// or quantity). The cost comes out of the account it was first entered to (e.g. "Landed Cost Clearing"); the share
/// of stock still on hand is added to its layers, the share of stock already sold goes to cost of goods sold.
/// </summary>
public sealed class LandedCost
{
    public const string Source = "inventory.landed_cost";

    private readonly List<LandedCostLine> _lines = [];

    private LandedCost() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>Where the cost was entered and now comes out of.</summary>
    public Guid FromAccountId { get; private set; }

    public decimal Amount { get; private set; }
    public LandedCostMethod Method { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<LandedCostLine> Lines => _lines;

    /// <summary>The bills whose stock got the cost.</summary>
    public IReadOnlyList<Guid> BillIds => _lines.Select(l => l.BillId).Distinct().ToList();

    public static void ValidateAmount(decimal amount)
    {
        if (amount <= 0m || decimal.Round(amount, 2) != amount)
            throw new DomainException("landed_cost.amount_invalid", "Enter an amount more than zero, to the cent.");
    }

    /// <summary>
    /// Splits <paramref name="amount"/> over the received lines by value or quantity, rounding to the cent with the
    /// last line taking what's left.
    /// </summary>
    public static IReadOnlyList<decimal> Allocate(decimal amount, IReadOnlyList<(decimal Quantity, decimal Cost)> received, LandedCostMethod method)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (received.Count == 0)
            throw new DomainException("landed_cost.no_stock", "Those bills didn't bring in any inventory.");
        var weights = received.Select(r => method == LandedCostMethod.ByQuantity ? r.Quantity : r.Cost).ToList();
        var total = weights.Sum();
        if (total <= 0m)
            throw new DomainException("landed_cost.no_basis", method == LandedCostMethod.ByValue
                ? "The stock on those bills cost nothing, so there's no value to spread by. Spread by quantity instead."
                : "Those bills brought in no quantity to spread over.");
        return Shares.ByWeight(amount, weights);
    }

    public static LandedCost Create(
        Guid id, DateOnly date, Guid fromAccountId, decimal amount, LandedCostMethod method,
        IReadOnlyList<(Guid BillId, Guid LayerId, Guid ItemId, decimal Amount, decimal ToStock)> lines, Guid journalEntryId, DateTimeOffset createdAt, string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ValidateAmount(amount);
        if (!Enum.IsDefined(method))
            throw new DomainException("landed_cost.method_invalid", "Unknown way to spread the cost.");
        var landed = new LandedCost
        {
            Id = id,
            Date = date,
            FromAccountId = fromAccountId,
            Amount = amount,
            Method = method,
            Memo = Text.Optional(memo, 1000, "landed_cost.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        landed._lines.AddRange(lines.Select((l, i) => new LandedCostLine(i + 1, l.BillId, l.LayerId, l.ItemId, l.Amount, l.ToStock)));
        return landed;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("landed_cost.voided", "This landed cost is already voided.");
        Status = DocumentStatus.Voided;
    }
}

public enum PendingBuildStatus
{
    /// <summary>Waiting for components.</summary>
    Open,

    /// <summary>Finished by a build (<see cref="PendingBuild.BuildId"/>).</summary>
    Built,

    Cancelled,
}

/// <summary>
/// A build waiting for components: it posts nothing and doesn't touch stock (components aren't reserved). Finishing it
/// makes an ordinary <see cref="AssemblyBuild"/> on the day it's finished.
/// </summary>
public sealed class PendingBuild
{
    private PendingBuild() => CreatedBy = string.Empty;

    public Guid Id { get; private set; }
    public Guid ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public Guid? WarehouseId { get; private set; }
    public string? Reference { get; private set; }
    public string? Memo { get; private set; }
    public PendingBuildStatus Status { get; private set; }

    /// <summary>The build that finished it.</summary>
    public Guid? BuildId { get; private set; }

    public string CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public static PendingBuild Create(Guid itemId, decimal quantity, Guid? warehouseId, string? reference, string? memo, string createdBy, DateTimeOffset now)
    {
        AssemblyBuild.ValidateQuantity(quantity);
        return new PendingBuild
        {
            Id = Guid.CreateVersion7(),
            ItemId = itemId,
            Quantity = quantity,
            WarehouseId = warehouseId,
            Reference = Text.Optional(reference, 30, "build.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, 1000, "build.memo_too_long", "Memo"),
            Status = PendingBuildStatus.Open,
            CreatedBy = createdBy,
            CreatedAt = now.ToUniversalTime(),
        };
    }

    public void Built(Guid buildId, DateTimeOffset now)
    {
        EnsureOpen();
        Status = PendingBuildStatus.Built;
        BuildId = buildId;
        ClosedAt = now.ToUniversalTime();
    }

    public void Cancel(DateTimeOffset now)
    {
        EnsureOpen();
        Status = PendingBuildStatus.Cancelled;
        ClosedAt = now.ToUniversalTime();
    }

    private void EnsureOpen()
    {
        if (Status != PendingBuildStatus.Open)
            throw new DomainException("build.pending_closed", "This pending build has already been finished or cancelled.");
    }
}
