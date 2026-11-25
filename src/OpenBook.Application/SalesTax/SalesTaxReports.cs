using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.SalesTax;

/// <summary>
/// One rate's line on the liability report. Sales are before tax and net of discounts; a document counts in
/// full on each rate of its code (as on a return, where each jurisdiction reports the same sale).
/// </summary>
/// <param name="IsUseTax">The agency's use tax: <paramref name="Taxable"/> is purchases, each document once, not sales.</param>
public sealed record TaxLiabilityRow(
    Guid TaxRateId, string RateName, decimal Percent, decimal TotalSales, decimal NonTaxable, decimal Exempt, decimal Taxable, decimal Tax, bool IsUseTax = false);

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

/// <summary>
/// What counts as sales on returns, nexus figures and sales by item: not lines that only hold money, a gift card sold or
/// redeemed (an other current liability) or retainage (an other current asset), unless they were taxed as a sale.
/// </summary>
internal static class SalesFigures
{
    public static async Task<IReadOnlySet<Guid>> HoldingAccountsAsync(AccountService accounts, CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: true).Select(x => x.Account)
            .Where(a => a.Type is AccountType.OtherCurrentLiability or AccountType.OtherCurrentAsset).Select(a => a.Id).ToHashSet();

    public static bool Counts(SalesLine line, IReadOnlySet<Guid> holding) =>
        line.Kind != SalesLineKind.Tax && (line.Kind != SalesLineKind.Item || line.Taxable || line.IncomeAccountId is not { } account || !holding.Contains(account));

    /// <summary>The document's sales before tax and net of discounts.</summary>
    public static decimal Amount(IEnumerable<SalesLine> lines, IReadOnlySet<Guid> holding) => lines.Where(l => Counts(l, holding)).Sum(l => l.Amount);
}

public sealed class SalesTaxReports(
    ISalesTaxStore store, ISalesStore sales, SalesTaxPaymentService payments, Channels.IChannelStore channels, AccountService accounts, IClock clock)
{
    public const int ExpiresSoonDays = 30;

    /// <summary>
    /// Tax charged by rate and agency on invoices, sales receipts and credit memos (subtracted) dated in the
    /// period: accrual, from the documents, which is how most states want it filed.
    /// </summary>
    /// <param name="basis">
    /// Accrual: each invoice, receipt and credit memo dated in the period. Cash: receipts as they are, invoices in
    /// proportion to what was paid on them in the period (payments on their date, less those the bank took back on the
    /// day it did, and retainers applied), and credit memos as they're refunded. A credit applied to an invoice brings no
    /// money: payments settle what it left, as on the cash-basis ledger. An invoice a gift card paid for counts on its date.
    /// Either way, use tax counts on the date of the purchase: it's owed when something is bought, paid for or not.
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
            foreach (var p in await sales.ListPostedPaymentsAsync(from, to, ct))
                foreach (var a in p.Applications)
                    settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) + a.Settled;
            foreach (var p in await sales.ListReturnedPaymentsAsync(from, to, ct))
                foreach (var a in p.Applications)
                    settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) - a.Settled;
            foreach (var r in await sales.ListRetainersAsync(null, ct))
                foreach (var a in r.Applications.Where(a => !a.IsVoided && InRange(a.AppliedOn)))
                    settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) + a.Amount;
            // Voided ones too: an invoice can be voided once its payment has come back, and both days stand.
            var invoices = (await sales.FindInvoicesAsync([.. settled.Where(s => s.Value != 0m).Select(s => s.Key)], ct)).Where(i => i.Total != 0m).ToList();
            var credits = await sales.FindCreditMemosAsync(await sales.ListCreditMemoIdsOnInvoicesAsync([.. invoices.Select(i => i.Id)], ct), ct);
            foreach (var invoice in invoices)
            {
                var taken = credits.Where(c => c.Status == DocumentStatus.Posted && c.Total != 0m)
                    .Select(c => (Credit: c, Amount: c.Applications.Where(a => a.InvoiceId == invoice.Id).Sum(a => a.Amount))).Where(x => x.Amount != 0m).ToList();
                var left = invoice.Total - taken.Sum(x => x.Amount);
                if (left <= 0m)
                    continue;
                var share = Math.Clamp(settled[invoice.Id] / left, -1m, 1m);
                documents.Add((invoice.Lines, invoice.TaxExemption is not null, share));
                documents.AddRange(taken.Select(x => (x.Credit.Lines, x.Credit.TaxExemption is not null, -share * x.Amount / x.Credit.Total)));
            }

            documents.AddRange((await sales.FindInvoicesAsync(await sales.ListZeroInvoiceIdsAsync(ct), ct)).Where(i => i.Status == DocumentStatus.Posted && InRange(i.Date))
                .Select(i => (i.Lines, i.TaxExemption is not null, 1m)));
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

        var holding = await SalesFigures.HoldingAccountsAsync(accounts, ct);
        var rows = new Dictionary<(Guid Rate, decimal Percent), (decimal Total, decimal NonTaxable, decimal Exempt, decimal Taxable, decimal Tax)>();
        var untaxed = 0m;
        foreach (var (lines, exempt, sign) in documents)
        {
            var total = Cents(sign * SalesFigures.Amount(lines, holding));
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
                // An exemption for some states only (C326) leaves the other states' rates charged, and taxable for them.
                var exemptHere = exempt && line.Amount == 0m;
                rows[key] = (
                    row.Total + total,
                    row.NonTaxable + total - taxable,
                    row.Exempt + (exemptHere ? taxable : 0m),
                    row.Taxable + (exemptHere ? 0m : taxable),
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

        // Use tax on purchases, a row per agency: each document's taxable amount once, however many of its rates are the agency's.
        var useTaxes = await store.ListUseTaxesAsync(from, to, ct);
        TaxLiabilityRow? UseTaxOwed(TaxAgency agency)
        {
            var mine = useTaxes.Select(u => (Document: u, Lines: u.Lines.Where(l => l.LiabilityAccountId == agency.LiabilityAccountId).ToList()))
                .Where(x => x.Lines.Count > 0).ToList();
            return mine.Count == 0 ? null : new TaxLiabilityRow(Guid.Empty, UseTax.Memo, 0m, 0m, 0m, 0m,
                mine.Sum(x => x.Document.Sign * x.Document.Taxable), mine.Sum(x => x.Document.Sign * x.Lines.Sum(l => l.Amount)), IsUseTax: true);
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
                    .Concat(UseTaxOwed(agency) is { } useTaxRow ? [useTaxRow] : [])
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
