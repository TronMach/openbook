using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>A budget line with the time counted against it: hours and their cost (hours × each entry's cost rate).</summary>
public sealed record BudgetLineStatus(ProjectBudgetLine Line, string Label, decimal Hours, decimal Cost)
{
    public decimal? HoursLeft => Line.Hours is { } budget ? budget - Hours : null;
    public decimal? CostLeft => Line.Cost is { } budget ? budget - Cost : null;

    /// <summary>At or past <see cref="ProjectFigures.WarnAtPercent"/> of its hours or cost.</summary>
    public bool Warn =>
        (Line.Hours is > 0m && Hours * 100m >= Line.Hours.Value * ProjectFigures.WarnAtPercent)
        || (Line.Cost is > 0m && Cost * 100m >= Line.Cost.Value * ProjectFigures.WarnAtPercent);
}

/// <summary>
/// A project's budget by role or by service (C310): each line's hours and cost against the time on the project, by the
/// person's role (People and rates) or the entry's service. Time with neither matching counts only in the project's totals.
/// </summary>
public sealed class ProjectBudgetService(IProjectStore store, ISalesStore sales, IClock clock, ICurrentUser user)
{
    public async Task<IReadOnlyList<BudgetLineStatus>> StatusAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await RequiredAsync(projectId, ct);
        if (project.BudgetLines.Count == 0)
            return [];
        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var time = (await store.ListTimeAsync(new TimeFilter(CustomerId: project.CustomerId), ct)).Where(t => !t.IsRunning).ToList();
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id, i => i.Name);
        return [.. project.BudgetLines.Select(line =>
        {
            var counted = time.Where(t => line.Role is { } role
                ? string.Equals(workers.GetValueOrDefault(t.WorkerId)?.Role, role, StringComparison.OrdinalIgnoreCase)
                : t.ItemId == line.ItemId).ToList();
            var label = line.Role is { } r ? $"Role: {r}" : $"Service: {items.GetValueOrDefault(line.ItemId!.Value, "?")}";
            return new BudgetLineStatus(line, label, counted.Sum(t => t.Hours), counted.Sum(t => t.Cost));
        })];
    }

    public async Task<Project> SetAsync(Guid projectId, IReadOnlyList<ProjectBudgetLineDraft> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var project = await RequiredAsync(projectId, ct);
        var ids = lines.Select(l => l.ItemId).OfType<Guid>().Where(id => id != Guid.Empty).Distinct().ToList();
        var found = ids.Count == 0 ? (IReadOnlyDictionary<Guid, Item>)new Dictionary<Guid, Item>() : await sales.GetItemsAsync(ids, ct);
        foreach (var itemId in ids)
        {
            var item = found.GetValueOrDefault(itemId) ?? throw new EntityNotFoundException("item", itemId);
            if (item.Type is not (ItemType.Service or ItemType.NonInventory))
                throw new DomainException("project.budget_service", $"{item.Name} isn't a service, so no time is entered on it.");
        }

        var before = Snapshot(project);
        project.SetBudgetLines(lines);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, ProjectService.EntityType, project.Id, before, Snapshot(project)));
        await store.SaveChangesAsync(ct);
        return project;
    }

    private async Task<Project> RequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetProjectAsync(id, ct) ?? throw new EntityNotFoundException(ProjectService.EntityType, id);

    private static string Snapshot(Project p) => JsonSerializer.Serialize(new
    {
        BudgetLines = p.BudgetLines.Select(l => new { l.LineNumber, l.Role, l.ItemId, l.Hours, l.Cost }),
    });
}
