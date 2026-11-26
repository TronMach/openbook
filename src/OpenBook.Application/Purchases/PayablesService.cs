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
/// <param name="Withheld">Backup withholding (box 4), part of <paramref name="Total"/>; any of it makes a 1099 due whatever the threshold.</param>
public sealed record Vendor1099(
    Vendor Vendor, decimal Total, decimal CardPayments, bool MeetsThreshold, decimal Refunds = 0m, decimal Prior = 0m, decimal NotReportable = 0m, decimal Withheld = 0m);

/// <param name="Year">The tax year.</param>
/// <param name="Threshold">The reporting threshold used.</param>
/// <param name="Vendors">Vendors tracked for 1099s.</param>
/// <param name="RefundsNetted">Whether refunds are taken off the totals (<see cref="PurchasingSettings.Net1099Refunds"/>).</param>
/// <param name="HistoryImported">History was moved in as journal entries dated in the year: payments in it aren't counted unless entered as paid before OpenBook.</param>
public sealed record Report1099(int Year, decimal Threshold, IReadOnlyList<Vendor1099> Vendors, bool RefundsNetted = false, bool HistoryImported = false);

/// <summary>
/// One thing paid to a vendor in a year, as 1099s see it (<see cref="PayablesService.Payments1099Async"/>). Money back is
/// negative.
/// </summary>
/// <param name="Date">Null for what was paid before OpenBook.</param>
/// <param name="AccountId">The account it was paid from (or came back into); null for what was paid before OpenBook.</param>
/// <param name="Amount">The whole payment, backup withholding included: the IRS counts what was withheld as paid.</param>
/// <param name="NotReportable">The part for what's kept in accounts marked not reportable; nothing on a card payment.</param>
/// <param name="EntityType">What it is, by its audit entity type, for a link.</param>
/// <param name="Withheld">Backup withholding held back from it, part of <paramref name="Amount"/>.</param>
public sealed record Payment1099(
    Guid VendorId, DateOnly? Date, string Type, string? Reference, Guid? AccountId, decimal Amount, decimal NotReportable, bool ByCard, string EntityType, Guid EntityId,
    decimal Withheld = 0m)
{
    /// <summary>What it adds to the 1099: card payments are on the card company's 1099-K.</summary>
    public decimal Counted => ByCard ? 0m : Amount - NotReportable;
}

/// <summary>What you owe vendors by age (A/P aging), and what you paid 1099 vendors in a year.</summary>
public sealed class PayablesService(
    IPurchasesStore store, AccountService accounts, ILedgerStore ledger, SalesTax.ISalesTaxStore taxes, BackupWithholdingService withholding, Settlements.IOwnerStore owners)
{
    /// <summary>
    /// The 1099-NEC/MISC reporting threshold for payments made in <paramref name="year"/>: $600 through 2025,
    /// $2,000 from 2026 (the One Big Beautiful Bill Act). It's indexed for inflation from 2027, so later years
    /// default to $2,000 and the report lets you enter the published figure.
    /// </summary>
    public static decimal DefaultThreshold(int year) => year <= 2025 ? 600m : 2000m;

    /// <summary>A/P aging on the date, from only the documents that can be open on it (<see cref="IPurchasesStore.ListPayablesAsync"/>).</summary>
    public async Task<PayablesAgingReport> AgingAsync(DateOnly asOf, CancellationToken ct = default) =>
        Aging(await store.ListVendorsAsync(ct), await store.ListPayablesAsync(asOf, ct), asOf);

    /// <summary>The A/P aging worked out from the documents given: every document, or those <see cref="IPurchasesStore.ListPayablesAsync"/> picks.</summary>
    public static PayablesAgingReport Aging(IReadOnlyList<Vendor> vendors, PayableDocuments documents, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var bills = documents.Bills;
        var payments = documents.Payments;
        var credits = documents.Credits;
        var refunds = documents.Refunds;

        // Settlements by bill up to the as-of date.
        var settled = new Dictionary<Guid, decimal>();
        foreach (var p in payments.Where(p => p.Status == DocumentStatus.Posted && p.Date <= asOf))
            foreach (var a in p.Applications)
                settled[a.BillId] = settled.GetValueOrDefault(a.BillId) + a.Amount;
        foreach (var c in credits.Where(c => c.Status == DocumentStatus.Posted))
            foreach (var a in c.Applications.Where(a => a.AppliedOn <= asOf))
                settled[a.BillId] = settled.GetValueOrDefault(a.BillId) + a.Amount;

        // Paid or credited ahead of a bill dated after the day: money you're owed on it, not a settlement of anything.
        var later = (documents.BillsDatedLater ?? bills.Where(b => b.Date > asOf).Select(b => b.Id)).ToHashSet();
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
    /// What vendors were paid in the calendar year, as 1099s count it: bill payments, deposits and expenses, what was paid
    /// before OpenBook, and (when the company nets them) money back. A deposit counts on the day it was paid and from the
    /// account it was paid from, so bill payments made from one don't; but backup withholding held back as one is used is
    /// paid to the vendor then, on top of the deposit. Every 1099 figure comes from here.
    /// </summary>
    public async Task<IReadOnlyList<Payment1099>> Payments1099Async(int year, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
            throw new DomainException("report.year_invalid", "Choose a year between 2000 and 2100.");

        var chart = await accounts.GetChartAsync(ct);
        bool InYear(DateOnly d) => d.Year == year;
        var result = new List<Payment1099>();
        void Add(Guid vendorId, DateOnly date, string type, string? reference, Guid accountId, decimal amount, decimal excluded, string entityType, Guid id, decimal withheld = 0m)
        {
            var card = chart.Find(accountId)?.Type == AccountType.CreditCard;
            result.Add(new Payment1099(vendorId, date, type, reference, accountId, amount, card ? 0m : excluded, card, entityType, id, withheld));
        }

        // A payment's share for accounts marked not reportable: each bill it pays, in proportion to the bill's lines on them.
        var excludedAccounts = NotReportable1099.Accounts(chart);
        var posted = (await store.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted).ToList();
        var payments = posted.Where(p => p.VendorDepositId is null && InYear(p.Date)).ToList();
        var deposits = (await store.ListVendorDepositsAsync(null, ct)).Where(d => d.Status == DocumentStatus.Posted).ToList();
        var paidInYear = deposits.Where(d => InYear(d.Date)).Select(d => d.Id).ToHashSet();
        var fromDeposit = posted.Where(p => p.VendorDepositId is { } d && paidInYear.Contains(d)).ToLookup(p => p.VendorDepositId!.Value);
        var excludedShare = new Dictionary<Guid, decimal>();
        if (excludedAccounts.Count > 0)
        {
            var paidBills = payments.Concat(fromDeposit.SelectMany(g => g)).SelectMany(p => p.Applications.Select(a => a.BillId)).ToHashSet();
            // A settlement's bill is counted from its gross below, whatever accounts its deductions went to.
            foreach (var bill in (await store.ListBillsAsync(null, ct)).Where(b => paidBills.Contains(b.Id) && b.OwnerSettlementId is null))
                excludedShare[bill.Id] = NotReportable1099.Share(bill, excludedAccounts);
        }

        decimal NotReportable(BillPayment p) => NotReportable1099.Of([.. p.Applications.Select(a => (a.BillId, a.Amount))], excludedShare);
        foreach (var p in payments)
            Add(p.VendorId, p.Date, "Bill payment", p.Reference, p.PaymentAccountId, p.Amount, NotReportable(p), "bill_payment", p.Id, p.Withheld);
        foreach (var p in posted.Where(p => p.VendorDepositId is not null && p.Withheld > 0m && InYear(p.Date)))
            Add(p.VendorId, p.Date, "Backup withholding", p.Reference, p.PaymentAccountId, p.Withheld, 0m, "bill_payment", p.Id, p.Withheld);
        foreach (var d in deposits.Where(d => InYear(d.Date)))
            Add(d.VendorId, d.Date, "Deposit", d.Reference, d.PaidFromAccountId, d.Amount, Math.Min(d.Amount, fromDeposit[d.Id].Sum(p => NotReportable(p))), "vendor_deposit", d.Id);

        var heldBack = await withholding.OnExpensesAsync(ct);
        var useTaxes = (await taxes.ListUseTaxesAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct))
            .Where(t => t.Kind == Domain.SalesTax.UseTaxDocument.Expense).ToDictionary(t => t.DocumentId);
        foreach (var e in (await store.ListExpensesAsync(null, ct)).Where(e => e.Status == BankTransactionStatus.Posted && e.VendorId is not null && InYear(e.Date)))
        {
            var excluded = 0m;
            var withheld = heldBack.GetValueOrDefault(e.Id);
            if (excludedAccounts.Count > 0 && await ledger.GetEntryAsync(e.JournalEntryId, ct) is { } entry)
            {
                // Use tax is the state's, not the vendor's.
                var tax = useTaxes.GetValueOrDefault(e.Id);
                excluded = Math.Clamp(entry.Lines.Where(l => l.LineNumber > 1 && excludedAccounts.Contains(l.AccountId) && tax?.Covers(entry.Id, l.LineNumber) != true)
                    .Sum(l => l.Amount), 0m, e.Amount + withheld);
            }

            Add(e.VendorId!.Value, e.Date, "Expense", e.Reference, e.AccountId, e.Amount + withheld, excluded, "bank_transaction", e.Id, withheld);
        }

        if ((await store.GetPurchasingSettingsAsync(ct))?.Net1099Refunds == true)
        {
            // Off the side the money went back to: a refund to a card comes off the card's total.
            foreach (var r in (await store.ListVendorRefundsAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted && InYear(r.Date)))
                Add(r.VendorId, r.Date, "Refund", r.Reference, r.DepositAccountId, -r.Amount, 0m, "vendor_credit", r.VendorCreditId);
            foreach (var d in deposits)
                foreach (var r in d.Refunds.Where(r => !r.IsVoided && InYear(r.Date)))
                    Add(d.VendorId, r.Date, "Deposit refund", r.Reference, r.AccountId, -r.Amount, 0m, "vendor_deposit", d.Id);
        }

        // An owner-operator's settlement: its bill's payment counts the net, so this adds what was taken out of the reportable
        // gross (deductions, charges, reserves, withholding) and takes off what was paid that isn't (reimbursements, reserves
        // paid out). A charge whose money was already counted (an advance paid as an expense) isn't counted again.
        var counted = (await owners.ListChargesAsync(null, ct)).Where(c => c.Counted1099).Select(c => c.Id).ToHashSet();
        var ownerVendors = (await owners.ListOwnersAsync(ct)).ToDictionary(o => o.Id, o => o.VendorId);
        foreach (var s in (await owners.ListSettlementsAsync(null, ct)).Where(s => s.Stands && InYear(s.Date)))
        {
            var already = -s.Lines.Where(l => l.Kind == Domain.Settlements.OwnerLineKind.Charge && l.ChargeId is { } c && counted.Contains(c)).Sum(l => l.Amount);
            var amount = s.ReportableGross - s.Net - already;
            if (ownerVendors.TryGetValue(s.OwnerId, out var vendor) && (amount != 0m || s.Withheld != 0m))
                result.Add(new Payment1099(vendor, s.Date, "Owner settlement, taken out of gross", $"Settlement {s.Number}", null, amount, 0m, false, "owner_settlement", s.Id, s.Withheld));
        }

        foreach (var p in await store.ListPrior1099Async(year, ct))
            result.Add(new Payment1099(p.VendorId, null, "Paid before OpenBook", null, null, p.Amount, 0m, false, "vendor", p.VendorId));
        return result;
    }

    /// <summary>
    /// What was paid in the calendar year to vendors tracked for 1099s (<see cref="Payments1099Async"/>, added up). Card
    /// payments are shown separately and left out, since card companies report them on 1099-K. Anyone with backup
    /// withholding is there too, tracked or not, and needs a 1099 whatever the amount.
    /// </summary>
    public async Task<Report1099> Report1099Async(int year, decimal? threshold = null, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
            throw new DomainException("report.year_invalid", "Choose a year between 2000 and 2100.");
        var limit = threshold ?? DefaultThreshold(year);
        if (limit < 0m)
            throw new DomainException("report.threshold_invalid", "The threshold can't be negative.");

        bool InYear(DateOnly d) => d.Year == year;
        var paid = (await Payments1099Async(year, ct)).ToLookup(p => p.VendorId);
        var refunds = (await store.ListVendorRefundsAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted && InYear(r.Date)).Select(r => (r.VendorId, r.Amount))
            .Concat((await store.ListVendorDepositsAsync(null, ct)).Where(d => d.Status == DocumentStatus.Posted)
                .SelectMany(d => d.Refunds.Where(r => !r.IsVoided && InYear(r.Date)).Select(r => (d.VendorId, r.Amount))))
            .GroupBy(r => r.VendorId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var net = (await store.GetPurchasingSettingsAsync(ct))?.Net1099Refunds ?? false;
        var prior = (await store.ListPrior1099Async(year, ct)).ToDictionary(p => p.VendorId, p => p.Amount);
        var imported = (await ledger.ListEntriesAsync(new JournalEntryFilter(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), Take: 1, Source: JournalEntry.ImportSource), ct)).Total > 0;

        var vendors = (await store.ListVendorsAsync(ct))
            .Where(v => v.Track1099 || paid[v.Id].Any(p => p.Withheld != 0m))
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var reportable = Math.Max(0m, paid[v.Id].Sum(p => p.Counted));
                var card = Math.Max(0m, paid[v.Id].Where(p => p.ByCard).Sum(p => p.Amount));
                var withheld = paid[v.Id].Sum(p => p.Withheld);
                return new Vendor1099(v, reportable, card, (reportable >= limit && reportable > 0m) || withheld > 0m, refunds.GetValueOrDefault(v.Id), prior.GetValueOrDefault(v.Id),
                    paid[v.Id].Sum(p => p.NotReportable), withheld);
            })
            .ToList();
        return new Report1099(year, limit, vendors, net, imported);
    }
}
