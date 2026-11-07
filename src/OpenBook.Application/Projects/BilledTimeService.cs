using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <param name="WriteOff">True writes the time off (it won't be billed); false puts it back with unbilled work.</param>
public sealed record TakeBackTimeCommand(Guid InvoiceId, IReadOnlyList<Guid> TimeEntryIds, DateOnly Date, string Reason, bool WriteOff = false);

/// <summary>
/// Takes billed time back off an invoice that can't simply be corrected (a payment is on it, say): a credit memo for
/// those entries at what they were billed (on the invoice's tax code), applied to the invoice up to what it still owes
/// (the rest is the customer's credit), and the time either back with unbilled work or written off. One transaction.
/// </summary>
public sealed class BilledTimeService(
    IProjectStore store, ISalesStore sales, CreditService credits, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public async Task<CreditMemo> TakeBackAsync(TakeBackTimeCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.TimeEntryIds);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("time.take_back_reason", "Say why the time is coming off the invoice.");
        var invoice = await sales.GetInvoiceAsync(command.InvoiceId, ct) ?? throw new EntityNotFoundException("invoice", command.InvoiceId);
        if (invoice.Status != DocumentStatus.Posted)
            throw new DomainException("invoice.voided", "That invoice is voided.");
        var ids = command.TimeEntryIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainException("time.take_back_none", "Choose the time to take off the invoice.");
        if (command.Date < invoice.Date)
            throw new DomainException("time.take_back_date", "Time can't come off an invoice before the invoice's date.");

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var entries = await store.GetTimeEntriesAsync(ids, ct);
        if (entries.Count != ids.Count || entries.Any(e => e.InvoiceId != invoice.Id))
            throw new DomainException("time.not_on_invoice", $"Some of that time isn't on invoice {invoice.Number}.");
        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);

        var lines = entries.OrderBy(e => e.Date).Select(e => new SalesLineDraft(e.ItemId,
            TextCleaning.Cut($"Taken off invoice {invoice.Number}: {e.Date:MMM d, yyyy} · {workers[e.WorkerId].Name}{(e.Notes is { } n ? ": " + n : "")}", 1000),
            e.Hours, e.RateBilled, PriceSource: ProjectBilling.TimePriceSource, ClassId: workers[e.WorkerId].ClassId)).ToList();
        var memo = await credits.CreateCreditMemoAsync(new CreateCreditMemoCommand(invoice.CustomerId, command.Date, lines,
            Memo: TextCleaning.Cut($"Time taken off invoice {invoice.Number}: {command.Reason.Trim()}", 500), TaxCodeId: invoice.TaxCodeId ?? Guid.Empty), ct);

        var owed = (await sales.GetInvoiceAsync(invoice.Id, ct))!.Balance;
        if (Math.Min(owed, memo.Total) is var applied and > 0m)
            await credits.ApplyAsync(invoice.CustomerId, command.Date, [new ApplyCredit(CreditKind.CreditMemo, memo.Id, invoice.Id, applied)], ct);

        foreach (var entry in entries)
        {
            var before = TimeService.Snapshot(entry);
            entry.ClearInvoice();
            if (command.WriteOff)
                entry.Adjust(0m, command.Reason, command.Date, user.Actor);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, TimeService.EntityType, entry.Id, before, TimeService.Snapshot(entry)));
        }

        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return memo;
    }
}
