using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;

namespace OpenBook.Application.Reporting;

/// <summary>Reports kept under a name with their settings. Saving under an existing name updates it.</summary>
public sealed class SavedReportService(IReportingStore store, ReportCatalog catalog, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "saved_report";

    public async Task<IReadOnlyList<SavedReport>> ListAsync(CancellationToken ct = default) =>
        (await store.ListSavedReportsAsync(ct)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<SavedReport> SaveAsync(string name, string reportKey, string? query, CancellationToken ct = default)
    {
        if (catalog.Find(reportKey) is null)
            throw new DomainException("report.unknown", "Unknown report.");
        var normalized = SavedReport.NormalizeName(name);
        query = WithoutToday(query);
        var existing = (await store.ListSavedReportsAsync(ct)).FirstOrDefault(r => string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.ReportKey != reportKey)
                throw new DomainException("report.name_taken", $"\"{existing.Name}\" is already saved for another report. Choose another name.");
            var report = await store.GetSavedReportAsync(existing.Id, ct) ?? throw new EntityNotFoundException(EntityType, existing.Id);
            await EnsureNotScheduledAsync(report, ct);
            var before = Snapshot(report);
            report.SetQuery(query);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, report.Id, before, Snapshot(report)));
            await store.SaveChangesAsync(ct);
            return report;
        }

        var created = SavedReport.Create(normalized, reportKey, query, clock.UtcNow, user.Actor);
        store.Add(created);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, created.Id, null, Snapshot(created)));
        await store.SaveChangesAsync(ct);
        return created;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var report = await store.GetSavedReportAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await EnsureNotScheduledAsync(report, ct);
        foreach (var schedule in (await store.ListSubscriptionsAsync(ct)).Where(s => s.SavedReportId == report.Id))
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, ReportSubscriptionService.EntityType, schedule.Id, null, null));
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, report.Id, Snapshot(report), null));
        store.Remove(report);
        await store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A saved report on a schedule goes out to its recipients, and anyone may save reports, even view-only: only whoever
    /// saved it can change what's sent or stop it.
    /// </summary>
    private async Task EnsureNotScheduledAsync(SavedReport report, CancellationToken ct)
    {
        if (!string.Equals(report.CreatedBy, user.Actor, StringComparison.Ordinal) && (await store.ListSubscriptionsAsync(ct)).Any(s => s.SavedReportId == report.Id))
            throw new DomainException("report.scheduled", $"\"{report.Name}\" is sent on a schedule, so only {report.CreatedBy} can change it. Save yours under another name.");
    }

    /// <summary>
    /// "As of today" stays today: a date equal to today's is left out of what's saved, or a report sent every week would be
    /// the one from the day it was saved.
    /// </summary>
    private static string? WithoutToday(string? query) =>
        query is null ? null : string.Join('&', query.Split('&').Where(p => p != $"asOf={CompanyTime.Today:yyyy-MM-dd}"));

    private static string Snapshot(SavedReport r) => JsonSerializer.Serialize(new { r.Name, Report = r.ReportKey, r.Query });
}
