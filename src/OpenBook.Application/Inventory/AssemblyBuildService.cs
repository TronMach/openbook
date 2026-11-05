using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

/// <param name="WarehouseId">Where the components are taken from and the assemblies go (null = the default warehouse).</param>
/// <param name="Lots">Lots or serial numbers for the assemblies built, when the assembly tracks them.</param>
public sealed record BuildAssemblyCommand(
    Guid ItemId, decimal Quantity, DateOnly Date, string? Reference = null, string? Memo = null, Guid? WarehouseId = null, string? Lots = null);

/// <summary>One component a build needs, and what's on hand of it on the date.</summary>
public sealed record BuildRequirement(Item Component, decimal PerUnit, decimal Needed, decimal OnHand)
{
    public bool IsShort => OnHand < Needed;
}

/// <param name="MaxBuildable">How many assemblies the stock on hand allows (whole units).</param>
public sealed record BuildPreview(Item Assembly, decimal Quantity, IReadOnlyList<BuildRequirement> Requirements, decimal MaxBuildable);

/// <summary>Assembly builds: components out of stock at their own cost, finished assemblies in at the total.</summary>
public sealed class AssemblyBuildService(
    IInventoryStore store,
    ISalesStore sales,
    InventoryService inventory,
    WarehouseService warehouses,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = InventorySources.AssemblyBuild;

    public async Task<IReadOnlyList<AssemblyBuild>> ListAsync(CancellationToken ct = default) =>
        (await store.ListBuildsAsync(ct)).OrderByDescending(b => b.Date).ThenByDescending(b => b.CreatedAt).ToList();

    public Task<AssemblyBuild?> GetAsync(Guid id, CancellationToken ct = default) => store.GetBuildAsync(id, ct);

    /// <summary>What building the quantity on the date needs, what's on hand, and the most that could be built.</summary>
    public async Task<BuildPreview> PreviewAsync(Guid itemId, decimal quantity, DateOnly date, Guid? warehouseId = null, CancellationToken ct = default)
    {
        var key = await warehouses.CheckAsync(warehouseId, ct);
        var (assembly, components) = await AssemblyAsync(itemId, ct);
        var requirements = new List<BuildRequirement>();
        decimal? max = null;
        foreach (var c in assembly.Components)
        {
            var component = components[c.ComponentItemId];
            // What the build could actually take: expired lots and stock since gone to later sales don't count.
            var onHand = Fifo.Available(await store.ListMovementsAsync(component.Id, ct), date, await inventory.ForPickingAsync(component, StockFilter.In(key), ct));
            requirements.Add(new BuildRequirement(component, c.Quantity, Needed(quantity, c.Quantity), onHand));
            var possible = Math.Floor(Math.Max(0m, onHand) / c.Quantity);
            max = max is null ? possible : Math.Min(max.Value, possible);
        }

        return new BuildPreview(assembly, quantity, requirements, max ?? 0m);
    }

    public async Task<AssemblyBuild> CreateAsync(BuildAssemblyCommand command, CancellationToken ct = default)
    {
        var build = await StageAsync(command, ct);
        await store.SaveChangesAsync(ct);
        return build;
    }

    /// <summary>Open pending builds, oldest first, each with what its components allow today.</summary>
    public async Task<IReadOnlyList<(PendingBuild Pending, Item Assembly, BuildPreview? Preview)>> ListPendingAsync(DateOnly date, CancellationToken ct = default)
    {
        var open = (await store.ListPendingBuildsAsync(ct)).Where(p => p.Status == PendingBuildStatus.Open).OrderBy(p => p.CreatedAt).ToList();
        var items = await sales.GetItemsAsync(open.Select(p => p.ItemId).Distinct().ToList(), ct);
        var result = new List<(PendingBuild, Item, BuildPreview?)>();
        foreach (var pending in open)
        {
            BuildPreview? preview = null;
            try
            {
                preview = await PreviewAsync(pending.ItemId, pending.Quantity, date, pending.WarehouseId, ct);
            }
            catch (DomainException)
            {
                // A changed bill of materials or an inactive warehouse shows up when it's built.
            }

            result.Add((pending, items[pending.ItemId], preview));
        }

        return result;
    }

    public Task<PendingBuild?> GetPendingAsync(Guid id, CancellationToken ct = default) => store.GetPendingBuildAsync(id, ct);

    /// <summary>Saves a build to finish later: nothing posts and no stock moves (components aren't reserved).</summary>
    public async Task<PendingBuild> SavePendingAsync(BuildAssemblyCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = await PendingAsync(command, command.Quantity, ct);
        await store.SaveChangesAsync(ct);
        return pending;
    }

    /// <summary>
    /// Builds as many whole units as the components on hand allow (lots or serials, if any, are for those) and saves
    /// the rest as a pending build, in one save. With enough of everything it's an ordinary build.
    /// </summary>
    public async Task<(AssemblyBuild? Build, PendingBuild? Pending)> BuildWhatYouCanAsync(BuildAssemblyCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AssemblyBuild.ValidateQuantity(command.Quantity);
        var preview = await PreviewAsync(command.ItemId, command.Quantity, command.Date, command.WarehouseId, ct);
        var now = Math.Min(command.Quantity, preview.MaxBuildable);
        var build = now > 0m ? await StageAsync(command with { Quantity = now }, ct) : null;
        var pending = command.Quantity > now ? await PendingAsync(command with { Lots = null }, command.Quantity - now, ct) : null;
        await store.SaveChangesAsync(ct);
        return (build, pending);
    }

    /// <summary>Finishes a pending build on <paramref name="date"/>: an ordinary build of its quantity, in one save.</summary>
    public async Task<AssemblyBuild> FinishPendingAsync(Guid pendingId, DateOnly date, string? lots = null, CancellationToken ct = default)
    {
        var pending = await store.GetPendingBuildAsync(pendingId, ct) ?? throw new EntityNotFoundException("pending_build", pendingId);
        if (pending.Status != PendingBuildStatus.Open)
            throw new DomainException("build.pending_closed", "This pending build has already been finished or cancelled.");
        var build = await StageAsync(new BuildAssemblyCommand(pending.ItemId, pending.Quantity, date, pending.Reference, pending.Memo, pending.WarehouseId, lots), ct);
        var before = PendingSnapshot(pending);
        pending.Built(build.Id, clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "pending_build", pending.Id, before, PendingSnapshot(pending)));
        await store.SaveChangesAsync(ct);
        return build;
    }

    public async Task<PendingBuild> CancelPendingAsync(Guid pendingId, CancellationToken ct = default)
    {
        var pending = await store.GetPendingBuildAsync(pendingId, ct) ?? throw new EntityNotFoundException("pending_build", pendingId);
        var before = PendingSnapshot(pending);
        pending.Cancel(clock.UtcNow);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "pending_build", pending.Id, before, PendingSnapshot(pending)));
        await store.SaveChangesAsync(ct);
        return pending;
    }

    private async Task<PendingBuild> PendingAsync(BuildAssemblyCommand command, decimal quantity, CancellationToken ct)
    {
        var (assembly, _) = await AssemblyAsync(command.ItemId, ct);
        if (!assembly.IsActive)
            throw new DomainException("build.item_inactive", $"{assembly.Name} is inactive.");
        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var pending = PendingBuild.Create(assembly.Id, quantity, warehouseId, command.Reference, command.Memo, user.Actor, clock.UtcNow);
        store.Add(pending);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, "pending_build", pending.Id, null, PendingSnapshot(pending)));
        return pending;
    }

    /// <summary>Checks and stages a build (entry, movements, document, audit) without saving.</summary>
    private async Task<AssemblyBuild> StageAsync(BuildAssemblyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        AssemblyBuild.ValidateQuantity(command.Quantity);
        var (assembly, components) = await AssemblyAsync(command.ItemId, ct);
        if (!assembly.IsActive)
            throw new DomainException("build.item_inactive", $"{assembly.Name} is inactive.");

        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        var outputLots = InventoryService.Required(LotText.Parse(command.Lots, assembly.Tracking, command.Quantity, assembly.Name), assembly);
        InventoryService.EnsureNewSerials(assembly, outputLots, await store.ListMovementsAsync(assembly.Id, ct));
        var plan = await inventory.PlanBuildAsync(assembly, components, command.Quantity, command.Date, warehouseId, outputLots, ct);

        var buildId = Guid.CreateVersion7();
        var memo = command.Memo ?? $"Build {command.Quantity:0.####} {assembly.Name}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, plan.Plan.Postings, memo, AssemblyBuild.Source, buildId), ct);
        var build = AssemblyBuild.Create(buildId, command.Date, assembly.Id, command.Quantity, plan.Lines, entry.Id, clock.UtcNow, command.Reference, command.Memo,
            warehouseId, LotText.Format(outputLots, assembly.Tracking));
        inventory.Apply(plan.Plan, EntityType, buildId);
        store.Add(build);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, buildId, null, Snapshot(build)));
        return build;
    }

    /// <summary>Unbuilds: components back on hand, assemblies out, refused once any of the assemblies built has been used.</summary>
    public async Task<AssemblyBuild> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var build = await store.GetBuildAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (build.Status == DocumentStatus.Voided)
            throw new DomainException("build.voided", "This build is already voided.");
        var plan = await inventory.PlanReversalAsync(EntityType, id, build.Date, "this build", ct);
        var before = Snapshot(build);
        await engine.StageVoidAsync(build.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(plan, EntityType, id);
        build.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, EntityType, id, before, Snapshot(build)));
        await store.SaveChangesAsync(ct);
        return build;
    }

    private async Task<(Item Assembly, IReadOnlyDictionary<Guid, Item> Components)> AssemblyAsync(Guid itemId, CancellationToken ct)
    {
        var assembly = (await sales.GetItemsAsync([itemId], ct)).GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
        if (!assembly.IsAssembly)
            throw new DomainException("build.not_assembly", $"{assembly.Name} isn't an assembly.");
        if (assembly.Components.Count == 0)
            throw new DomainException("build.no_components", $"{assembly.Name} has no components yet. Add its bill of materials on the item.");
        var components = await sales.GetItemsAsync(assembly.Components.Select(c => c.ComponentItemId).ToList(), ct);
        foreach (var c in assembly.Components)
        {
            var component = components.GetValueOrDefault(c.ComponentItemId) ?? throw new EntityNotFoundException("item", c.ComponentItemId);
            if (!component.IsInventory)
                throw new DomainException("build.component_untracked", $"{component.Name} isn't tracked stock any more, so {assembly.Name} can't be built.");
        }

        return (assembly, components);
    }

    private static decimal Needed(decimal quantity, decimal perUnit) =>
        decimal.Round(quantity * perUnit, SalesLine.Precision, MidpointRounding.AwayFromZero);

    private static string PendingSnapshot(PendingBuild p) => JsonSerializer.Serialize(new
    {
        p.ItemId,
        p.Quantity,
        p.WarehouseId,
        p.Reference,
        p.Memo,
        Status = p.Status.ToString(),
        p.BuildId,
    });

    private static string Snapshot(AssemblyBuild b) => JsonSerializer.Serialize(new
    {
        b.Date,
        b.ItemId,
        b.Quantity,
        b.Reference,
        b.Memo,
        Status = b.Status.ToString(),
        Lines = b.Lines.Select(l => new { l.ItemId, l.Quantity, l.Cost }),
    });
}
