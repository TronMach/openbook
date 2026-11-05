using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Ledger;

/// <param name="LocationId">The transaction's location (Domain.Segments): every line of the entry carries it.</param>
/// <param name="TagIds">Tags on the entry.</param>
public sealed record PostJournalEntryCommand(
    DateOnly Date,
    IReadOnlyCollection<JournalLineDraft> Lines,
    string? Memo = null,
    string Source = JournalEntry.ManualSource,
    Guid? SourceId = null,
    EntryBasis Basis = EntryBasis.Both,
    DateOnly? ReverseOn = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);

/// <summary>
/// The closing date password for this unit of work, if the user gave one. Hosts set it before calling a
/// service (from a form field or the X-Closing-Password header); <see cref="PostingEngine"/> checks it.
/// </summary>
public sealed class ClosedPeriodOverride
{
    public string? Password { get; set; }
}

/// <summary>
/// The single way anything reaches the ledger. Validates against the chart of accounts and the closing
/// date, assigns entry numbers and writes the audit trail in the same save.
/// </summary>
public sealed class PostingEngine(
    ILedgerStore store,
    ICompanyStore companies,
    IPasswordHasher hasher,
    ClosedPeriodOverride closedPeriod,
    IClock clock,
    ICurrentUser user,
    Attachments.AttachmentService attachments,
    Notes.NoteService notes,
    Banking.IBankingStore bank)
{
    private const string EntityType = "journal_entry";

    /// <summary>
    /// What entries voided in this scope carried. A correction voids an entry and posts its replacement: what the
    /// document already had stays allowed though it's been deactivated since, or a memo fix would be refused over it.
    /// </summary>
    private readonly HashSet<Guid> _carried = [];

    public async Task<JournalEntry> PostAsync(PostJournalEntryCommand command, CancellationToken ct = default)
    {
        var entry = await StageAsync(command, ct);
        await store.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Voids a journal entry (manual, recurring, opening balance or automatic reversal). Entries posted by documents (bank transactions, invoices, ...) are voided
    /// through the document, so the document's status stays in step. An automatic reversal still standing is voided
    /// with it, or the later period would keep the reversal of something that no longer happened.
    /// </summary>
    /// <returns>The reversing entry.</returns>
    public async Task<JournalEntry> VoidAsync(Guid entryId, string reason, CancellationToken ct = default)
    {
        var mirror = await StandingAutoReversalAsync(entryId, ct);
        if (mirror is not null && await store.IsInReconciliationAsync(mirror.Id, ct))
            throw new DomainException("journal.reconciled",
                $"This entry's automatic reversal, entry #{mirror.Number}, has been reconciled, or is ticked in a reconciliation in progress, so neither can be voided.");

        var reversal = await StageVoidAsync(entryId, reason, fromDocument: false, ct);
        if (mirror is not null)
            await StageVoidAsync(mirror.Id, reason, fromDocument: false, ct);
        await store.SaveChangesAsync(ct);
        return reversal;
    }

    /// <summary>The automatic reversal of an entry, while it stands.</summary>
    public async Task<JournalEntry?> StandingAutoReversalAsync(Guid entryId, CancellationToken ct = default)
    {
        // A voided reversal's own reversing entry shares its source, so look past the newest.
        var (entries, _) = await store.ListEntriesAsync(new JournalEntryFilter(Take: 10, Source: JournalEntry.AutoReversalSource, SourceId: entryId), ct);
        return entries.FirstOrDefault(e => e.Status == JournalEntryStatus.Posted && e.Kind == JournalEntryKind.Standard);
    }

    /// <summary>
    /// Corrects a journal entry entered by hand: voids it (a same-date reversal, as <see cref="VoidAsync"/>) and posts
    /// <paramref name="command"/> in its place, in one save, so the books never show both or neither. The original
    /// keeps its number; the replacement gets the next. Recorded as an <c>Updated</c> audit record on the original with
    /// the reason and the replacement's number. Not for entries posted by documents, imports, recurring templates
    /// or opening balances, nor for an entry with an automatic reversal (void that pair and enter it again).
    /// </summary>
    /// <returns>The replacement entry.</returns>
    public async Task<JournalEntry> ChangeAsync(Guid entryId, PostJournalEntryCommand command, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("journal.change_reason_required", "Say why the entry is being changed.");
        var entry = await store.GetEntryAsync(entryId, ct) ?? throw new EntityNotFoundException(EntityType, entryId);
        if (entry.Source != JournalEntry.ManualSource || entry.Kind != JournalEntryKind.Standard)
            throw new DomainException("journal.change_source",
                "Only journal entries entered by hand can be changed. Change the transaction that posted this one, or void it and enter it again.");
        if (entry.Status != JournalEntryStatus.Posted)
            throw new DomainException("journal.already_voided", $"Entry #{entry.Number} is voided.");
        if (await StandingAutoReversalAsync(entry.Id, ct) is not null)
            throw new DomainException("journal.change_reversing",
                "This entry has an automatic reversal. Void it, which voids the reversal too, and enter it again.");

        // A line from the bank follows the entry while the money in its account is the same; otherwise it's looked at again.
        var feedLines = await bank.ListFeedItemsTiedToEntryAsync(entry.Id, ct);
        var sameMoney = feedLines.All(i => command.Lines.Where(l => l.AccountId == i.AccountId).Sum(l => l.Debit - l.Credit) == i.Amount);
        await StageVoidAsync(entry.Id, $"Changed: {reason.Trim()}", fromDocument: false, keepFeedLines: sameMoney, ct);
        var replacement = await StageAsync(command with { Source = JournalEntry.ManualSource, SourceId = null }, ct);
        if (sameMoney)
        {
            foreach (var line in feedLines)
                line.FollowEntry(replacement.Id);
        }

        // The entry's files and notes go on with it (the voided one keeps its own).
        await attachments.StageCopiesAsync(EntityType, entry.Id, replacement.Id, ct);
        await notes.StageCopiesAsync(EntityType, entry.Id, replacement.Id, ct);
        store.Add(Audit(AuditAction.Updated, entry.Id, before: null,
            after: JsonSerializer.Serialize(new Dictionary<string, object> { [DocumentChanges.ReasonKey] = reason.Trim(), ["ReplacedBy"] = replacement.Number })));
        await store.SaveChangesAsync(ct);
        return replacement;
    }

    /// <summary>
    /// A saved entry as a command to post again: its lines (with classes), memo, basis, location and tags, for a form
    /// that copies or changes it. Null when there's no such entry.
    /// </summary>
    public async Task<PostJournalEntryCommand?> DraftOfAsync(Guid entryId, CancellationToken ct = default)
    {
        if (await store.GetEntryAsync(entryId, ct) is not { } entry)
            return null;
        var segments = (await store.GetLineSegmentsAsync([entry.Id], ct)).ToDictionary(s => s.JournalLineId);
        var tags = (await store.GetEntryTagsAsync([entry.Id], ct)).Select(t => t.TagId).ToList();
        var lines = entry.Lines.OrderBy(l => l.LineNumber)
            .Select(l => new JournalLineDraft(l.AccountId, l.Debit, l.Credit, l.Memo, segments.GetValueOrDefault(l.Id)?.ClassId))
            .ToList();
        return new PostJournalEntryCommand(entry.Date, lines, entry.Memo, Basis: entry.Basis,
            LocationId: segments.Values.Select(s => s.LocationId).FirstOrDefault(l => l is not null), TagIds: tags);
    }

    /// <summary>Saves everything staged, for callers that stage several entries without a document of their own.</summary>
    internal Task SaveAsync(CancellationToken ct) => store.SaveChangesAsync(ct);

    /// <summary>Validates and adds an entry without saving, so a document and its entry save together.</summary>
    internal async Task<JournalEntry> StageAsync(PostJournalEntryCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);

        if (command.ReverseOn is { } reverseOn && reverseOn <= command.Date)
            throw new DomainException("journal.reverse_date", "The reversal date must be after the entry date.");

        await EnsurePostableAsync(command.Lines.Select(l => l.AccountId).ToHashSet(), ct);
        var locationId = command.LocationId == Guid.Empty ? null : command.LocationId;
        var tagIds = (command.TagIds ?? []).Where(t => t != Guid.Empty).Distinct().ToList();
        await EnsureSegmentsAsync(command.Lines.Select(l => l.ClassId), locationId, tagIds, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        EnsureOpen(company, command.Date, $"posting an entry dated {command.Date:yyyy-MM-dd}");

        var entry = JournalEntry.Post(
            await store.NextEntryNumberAsync(ct),
            command.Date,
            company.HomeCurrency,
            command.Lines,
            clock.UtcNow,
            command.Memo,
            command.Source,
            command.SourceId,
            command.Basis);

        store.Add(entry);
        AddSegments(entry, command.Lines.Select(l => l.ClassId).ToList(), locationId, tagIds);
        store.Add(Audit(AuditAction.Posted, entry.Id, before: null, after: Snapshot(entry)));

        // A reversing entry: the mirror image, posted now for its later date (e.g. reversing an accrual next month).
        if (command.ReverseOn is { } on)
        {
            EnsureOpen(company, on, $"posting a reversal dated {on:yyyy-MM-dd}");
            var mirror = JournalEntry.Post(
                await store.NextEntryNumberAsync(ct),
                on,
                company.HomeCurrency,
                command.Lines.Select(l => new JournalLineDraft(l.AccountId, l.Credit, l.Debit, l.Memo)).ToList(),
                clock.UtcNow,
                JournalEntry.Fit($"Reversal of #{entry.Number}" + (entry.Memo is null ? string.Empty : $": {entry.Memo}")),
                JournalEntry.AutoReversalSource,
                entry.Id,
                command.Basis);
            store.Add(mirror);
            AddSegments(mirror, command.Lines.Select(l => l.ClassId).ToList(), locationId, tagIds);
            store.Add(Audit(AuditAction.Posted, mirror.Id, before: null, after: Snapshot(mirror)));
        }

        return entry;
    }

    internal Task<JournalEntry> StageVoidAsync(Guid entryId, string reason, bool fromDocument, CancellationToken ct) =>
        StageVoidAsync(entryId, reason, fromDocument, keepFeedLines: false, ct);

    /// <summary>
    /// Bank feed lines standing for the entry go back to the queue, or they'd sit on a dead entry where no re-import or
    /// review would find them. A correction that posts a replacement passes <paramref name="keepFeedLines"/>: its
    /// lines stay, and matched ones follow the new entry.
    /// </summary>
    internal async Task<JournalEntry> StageVoidAsync(Guid entryId, string reason, bool fromDocument, bool keepFeedLines, CancellationToken ct)
    {
        var entry = await store.GetEntryAsync(entryId, ct) ?? throw new EntityNotFoundException(EntityType, entryId);
        if (!fromDocument && !JournalEntry.IsJournalSource(entry.Source))
            throw new DomainException("journal.void_document", "This entry was posted by another transaction; void that transaction instead.");
        if (await store.IsInReconciliationAsync(entry.Id, ct))
            throw new DomainException("journal.reconciled",
                "This transaction has been reconciled, or is ticked in a reconciliation in progress, so it can't be voided.");

        // The reversal is dated the same day as the entry, so voiding changes that period.
        EnsureOpen(await companies.GetRequiredCompanyAsync(ct), entry.Date, $"voiding entry #{entry.Number}");

        _carried.UnionWith(entry.Lines.Select(l => l.AccountId));
        foreach (var segment in await store.GetLineSegmentsAsync([entry.Id], ct))
            _carried.UnionWith(new[] { segment.ClassId, segment.LocationId }.OfType<Guid>());
        _carried.UnionWith((await store.GetEntryTagsAsync([entry.Id], ct)).Select(t => t.TagId));

        var before = Snapshot(entry);
        var reversal = entry.Void(await store.NextEntryNumberAsync(ct), reason, clock.UtcNow);

        store.Add(reversal);

        // The reversal carries the original's segments and tags, so reports by class, location or tag net to zero.
        var lineIds = reversal.Lines.ToDictionary(l => l.LineNumber, l => l.Id);
        var originalLines = entry.Lines.ToDictionary(l => l.Id, l => l.LineNumber);
        foreach (var segment in await store.GetLineSegmentsAsync([entry.Id], ct))
            store.Add(new LineSegment(lineIds[originalLines[segment.JournalLineId]], reversal.Id, segment.ClassId, segment.LocationId));
        foreach (var tag in await store.GetEntryTagsAsync([entry.Id], ct))
            store.Add(new EntryTag(reversal.Id, tag.TagId));

        store.Add(Audit(AuditAction.Voided, entry.Id, before, Snapshot(entry)));
        store.Add(Audit(AuditAction.Posted, reversal.Id, before: null, after: Snapshot(reversal)));
        if (!keepFeedLines)
        {
            foreach (var line in await bank.ListFeedItemsTiedToEntryAsync(entry.Id, ct))
            {
                var lineBefore = Banking.BankFeedService.Snapshot(line);
                line.Reopen();
                store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, Banking.BankFeedService.EntityType, line.Id, lineBefore, Banking.BankFeedService.Snapshot(line)));
            }
        }

        return reversal;
    }

    /// <summary>Classes, the location and tags must exist, be the right kind and be active.</summary>
    internal async Task EnsureSegmentsAsync(IEnumerable<Guid?> classIds, Guid? locationId, IReadOnlyCollection<Guid> tagIds, CancellationToken ct)
    {
        var wanted = classIds.Where(c => c is not null && c != Guid.Empty).Select(c => (c!.Value, SegmentKind.Class))
            .Concat(locationId is { } l ? [(l, SegmentKind.Location)] : [])
            .Concat(tagIds.Select(t => (t, SegmentKind.Tag)))
            .Distinct()
            .ToList();
        if (wanted.Count == 0)
            return;

        var segments = (await store.ListSegmentsAsync(ct)).ToDictionary(s => s.Id);
        foreach (var (id, kind) in wanted)
        {
            var segment = segments.GetValueOrDefault(id) ?? throw new EntityNotFoundException("segment", id);
            if (segment.Kind != kind)
                throw new DomainException("segment.kind_mismatch", $"'{segment.Name}' isn't a {kind.ToString().ToLowerInvariant()}.");
            if (!segment.IsActive && !_carried.Contains(id))
                throw new DomainException("segment.inactive", $"'{segment.Name}' is inactive.");
        }
    }

    /// <summary>A segment row for each line with a class or location, and the entry's tags.</summary>
    private void AddSegments(JournalEntry entry, List<Guid?> classIds, Guid? locationId, IReadOnlyCollection<Guid> tagIds)
    {
        foreach (var line in entry.Lines)
        {
            var classId = classIds[line.LineNumber - 1];
            if (classId is not null && classId != Guid.Empty || locationId is not null)
                store.Add(new LineSegment(line.Id, entry.Id, classId, locationId));
        }

        foreach (var tagId in tagIds)
            store.Add(new EntryTag(entry.Id, tagId));
    }

    public Task<JournalEntry?> GetAsync(Guid entryId, CancellationToken ct = default) => store.GetEntryAsync(entryId, ct);

    public Task<Guid?> FindReversalIdAsync(Guid entryId, CancellationToken ct = default) => store.FindReversalIdAsync(entryId, ct);

    public Task<(IReadOnlyList<JournalEntry> Entries, int Total)> ListAsync(JournalEntryFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Take is < 1 or > 500 || filter.Skip < 0)
            throw new DomainException("journal.page_invalid", "Pages hold 1 to 500 entries.");
        return store.ListEntriesAsync(filter, ct);
    }

    /// <summary>Every entry the filter finds, however many pages that takes, for what has to be whole (its Skip and Take are ignored).</summary>
    public async Task<IReadOnlyList<JournalEntry>> ListAllAsync(JournalEntryFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        const int page = 500;
        var all = new List<JournalEntry>();
        for (var skip = 0; ; skip += page)
        {
            var (entries, total) = await ListAsync(filter with { Skip = skip, Take = page }, ct);
            all.AddRange(entries);
            if (skip + page >= total)
                return all;
        }
    }

    /// <summary>
    /// Refuses changes dated on or before the closing date unless the closing password was given, and
    /// records every override in the audit log.
    /// </summary>
    internal void EnsureOpen(Domain.Companies.Company company, DateOnly date, string what)
    {
        if (!company.IsClosed(date))
            return;

        var closing = company.ClosingDate!.Value;
        if (company.ClosingPasswordHash is null)
            throw new DomainException("period.closed",
                $"The books are closed through {closing:yyyy-MM-dd}. Move the closing date in Settings to change that period.");
        if (string.IsNullOrEmpty(closedPeriod.Password))
            throw new DomainException("period.password_required",
                $"{date:yyyy-MM-dd} is on or before the closing date ({closing:yyyy-MM-dd}). Enter the closing date password to save it anyway.");
        if (hasher.Verify(company.ClosingPasswordHash, closedPeriod.Password) == PasswordCheck.Failed)
            throw new DomainException("period.wrong_password", "That closing date password is incorrect.");

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.ClosedPeriodOverride, "company", company.Id, null,
            JsonSerializer.Serialize(new { ClosingDate = closing, Date = date, Change = what })));
    }

    private async Task EnsurePostableAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var accounts = await store.GetAccountsAsync(accountIds, ct);
        foreach (var id in accountIds)
        {
            if (!accounts.TryGetValue(id, out var account))
                throw new EntityNotFoundException("account", id);
            if (!account.IsActive && !_carried.Contains(id))
                throw new DomainException("journal.account_inactive", $"Account '{account.Name}' is inactive.");
        }
    }

    private AuditRecord Audit(AuditAction action, Guid entityId, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, entityId, before, after);

    private static string Snapshot(JournalEntry e) => JsonSerializer.Serialize(new
    {
        e.Number,
        e.Date,
        Currency = e.Currency.Code,
        e.Memo,
        Kind = e.Kind.ToString(),
        Status = e.Status.ToString(),
        Basis = e.Basis.ToString(),
        e.Source,
        e.SourceId,
        e.ReversesEntryId,
        e.VoidReason,
        Lines = e.Lines.Select(l => new { l.LineNumber, l.AccountId, l.Debit, l.Credit, l.Memo }),
    });
}
