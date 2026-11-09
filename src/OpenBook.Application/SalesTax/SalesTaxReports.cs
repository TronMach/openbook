using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
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
public sealed record TaxLiabilityReport(DateOnly From, DateOnly To, IReadOnlyList<TaxLiabilityAgency> Agencies, decimal UntaxedSales, ReportBasis Basis = ReportBasis.Accrual);

/// <summary>A tax-exempt customer, for checking certificates.</summary>
public sealed record ExemptCustomer(Customer Customer, bool Expired, bool ExpiresSoon);

public sealed class SalesTaxReports(ISalesTaxStore store, ISalesStore sales, SalesTaxPaymentService payments, Channels.IChannelStore channels, IClock clock)
{
    public const int ExpiresSoonDays = 30;

    /// <summary>
    /// Tax charged by rate and agency on invoices, sales receipts and credit memos (subtracted) dated in the
    /// period: accrual, from the documents, which is how most states want it filed.
    /// </summary>
    /// <param name="basis">
    /// Accrual: each invoice, receipt and credit memo dated in the period. Cash: receipts as they are, invoices in
    /// proportion to what was paid on them in the period (payments on their date, retainers applied), and credit memos
    /// as they're refunded; a credit applied to an invoice brings no money, so it counts on neither side.
    /// </param>
    public async Task<TaxLiabilityReport> LiabilityAsync(DateOnly from, DateOnly to, ReportBasis basis = ReportBasis.Accrual, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("report.range_invalid", "The end date can't be before the start date.");

        bool InRange(DateOnly d) => d >= from && d <= to;
        var documents = new List<(IReadOnlyList<SalesLine> Lines, bool Exempt, decimal Sign)>();
        if (basis == ReportBasis.Cash)
        {
            // From the money in the period to the documents it paid, rather than every document.
            var settled = new Dictionary<Guid, decimal>();
            foreach (var p in (await sales.ListPostedPaymentsAsync(from, to, ct)).Where(p => p.StandsOn(to)))
                foreach (var a in p.Applications)
                    settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) + a.Settled;
            foreach (var r in await sales.ListRetainersAsync(null, ct))
                foreach (var a in r.Applications.Where(a => !a.IsVoided && InRange(a.AppliedOn)))
                    settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) + a.Amount;
            var invoices = (await sales.FindInvoicesAsync(settled.Keys, ct)).Where(i => i.Status == DocumentStatus.Posted);
            documents.AddRange(invoices.Where(i => settled.ContainsKey(i.Id) && i.Total != 0m)
                .Select(i => (i.Lines, i.TaxExemption is not null, Math.Min(1m, settled[i.Id] / i.Total))));
            var refunded = (await sales.ListPostedRefundsAsync(from, to, ct)).Where(r => r.CreditMemoId is not null)
                .GroupBy(r => r.CreditMemoId!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
            var memos = (await sales.FindCreditMemosAsync(refunded.Keys, ct)).Where(c => c.Status == DocumentStatus.Posted);
            documents.AddRange(memos.Where(c => refunded.ContainsKey(c.Id) && c.Total != 0m)
                .Select(c => (c.Lines, c.TaxExemption is not null, -Math.Min(1m, refunded[c.Id] / c.Total))));
        }
        else
        {
            documents.AddRange((await sales.ListInvoicesDatedAsync(from, to, ct)).Where(i => i.Status == DocumentStatus.Posted)
                .Select(i => (i.Lines, i.TaxExemption is not null, 1m)));
            documents.AddRange((await sales.ListCreditMemosDatedAsync(from, to, ct)).Where(c => c.Status == DocumentStatus.Posted)
                .Select(c => (c.Lines, c.TaxExemption is not null, -1m)));
        }

        documents.AddRange((await sales.ListReceiptsDatedAsync(from, to, ct)).Where(r => r.Status == DocumentStatus.Posted)
            .Select(r => (r.Lines, r.TaxExemption is not null, 1m)));
        static decimal Cents(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

        var rows = new Dictionary<(Guid Rate, decimal Percent), (decimal Total, decimal NonTaxable, decimal Exempt, decimal Taxable, decimal Tax)>();
        var untaxed = 0m;
        foreach (var (lines, exempt, sign) in documents)
        {
            var total = Cents(sign * TaxCalculator.SalesAmount(lines));
            var taxLines = lines.Where(l => l.Kind == SalesLineKind.Tax && l.TaxRateId is not null).ToList();
            if (taxLines.Count == 0)
            {
                untaxed += total;
                continue;
            }

            var taxable = Cents(sign * TaxCalculator.TaxableAmount(lines));
            foreach (var line in taxLines)
            {
                // By the percent charged: a rate that changed is two rows, and a past period keeps the percent it had.
                var key = (line.TaxRateId!.Value, line.Rate);
                var row = rows.GetValueOrDefault(key);
                rows[key] = (
                    row.Total + total,
                    row.NonTaxable + total - taxable,
                    row.Exempt + (exempt ? taxable : 0m),
                    row.Taxable + (exempt ? 0m : taxable),
                    row.Tax + Cents(sign * line.Amount));
            }
        }

        // Sales channels' payouts: tax by the state shipped to, which the agency for that state is owed.
        var online = (await channels.ListPayoutsAsync(null, ct)).Where(p => p.Status == Domain.Channels.PayoutStatus.Posted && InRange(p.Date))
            .SelectMany(p => p.Lines).Where(l => l.Region is not null).ToLookup(l => l.Region!, StringComparer.OrdinalIgnoreCase);

        TaxLiabilityRow? Online(TaxAgency agency)
        {
            if (agency.Region is not { } region || !online.Contains(region))
                return null;
            var tax = online[region].Where(l => l.Type is Domain.Channels.ChannelLineType.Tax or Domain.Channels.ChannelLineType.MarketplaceTax).Sum(l => l.Amount);
            var total = online[region].Where(l => l.Type is Domain.Channels.ChannelLineType.Sale or Domain.Channels.ChannelLineType.Shipping or Domain.Channels.ChannelLineType.Refund).Sum(l => l.Amount);
            return tax == 0m ? null : new TaxLiabilityRow(Guid.Empty, "Online sales", 0m, total, 0m, 0m, total, tax);
        }

        var rates = await store.ListRatesAsync(ct);
        var startOwed = await payments.OwedAsync(from.AddDays(-1), basis, ct);
        var endOwed = await payments.OwedAsync(to, basis, ct);
        var paid = (await store.ListPaymentsAsync(null, ct))
            .Where(p => p.Status == DocumentStatus.Posted && InRange(p.Date))
            .GroupBy(p => p.AgencyId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.LiabilitySettled));

        var agencies = (await store.ListAgenciesAsync(ct))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(agency => new TaxLiabilityAgency(
                agency,
                rates.Where(r => r.AgencyId == agency.Id)
                    .SelectMany(r => rows.Where(x => x.Key.Rate == r.Id).Select(x => (Rate: r, x.Key.Percent, Row: x.Value)))
                    .OrderBy(x => x.Rate.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Percent)
                    .Select(x => new TaxLiabilityRow(x.Rate.Id, x.Rate.Name, x.Percent, x.Row.Total, x.Row.NonTaxable, x.Row.Exempt, x.Row.Taxable, x.Row.Tax))
                    .Concat(Online(agency) is { } channelRow ? [channelRow] : [])
                    .ToList(),
                startOwed.GetValueOrDefault(agency.Id),
                paid.GetValueOrDefault(agency.Id),
                endOwed.GetValueOrDefault(agency.Id)))
            .Where(a => a.Agency.IsActive || a.Rates.Count > 0 || a.OwedAtStart != 0m || a.OwedAtEnd != 0m || a.Paid != 0m)
            .ToList();

        return new TaxLiabilityReport(from, to, agencies, untaxed, basis);
    }

    /// <summary>Customers with an exemption certificate, expired and soon-to-expire ones first.</summary>
    public async Task<IReadOnlyList<ExemptCustomer>> ExemptCustomersAsync(CancellationToken ct = default)
    {
        var today = CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
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
