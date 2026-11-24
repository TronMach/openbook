using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfCommissionStore(OpenBookDbContext db) : ICommissionStore
{
    public Task<CommissionSettings?> GetSettingsAsync(CancellationToken ct) => db.CommissionSettings.SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CommissionRate>> ListRatesAsync(CancellationToken ct) => await db.CommissionRates.ToListAsync(ct);

    public async Task<IReadOnlyList<CommissionRun>> ListRunsAsync(CancellationToken ct) =>
        await db.CommissionRuns.AsNoTracking().OrderBy(r => r.Number).ToListAsync(ct);

    public async Task<int> LastRunNumberAsync(CancellationToken ct) => await db.CommissionRuns.MaxAsync(r => (int?)r.Number, ct) ?? 0;

    public async Task<RepDocuments> ListDocumentsAsync(CancellationToken ct)
    {
        var invoices = await db.Invoices.AsNoTracking().Where(i => i.SalesRepId != null && i.Status == DocumentStatus.Posted).ToListAsync(ct);
        var ids = invoices.Select(i => i.Id).ToList();
        return new RepDocuments(
            invoices,
            await db.SalesReceipts.AsNoTracking().Where(r => r.SalesRepId != null && r.Status == DocumentStatus.Posted).ToListAsync(ct),
            await db.CreditMemos.AsNoTracking().Where(c => c.SalesRepId != null && c.Status == DocumentStatus.Posted).ToListAsync(ct),
            await db.CustomerPayments.AsNoTracking()
                .Where(p => p.Status == DocumentStatus.Posted && p.Applications.Any(a => ids.Contains(a.InvoiceId))).ToListAsync(ct),
            await db.Retainers.AsNoTracking()
                .Where(r => r.Status == DocumentStatus.Posted && r.Applications.Any(a => ids.Contains(a.InvoiceId))).ToListAsync(ct));
    }

    public void Add(CommissionSettings settings) => db.CommissionSettings.Add(settings);

    public void Add(CommissionRate rate) => db.CommissionRates.Add(rate);

    public void Add(CommissionRun run) => db.CommissionRuns.Add(run);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public void Remove(CommissionRate rate) => db.CommissionRates.Remove(rate);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
