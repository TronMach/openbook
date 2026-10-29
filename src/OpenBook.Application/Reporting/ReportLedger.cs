using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Reporting;

/// <summary>
/// Narrows report lines to a class, a location (each including those under it) and/or a tag.
/// <see cref="Guid.Empty"/> for a class or location means lines without one ("Not specified").
/// </summary>
public sealed record SegmentFilter(Guid? ClassId = null, Guid? LocationId = null, Guid? TagId = null)
{
    public bool IsEmpty => ClassId is null && LocationId is null && TagId is null;
}

/// <summary>
/// The lines every report is built from, for one basis. Accrual: the ledger as posted. Cash: invoices,
/// credit memos and bills (and the payments' own A/R and A/P postings) are left out; instead each payment is
/// recognized on its date as income or expense, split across the paid documents' accounts in proportion to
/// their lines. Credit applied to an invoice moves no cash, so it's recognized nowhere; credit applied later
/// from an unapplied payment counts on the payment's date. A refund of a credit memo reverses income on the
/// refund date; a refund of an unapplied payment just reverses that cash. The inventory cost lines on invoices
/// and credit memos (cost of goods sold against the inventory asset) are kept as posted: stock leaves when it's sold.
/// </summary>
public sealed class ReportLedger(ILedgerStore ledger, ISalesStore sales, IPurchasesStore purchases, Companies.ICompanyStore companies)
{
    /// <summary>Entries whose effect cash basis replaces with payment-time recognition.</summary>
    private static readonly HashSet<string> AccrualDocuments =
        [Invoice.Source, CreditMemo.Source, CustomerRefund.Source, BadDebtWriteOff.Source, Retainer.ApplicationSource, Bill.Source, VendorCredit.Source, VendorRefund.Source, CustomerPayment.Source, BillPayment.Source];

    /// <summary>Every class, location and tag's full name.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> SegmentNamesAsync(CancellationToken ct = default) =>
        SegmentRules.FullNames(await ledger.ListSegmentsAsync(ct));

    /// <summary>"Class: Retail · Location: East", for report subtitles; null without a filter.</summary>
    public async Task<string?> DescribeAsync(SegmentFilter? filter, CancellationToken ct = default)
    {
        if (filter is null || filter.IsEmpty)
            return null;
        var names = SegmentRules.FullNames(await ledger.ListSegmentsAsync(ct));
        string Name(Guid id) => id == Guid.Empty ? SegmentRules.NotSpecified : names.GetValueOrDefault(id) ?? "?";
        var label = (await companies.GetCompanyAsync(ct))?.LocationLabel ?? "Location";
        return string.Join(" · ", new[]
        {
            filter.ClassId is { } c ? $"Class: {Name(c)}" : null,
            filter.LocationId is { } l ? $"{label}: {Name(l)}" : null,
            filter.TagId is { } t ? $"Tag: {Name(t)}" : null,
        }.Where(s => s is not null));
    }

    /// <param name="from">Null for "since the beginning".</param>
    public async Task<IReadOnlyList<LedgerLine>> LinesAsync(ReportBasis basis, DateOnly? from, DateOnly to, SegmentFilter? filter, CancellationToken ct = default)
    {
        var lines = await LinesAsync(basis, from, to, ct);
        if (filter is null || filter.IsEmpty)
            return lines;

        var segments = await ledger.ListSegmentsAsync(ct);
        IReadOnlySet<Guid>? Under(Guid? id) => id is { } s && s != Guid.Empty ? SegmentRules.WithDescendants(s, segments) : null;
        var classes = Under(filter.ClassId);
        var locations = Under(filter.LocationId);
        var tagged = filter.TagId is { } tagId
            ? (await ledger.GetEntryTagsAsync(null, ct)).Where(t => t.TagId == tagId).Select(t => t.JournalEntryId).ToHashSet()
            : null;
        return lines.Where(l =>
            (filter.ClassId is null || (classes is null ? l.ClassId is null : l.ClassId is { } c && classes.Contains(c)))
            && (filter.LocationId is null || (locations is null ? l.LocationId is null : l.LocationId is { } loc && locations.Contains(loc)))
            && (tagged is null || tagged.Contains(l.JournalEntryId))).ToList();
    }

    /// <param name="from">Null for "since the beginning".</param>
    public async Task<IReadOnlyList<LedgerLine>> LinesAsync(ReportBasis basis, DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var lines = await ledger.GetLinesAsync(from, to, ct);
        if (basis == ReportBasis.Accrual)
            return lines.Where(l => l.Basis.AppliesTo(ReportBasis.Accrual)).ToList();

        var stockAccounts = (await sales.ListItemsAsync(ct))
            .Where(i => i.IsInventory)
            .SelectMany(i => new[] { i.InventoryAssetAccountId!.Value, i.CostAccountId!.Value })
            .ToHashSet();
        var cash = lines.Where(l => l.Basis.AppliesTo(ReportBasis.Cash)
            && (!AccrualDocuments.Contains(l.Source) || l.Source is Invoice.Source or CreditMemo.Source && stockAccounts.Contains(l.AccountId))).ToList();
        var entryNumbers = lines.GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.First().EntryNumber);
        bool InRange(DateOnly d) => d <= to && (from is null || d >= from);

        var customerPayments = (await sales.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && InRange(p.Date)).ToList();
        if (customerPayments.Count > 0)
        {
            var invoices = (await sales.ListInvoicesAsync(null, ct)).ToDictionary(i => i.Id);
            foreach (var payment in customerPayments)
            {
                var number = entryNumbers.GetValueOrDefault(payment.JournalEntryId);
                var entryLines = lines.Where(l => l.JournalEntryId == payment.JournalEntryId).ToList();
                var memo = entryLines.FirstOrDefault()?.EntryMemo;
                if (payment.Amount != 0m)
                    cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.DepositAccountId, payment.Amount, memo, CustomerPayment.Source, payment.Id));
                // Early payment discounts: the entry's lines on accounts other than the deposit and A/R accounts.
                cash.AddRange(entryLines.Where(l => l.AccountId != payment.DepositAccountId && l.AccountId != payment.ReceivableAccountId));
                foreach (var application in payment.Applications)
                {
                    var invoice = invoices[application.InvoiceId];
                    foreach (var (accountId, classId, share) in AllocateByClass(application.Settled, IncomeLines(invoice.Lines)))
                        cash.Add(Derived(payment.JournalEntryId, number, payment.Date, accountId, -share, memo, CustomerPayment.Source, payment.Id, classId, invoice.LocationId));
                }

                // Money not applied to invoices is still the customer's (or refunded): it stays in A/R.
                var unapplied = payment.Amount - payment.AmountApplied;
                if (unapplied != 0m)
                    cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.ReceivableAccountId, -unapplied, memo, CustomerPayment.Source, payment.Id));
            }
        }

        // A retainer applied to an invoice is the invoice being paid: the deposit liability goes down by the amount
        // applied, and the invoice's income is recognized on the date it's applied.
        var applications = (await sales.ListRetainersAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted)
            .SelectMany(r => r.Applications.Where(a => !a.IsVoided && InRange(a.AppliedOn)).Select(a => (Retainer: r, Application: a))).ToList();
        if (applications.Count > 0)
        {
            var invoices = (await sales.ListInvoicesAsync(null, ct)).ToDictionary(i => i.Id);
            foreach (var (retainer, application) in applications)
            {
                var number = entryNumbers.GetValueOrDefault(application.JournalEntryId);
                var memo = lines.FirstOrDefault(l => l.JournalEntryId == application.JournalEntryId)?.EntryMemo;
                cash.Add(Derived(application.JournalEntryId, number, application.AppliedOn, retainer.LiabilityAccountId, application.Amount, memo,
                    Retainer.ApplicationSource, retainer.Id));
                var invoice = invoices[application.InvoiceId];
                foreach (var (accountId, classId, share) in AllocateByClass(application.Amount, IncomeLines(invoice.Lines)))
                    cash.Add(Derived(application.JournalEntryId, number, application.AppliedOn, accountId, -share, memo, Retainer.ApplicationSource, retainer.Id,
                        classId, invoice.LocationId));
            }
        }

        var refunds = (await sales.ListRefundsAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted && InRange(r.Date)).ToList();
        if (refunds.Count > 0)
        {
            var credits = (await sales.ListCreditMemosAsync(null, ct)).ToDictionary(c => c.Id);
            foreach (var refund in refunds)
            {
                if (refund.CreditMemoId is not { } creditId)
                {
                    // A refund of unapplied payment money: as posted (A/R against the bank).
                    cash.AddRange(lines.Where(l => l.JournalEntryId == refund.JournalEntryId));
                    continue;
                }

                var number = entryNumbers.GetValueOrDefault(refund.JournalEntryId);
                var memo = lines.FirstOrDefault(l => l.JournalEntryId == refund.JournalEntryId)?.EntryMemo;
                cash.Add(Derived(refund.JournalEntryId, number, refund.Date, refund.PaymentAccountId, -refund.Amount, memo, CustomerRefund.Source, refund.Id));
                var credit = credits[creditId];
                foreach (var (accountId, classId, share) in AllocateByClass(refund.Amount, IncomeLines(credit.Lines)))
                    cash.Add(Derived(refund.JournalEntryId, number, refund.Date, accountId, share, memo, CustomerRefund.Source, refund.Id, classId, credit.LocationId));
            }
        }

        var billPayments = (await purchases.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && InRange(p.Date)).ToList();
        if (billPayments.Count > 0)
        {
            var bills = (await purchases.ListBillsAsync(null, ct)).ToDictionary(b => b.Id);
            foreach (var payment in billPayments)
            {
                var number = entryNumbers.GetValueOrDefault(payment.JournalEntryId);
                var memo = lines.FirstOrDefault(l => l.JournalEntryId == payment.JournalEntryId)?.EntryMemo;
                cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.PaymentAccountId, -payment.Amount, memo, BillPayment.Source, payment.Id));
                foreach (var application in payment.Applications)
                {
                    var bill = bills[application.BillId];
                    var billLines = bill.Lines.Select(l => (l.AccountId, l.ClassId, l.Amount)).ToList();
                    foreach (var (accountId, classId, share) in AllocateByClass(application.Amount, billLines))
                        cash.Add(Derived(payment.JournalEntryId, number, payment.Date, accountId, share, memo, BillPayment.Source, payment.Id, classId, bill.LocationId));
                }
            }
        }

        // A vendor refund of a credit reduces expense on the refund date, split like the credit's lines.
        var vendorRefunds = (await purchases.ListVendorRefundsAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted && InRange(r.Date)).ToList();
        if (vendorRefunds.Count > 0)
        {
            var vendorCredits = (await purchases.ListVendorCreditsAsync(null, ct)).ToDictionary(c => c.Id);
            foreach (var refund in vendorRefunds)
            {
                var number = entryNumbers.GetValueOrDefault(refund.JournalEntryId);
                var memo = lines.FirstOrDefault(l => l.JournalEntryId == refund.JournalEntryId)?.EntryMemo;
                cash.Add(Derived(refund.JournalEntryId, number, refund.Date, refund.DepositAccountId, refund.Amount, memo, VendorRefund.Source, refund.Id));
                var vendorCredit = vendorCredits[refund.VendorCreditId];
                var creditLines = vendorCredit.Lines.Select(l => (l.AccountId, l.ClassId, l.Amount)).ToList();
                foreach (var (accountId, classId, share) in AllocateByClass(refund.Amount, creditLines))
                    cash.Add(Derived(refund.JournalEntryId, number, refund.Date, accountId, -share, memo, VendorRefund.Source, refund.Id, classId, vendorCredit.LocationId));
            }
        }

        return cash.OrderBy(l => l.Date).ThenBy(l => l.EntryNumber).ToList();
    }

    private static List<(Guid AccountId, Guid? ClassId, decimal Amount)> IncomeLines(IEnumerable<SalesLine> lines) => lines
        .Where(l => l.IncomeAccountId is not null && l.Amount != 0m)
        .Select(l => (l.IncomeAccountId!.Value, l.ClassId, l.Amount))
        .ToList();

    /// <summary>
    /// Splits <paramref name="amount"/> across accounts in proportion to the document's line amounts, in cents,
    /// with the rounding remainder on the last line so the parts add up exactly.
    /// </summary>
    public static IReadOnlyList<(Guid AccountId, decimal Amount)> Allocate(decimal amount, IReadOnlyList<(Guid AccountId, decimal Amount)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return AllocateByClass(amount, lines.Select(l => (l.AccountId, (Guid?)null, l.Amount)).ToList()).Select(p => (p.AccountId, p.Amount)).ToList();
    }

    /// <summary><see cref="Allocate"/>, keeping each line's class: one part per account and class.</summary>
    public static IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> AllocateByClass(decimal amount, IReadOnlyList<(Guid AccountId, Guid? ClassId, decimal Amount)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var total = lines.Sum(l => l.Amount);
        if (lines.Count == 0 || total == 0m)
            return [];

        var parts = new List<(Guid AccountId, Guid? ClassId, decimal Share)>(lines.Count);
        var allocated = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var share = i == lines.Count - 1
                ? amount - allocated
                : decimal.Round(amount * lines[i].Amount / total, 2, MidpointRounding.AwayFromZero);
            allocated += share;
            parts.Add((lines[i].AccountId, lines[i].ClassId, share));
        }

        return parts
            .GroupBy(p => (p.AccountId, p.ClassId))
            .Select(g => (g.Key.AccountId, g.Key.ClassId, g.Sum(p => p.Share)))
            .Where(p => p.Item3 != 0m)
            .ToList();
    }

    private static LedgerLine Derived(
        Guid entryId, long number, DateOnly date, Guid accountId, decimal amount, string? memo, string source, Guid sourceId, Guid? classId = null, Guid? locationId = null) =>
        new(Guid.Empty, entryId, number, date, accountId, amount, null, memo, source, sourceId, EntryBasis.Both, classId, locationId);
}
