using System.Text.Json;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>A milestone as it stands: billed means its invoice is posted (a voided one leaves it to bill again).</summary>
public sealed record MilestoneStatus(ProjectMilestone Milestone, Invoice? Invoice)
{
    public bool Billed => Invoice is { Status: DocumentStatus.Posted };
}

/// <summary>
/// Milestones of a fixed-fee project (C083): the fee in named parts, each billed by its own invoice to the project's
/// customer when the work reaches it. They add up to at most the fee; what's billed outside them isn't counted here.
/// </summary>
public sealed class ProjectMilestoneService(IProjectStore store, ISalesStore sales, InvoiceService invoices, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public async Task<IReadOnlyList<MilestoneStatus>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await RequiredAsync(projectId, ct);
        var result = new List<MilestoneStatus>();
        foreach (var m in project.Milestones.OrderBy(m => m.DueOn ?? DateOnly.MaxValue).ThenBy(m => m.LineNumber))
            result.Add(new MilestoneStatus(m, m.InvoiceId is { } id ? await sales.GetInvoiceAsync(id, ct) : null));
        return result;
    }

    public async Task<Project> AddAsync(Guid projectId, string? name, decimal amount, DateOnly? dueOn, CancellationToken ct = default)
    {
        var project = await RequiredAsync(projectId, ct);
        var before = Snapshot(project);
        project.AddMilestone(name, amount, dueOn);
        store.Add(Audit(project, before));
        await store.SaveChangesAsync(ct);
        return project;
    }

    public async Task<Project> RemoveAsync(Guid projectId, int line, CancellationToken ct = default)
    {
        var project = await RequiredAsync(projectId, ct);
        var before = Snapshot(project);
        await ForgetVoidedAsync(project.Milestone(line), ct);
        project.RemoveMilestone(line);
        store.Add(Audit(project, before));
        await store.SaveChangesAsync(ct);
        return project;
    }

    /// <summary>Bills the milestone: an invoice to the project's customer for its amount, with the product or service chosen, and the milestone marked billed, together.</summary>
    public async Task<Invoice> BillAsync(Guid projectId, int line, Guid itemId, DateOnly date, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var project = await RequiredAsync(projectId, ct);
        var milestone = project.Milestone(line);
        await ForgetVoidedAsync(milestone, ct);
        if (milestone.InvoiceId is not null)
            throw new DomainException("project.milestone_billed", "This milestone is billed already.");
        var before = Snapshot(project);
        var invoice = await invoices.CreateAsync(new CreateInvoiceCommand(project.CustomerId, date, [new SalesLineDraft(itemId, milestone.Name, 1m, milestone.Amount)]), ct);
        milestone.MarkBilled(invoice.Id, date);
        store.Add(Audit(project, before));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return invoice;
    }

    /// <summary>A milestone whose invoice was voided is waiting to be billed again.</summary>
    private async Task ForgetVoidedAsync(ProjectMilestone milestone, CancellationToken ct)
    {
        if (milestone.InvoiceId is { } id && (await sales.GetInvoiceAsync(id, ct))?.Status != DocumentStatus.Posted)
            milestone.Unbill();
    }

    private async Task<Project> RequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetProjectAsync(id, ct) ?? throw new EntityNotFoundException(ProjectService.EntityType, id);

    private AuditRecord Audit(Project p, string before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, ProjectService.EntityType, p.Id, before, Snapshot(p));

    private static string Snapshot(Project p) => JsonSerializer.Serialize(new
    {
        p.Fee,
        Milestones = p.Milestones.Select(m => new { m.LineNumber, m.Name, m.Amount, m.DueOn, m.InvoiceId, m.BilledOn }),
    });
}
