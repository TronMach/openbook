using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Error">Why it wasn't sent; null when it went.</param>
public sealed record ScheduledEmailResult(ScheduledEmail Email, string? Error);

/// <summary>
/// Invoice emails scheduled for later. The message is fixed when scheduled; the PDF is rendered when it goes, so
/// it shows the invoice as it stands then. <see cref="SendDueAsync"/> (run every minute by the host) sends what's
/// due, each on its own: a failure is kept on the email (try again or cancel) and the rest still go; an invoice
/// voided since is cancelled rather than sent.
/// </summary>
public sealed class ScheduledEmailService(ISalesStore store, EmailService email, IClock clock, ICurrentUser user)
{
    public const string EntityType = "scheduled_email";

    public async Task<ScheduledEmail> ScheduleAsync(Guid invoiceId, EmailDraft message, DateTimeOffset sendAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var invoice = await store.GetInvoiceAsync(invoiceId, ct) ?? throw new EntityNotFoundException("invoice", invoiceId);
        if (invoice.Status == DocumentStatus.Voided)
            throw new DomainException("scheduled_email.invoice_voided", "A voided invoice can't be emailed.");
        if (Domain.Email.EmailAddresses.Parse(message.To, "To").Count == 0)
            throw new DomainException("email.to_required", "Enter the customer's email address.");

        if ((await store.ListScheduledEmailsAsync(invoiceId, ct)).Any(e => e.Status == ScheduledEmailStatus.Scheduled && (e.SendAt - sendAt).Duration() < TimeSpan.FromSeconds(1)))
            throw new DomainException("scheduled_email.duplicate", "This invoice already has an email waiting to go at that time.");

        var scheduled = ScheduledEmail.Create(invoiceId, message.To, message.Subject, message.Body, sendAt, clock.UtcNow, user.Actor,
            message.Cc, message.Bcc, message.DesignId);
        store.Add(scheduled);
        store.Add(Audit(AuditAction.Created, scheduled, null));
        await store.SaveChangesAsync(ct);
        return scheduled;
    }

    /// <summary>An email that couldn't go when it was meant to: kept as failed, to try again or cancel.</summary>
    public async Task QueueFailedAsync(Guid invoiceId, EmailDraft message, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = clock.UtcNow;
        var failed = ScheduledEmail.Create(invoiceId, message.To, message.Subject, message.Body, now.AddMinutes(1), now, user.Actor, message.Cc, message.Bcc, message.DesignId);
        failed.MarkFailed(reason);
        store.Add(failed);
        store.Add(Audit(AuditAction.Created, failed, null));
        await store.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        var scheduled = await GetAsync(id, ct);
        var before = Snapshot(scheduled);
        scheduled.Cancel();
        store.Add(Audit(AuditAction.Updated, scheduled, before));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Puts a failed email back in the queue to go on the next run.</summary>
    public async Task RetryAsync(Guid id, CancellationToken ct = default)
    {
        var scheduled = await GetAsync(id, ct);
        var before = Snapshot(scheduled);
        scheduled.Retry(clock.UtcNow);
        store.Add(Audit(AuditAction.Updated, scheduled, before));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Scheduled emails (one invoice's when given), by send time.</summary>
    public Task<IReadOnlyList<ScheduledEmail>> ListAsync(Guid? invoiceId = null, CancellationToken ct = default) =>
        store.ListScheduledEmailsAsync(invoiceId, ct);

    public async Task<IReadOnlyList<ScheduledEmailResult>> SendDueAsync(CancellationToken ct = default)
    {
        var results = new List<ScheduledEmailResult>();
        foreach (var scheduled in await store.ListDueEmailsAsync(clock.UtcNow, ct))
        {
            var before = Snapshot(scheduled);
            var invoice = await store.GetInvoiceAsync(scheduled.InvoiceId, ct);
            if (invoice is null || invoice.Status == DocumentStatus.Voided)
            {
                scheduled.Cancel("The invoice was voided.");
                results.Add(new ScheduledEmailResult(scheduled, scheduled.Error));
            }
            else
            {
                try
                {
                    await email.SendFormAsync(FormKind.Invoice, scheduled.InvoiceId, new EmailDraft(scheduled.To, scheduled.Subject, scheduled.Body, scheduled.Cc, scheduled.Bcc, scheduled.DesignId), ct);
                    scheduled.MarkSent(clock.UtcNow);
                    results.Add(new ScheduledEmailResult(scheduled, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Anything: one email that can't be made mustn't hold up the ones behind it, every minute.
                    scheduled.MarkFailed(ex.Message);
                    results.Add(new ScheduledEmailResult(scheduled, ex.Message));
                }
            }

            store.Add(Audit(AuditAction.Updated, scheduled, before));
            await store.SaveChangesAsync(ct);
        }

        return results;
    }

    private async Task<ScheduledEmail> GetAsync(Guid id, CancellationToken ct) =>
        await store.GetScheduledEmailAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private AuditRecord Audit(AuditAction action, ScheduledEmail e, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, e.Id, before, Snapshot(e));

    private static string Snapshot(ScheduledEmail e) => JsonSerializer.Serialize(new
    {
        e.InvoiceId,
        e.To,
        e.Cc,
        e.Bcc,
        e.Subject,
        e.SendAt,
        Status = e.Status.ToString(),
        e.SentAt,
        e.Error,
    });
}
