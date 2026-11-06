using OpenBook.Application.Companies;
using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;

namespace OpenBook.Application.Reporting;

/// <summary>The outcome of one subscription's send.</summary>
public sealed record SubscriptionRun(ReportSubscription Subscription, string ReportName, string? Error);

/// <summary>
/// Saved reports emailed on a schedule as PDF, Excel or CSV. The hourly background job calls
/// <see cref="SendDueAsync"/>; each send is recorded on the subscription and audited as <see cref="AuditAction.Emailed"/>.
/// </summary>
public sealed class ReportSubscriptionService(
    IReportingStore store, ReportCatalog catalog, IReportExporter exporter, EmailService email, Companies.ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string EntityType = "report_subscription";

    public async Task<IReadOnlyList<ReportSubscription>> ListAsync(CancellationToken ct = default) =>
        (await store.ListSubscriptionsAsync(ct)).OrderBy(s => s.CreatedAt).ToList();

    public Task<ReportSubscription?> GetAsync(Guid id, CancellationToken ct = default) => store.GetSubscriptionAsync(id, ct);

    public async Task<ReportSubscription> CreateAsync(ReportSubscriptionDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await SavedReportAsync(details.SavedReportId, ct);
        var subscription = ReportSubscription.Create(details, user.Actor, clock.UtcNow);
        store.Add(subscription);
        store.Add(Audit(AuditAction.Created, subscription, null));
        await store.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task<ReportSubscription> UpdateAsync(Guid id, ReportSubscriptionDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var subscription = await store.GetSubscriptionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await SavedReportAsync(details.SavedReportId, ct);
        var before = Snapshot(subscription);
        subscription.Update(details);
        store.Add(Audit(AuditAction.Updated, subscription, before));
        await store.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var subscription = await store.GetSubscriptionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        store.Remove(subscription);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, subscription.Id, Snapshot(subscription), null));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Sends one now, whatever its schedule; the error, if any, is returned and kept on the subscription.</summary>
    public async Task<SubscriptionRun> SendNowAsync(Guid id, CancellationToken ct = default)
    {
        var subscription = await store.GetSubscriptionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        return await SendAsync(subscription, ct);
    }

    /// <summary>Sends every active subscription that's due, each saved on its own so one failure doesn't stop the rest.</summary>
    public async Task<IReadOnlyList<SubscriptionRun>> SendDueAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var runs = new List<SubscriptionRun>();
        foreach (var subscription in (await store.ListSubscriptionsAsync(ct)).Where(s => s.IsDue(now, CompanyTime.Zone)).ToList())
            runs.Add(await SendAsync(subscription, ct));
        return runs;
    }

    /// <summary>A report rendered as the file a subscription sends.</summary>
    public async Task<EmailAttachment> RenderAsync(SavedReport saved, ReportFileFormat format, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var table = await catalog.RunAsync(saved.ReportKey, ReportParameters.Parse(saved.Query), ct);
        var name = FileName(saved.Name);
        return format switch
        {
            ReportFileFormat.Excel => new EmailAttachment($"{name}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exporter.ToXlsx(table)),
            ReportFileFormat.Csv => new EmailAttachment($"{name}.csv", "text/csv", ReportCsv.Write(table)),
            _ => new EmailAttachment($"{name}.pdf", "application/pdf", exporter.ToPdf(table)),
        };
    }

    private async Task<SubscriptionRun> SendAsync(ReportSubscription subscription, CancellationToken ct)
    {
        using var region = CompanyRegion.Use(await companies.GetRequiredCompanyAsync(ct));
        var saved = await store.GetSavedReportAsync(subscription.SavedReportId, ct);
        var reportName = saved?.Name ?? "(deleted report)";
        string? error = null;
        var recipients = subscription.RecipientsDue;
        var notReached = new List<string>();
        try
        {
            if (saved is null)
                throw new DomainException("subscription.report_missing", "The saved report no longer exists.");
            var file = await RenderAsync(saved, subscription.Format, ct);
            var company = (await companies.GetRequiredCompanyAsync(ct)).Name;
            foreach (var to in recipients)
            {
                // One bad address doesn't stop the others; whoever it didn't reach is tried again.
                try
                {
                    await email.SendWordedAsync(Domain.Email.EmailKind.Report, to, new Dictionary<string, string>
                    {
                        ["report"] = saved.Name, ["as_of"] = CompanyTime.Today.ToString("d", System.Globalization.CultureInfo.CurrentCulture),
                        ["scheduled_by"] = subscription.CreatedBy, ["company"] = company,
                    }, [file], null, EntityType, subscription.Id, ct);
                }
                catch (DomainException ex)
                {
                    notReached.Add(to);
                    error = $"{to}: {ex.Message}";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything, an add-in's report included: the ones after it still go, and this one waits for its next slot.
            error = ex.Message;
        }

        subscription.RecordRun(clock.UtcNow, error, notReached);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, EntityType, subscription.Id, null,
            JsonSerializer.Serialize(new { To = string.Join(", ", recipients.Except(notReached)), NotReached = notReached, Report = reportName, Error = error })));
        await store.SaveChangesAsync(ct);
        return new SubscriptionRun(subscription, reportName, error);
    }

    private async Task<SavedReport> SavedReportAsync(Guid id, CancellationToken ct) =>
        await store.GetSavedReportAsync(id, ct) ?? throw new EntityNotFoundException("saved_report", id);

    private static string FileName(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').ToArray()).Trim();
        return safe.Length == 0 ? "report" : safe;
    }

    private AuditRecord Audit(AuditAction action, ReportSubscription s, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, s.Id, before, Snapshot(s));

    private static string Snapshot(ReportSubscription s) => JsonSerializer.Serialize(new
    {
        s.SavedReportId,
        s.Recipients,
        Frequency = s.Frequency.ToString(),
        s.Day,
        s.Hour,
        Format = s.Format.ToString(),
        s.IsActive,
    });
}

/// <summary>Which dashboard panels each user hides.</summary>
public sealed class DashboardService(IReportingStore store, ICurrentUser user)
{
    public async Task<IReadOnlySet<string>> HiddenAsync(CancellationToken ct = default) =>
        (await store.GetDashboardPreferenceAsync(user.Actor, ct))?.Hidden ?? new HashSet<string>();

    public async Task SetHiddenAsync(IEnumerable<string> panels, CancellationToken ct = default)
    {
        var preference = await store.GetDashboardPreferenceAsync(user.Actor, ct);
        if (preference is null)
        {
            preference = DashboardPreference.Create(user.Actor);
            store.Add(preference);
        }

        preference.Hide(panels);
        await store.SaveChangesAsync(ct);
    }
}
