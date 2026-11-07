using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public sealed record OpenBill(Bill Bill, decimal Balance, int DaysPastDue);

public sealed record VendorAging(Vendor Vendor, AgingBuckets Buckets, IReadOnlyList<OpenBill> Bills, IReadOnlyList<(VendorCredit Credit, decimal Amount)> Credits, decimal PaidAhead = 0m);

public sealed record PayablesAgingReport(DateOnly AsOf, IReadOnlyList<VendorAging> Vendors)
{
    public AgingBuckets Totals => Vendors.Aggregate(AgingBuckets.Zero, (sum, v) => sum + v.Buckets);
}

/// <param name="CardPayments">Paid by credit card: reported by the card company on 1099-K, so left out of the total.</param>
/// <param name="Vendor">The vendor.</param>
/// <param name="Total">Paid by cash, check or bank in the year, less refunds when the company nets them.</param>
/// <param name="CardPayments">Paid by card (Form 1099-K), left out of <paramref name="Total"/>; less refunds to a card when netted.</param>
/// <param name="MeetsThreshold">Whether a 1099 is due.</param>
/// <param name="Refunds">Refunds the vendor made in the year.</param>
/// <param name="Prior">Paid before OpenBook (<see cref="Prior1099Payment"/>), included in <paramref name="Total"/>.</param>
/// <param name="NotReportable">Paid by cash, check or bank for what's kept in accounts marked not reportable on 1099s, left out of <paramref name="Total"/>.</param>
public sealed record Vendor1099(Vendor Vendor, decimal Total, decimal CardPayments, bool MeetsThreshold, decimal Refunds = 0m, decimal Prior = 0m, decimal NotReportable = 0m);

/// <param name="Year">The tax year.</param>
/// <param name="Threshold">The reporting threshold used.</param>
/// <param name="Vendors">Vendors tracked for 1099s.</param>
/// <param name="RefundsNetted">Whether refunds are taken off the totals (<see cref="PurchasingSettings.Net1099Refunds"/>).</param>
/// <param name="HistoryImported">History was moved in as journal entries dated in the year: payments in it aren't counted unless entered as paid before OpenBook.</param>
public sealed record Report1099(int Year, decimal Threshold, IReadOnlyList<Vendor1099> Vendors, bool RefundsNetted = false, bool HistoryImported = false);

/// <summary>What you owe vendors by age (A/P aging), and what you paid 1099 vendors in a year.</summary>
public sealed class PayablesService(IPurchasesStore store, AccountService accounts, ILedgerStore ledger)
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

        // Paid or credited ahead of a bill dated after the day: money you're owed on it, not a settlement of anything.
        var later = bills.Where(b => b.Date > asOf).Select(b => b.Id).ToHashSet();
        var paidAhead = payments.Where(p => p.Status == DocumentStatus.Posted && p.Date <= asOf)
            .GroupBy(p => p.VendorId)
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Applications).Where(a => later.Contains(a.BillId)).Sum(a => a.Amount));
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
                var left = credit.Total - credit.Applications.Where(a => a.AppliedOn <= asOf && !later.Contains(a.BillId)).Sum(a => a.Amount)
                    - refundsOf[credit.Id].Where(r => r.Status == DocumentStatus.Posted && r.Date <= asOf).Sum(r => r.Amount);
                if (left != 0m)
                    unused.Add((credit, left));
            }

            var ahead = paidAhead.GetValueOrDefault(vendor.Id);
            buckets = buckets with { Current = buckets.Current - unused.Sum(c => c.Item2) - ahead };
            if (open.Count > 0 || unused.Count > 0 || ahead != 0m)
                result.Add(new VendorAging(vendor, buckets, open, unused, ahead));
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
        var notReportable = new Dictionary<Guid, decimal>();
        void Add(Guid vendorId, Guid accountId, decimal amount, decimal excluded = 0m)
        {
            var (reportable, card) = paid.GetValueOrDefault(vendorId);
            if (IsCard(accountId))
            {
                paid[vendorId] = (reportable, card + amount);
                return;
            }

            paid[vendorId] = (reportable + amount - excluded, card);
            if (excluded != 0m)
                notReportable[vendorId] = notReportable.GetValueOrDefault(vendorId) + excluded;
        }

        // A payment's share for accounts marked not reportable: each bill it pays, in proportion to the bill's lines on them.
        var excludedAccounts = chart.All.Where(a => a.Excluded1099).Select(a => a.Id).ToHashSet();
        var payments = (await store.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && InYear(p.Date)).ToList();
        var excludedShare = new Dictionary<Guid, decimal>();
        if (excludedAccounts.Count > 0)
        {
            var paidBills = payments.SelectMany(p => p.Applications.Select(a => a.BillId)).ToHashSet();
            foreach (var bill in (await store.ListBillsAsync(null, ct)).Where(b => paidBills.Contains(b.Id)))
            {
                var total = bill.Lines.Sum(l => l.Amount);
                var excluded = bill.Lines.Where(l => excludedAccounts.Contains(l.AccountId)).Sum(l => l.Amount);
                if (total > 0m && excluded > 0m)
                    excludedShare[bill.Id] = Math.Min(1m, excluded / total);
            }
        }

        foreach (var p in payments)
        {
            var excluded = decimal.Round(p.Applications.Sum(a => a.Amount * excludedShare.GetValueOrDefault(a.BillId)), 2, MidpointRounding.AwayFromZero);
            Add(p.VendorId, p.PaymentAccountId, p.Amount, Math.Min(excluded, p.Amount));
        }

        foreach (var e in (await store.ListExpensesAsync(null, ct)).Where(e => e.Status == BankTransactionStatus.Posted && e.VendorId is not null && InYear(e.Date)))
        {
            var excluded = 0m;
            if (excludedAccounts.Count > 0 && await ledger.GetEntryAsync(e.JournalEntryId, ct) is { } entry)
                excluded = Math.Clamp(entry.Lines.Where(l => l.LineNumber > 1 && excludedAccounts.Contains(l.AccountId)).Sum(l => l.Amount), 0m, e.Amount);
            Add(e.VendorId!.Value, e.AccountId, e.Amount, excluded);
        }

        var refundsInYear = (await store.ListVendorRefundsAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted && InYear(r.Date)).ToList();
        var refunds = refundsInYear.GroupBy(r => r.VendorId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var net = (await store.GetPurchasingSettingsAsync(ct))?.Net1099Refunds ?? false;
        if (net)
        {
            // Off the side the money went back to: a refund to a card comes off the card's total.
            foreach (var r in refundsInYear)
                Add(r.VendorId, r.DepositAccountId, -r.Amount);
        }

        var prior = (await store.ListPrior1099Async(year, ct)).ToDictionary(p => p.VendorId, p => p.Amount);
        foreach (var (vendorId, amount) in prior)
            paid[vendorId] = (paid.GetValueOrDefault(vendorId).Reportable + amount, paid.GetValueOrDefault(vendorId).Card);
        var imported = (await ledger.ListEntriesAsync(new JournalEntryFilter(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), Take: 1, Source: JournalEntry.ImportSource), ct)).Total > 0;

        var vendors = (await store.ListVendorsAsync(ct))
            .Where(v => v.Track1099)
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var (reportable, card) = paid.GetValueOrDefault(v.Id);
                (reportable, card) = (Math.Max(0m, reportable), Math.Max(0m, card));
                return new Vendor1099(v, reportable, card, reportable >= limit && reportable > 0m, refunds.GetValueOrDefault(v.Id), prior.GetValueOrDefault(v.Id),
                    notReportable.GetValueOrDefault(v.Id));
            })
            .ToList();
        return new Report1099(year, limit, vendors, net, imported);
    }
}
