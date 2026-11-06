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
/// and credit memos (cost of goods sold against the inventory asset) are kept as posted: stock leaves when it's sold. So
/// are a bill's corrections of the cost of stock sold before it came in (<see cref="Inventory.InventoryService.ShortfallMemo"/>).
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

    /// <summary>Source of the lines <see cref="LinesSinceAsync"/> folds history into.</summary>
    public const string BroughtForwardSource = "ledger.brought_forward";

    /// <summary>
    /// The lines up to <paramref name="to"/>, with everything dated before <paramref name="detailFrom"/> folded into one
    /// line per account (and class and location), dated the day before. Sums by account, and anything dated from
    /// <paramref name="detailFrom"/> on, are exactly what <see cref="LinesAsync(ReportBasis, DateOnly?, DateOnly, SegmentFilter?, CancellationToken)"/>
    /// gives; for balance sheets, trial balances and opening balances, which would otherwise read the whole history.
    /// On the accrual basis without a filter the history is added up by the database.
    /// </summary>
    public async Task<IReadOnlyList<LedgerLine>> LinesSinceAsync(ReportBasis basis, DateOnly detailFrom, DateOnly to, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        if (detailFrom == DateOnly.MinValue)
            return await LinesAsync(basis, null, to, filter, ct);

        var broughtForward = detailFrom.AddDays(-1);
        if (basis == ReportBasis.Accrual && (filter is null || filter.IsEmpty))
        {
            var totals = await ledger.GetAccrualTotalsAsync(detailFrom, broughtForward, ct);
            var detail = detailFrom > to ? [] : await LinesAsync(basis, detailFrom, to, ct);
            return [.. totals.Where(t => t.Value.Before != 0m).Select(t => BroughtForward(broughtForward, t.Key, t.Value.Before, null, null)), .. detail];
        }

        var lines = await LinesAsync(basis, null, to, filter, ct);
        var folded = lines.Where(l => l.Date < detailFrom)
            .GroupBy(l => (l.AccountId, l.ClassId, l.LocationId))
            .Select(g => BroughtForward(broughtForward, g.Key.AccountId, g.Sum(l => l.Amount), g.Key.ClassId, g.Key.LocationId))
            .Where(l => l.Amount != 0m);
        return [.. folded, .. lines.Where(l => l.Date >= detailFrom)];
    }

    /// <summary>
    /// Totals only: each account's lines before <paramref name="splitAt"/> folded into one line dated the day before,
    /// and those from it to <paramref name="to"/> into one dated <paramref name="to"/>. For the balance sheet (prior
    /// years against this year) and the trial balance, which need no detail. On the accrual basis the database adds them up.
    /// </summary>
    public async Task<IReadOnlyList<LedgerLine>> TotalsAsync(ReportBasis basis, DateOnly splitAt, DateOnly to, CancellationToken ct = default)
    {
        if (basis == ReportBasis.Accrual && splitAt > DateOnly.MinValue && splitAt <= to)
        {
            var totals = await ledger.GetAccrualTotalsAsync(splitAt, to, ct);
            return
            [
                .. totals.Where(t => t.Value.Before != 0m).Select(t => BroughtForward(splitAt.AddDays(-1), t.Key, t.Value.Before, null, null)),
                // An account brought to zero in the period still needs its period line, or the history would stand alone.
                .. totals.Where(t => t.Value.Through != t.Value.Before)
                    .Select(t => BroughtForward(to, t.Key, t.Value.Through - t.Value.Before, null, null)),
            ];
        }

        var lines = await LinesSinceAsync(basis, splitAt, to, ct: ct);
        var history = lines.Where(l => l.Source == BroughtForwardSource);
        var period = lines.Where(l => l.Source != BroughtForwardSource)
            .GroupBy(l => (l.AccountId, l.ClassId, l.LocationId))
            .Select(g => BroughtForward(to, g.Key.AccountId, g.Sum(l => l.Amount), g.Key.ClassId, g.Key.LocationId))
            .Where(l => l.Amount != 0m);
        return [.. history, .. period];
    }

    private static LedgerLine BroughtForward(DateOnly date, Guid accountId, decimal amount, Guid? classId, Guid? locationId) =>
        new(Guid.Empty, Guid.Empty, 0, date, accountId, amount, null, "Brought forward", BroughtForwardSource, null, EntryBasis.Both, classId, locationId);

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

        // An item's cost account can change later; what sold before stays on the old one, and income never sits on an expense account.
        var onSales = lines.Where(l => l.Source is Invoice.Source or CreditMemo.Source).Select(l => l.AccountId).Distinct().ToList();
        foreach (var (id, account) in await ledger.GetAccountsAsync(onSales, ct))
        {
            if (Domain.Accounts.AccountTypes.ClassOf(account.Type) == Domain.Accounts.AccountClass.Expense)
                stockAccounts.Add(id);
        }

        var cash = lines.Where(l => l.Basis.AppliesTo(ReportBasis.Cash)
            && (!AccrualDocuments.Contains(l.Source) || l.Source is Invoice.Source or CreditMemo.Source && stockAccounts.Contains(l.AccountId)
                // A bill's correction of the cost of stock sold before it came in: cost of goods sold, recognized as posted.
                || l.Source == Bill.Source && l.LineMemo?.StartsWith(Inventory.InventoryService.ShortfallMemo, StringComparison.Ordinal) == true)).ToList();
        var entryNumbers = lines.GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.First().EntryNumber);
        // Each document's posted lines, looked up once rather than by scanning every line per document.
        var byEntry = lines.ToLookup(l => l.JournalEntryId);
        bool InRange(DateOnly d) => d <= to && (from is null || d >= from);

        // Only what's dated in the range, the documents it settles, and whatever else settles those.
        var customerPayments = await sales.ListPostedPaymentsAsync(from, to, ct);
        var retainers = (await sales.ListRetainersAsync(null, ct)).Where(r => r.Status == DocumentStatus.Posted).ToList();
        var applications = retainers
            .SelectMany(r => r.Applications.Where(a => !a.IsVoided && InRange(a.AppliedOn)).Select(a => (Retainer: r, Application: a))).ToList();
        var refunds = await sales.ListPostedRefundsAsync(from, to, ct);
        var invoices = (from is null
            ? await sales.ListInvoicesAsync(null, ct)
            : await sales.FindInvoicesAsync(customerPayments.SelectMany(p => p.Applications.Select(a => a.InvoiceId)).Concat(applications.Select(a => a.Application.InvoiceId)).ToHashSet(), ct))
            .ToDictionary(i => i.Id);
        var credits = await SalesCreditsAsync(invoices, refunds, from is null, ct);
        var salesPlan = await SalesPlanAsync(invoices, credits, retainers, ct);
        if (customerPayments.Count > 0)
        {
            foreach (var payment in customerPayments)
            {
                var number = entryNumbers.GetValueOrDefault(payment.JournalEntryId);
                var entryLines = byEntry[payment.JournalEntryId].ToList();
                var memo = entryLines.FirstOrDefault()?.EntryMemo;
                if (payment.Amount != 0m)
                    cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.DepositAccountId, payment.Amount, memo, CustomerPayment.Source, payment.Id));
                // Early payment discounts: the entry's lines on accounts other than the deposit and A/R accounts.
                cash.AddRange(entryLines.Where(l => l.AccountId != payment.DepositAccountId && l.AccountId != payment.ReceivableAccountId));
                foreach (var application in payment.Applications)
                {
                    var invoice = invoices[application.InvoiceId];
                    foreach (var (accountId, classId, share) in salesPlan.Of(invoice.Id, payment.Id))
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
        if (applications.Count > 0)
        {
            foreach (var (retainer, application) in applications)
            {
                var number = entryNumbers.GetValueOrDefault(application.JournalEntryId);
                var memo = byEntry[application.JournalEntryId].FirstOrDefault()?.EntryMemo;
                cash.Add(Derived(application.JournalEntryId, number, application.AppliedOn, retainer.LiabilityAccountId, application.Amount, memo,
                    Retainer.ApplicationSource, retainer.Id));
                var invoice = invoices[application.InvoiceId];
                foreach (var (accountId, classId, share) in salesPlan.Of(invoice.Id, application.Id))
                    cash.Add(Derived(application.JournalEntryId, number, application.AppliedOn, accountId, -share, memo, Retainer.ApplicationSource, retainer.Id,
                        classId, invoice.LocationId));
            }
        }

        if (refunds.Count > 0)
        {
            foreach (var refund in refunds)
            {
                if (refund.CreditMemoId is not { } creditId)
                {
                    // A refund of unapplied payment money: as posted (A/R against the bank).
                    cash.AddRange(byEntry[refund.JournalEntryId]);
                    continue;
                }

                var number = entryNumbers.GetValueOrDefault(refund.JournalEntryId);
                var memo = byEntry[refund.JournalEntryId].FirstOrDefault()?.EntryMemo;
                cash.Add(Derived(refund.JournalEntryId, number, refund.Date, refund.PaymentAccountId, -refund.Amount, memo, CustomerRefund.Source, refund.Id));
                var credit = credits[creditId];
                foreach (var (accountId, classId, share) in salesPlan.Of(credit.Id, refund.Id))
                    cash.Add(Derived(refund.JournalEntryId, number, refund.Date, accountId, share, memo, CustomerRefund.Source, refund.Id, classId, credit.LocationId));
            }
        }

        var billPayments = await purchases.ListPostedPaymentsAsync(from, to, ct);
        var vendorRefunds = await purchases.ListPostedVendorRefundsAsync(from, to, ct);
        var bills = (from is null
            ? await purchases.ListBillsAsync(null, ct)
            : await purchases.FindBillsAsync(billPayments.SelectMany(p => p.Applications.Select(a => a.BillId)).ToHashSet(), ct)).ToDictionary(b => b.Id);
        var vendorCredits = await VendorCreditsAsync(bills, vendorRefunds, from is null, ct);
        var billPlan = await BillPlanAsync(bills, vendorCredits, ct);
        if (billPayments.Count > 0)
        {
            foreach (var payment in billPayments)
            {
                var number = entryNumbers.GetValueOrDefault(payment.JournalEntryId);
                var memo = byEntry[payment.JournalEntryId].FirstOrDefault()?.EntryMemo;
                cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.PaymentAccountId, -payment.Amount, memo, BillPayment.Source, payment.Id));
                foreach (var application in payment.Applications)
                {
                    var bill = bills[application.BillId];
                    foreach (var (accountId, classId, share) in billPlan.Of(bill.Id, payment.Id))
                        cash.Add(Derived(payment.JournalEntryId, number, payment.Date, accountId, share, memo, BillPayment.Source, payment.Id, classId, bill.LocationId));
                }
            }
        }

        // A vendor refund of a credit reduces expense on the refund date, split like the credit's lines.
        if (vendorRefunds.Count > 0)
        {
            foreach (var refund in vendorRefunds)
            {
                var number = entryNumbers.GetValueOrDefault(refund.JournalEntryId);
                var memo = byEntry[refund.JournalEntryId].FirstOrDefault()?.EntryMemo;
                cash.Add(Derived(refund.JournalEntryId, number, refund.Date, refund.DepositAccountId, refund.Amount, memo, VendorRefund.Source, refund.Id));
                var vendorCredit = vendorCredits[refund.VendorCreditId];
                foreach (var (accountId, classId, share) in billPlan.Of(vendorCredit.Id, refund.Id))
                    cash.Add(Derived(refund.JournalEntryId, number, refund.Date, accountId, -share, memo, VendorRefund.Source, refund.Id, classId, vendorCredit.LocationId));
            }
        }

        return cash.OrderBy(l => l.Date).ThenBy(l => l.EntryNumber).ToList();
    }

    /// <summary>The credit memos applied to these invoices or refunded in the range.</summary>
    private async Task<Dictionary<Guid, CreditMemo>> SalesCreditsAsync(
        IReadOnlyDictionary<Guid, Invoice> invoices, IReadOnlyList<CustomerRefund> refunds, bool everything, CancellationToken ct)
    {
        if (everything)
            return (await sales.ListCreditMemosAsync(null, ct)).ToDictionary(c => c.Id);
        var found = (await sales.ListPostedCreditMemosOnInvoicesAsync([.. invoices.Keys], ct)).ToDictionary(c => c.Id);
        var more = refunds.Select(r => r.CreditMemoId).OfType<Guid>().Where(id => !found.ContainsKey(id)).ToHashSet();
        foreach (var credit in await sales.FindCreditMemosAsync(more, ct))
            found[credit.Id] = credit;
        return found;
    }

    private async Task<SettlementPlan> SalesPlanAsync(
        IReadOnlyDictionary<Guid, Invoice> invoices, IReadOnlyDictionary<Guid, CreditMemo> credits, IReadOnlyList<Retainer> retainers, CancellationToken ct)
    {
        var plan = new SettlementPlan();
        var refunds = (await sales.ListPostedRefundsOfCreditMemosAsync([.. credits.Keys], ct)).ToLookup(r => r.CreditMemoId!.Value);
        var posted = credits.Values.Where(c => c.Status == DocumentStatus.Posted).ToList();
        foreach (var credit in posted)
        {
            plan.Add(credit.Id, IncomeLines(credit.Lines),
                credit.Applications.Select(a => new SettlementPlan.Turn(a.InvoiceId, a.AppliedOn, a.Amount))
                    .Concat(refunds[credit.Id].Select(r => new SettlementPlan.Turn(r.Id, r.Date, r.Amount))));
        }

        var taken = posted.SelectMany(c => c.Applications.Select(a => (a.InvoiceId, Credit: c.Id))).ToLookup(x => x.InvoiceId, x => x.Credit);
        var paid = (await sales.ListPostedPaymentsOnInvoicesAsync([.. invoices.Keys], ct))
            .SelectMany(p => p.Applications.Select(a => (a.InvoiceId, Turn: new SettlementPlan.Turn(p.Id, p.Date, a.Settled))))
            .Concat(retainers.SelectMany(r => r.Applications.Where(a => !a.IsVoided).Select(a => (a.InvoiceId, Turn: new SettlementPlan.Turn(a.Id, a.AppliedOn, a.Amount)))))
            .ToLookup(x => x.InvoiceId, x => x.Turn);
        foreach (var invoice in invoices.Values)
            plan.Add(invoice.Id, SettlementPlan.Less(IncomeLines(invoice.Lines), [.. taken[invoice.Id].SelectMany(c => plan.Of(c, invoice.Id))]), paid[invoice.Id]);
        return plan;
    }

    /// <summary>The vendor credits applied to these bills or refunded in the range.</summary>
    private async Task<Dictionary<Guid, VendorCredit>> VendorCreditsAsync(
        IReadOnlyDictionary<Guid, Bill> bills, IReadOnlyList<VendorRefund> refunds, bool everything, CancellationToken ct)
    {
        if (everything)
            return (await purchases.ListVendorCreditsAsync(null, ct)).ToDictionary(c => c.Id);
        var found = (await purchases.ListPostedVendorCreditsOnBillsAsync([.. bills.Keys], ct)).ToDictionary(c => c.Id);
        var more = refunds.Select(r => r.VendorCreditId).Where(id => !found.ContainsKey(id)).ToHashSet();
        foreach (var credit in await purchases.FindVendorCreditsAsync(more, ct))
            found[credit.Id] = credit;
        return found;
    }

    private async Task<SettlementPlan> BillPlanAsync(IReadOnlyDictionary<Guid, Bill> bills, IReadOnlyDictionary<Guid, VendorCredit> credits, CancellationToken ct)
    {
        var plan = new SettlementPlan();
        var refunds = (await purchases.ListPostedRefundsOfVendorCreditsAsync([.. credits.Keys], ct)).ToLookup(r => r.VendorCreditId);
        var posted = credits.Values.Where(c => c.Status == DocumentStatus.Posted).ToList();
        foreach (var credit in posted)
        {
            plan.Add(credit.Id, [.. credit.Lines.Select(l => (l.AccountId, l.ClassId, l.Amount))],
                credit.Applications.Select(a => new SettlementPlan.Turn(a.BillId, a.AppliedOn, a.Amount))
                    .Concat(refunds[credit.Id].Select(r => new SettlementPlan.Turn(r.Id, r.Date, r.Amount))));
        }

        var taken = posted.SelectMany(c => c.Applications.Select(a => (a.BillId, Credit: c.Id))).ToLookup(x => x.BillId, x => x.Credit);
        var paid = (await purchases.ListPostedPaymentsOnBillsAsync([.. bills.Keys], ct))
            .SelectMany(p => p.Applications.Select(a => (a.BillId, Turn: new SettlementPlan.Turn(p.Id, p.Date, a.Amount))))
            .ToLookup(x => x.BillId, x => x.Turn);
        foreach (var bill in bills.Values)
        {
            plan.Add(bill.Id, SettlementPlan.Less([.. bill.Lines.Select(l => (l.AccountId, l.ClassId, l.Amount))], [.. taken[bill.Id].SelectMany(c => plan.Of(c, bill.Id))]), paid[bill.Id]);
        }

        return plan;
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
