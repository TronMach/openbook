using Microsoft.EntityFrameworkCore;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfSalesTaxStore(OpenBookDbContext db) : ISalesTaxStore
{
    public async Task<IReadOnlyList<TaxAgency>> ListAgenciesAsync(CancellationToken ct) => await db.TaxAgencies.ToListAsync(ct);

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

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
