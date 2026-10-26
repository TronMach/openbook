using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Inventory;

public sealed record BuildAssemblyCommand(Guid ItemId, decimal Quantity, DateOnly Date, string? Reference = null, string? Memo = null);

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
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = InventorySources.AssemblyBuild;

    public async Task<IReadOnlyList<AssemblyBuild>> ListAsync(CancellationToken ct = default) =>
        (await store.ListBuildsAsync(ct)).OrderByDescending(b => b.Date).ThenByDescending(b => b.CreatedAt).ToList();

    public Task<AssemblyBuild?> GetAsync(Guid id, CancellationToken ct = default) => store.GetBuildAsync(id, ct);

    /// <summary>What building the quantity on the date needs, what's on hand, and the most that could be built.</summary>
    public async Task<BuildPreview> PreviewAsync(Guid itemId, decimal quantity, DateOnly date, CancellationToken ct = default)
    {
        var (assembly, components) = await AssemblyAsync(itemId, ct);
        var requirements = new List<BuildRequirement>();
        decimal? max = null;
        foreach (var c in assembly.Components)
        {
            var component = components[c.ComponentItemId];
            var onHand = (await store.ListMovementsAsync(component.Id, ct)).Where(m => m.Date <= date).Sum(m => m.Quantity);
            requirements.Add(new BuildRequirement(component, c.Quantity, Needed(quantity, c.Quantity), onHand));
            var possible = Math.Floor(Math.Max(0m, onHand) / c.Quantity);
            max = max is null ? possible : Math.Min(max.Value, possible);
        }

        return new BuildPreview(assembly, quantity, requirements, max ?? 0m);
    }

    public async Task<AssemblyBuild> CreateAsync(BuildAssemblyCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AssemblyBuild.ValidateQuantity(command.Quantity);
        var (assembly, components) = await AssemblyAsync(command.ItemId, ct);
        if (!assembly.IsActive)
            throw new DomainException("build.item_inactive", $"{assembly.Name} is inactive.");

        var lines = new List<(Guid ItemId, decimal Quantity, decimal Cost)>();
        var postings = new List<JournalLineDraft>();
        var movements = new List<Func<string, Guid, DateTimeOffset, InventoryMovement>>();
        foreach (var c in assembly.Components)
        {
            var component = components[c.ComponentItemId];
            var needed = Needed(command.Quantity, c.Quantity);
            var takes = Costing.Take(component, await store.ListMovementsAsync(component.Id, ct), needed, command.Date);
            var cost = takes.Sum(t => t.Cost);
            lines.Add((component.Id, needed, cost));
            if (cost != 0m)
                postings.Add(JournalLineDraft.Cr(component.InventoryAssetAccountId!.Value, cost, $"{component.Name} into {assembly.Name}"));
            movements.AddRange(takes.Select(t => (Func<string, Guid, DateTimeOffset, InventoryMovement>)((type, id, at) => InventoryMovement.Issue(t, component.Id, command.Date, type, id, at))));
        }

        var total = lines.Sum(l => l.Cost);
        if (total == 0m)
            throw new DomainException("build.zero_cost", "The components have no cost on hand to move into the assembly. Give them a cost (an adjustment or a bill) first.");
        postings.Insert(0, JournalLineDraft.Dr(assembly.InventoryAssetAccountId!.Value, total, $"Build {command.Quantity:0.####} {assembly.Name}"));
        var quantity = command.Quantity;
        movements.Add((type, id, at) => InventoryMovement.Receipt(assembly.Id, command.Date, quantity, total, type, id, at));

        var buildId = Guid.CreateVersion7();
        var memo = command.Memo ?? $"Build {command.Quantity:0.####} {assembly.Name}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, postings, memo, AssemblyBuild.Source, buildId), ct);
        var build = AssemblyBuild.Create(buildId, command.Date, assembly.Id, command.Quantity, lines, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        inventory.Apply(new InventoryPlan(postings, movements), EntityType, buildId);
        store.Add(build);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, buildId, null, Snapshot(build)));
        await store.SaveChangesAsync(ct);
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
