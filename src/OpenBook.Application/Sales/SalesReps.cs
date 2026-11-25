using OpenBook.Application.Projects;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>
/// Sales reps: the person (<see cref="Worker"/>) an invoice, sales receipt or credit memo is credited to. A new document
/// takes the customer's responsible person (its own, else the nearest up the customer tree) unless told otherwise.
/// </summary>
public sealed class SalesReps(ISalesStore sales, IProjectStore projects)
{
    /// <summary>The people to choose from, by name: the active ones, and <paramref name="keep"/> if a document has them.</summary>
    public async Task<IReadOnlyList<Worker>> ListAsync(Guid? keep = null, CancellationToken ct = default) =>
        [.. (await projects.ListWorkersAsync(ct)).Where(w => w.IsActive || w.Id == keep).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The customer's responsible person, when they're active.</summary>
    public async Task<Guid?> DefaultAsync(Guid? customerId, CancellationToken ct = default)
    {
        var next = customerId;
        for (var i = 0; i <= CustomerTree.MaxDepth && next is { } id && await sales.GetCustomerAsync(id, ct) is { } customer; i++)
        {
            if (customer.ResponsibleWorkerId is { } worker)
                return await projects.GetWorkerAsync(worker, ct) is { IsActive: true } ? worker : null;
            next = customer.ParentId;
        }

        return null;
    }

    /// <summary>The rep of a new document: null takes the customer's, <see cref="Guid.Empty"/> is none.</summary>
    public Task<Guid?> ForNewAsync(Guid? chosen, Guid? customerId, CancellationToken ct) =>
        chosen is null ? DefaultAsync(customerId, ct) : CheckAsync(chosen, current: null, ct);

    /// <summary>The rep after a correction: null keeps the document's, <see cref="Guid.Empty"/> is none.</summary>
    public Task<Guid?> ForChangeAsync(Guid? chosen, Guid? current, CancellationToken ct) =>
        chosen is null ? Task.FromResult(current) : CheckAsync(chosen, current, ct);

    private async Task<Guid?> CheckAsync(Guid? chosen, Guid? current, CancellationToken ct)
    {
        if (chosen is not { } id || id == Guid.Empty)
            return null;
        if (id == current)
            return id;
        var worker = await projects.GetWorkerAsync(id, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, id);
        return worker.IsActive ? id : throw new DomainException("salesrep.inactive", $"{worker.Name} is inactive, so can't be the sales rep of a new sale.");
    }
}
