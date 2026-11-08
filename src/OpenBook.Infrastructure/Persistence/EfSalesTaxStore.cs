using Microsoft.EntityFrameworkCore;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfSalesTaxStore(OpenBookDbContext db) : ISalesTaxStore
{
    public async Task<bool> RateChargedAsync(Guid rateId, CancellationToken ct) =>
        await db.Invoices.AnyAsync(i => i.Lines.Any(l => l.TaxRateId == rateId), ct)
        || await db.SalesReceipts.AnyAsync(r => r.Lines.Any(l => l.TaxRateId == rateId), ct)
        || await db.CreditMemos.AnyAsync(c => c.Lines.Any(l => l.TaxRateId == rateId), ct);

    public async Task<IReadOnlyList<TaxAgency>> ListAgenciesAsync(CancellationToken ct) => await db.TaxAgencies.ToListAsync(ct);

    public Task<int> CountActiveCustomersOnAsync(Guid codeId, CancellationToken ct) =>
        db.Customers.CountAsync(c => c.TaxCodeId == codeId && c.IsActive, ct);

    public Task<TaxAgency?> GetAgencyAsync(Guid id, CancellationToken ct) => db.TaxAgencies.SingleOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<TaxRate>> ListRatesAsync(CancellationToken ct) => await db.TaxRates.ToListAsync(ct);

    public Task<TaxRate?> GetRateAsync(Guid id, CancellationToken ct) => db.TaxRates.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<TaxCode>> ListCodesAsync(CancellationToken ct) => await db.TaxCodes.ToListAsync(ct);

    public Task<TaxCode?> GetCodeAsync(Guid id, CancellationToken ct) => db.TaxCodes.SingleOrDefaultAsync(c => c.Id == id, ct);

    public Task<SalesTaxPayment?> GetPaymentAsync(Guid id, CancellationToken ct) => db.SalesTaxPayments.SingleOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<SalesTaxPayment>> ListPaymentsAsync(Guid? agencyId, CancellationToken ct) =>
        (await db.SalesTaxPayments.AsNoTracking().Where(p => agencyId == null || p.AgencyId == agencyId).ToListAsync(ct))
            .OrderByDescending(p => p.Date)
            .ThenByDescending(p => p.CreatedAt)
            .ToList();

    public void Add(TaxAgency agency) => db.TaxAgencies.Add(agency);

    public void Add(TaxRate rate) => db.TaxRates.Add(rate);

    public void Add(TaxCode code) => db.TaxCodes.Add(code);

    public void Add(SalesTaxPayment payment) => db.SalesTaxPayments.Add(payment);

    public async Task<IReadOnlyList<SalesTaxFiling>> ListFilingsAsync(Guid? agencyId, CancellationToken ct) =>
        (await db.SalesTaxFilings.Where(f => agencyId == null || f.AgencyId == agencyId).ToListAsync(ct))
            .OrderByDescending(f => f.PeriodTo).ThenBy(f => f.AgencyId).ToList();

    public Task<SalesTaxFiling?> GetFilingAsync(Guid id, CancellationToken ct) => db.SalesTaxFilings.SingleOrDefaultAsync(f => f.Id == id, ct);

    public void Add(SalesTaxFiling filing) => db.SalesTaxFilings.Add(filing);

    public void Remove(SalesTaxFiling filing) => db.SalesTaxFilings.Remove(filing);

    public async Task<IReadOnlyList<NexusThreshold>> ListNexusThresholdsAsync(CancellationToken ct) => await db.NexusThresholds.ToListAsync(ct);

    public void Add(NexusThreshold threshold) => db.NexusThresholds.Add(threshold);

    public void Remove(NexusThreshold threshold) => db.NexusThresholds.Remove(threshold);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
