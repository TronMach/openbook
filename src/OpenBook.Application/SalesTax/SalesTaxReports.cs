using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.SalesTax;

/// <summary>
/// One rate's line on the liability report. Sales are before tax and net of discounts; a document counts in
/// full on each rate of its code (as on a return, where each jurisdiction reports the same sale).
/// </summary>
public sealed record TaxLiabilityRow(Guid TaxRateId, string RateName, decimal Percent, decimal TotalSales, decimal NonTaxable, decimal Exempt, decimal Taxable, decimal Tax);

/// <param name="OwedAtStart">The agency account's balance the day before the period.</param>
/// <param name="Paid">What payments dated in the period settled (adjustments included).</param>
/// <param name="OwedAtEnd">The balance on the last day; differs from start + tax − paid by any journal entries to the account.</param>
public sealed record TaxLiabilityAgency(TaxAgency Agency, IReadOnlyList<TaxLiabilityRow> Rates, decimal OwedAtStart, decimal Paid, decimal OwedAtEnd)
{
    public decimal Tax => Rates.Sum(r => r.Tax);
}

/// <param name="UntaxedSales">Sales on documents with no tax code.</param>
public sealed record TaxLiabilityReport(DateOnly From, DateOnly To, IReadOnlyList<TaxLiabilityAgency> Agencies, decimal UntaxedSales);

/// <summary>A tax-exempt customer, for checking certificates.</summary>
public sealed record ExemptCustomer(Customer Customer, bool Expired, bool ExpiresSoon);

public sealed class SalesTaxReports(ISalesTaxStore store, ISalesStore sales, SalesTaxPaymentService payments, IClock clock)
{
    public const int ExpiresSoonDays = 30;

    /// <summary>
    /// Tax charged by rate and agency on invoices, sales receipts and credit memos (subtracted) dated in the
    /// period: accrual, from the documents, which is how most states want it filed.
    /// </summary>
    public async Task<TaxLiabilityReport> LiabilityAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("report.range_invalid", "The end date can't be before the start date.");

        bool InRange(DateOnly d) => d >= from && d <= to;
        var documents = new List<(IReadOnlyList<SalesLine> Lines, bool Exempt, int Sign)>();
        documents.AddRange((await sales.ListInvoicesAsync(null, ct)).Where(i => i.Status == DocumentStatus.Posted && InRange(i.Date))
            .Select(i => (i.Lines, i.TaxExemption is not null, 1)));
        documents.AddRange((await sales.ListReceiptsAsync(ct)).Where(r => r.Status == DocumentStatus.Posted && InRange(r.Date))
            .Select(r => (r.Lines, r.TaxExemption is not null, 1)));
        documents.AddRange((await sales.ListCreditMemosAsync(null, ct)).Where(c => c.Status == DocumentStatus.Posted && InRange(c.Date))
            .Select(c => (c.Lines, c.TaxExemption is not null, -1)));

        var rows = new Dictionary<Guid, (decimal Total, decimal NonTaxable, decimal Exempt, decimal Taxable, decimal Tax)>();
        var untaxed = 0m;
        foreach (var (lines, exempt, sign) in documents)
        {
            var total = sign * TaxCalculator.SalesAmount(lines);
            var taxLines = lines.Where(l => l.Kind == SalesLineKind.Tax && l.TaxRateId is not null).ToList();
            if (taxLines.Count == 0)
            {
                untaxed += total;
                continue;
            }

            var taxable = sign * TaxCalculator.TaxableAmount(lines);
            foreach (var line in taxLines)
            {
                var row = rows.GetValueOrDefault(line.TaxRateId!.Value);
                rows[line.TaxRateId.Value] = (
                    row.Total + total,
                    row.NonTaxable + total - taxable,
                    row.Exempt + (exempt ? taxable : 0m),
                    row.Taxable + (exempt ? 0m : taxable),
                    row.Tax + sign * line.Amount);
            }
        }

        var rates = await store.ListRatesAsync(ct);
        var startOwed = await payments.OwedAsync(from.AddDays(-1), ct);
        var endOwed = await payments.OwedAsync(to, ct);
        var paid = (await store.ListPaymentsAsync(null, ct))
            .Where(p => p.Status == DocumentStatus.Posted && InRange(p.Date))
            .GroupBy(p => p.AgencyId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.LiabilitySettled));

        var agencies = (await store.ListAgenciesAsync(ct))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(agency => new TaxLiabilityAgency(
                agency,
                rates.Where(r => r.AgencyId == agency.Id && rows.ContainsKey(r.Id))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => (Rate: r, Row: rows[r.Id]))
                    .Select(x => new TaxLiabilityRow(x.Rate.Id, x.Rate.Name, x.Rate.Percent, x.Row.Total, x.Row.NonTaxable, x.Row.Exempt, x.Row.Taxable, x.Row.Tax))
                    .ToList(),
                startOwed.GetValueOrDefault(agency.Id),
                paid.GetValueOrDefault(agency.Id),
                endOwed.GetValueOrDefault(agency.Id)))
            .Where(a => a.Agency.IsActive || a.Rates.Count > 0 || a.OwedAtStart != 0m || a.OwedAtEnd != 0m || a.Paid != 0m)
            .ToList();

        return new TaxLiabilityReport(from, to, agencies, untaxed);
    }

    /// <summary>Customers with an exemption certificate, expired and soon-to-expire ones first.</summary>
    public async Task<IReadOnlyList<ExemptCustomer>> ExemptCustomersAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        return (await sales.ListCustomersAsync(ct))
            .Where(c => c.ExemptionCertificate is not null)
            .Select(c => new ExemptCustomer(c, c.ExemptionExpired(today),
                !c.ExemptionExpired(today) && c.ExemptionExpiresOn is { } e && e <= today.AddDays(ExpiresSoonDays)))
            .OrderByDescending(x => x.Expired)
            .ThenByDescending(x => x.ExpiresSoon)
            .ThenBy(x => x.Customer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
