using OpenBook.Application.Companies;
using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Ledger;

/// <param name="Error">Why an occurrence couldn't be posted (e.g. a closed period); later ones wait behind it.</param>
public sealed record RecurringPostResult(RecurringEntry Template, IReadOnlyList<JournalEntry> Posted, string? Error);

/// <summary>Recurring journal entry templates, and posting their due occurrences.</summary>
public sealed class RecurringEntryService(ILedgerStore store, PostingEngine engine, IClock clock, ICurrentUser user)
{
    private const string EntityType = "recurring_entry";

    /// <summary>Stop runaway catch-up: at most this many occurrences per template per run.</summary>
    public const int MaxCatchUp = 120;

    public Task<IReadOnlyList<RecurringEntry>> ListAsync(CancellationToken ct = default) => store.ListRecurringAsync(ct);

    public Task<RecurringEntry?> GetAsync(Guid id, CancellationToken ct = default) => store.GetRecurringAsync(id, ct);

    public async Task<RecurringEntry> CreateAsync(RecurringEntryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await EnsureSegmentsAsync(details, ct);
        var entry = RecurringEntry.Create(details, clock.UtcNow);
        store.Add(entry);
        store.Add(Audit(AuditAction.Created, entry, before: null));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<RecurringEntry> UpdateAsync(Guid id, RecurringEntryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var entry = await store.GetRecurringAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await EnsureSegmentsAsync(details, ct);
        var before = Snapshot(entry);
        entry.Update(details);
        store.Add(Audit(AuditAction.Updated, entry, before));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    public Task<RecurringEntry> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) => SetActiveAsync(id, active, false, ct);

    /// <param name="catchUp">Turning it back on makes every date it missed; otherwise those are passed over.</param>
    public async Task<RecurringEntry> SetActiveAsync(Guid id, bool active, bool catchUp, CancellationToken ct = default)
    {
        var entry = await store.GetRecurringAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (entry.IsActive == active)
            return entry;
        var before = Snapshot(entry);
        if (active)
            entry.Reactivate(catchUp ? null : CompanyTime.Today);
        else
            entry.Deactivate();
        store.Add(Audit(AuditAction.Updated, entry, before));
        await store.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Posts every occurrence due on or before <paramref name="today"/>, each in its own save, so one
    /// failure (a closed period, an inactive account) doesn't undo the others.
    /// </summary>
    /// <param name="autoPostOnly">True for the background job, which only posts templates set to post automatically.</param>
    public async Task<IReadOnlyList<RecurringPostResult>> PostDueAsync(DateOnly today, bool autoPostOnly, CancellationToken ct = default)
    {
        var results = new List<RecurringPostResult>();
        foreach (var template in await store.ListRecurringAsync(ct))
        {
            if (!template.IsDue(today) || (autoPostOnly && !template.AutoPost))
                continue;

            var posted = new List<JournalEntry>();
            string? error = null;
            while (template.IsDue(today) && posted.Count < MaxCatchUp)
            {
                try
                {
                    var entry = await engine.StageAsync(new PostJournalEntryCommand(
                        template.NextDate!.Value,
                        template.Lines.Select(l => l.ToDraft()).ToList(),
                        template.Memo ?? template.Name,
                        JournalEntry.RecurringSource,
                        template.Id,
                        template.Basis,
                        LocationId: template.LocationId,
                        TagIds: template.TagIds), ct);
                    template.RecordPosted(entry.Id);
                    await store.SaveChangesAsync(ct);
                    posted.Add(entry);
                }
                catch (DomainException ex)
                {
                    error = ex.Message;
                    break;
                }
            }

            results.Add(new RecurringPostResult(template, posted, error));
        }

        return results;
    }

    private Task EnsureSegmentsAsync(RecurringEntryDetails details, CancellationToken ct) =>
        engine.EnsureSegmentsAsync((details.Lines ?? []).Select(l => l.ClassId), details.LocationId == Guid.Empty ? null : details.LocationId,
            Recurrence.ParseTags(Recurrence.JoinTags(details.TagIds)), ct);

    private AuditRecord Audit(AuditAction action, RecurringEntry e, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, e.Id, before, Snapshot(e));

    private static string Snapshot(RecurringEntry e) => JsonSerializer.Serialize(new
    {
        e.Name,
        e.Memo,
        Frequency = e.Frequency.ToString(),
        e.Interval,
        e.StartDate,
        e.EndDate,
        e.MaxOccurrences,
        e.OccurrencesPosted,
        e.AutoPost,
        e.IsActive,
        e.LocationId,
        e.Tags,
        Lines = e.Lines.Select(l => new { l.AccountId, l.Debit, l.Credit, l.Memo, l.ClassId }),
    });
}
