using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Import;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;

namespace OpenBook.Application.Reporting;

/// <summary>Where a year's request list stands.</summary>
public sealed record AuditRequestSummary(int Total, int Open, int Overdue, int Received, int NotNeeded);

/// <summary>
/// The "prepared by client" (PBC) list for an audit or review (C306): what the auditor asked for, who prepares each item,
/// when it's due and when it went over. Typed in, imported from the auditor's spreadsheet, or copied from last year's.
/// </summary>
public sealed class AuditRequestService(IReportingStore store, IClock clock, ICurrentUser user)
{
    public const string EntityType = "audit_request";
    public const int MaxImportRows = 1000;

    private DateOnly Today => CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);

    public async Task<IReadOnlyList<AuditRequest>> ListAsync(int year, CancellationToken ct = default) =>
        [.. (await store.ListAuditRequestsAsync(year, ct))
            .OrderBy(r => r.Status == AuditRequestStatus.Open ? 0 : 1)
            .ThenBy(r => r.DueOn ?? DateOnly.MaxValue)
            .ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The years that have a list, newest first.</summary>
    public async Task<IReadOnlyList<int>> YearsAsync(CancellationToken ct = default) =>
        [.. (await store.ListAuditRequestsAsync(null, ct)).Select(r => r.Year).Distinct().OrderDescending()];

    public AuditRequestSummary Summarize(IReadOnlyCollection<AuditRequest> requests)
    {
        var today = Today;
        return new(requests.Count, requests.Count(r => r.Status == AuditRequestStatus.Open), requests.Count(r => r.IsOverdue(today)),
            requests.Count(r => r.Status == AuditRequestStatus.Received), requests.Count(r => r.Status == AuditRequestStatus.NotNeeded));
    }

    public async Task<AuditRequest> CreateAsync(AuditRequestDetails details, CancellationToken ct = default)
    {
        var request = AuditRequest.Create(details, clock.UtcNow);
        store.Add(request);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, request.Id, null, Snapshot(request)));
        await store.SaveChangesAsync(ct);
        return request;
    }

    public async Task<AuditRequest> UpdateAsync(Guid id, AuditRequestDetails details, AuditRequestStatus status, CancellationToken ct = default)
    {
        var request = await RequiredAsync(id, ct);
        var before = Snapshot(request);
        request.Update(details with { Year = request.Year });
        request.SetStatus(status, Today);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, request.Id, before, Snapshot(request)));
        await store.SaveChangesAsync(ct);
        return request;
    }

    public async Task<AuditRequest> SetStatusAsync(Guid id, AuditRequestStatus status, CancellationToken ct = default)
    {
        var request = await RequiredAsync(id, ct);
        var before = Snapshot(request);
        request.SetStatus(status, Today);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, request.Id, before, Snapshot(request)));
        await store.SaveChangesAsync(ct);
        return request;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var request = await RequiredAsync(id, ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, request.Id, Snapshot(request), null));
        store.Remove(request);
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Starts <paramref name="toYear"/>'s list from <paramref name="fromYear"/>'s: every item open again, due dates moved
    /// on by the years between, notes kept. Items already on the new list (by name) are left out. Returns how many came.
    /// </summary>
    public async Task<int> CopyYearAsync(int fromYear, int toYear, CancellationToken ct = default)
    {
        if (fromYear == toYear)
            throw new DomainException("audit_request.same_year", "Choose a different year to copy from.");
        var source = await store.ListAuditRequestsAsync(fromYear, ct);
        if (source.Count == 0)
            throw new DomainException("audit_request.nothing_to_copy", $"{fromYear} has no requests to copy.");
        var taken = (await store.ListAuditRequestsAsync(toYear, ct)).Select(r => r.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copies = new List<AuditRequest>();
        foreach (var r in source.OrderBy(r => r.DueOn ?? DateOnly.MaxValue).ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase))
        {
            if (!taken.Add(r.Item))
                continue;
            copies.Add(AuditRequest.Create(new AuditRequestDetails(toYear, r.Item, r.Owner, r.DueOn?.AddYears(toYear - fromYear), r.Notes), clock.UtcNow));
        }

        return await AddAllAsync(copies, ct);
    }

    /// <summary>
    /// Reads the auditor's list from a CSV: an Item (or Request, Description) column, and Owner (Assigned to, Preparer),
    /// Due (Due date) and Notes (Comments) if there are. All or nothing; items already on the year's list are skipped.
    /// </summary>
    public async Task<int> ImportCsvAsync(int year, string csv, CancellationToken ct = default) =>
        await AddAllAsync(Read(year, csv, (await store.ListAuditRequestsAsync(year, ct)).Select(r => r.Item), clock.UtcNow), ct);

    /// <summary>The CSV reader on its own (fuzzed): the new requests, or a <see cref="DomainException"/>.</summary>
    public static IReadOnlyList<AuditRequest> Read(int year, string csv, IEnumerable<string> existing, DateTimeOffset now)
    {
        var records = Csv.Parse(csv ?? "");
        if (records.Count < 2)
            throw new DomainException("import.empty", "The file needs a header row and at least one request.");
        if (records.Count - 1 > MaxImportRows)
            throw new DomainException("import.too_many_rows", $"A request list can have at most {MaxImportRows} rows.");
        var cols = new CsvColumns(records[0]);
        var itemColumn = cols.Find("item", "request", "requesteditem", "description", "document", "documents")
            ?? throw new DomainException("import.no_item_column", "The file needs an Item (or Request) column.");
        var ownerColumn = cols.Find("owner", "assignedto", "preparer", "preparedby", "responsible", "who");
        var dueColumn = cols.Find("due", "duedate", "dueon", "date", "needed", "neededby");
        var notesColumn = cols.Find("notes", "note", "comments", "comment");
        var rows = records.Skip(1).ToList();
        var order = StatementValues.DetectDateOrder([.. rows.Select(r => CsvColumns.Field(r, dueColumn)).OfType<string>()], CultureInfo.CurrentCulture, out _);
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requests = new List<AuditRequest>();
        var line = 1;
        foreach (var r in rows)
        {
            line++;
            var item = CsvColumns.Field(r, itemColumn)?.Trim();
            if (string.IsNullOrEmpty(item))
                continue;
            if (!taken.Add(item))
                continue;
            var dueText = CsvColumns.Field(r, dueColumn);
            DateOnly? due = null;
            if (dueText is not null)
            {
                if (!StatementValues.TryParseDate(dueText, order, out var d))
                    throw new DomainException("import.bad_date", $"Line {line}: \"{Clip(dueText)}\" isn't a date.");
                due = d;
            }

            try
            {
                requests.Add(AuditRequest.Create(new AuditRequestDetails(year, item, CsvColumns.Field(r, ownerColumn), due, CsvColumns.Field(r, notesColumn)), now));
            }
            catch (DomainException ex)
            {
                throw new DomainException(ex.Code, $"Line {line}: {ex.Message}");
            }
        }

        return requests.Count > 0 ? requests : throw new DomainException("import.nothing_new", "The file has no requests that aren't on the list already.");
    }

    /// <summary>A year's list as CSV, to send back to the auditor.</summary>
    public async Task<string> CsvAsync(int year, CancellationToken ct = default)
    {
        var today = Today;
        var csv = new System.Text.StringBuilder("Item,Owner,Due,Status,Done on,Notes\r\n");
        foreach (var r in await ListAsync(year, ct))
        {
            var status = r.Status switch
            {
                AuditRequestStatus.Received => "Received",
                AuditRequestStatus.NotNeeded => "Not needed",
                _ => r.IsOverdue(today) ? "Overdue" : "Open",
            };
            csv.Append(string.Join(',', new[]
            {
                r.Item, r.Owner ?? "", r.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", status,
                r.DoneOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", r.Notes ?? "",
            }.Select(ReportCsv.Quote))).Append("\r\n");
        }

        return csv.ToString();
    }

    private async Task<int> AddAllAsync(IReadOnlyList<AuditRequest> requests, CancellationToken ct)
    {
        foreach (var request in requests)
        {
            store.Add(request);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, request.Id, null, Snapshot(request)));
        }

        await store.SaveChangesAsync(ct);
        return requests.Count;
    }

    private async Task<AuditRequest> RequiredAsync(Guid id, CancellationToken ct) =>
        await store.GetAuditRequestAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "…";

    private static string Snapshot(AuditRequest r) => JsonSerializer.Serialize(new
    {
        r.Year,
        r.Item,
        r.Owner,
        r.DueOn,
        Status = r.Status.ToString(),
        r.DoneOn,
        r.Notes,
    });
}
