using System.Text.Json;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Segments;

/// <summary>A segment with its "Parent:Child" name, for lists and pickers.</summary>
public sealed record SegmentChoice(Segment Segment, string FullName, int Depth);

public sealed record SegmentTracking(bool Classes, bool Locations, string LocationLabel);

/// <summary>
/// Changes the class and/or location of posted lines. <see cref="SetClass"/>/<see cref="SetLocation"/> say which
/// to change; a null id clears it. Lines of voided entries change on their reversal too.
/// </summary>
public sealed record ReclassifyCommand(IReadOnlyCollection<Guid> LineIds, bool SetClass, Guid? ClassId, bool SetLocation, Guid? LocationId);

/// <summary>A posted line as the reclassify page lists it.</summary>
public sealed record ReclassifyLine(LedgerLine Line, string Account, string? Class, string? Location, bool Voided);

/// <summary>
/// Classes, locations (departments) and tags: their lists, whether forms show them, tags on posted entries, and
/// reclassifying posted lines. Segments live beside the ledger (<see cref="LineSegment"/>, <see cref="EntryTag"/>),
/// so changing them never touches a posted line.
/// </summary>
public sealed class SegmentService(
    ILedgerStore ledger,
    ICompanyStore companies,
    ISalesStore sales,
    IPurchasesStore purchases,
    IBankingStore banking,
    Nonprofit.INonprofitStore nonprofit,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    public const string EntityType = "segment";
    private const string EntryEntity = "journal_entry";

    /// <summary>In tree order (each parent followed by its children, by name).</summary>
    public async Task<IReadOnlyList<SegmentChoice>> ListAsync(SegmentKind kind, bool includeInactive = true, CancellationToken ct = default)
    {
        var all = await ledger.ListSegmentsAsync(ct);
        var names = SegmentRules.FullNames(all);
        var ofKind = all.Where(s => s.Kind == kind).ToList();
        var result = new List<SegmentChoice>();
        void Walk(Guid? parentId, int depth)
        {
            foreach (var s in ofKind.Where(s => s.ParentId == parentId).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (includeInactive || s.IsActive)
                    result.Add(new SegmentChoice(s, names[s.Id], depth));
                Walk(s.Id, depth + 1);
            }
        }

        Walk(null, 0);
        return result;
    }

    /// <summary>Every segment's full name, for reports and pages.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(CancellationToken ct = default) =>
        SegmentRules.FullNames(await ledger.ListSegmentsAsync(ct));

    public async Task<Segment?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ledger.ListSegmentsAsync(ct)).FirstOrDefault(s => s.Id == id);

    public async Task<Segment> CreateAsync(SegmentKind kind, string name, Guid? parentId = null, CancellationToken ct = default)
    {
        var segment = Segment.Create(kind, name, parentId);
        var all = await ledger.ListSegmentsAsync(ct);
        EnsureParentActive(all, parentId);
        SegmentRules.Validate(segment, all);
        ledger.Add(segment);
        ledger.Add(Audit(AuditAction.Created, EntityType, segment.Id, null, Snapshot(segment)));
        await ledger.SaveChangesAsync(ct);
        return segment;
    }

    public async Task<Segment> UpdateAsync(Guid id, string name, Guid? parentId, CancellationToken ct = default)
    {
        var all = await ledger.ListSegmentsAsync(ct);
        var segment = all.FirstOrDefault(s => s.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(segment);
        if (parentId != segment.ParentId)
            EnsureParentActive(all, parentId);
        segment.Rename(name);
        segment.Move(parentId);
        SegmentRules.Validate(segment, all);
        ledger.Add(Audit(AuditAction.Updated, EntityType, id, before, Snapshot(segment)));
        await ledger.SaveChangesAsync(ct);
        return segment;
    }

    private static void EnsureParentActive(IEnumerable<Segment> all, Guid? parentId)
    {
        if (parentId is { } id && all.FirstOrDefault(s => s.Id == id) is { IsActive: false } parent)
            throw new DomainException("segment.parent_inactive", $"'{parent.Name}' is inactive, so nothing new can go under it.");
    }

    /// <summary>Nonprofits: what a class's expenses count as (program, management and general, fundraising); null takes the parent's.</summary>
    public async Task<Segment> SetFunctionalAreaAsync(Guid id, FunctionalArea? area, CancellationToken ct = default)
    {
        var segment = (await ledger.ListSegmentsAsync(ct)).FirstOrDefault(s => s.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        if (segment.FunctionalArea == area)
            return segment;
        var before = Snapshot(segment);
        segment.SetFunctionalArea(area);
        ledger.Add(Audit(AuditAction.Updated, EntityType, id, before, Snapshot(segment)));
        await ledger.SaveChangesAsync(ct);
        return segment;
    }

    /// <summary>Inactive segments can't be put on new transactions; reports still show them.</summary>
    public async Task<Segment> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var all = await ledger.ListSegmentsAsync(ct);
        var segment = all.FirstOrDefault(s => s.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        if (segment.IsActive == active)
            return segment;
        if (!active && all.Any(s => s.ParentId == id && s.IsActive))
            throw new DomainException("segment.has_active_children", $"Make the segments under '{segment.Name}' inactive first.");
        if (active && segment.ParentId is { } parentId && all.First(s => s.Id == parentId) is { IsActive: false } parent)
            throw new DomainException("segment.parent_inactive", $"Make '{parent.Name}' active first.");
        var before = Snapshot(segment);
        if (active)
            segment.Reactivate();
        else
            segment.Deactivate();
        ledger.Add(Audit(AuditAction.Updated, EntityType, id, before, Snapshot(segment)));
        await ledger.SaveChangesAsync(ct);
        return segment;
    }

    public async Task<SegmentTracking> GetTrackingAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        return new SegmentTracking(company.TrackClasses, company.TrackLocations, company.LocationLabel);
    }

    public async Task<SegmentTracking> SetTrackingAsync(SegmentTracking tracking, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var before = JsonSerializer.Serialize(new { company.TrackClasses, company.TrackLocations, company.LocationLabel });
        company.SetSegmentTracking(tracking.Classes, tracking.Locations, tracking.LocationLabel);
        var after = JsonSerializer.Serialize(new { company.TrackClasses, company.TrackLocations, company.LocationLabel });
        if (before != after)
        {
            ledger.Add(Audit(AuditAction.Updated, "company", company.Id, before, after));
            await ledger.SaveChangesAsync(ct);
        }

        return new SegmentTracking(company.TrackClasses, company.TrackLocations, company.LocationLabel);
    }

    public async Task<IReadOnlyList<Guid>> GetTagsAsync(Guid entryId, CancellationToken ct = default) =>
        (await ledger.GetEntryTagsAsync([entryId], ct)).Select(t => t.TagId).ToList();

    /// <summary>
    /// Replaces an entry's tags (tags are labels, so posted entries can change them). A voided entry's reversal
    /// gets the same tags. Inactive tags already on the entry can stay; new ones must be active.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SetTagsAsync(Guid entryId, IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        var entry = await ledger.GetEntryAsync(entryId, ct) ?? throw new EntityNotFoundException(EntryEntity, entryId);
        if (entry.Kind == JournalEntryKind.Reversal)
            throw new DomainException("segment.reversal", "Change the tags on the original entry; its reversal follows.");
        var wanted = tagIds.Where(t => t != Guid.Empty).Distinct().ToList();
        var segments = (await ledger.ListSegmentsAsync(ct)).ToDictionary(s => s.Id);
        var current = await ledger.GetEntryTagsAsync([entryId], ct);
        foreach (var id in wanted.Where(id => current.All(t => t.TagId != id)))
        {
            var tag = segments.GetValueOrDefault(id) ?? throw new EntityNotFoundException(EntityType, id);
            if (tag.Kind != SegmentKind.Tag)
                throw new DomainException("segment.kind_mismatch", $"'{tag.Name}' isn't a tag.");
            if (!tag.IsActive)
                throw new DomainException("segment.inactive", $"'{tag.Name}' is inactive.");
        }

        var before = current.Select(t => t.TagId).Order().ToList();
        if (before.SequenceEqual(wanted.Order()))
            return wanted;

        var entryIds = new List<Guid> { entryId };
        if (await ledger.FindReversalIdAsync(entryId, ct) is { } reversalId)
            entryIds.Add(reversalId);
        if (await engine.StandingAutoReversalAsync(entryId, ct) is { } mirror)
            entryIds.Add(mirror.Id);
        foreach (var id in entryIds)
        {
            foreach (var tag in await ledger.GetEntryTagsAsync([id], ct))
                ledger.Remove(tag);
            foreach (var tagId in wanted)
                ledger.Add(new EntryTag(id, tagId));
        }

        ledger.Add(Audit(AuditAction.Updated, EntryEntity, entryId,
            JsonSerializer.Serialize(new { Tags = before }), JsonSerializer.Serialize(new { Tags = wanted.Order().ToList() })));
        await ledger.SaveChangesAsync(ct);
        return wanted;
    }

    /// <summary>Posted lines for the reclassify page: in the period, optionally one account, class or location ("Not specified" = <see cref="Guid.Empty"/>). Reversals are left out.</summary>
    public async Task<IReadOnlyList<ReclassifyLine>> ListLinesAsync(
        DateOnly from, DateOnly to, Guid? accountId, Guid? classId, Guid? locationId, CancellationToken ct = default)
    {
        var lines = await ledger.GetLinesAsync(from, to, ct);
        var entries = (await ledger.ListEntriesAsync(new JournalEntryFilter(from, to, 0, int.MaxValue), ct)).Entries.ToDictionary(e => e.Id);
        var accounts = await ledger.ListAccountsAsync(ct);
        var chart = new Domain.Accounts.ChartOfAccounts(accounts);
        var names = SegmentRules.FullNames(await ledger.ListSegmentsAsync(ct));
        return lines
            .Where(l => entries.TryGetValue(l.JournalEntryId, out var e) && e.Kind != JournalEntryKind.Reversal)
            .Where(l => accountId is null || l.AccountId == accountId)
            .Where(l => classId is null || (classId == Guid.Empty ? l.ClassId is null : l.ClassId == classId))
            .Where(l => locationId is null || (locationId == Guid.Empty ? l.LocationId is null : l.LocationId == locationId))
            .Select(l => new ReclassifyLine(l, chart.FullName(l.AccountId),
                l.ClassId is { } c ? names.GetValueOrDefault(c) : null,
                l.LocationId is { } loc ? names.GetValueOrDefault(loc) : null,
                entries[l.JournalEntryId].Status == JournalEntryStatus.Voided))
            .ToList();
    }

    /// <summary>
    /// Sets the class and/or location of posted lines; the ledger itself doesn't change. Lines dated in a closed
    /// period need the closing password. Documents follow, so cash-basis reports agree: an invoice, credit memo or
    /// receipt line's class changes with its income line, a bill or vendor credit line with its category line, and
    /// a document's location applies to its whole entry. Returns how many lines changed.
    /// </summary>
    public async Task<int> ReclassifyAsync(ReclassifyCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.SetClass && !command.SetLocation)
            throw new DomainException("segment.nothing_to_change", "Choose a class or location to set.");
        var lineIds = command.LineIds.Distinct().ToHashSet();
        if (lineIds.Count == 0)
            throw new DomainException("segment.no_lines", "Choose the lines to reclassify.");

        var segments = (await ledger.ListSegmentsAsync(ct)).ToDictionary(s => s.Id);
        var classId = command.ClassId == Guid.Empty ? null : command.ClassId;
        var locationId = command.LocationId == Guid.Empty ? null : command.LocationId;
        if (command.SetClass && classId is { } c)
            EnsureUsable(segments, c, SegmentKind.Class);
        if (command.SetLocation && locationId is { } l)
            EnsureUsable(segments, l, SegmentKind.Location);

        var company = await companies.GetRequiredCompanyAsync(ct);
        var chosen = (await ledger.GetLinesByIdAsync(lineIds, ct)).ToList();
        if (chosen.Count != lineIds.Count)
            throw new EntityNotFoundException("journal_line", lineIds.First(id => chosen.All(x => x.JournalLineId != id)));

        var changed = 0;
        foreach (var group in chosen.GroupBy(x => x.JournalEntryId))
        {
            var entry = await ledger.GetEntryAsync(group.Key, ct) ?? throw new EntityNotFoundException(EntryEntity, group.Key);
            if (entry.Kind == JournalEntryKind.Reversal)
                throw new DomainException("segment.reversal", $"Entry #{entry.Number} reverses another entry; reclassify the original and its reversal follows.");
            engine.EnsureOpen(company, entry.Date, $"reclassifying entry #{entry.Number}");

            var reversalId = await ledger.FindReversalIdAsync(entry.Id, ct);
            var reversal = reversalId is { } rid ? await ledger.GetEntryAsync(rid, ct) : null;

            // An automatic reversal is the entry undone on a later date: it follows too, or the two would land in different classes.
            if (await engine.StandingAutoReversalAsync(entry.Id, ct) is { } standing)
            {
                engine.EnsureOpen(company, standing.Date, $"reclassifying entry #{standing.Number}");
                reversal = await ledger.GetEntryAsync(standing.Id, ct);
            }

            var rows = (await ledger.GetLineSegmentsAsync(reversal is null ? [entry.Id] : [entry.Id, reversal.Id], ct)).ToDictionary(r => r.JournalLineId);
            var before = Describe(entry, rows, segments);

            // A document's location is the whole transaction's.
            var targets = command.SetLocation && entry.Source != JournalEntry.ManualSource && !JournalEntry.IsJournalSource(entry.Source)
                ? entry.Lines.ToList()
                : entry.Lines.Where(x => group.Any(g => g.JournalLineId == x.Id)).ToList();
            var classLines = group.Select(g => g.JournalLineId).ToHashSet();
            // A voided entry's document has moved on to its replacement, which this line doesn't describe.
            var document = entry.Status == JournalEntryStatus.Posted ? await DocumentAsync(entry, ct) : null;
            foreach (var line in targets)
            {
                var row = rows.GetValueOrDefault(line.Id);
                var oldClass = row?.ClassId;
                var newClass = command.SetClass && classLines.Contains(line.Id) ? classId : oldClass;
                var newLocation = command.SetLocation ? locationId : row?.LocationId;
                if (newClass == oldClass && newLocation == row?.LocationId)
                    continue;

                Set(rows, line.Id, entry.Id, newClass, newLocation);
                if (reversal?.Lines.FirstOrDefault(r => r.LineNumber == line.LineNumber) is { } mirror)
                    Set(rows, mirror.Id, reversal.Id, newClass, newLocation);
                if (newClass != oldClass)
                    document?.SetClass(line, oldClass, newClass);
                changed++;
            }

            if (command.SetLocation)
                document?.SetLocation(locationId);
            ledger.Add(Audit(AuditAction.Updated, EntryEntity, entry.Id, before, Describe(entry, rows, segments)));
        }

        await ledger.SaveChangesAsync(ct);
        return changed;
    }

    private void Set(Dictionary<Guid, LineSegment> rows, Guid lineId, Guid entryId, Guid? classId, Guid? locationId)
    {
        if (rows.TryGetValue(lineId, out var row))
        {
            row.Set(classId, locationId);
            if (row.IsEmpty)
            {
                ledger.Remove(row);
                rows.Remove(lineId);
            }
        }
        else if (classId is not null || locationId is not null)
        {
            row = new LineSegment(lineId, entryId, classId, locationId);
            ledger.Add(row);
            rows[lineId] = row;
        }
    }

    private static void EnsureUsable(Dictionary<Guid, Segment> segments, Guid id, SegmentKind kind)
    {
        var segment = segments.GetValueOrDefault(id) ?? throw new EntityNotFoundException(EntityType, id);
        if (segment.Kind != kind)
            throw new DomainException("segment.kind_mismatch", $"'{segment.Name}' isn't a {kind.ToString().ToLowerInvariant()}.");
        if (!segment.IsActive)
            throw new DomainException("segment.inactive", $"'{segment.Name}' is inactive.");
    }

    /// <summary>One field per line, by name: the audit log shows the fields that changed, so only the lines moved show.</summary>
    private static string Describe(JournalEntry entry, Dictionary<Guid, LineSegment> rows, Dictionary<Guid, Segment> segments)
    {
        string Name(Guid? id) => id is { } known && segments.TryGetValue(known, out var segment) ? segment.Name : "none";
        return JsonSerializer.Serialize(entry.Lines.OrderBy(l => l.LineNumber).ToDictionary(
            l => $"Line {l.LineNumber}",
            l => rows.GetValueOrDefault(l.Id) is { } row ? $"Class {Name(row.ClassId)}, location {Name(row.LocationId)}" : "Class none, location none"));
    }

    /// <summary>The document that posted the entry, when its lines or location record segments too.</summary>
    private async Task<DocumentSegments?> DocumentAsync(JournalEntry entry, CancellationToken ct)
    {
        if (entry.SourceId is not { } id)
            return null;
        switch (entry.Source)
        {
            case Invoice.Source when await sales.GetInvoiceAsync(id, ct) is { } invoice:
                return DocumentSegments.ForSales(invoice.Lines, invoice.SetLocation);
            case CreditMemo.Source when await sales.GetCreditMemoAsync(id, ct) is { } credit:
                return DocumentSegments.ForSales(credit.Lines, credit.SetLocation);
            case SalesReceipt.Source when await sales.GetReceiptAsync(id, ct) is { } receipt:
                return DocumentSegments.ForSales(receipt.Lines, receipt.SetLocation);
            case Bill.Source when await purchases.GetBillAsync(id, ct) is { } bill:
                return DocumentSegments.ForBill(bill.Lines, bill.SetLocation);
            case VendorCredit.Source when await purchases.GetVendorCreditAsync(id, ct) is { } vendorCredit:
                return DocumentSegments.ForBill(vendorCredit.Lines, vendorCredit.SetLocation);
            case Domain.Nonprofit.Donation.Source when await nonprofit.GetDonationAsync(id, ct) is { } gift:
                return new DocumentSegments((line, _, newClass) =>
                {
                    if (line.AccountId == gift.IncomeAccountId)
                        gift.SetClass(newClass);
                }, _ => { });
            case Domain.Nonprofit.Pledge.Source when await nonprofit.GetPledgeAsync(id, ct) is { } pledge:
                return new DocumentSegments((line, _, newClass) =>
                {
                    if (line.AccountId == pledge.IncomeAccountId)
                        pledge.SetClass(newClass);
                }, _ => { });
            default:
                if (entry.Source.StartsWith("bank.", StringComparison.Ordinal) && await banking.GetTransactionAsync(id, ct) is { } transaction)
                    return new DocumentSegments((_, _, _) => { }, transaction.SetLocation);
                return null;
        }
    }

    /// <summary>How a ledger line's class change maps back to the document's lines.</summary>
    private sealed record DocumentSegments(Action<JournalLine, Guid?, Guid?> SetClassOf, Action<Guid?> SetLocation)
    {
        public void SetClass(JournalLine line, Guid? oldClass, Guid? newClass) => SetClassOf(line, oldClass, newClass);

        /// <summary>Sales postings are one line per income account and class: every document line in that group moves.</summary>
        public static DocumentSegments ForSales(IReadOnlyList<SalesLine> lines, Action<Guid?> setLocation) => new(
            (line, oldClass, newClass) =>
            {
                foreach (var l in lines.Where(l => l.IncomeAccountId == line.AccountId && l.ClassId == oldClass))
                    l.SetClass(newClass);
            },
            setLocation);

        /// <summary>Bill postings are payables first, then one line per bill line in order.</summary>
        public static DocumentSegments ForBill(IReadOnlyList<BillLine> lines, Action<Guid?> setLocation) => new(
            (line, _, newClass) => lines.FirstOrDefault(l => l.LineNumber == line.LineNumber - 1)?.SetClass(newClass),
            setLocation);
    }

    private AuditRecord Audit(AuditAction action, string entityType, Guid id, string? before, string after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, entityType, id, before, after);

    private static string Snapshot(Segment s) => JsonSerializer.Serialize(new { Kind = s.Kind.ToString(), s.Name, s.ParentId, s.IsActive, FunctionalArea = s.FunctionalArea?.ToString() });
}
