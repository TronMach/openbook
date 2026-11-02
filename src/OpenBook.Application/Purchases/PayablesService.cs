using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public sealed record OpenBill(Bill Bill, decimal Balance, int DaysPastDue);

public sealed record VendorAging(Vendor Vendor, AgingBuckets Buckets, IReadOnlyList<OpenBill> Bills, IReadOnlyList<(VendorCredit Credit, decimal Amount)> Credits);

public sealed record PayablesAgingReport(DateOnly AsOf, IReadOnlyList<VendorAging> Vendors)
{
    public AgingBuckets Totals => Vendors.Aggregate(AgingBuckets.Zero, (sum, v) => sum + v.Buckets);
}

/// <param name="CardPayments">Paid by credit card: reported by the card company on 1099-K, so left out of the total.</param>
public sealed record Vendor1099(Vendor Vendor, decimal Total, decimal CardPayments, bool MeetsThreshold);

public sealed record Report1099(int Year, decimal Threshold, IReadOnlyList<Vendor1099> Vendors);

/// <summary>What you owe vendors by age (A/P aging), and what you paid 1099 vendors in a year.</summary>
public sealed class PayablesService(IPurchasesStore store, AccountService accounts)
{
    /// <summary>
    /// The 1099-NEC/MISC reporting threshold for payments made in <paramref name="year"/>: $600 through 2025,
    /// $2,000 from 2026 (the One Big Beautiful Bill Act). It's indexed for inflation from 2027, so later years
    /// default to $2,000 and the report lets you enter the published figure.
    /// </summary>
    public static decimal DefaultThreshold(int year) => year <= 2025 ? 600m : 2000m;

    public async Task<PayablesAgingReport> AgingAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var vendors = await store.ListVendorsAsync(ct);
        var bills = await store.ListBillsAsync(null, ct);
        var payments = await store.ListPaymentsAsync(null, ct);
        var credits = await store.ListVendorCreditsAsync(null, ct);
        var refunds = await store.ListVendorRefundsAsync(null, ct);

        // Settlements by bill up to the as-of date.
        var settled = new Dictionary<Guid, decimal>();
        foreach (var p in payments.Where(p => p.Status == DocumentStatus.Posted && p.Date <= asOf))
            foreach (var a in p.Applications)
                settled[a.BillId] = settled.GetValueOrDefault(a.BillId) + a.Amount;
        foreach (var c in credits.Where(c => c.Status == DocumentStatus.Posted))
            foreach (var a in c.Applications.Where(a => a.AppliedOn <= asOf))
                settled[a.BillId] = settled.GetValueOrDefault(a.BillId) + a.Amount;

        var billsOf = bills.ToLookup(b => b.VendorId);
        var creditsOf = credits.ToLookup(c => c.VendorId);
        var refundsOf = refunds.ToLookup(r => r.VendorCreditId);
        var result = new List<VendorAging>();
        foreach (var vendor in vendors.OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var buckets = AgingBuckets.Zero;
            var open = new List<OpenBill>();
            foreach (var bill in billsOf[vendor.Id].Where(b => b.Status == DocumentStatus.Posted && b.Date <= asOf).OrderBy(b => b.DueDate))
            {
                var balance = bill.Total - settled.GetValueOrDefault(bill.Id);
                if (balance == 0m)
                    continue;
                var days = asOf.DayNumber - bill.DueDate.DayNumber;
                open.Add(new OpenBill(bill, balance, Math.Max(0, days)));
                buckets = buckets.Add(days, balance);
            }

            var unused = new List<(VendorCredit, decimal)>();
            foreach (var credit in creditsOf[vendor.Id].Where(c => c.Status == DocumentStatus.Posted && c.Date <= asOf).OrderBy(c => c.Date))
            {
                var left = credit.Total - credit.Applications.Where(a => a.AppliedOn <= asOf).Sum(a => a.Amount)
                    - refundsOf[credit.Id].Where(r => r.Status == DocumentStatus.Posted && r.Date <= asOf).Sum(r => r.Amount);
                if (left != 0m)
                    unused.Add((credit, left));
            }

            buckets = buckets with { Current = buckets.Current - unused.Sum(c => c.Item2) };
            if (open.Count > 0 || unused.Count > 0)
                result.Add(new VendorAging(vendor, buckets, open, unused));
        }

        return new PayablesAgingReport(asOf, result);
    }

    /// <summary>
    /// Payments in the calendar year to vendors tracked for 1099s: bill payments and expenses paid from bank
    /// accounts. Card payments are shown separately and left out, since card companies report them on 1099-K.
    /// </summary>
    public async Task<Report1099> Report1099Async(int year, decimal? threshold = null, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
            throw new DomainException("report.year_invalid", "Choose a year between 2000 and 2100.");
        var limit = threshold ?? DefaultThreshold(year);
        if (limit < 0m)
            throw new DomainException("report.threshold_invalid", "The threshold can't be negative.");

        var chart = await accounts.GetChartAsync(ct);
        bool IsCard(Guid accountId) => chart.Find(accountId)?.Type == AccountType.CreditCard;
        bool InYear(DateOnly d) => d.Year == year;

        var paid = new Dictionary<Guid, (decimal Reportable, decimal Card)>();
        void Add(Guid vendorId, Guid accountId, decimal amount)
        {
            var (reportable, card) = paid.GetValueOrDefault(vendorId);
            paid[vendorId] = IsCard(accountId) ? (reportable, card + amount) : (reportable + amount, card);
        }

        foreach (var p in (await store.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && InYear(p.Date)))
            Add(p.VendorId, p.PaymentAccountId, p.Amount);
        foreach (var e in (await store.ListExpensesAsync(null, ct)).Where(e => e.Status == BankTransactionStatus.Posted && e.VendorId is not null && InYear(e.Date)))
            Add(e.VendorId!.Value, e.AccountId, e.Amount);

        var vendors = (await store.ListVendorsAsync(ct))
            .Where(v => v.Track1099)
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var (reportable, card) = paid.GetValueOrDefault(v.Id);
                return new Vendor1099(v, reportable, card, reportable >= limit && reportable > 0m);
            })
            .ToList();
        return new Report1099(year, limit, vendors);
    }
}
