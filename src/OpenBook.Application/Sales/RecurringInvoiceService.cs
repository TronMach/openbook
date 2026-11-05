using OpenBook.Application.Companies;
using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="EmailError">Why emailing the invoices failed, when they were created but not sent.</param>
public sealed record RecurringInvoiceResult(RecurringInvoice Template, IReadOnlyList<Invoice> Created, string? Error, int Emailed = 0, string? EmailError = null);

/// <summary>Recurring invoices: templates that create an invoice on a schedule, optionally emailing it.</summary>
public sealed class RecurringInvoiceService(IUnitOfWork unit, ISalesStore store, InvoiceService invoices, EmailService email, PostingEngine engine, IClock clock, ICurrentUser user)
{
    private const string EntityType = "recurring_invoice";
    public const int MaxCatchUp = 60;

    public Task<IReadOnlyList<RecurringInvoice>> ListAsync(CancellationToken ct = default) => store.ListRecurringInvoicesAsync(ct);

    public Task<RecurringInvoice?> GetAsync(Guid id, CancellationToken ct = default) => store.GetRecurringInvoiceAsync(id, ct);

    public async Task<RecurringInvoice> CreateAsync(RecurringInvoiceDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await EnsureCustomerAsync(details.CustomerId, ct);
        await EnsureSegmentsAsync(details, ct);
        var template = RecurringInvoice.Create(details, clock.UtcNow);
        store.Add(template);
        store.Add(Audit(AuditAction.Created, template, before: null));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringInvoice> UpdateAsync(Guid id, RecurringInvoiceDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var template = await store.GetRecurringInvoiceAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await EnsureCustomerAsync(details.CustomerId, ct);
        await EnsureSegmentsAsync(details, ct);
        var before = Snapshot(template);
        template.Update(details);
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public Task<RecurringInvoice> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) => SetActiveAsync(id, active, false, ct);

    /// <param name="catchUp">Turning it back on makes every date it missed; otherwise those are passed over.</param>
    public async Task<RecurringInvoice> SetActiveAsync(Guid id, bool active, bool catchUp, CancellationToken ct = default)
    {
        var template = await store.GetRecurringInvoiceAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(template);
        if (active)
            template.Reactivate(catchUp ? null : CompanyTime.Today);
        else
            template.Deactivate();
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    /// <summary>
    /// Creates every invoice due on or before <paramref name="today"/>, each in its own save, then emails
    /// them for templates set to. A template stops at its first refusal (an inactive item, a closed period).
    /// </summary>
    /// <param name="automaticOnly">True for the background job, which only runs templates set to create automatically.</param>
    public async Task<IReadOnlyList<RecurringInvoiceResult>> CreateDueAsync(DateOnly today, bool automaticOnly, CancellationToken ct = default)
    {
        var results = new List<RecurringInvoiceResult>();
        foreach (var listed in await store.ListRecurringInvoicesAsync(ct))
        {
            if (!listed.IsDue(today) || (automaticOnly && !listed.AutoCreate))
                continue;

            // Afresh: a refused occurrence below forgets everything this scope was tracking.
            var template = await store.GetRecurringInvoiceAsync(listed.Id, ct);
            if (template is null)
                continue;


            var created = new List<Invoice>();
            string? error = null;
            while (template.IsDue(today) && created.Count < MaxCatchUp)
            {
                try
                {
                    // The document and the template's count in one transaction, so a failure in between can't make it twice.
                    await using var transaction = await unit.BeginAsync(ct);
                    var date = template.NextDate!.Value;
                    var invoice = await invoices.CreateAsync(new CreateInvoiceCommand(
                        template.CustomerId, date, template.Lines.OrderBy(l => l.LineNumber).Select(l => l.ToDraft()).ToList(), Memo: template.Memo,
                        LocationId: template.LocationId, TagIds: template.TagIds),
                        template.Id, ct);
                    template.RecordCreated(invoice.Id);
                    await store.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    created.Add(invoice);
                }
                catch (DomainException ex)
                {
                    unit.DiscardPending();
                    error = ex.Message;
                    break;
                }
            }

            var (emailed, emailError) = template.AutoEmail && created.Count > 0 ? await EmailAsync(created, ct) : (0, null);
            results.Add(new RecurringInvoiceResult(template, created, error, emailed, emailError));
        }

        return results;
    }

    private async Task<(int Sent, string? Error)> EmailAsync(IReadOnlyList<Invoice> created, CancellationToken ct)
    {
        var sent = 0;
        foreach (var invoice in created)
        {
            try
            {
                var draft = await email.DraftAsync(FormKind.Invoice, invoice.Id, ct);
                if (string.IsNullOrEmpty(draft.To))
                    return (sent, "The customer has no email address.");
                await email.SendFormAsync(FormKind.Invoice, invoice.Id, draft, ct);
                sent++;
            }
            catch (DomainException ex)
            {
                return (sent, ex.Message);
            }
        }

        return (sent, null);
    }

    private async Task EnsureCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
    }

    private Task EnsureSegmentsAsync(RecurringInvoiceDetails details, CancellationToken ct) =>
        engine.EnsureSegmentsAsync((details.Lines ?? []).Select(l => l.ClassId), details.LocationId == Guid.Empty ? null : details.LocationId,
            Recurrence.ParseTags(Recurrence.JoinTags(details.TagIds)), ct);

    private AuditRecord Audit(AuditAction action, RecurringInvoice t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    private static string Snapshot(RecurringInvoice t) => JsonSerializer.Serialize(new
    {
        t.Name,
        t.CustomerId,
        t.Memo,
        Frequency = t.Frequency.ToString(),
        t.Interval,
        t.StartDate,
        t.EndDate,
        t.MaxOccurrences,
        t.AutoCreate,
        t.AutoEmail,
        t.IsActive,
        t.LocationId,
        t.Tags,
        Lines = t.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.ClassId }),
    });
}
