using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Ledger;

public enum CheckOutcome
{
    /// <summary>Everything agrees.</summary>
    Ok,
    /// <summary>Agrees once something people did on purpose is counted (a journal entry to A/R, say); worth knowing.</summary>
    Note,
    /// <summary>Figures that should agree don't.</summary>
    Problem,
}

/// <summary>One thing found, with the record it's about when there is one (an audit entity type and id).</summary>
public sealed record CheckFinding(string Text, string? EntityType = null, Guid? EntityId = null);

public sealed record CheckResult(string Key, string Title, string Description, CheckOutcome Outcome, string Summary, IReadOnlyList<CheckFinding> Findings);

public sealed record BooksCheckReport(DateTimeOffset RanAt, IReadOnlyList<CheckResult> Results)
{
    public CheckOutcome Outcome => Results.Count == 0 ? CheckOutcome.Ok : Results.Max(r => r.Outcome);
    public int Problems => Results.Count(r => r.Outcome == CheckOutcome.Problem);
}

/// <summary>
/// Checks that the books agree with themselves, like QuickBooks' Verify Data: every entry balances, documents and
/// their entries agree, what invoices and bills say is paid matches their payments, and the A/R, A/P, undeposited
/// funds and inventory figures kept beside the ledger match its accounts. Reads only; changes nothing.
/// </summary>
public sealed class BooksCheckService(
    IUnitOfWork unitOfWork,
    ILedgerStore ledger,
    ISalesStore sales,
    IPurchasesStore purchases,
    Accounts.IPartnerStore partners,
    IBankingStore banking,
    AccountService accounts,
    ReceivablesService receivables,
    PayablesService payables,
    DepositService deposits,
    InventoryService inventory,
    ICompanyStore companies,
    IClock clock,
    SalesTax.ISalesTaxStore salesTax,
    Inventory.IInventoryStore stock,
    Assets.IAssetStore assets,
    Nonprofit.INonprofitStore nonprofit,
    Channels.IChannelStore channels,
    Channels.IChannelOrderStore channelOrders)
{
    /// <summary>Most findings listed per check; the summary still counts them all.</summary>
    public const int MaxFindings = 50;

    /// <summary>A date after anything anyone will enter, for "everything, whatever its date".</summary>
    private static readonly DateOnly Always = DateOnly.MaxValue;

    /// <summary>The sources whose entries A/R aging accounts for.</summary>
    public static readonly IReadOnlySet<string> ReceivableSources = new HashSet<string>(StringComparer.Ordinal)
    {
        Invoice.Source, CustomerPayment.Source, CustomerPayment.ReturnSource, CreditMemo.Source, CustomerRefund.Source, BadDebtWriteOff.Source, Retainer.ApplicationSource,
    };

    /// <summary>The sources whose entries put money in, or take it out of, holding accounts such as Undeposited Funds.</summary>
    public static readonly IReadOnlySet<string> UndepositedSources = new HashSet<string>(StringComparer.Ordinal)
    {
        CustomerPayment.Source, SalesReceipt.Source, Retainer.Source, BankDeposit.Source, Domain.Nonprofit.Donation.Source,
        Domain.Nonprofit.Grant.ReceiptSource,
    };

    /// <summary>The sources whose entries A/P aging accounts for.</summary>
    public static readonly IReadOnlySet<string> PayableSources = new HashSet<string>(StringComparer.Ordinal)
    {
        Bill.Source, BillPayment.Source, VendorCredit.Source, VendorRefund.Source,
    };

    public async Task<BooksCheckReport> RunAsync(CancellationToken ct = default)
    {
        // One moment of the books: someone saving between two reads isn't a problem to report.
        await using var snapshot = await unitOfWork.BeginSnapshotAsync(ct);
        var data = await LoadAsync(ct);
        var results = new List<CheckResult>
        {
            EntriesBalance(data),
            DocumentsHaveTheirEntries(data),
            InvoicesPaid(data),
            BillsPaid(data),
            await ReceivablesTieAsync(data, ct),
            await PayablesTieAsync(data, ct),
            await UndepositedTieAsync(data, ct),
            await InventoryTieAsync(data, ct),
            CustomerDepositsTie(data),
            await ReceivedNotBilledTieAsync(data, ct),
            await PledgesTieAsync(data, ct),
            await GrantsTieAsync(data, ct),
            await DepreciationTieAsync(data, ct),
            await LoansTieAsync(data, ct),
            await ReconciliationsAsync(data, ct),
            await StockBelowZeroAsync(ct),
        };
        return new BooksCheckReport(clock.UtcNow, results);
    }

    private async Task<Data> LoadAsync(CancellationToken ct) => new(
        await accounts.GetChartAsync(ct),
        await ledger.ListEntryHeadersAsync(ct),
        await ledger.GetLinesAsync(null, Always, ct),
        await sales.ListInvoicesAsync(null, ct),
        await sales.ListPaymentsAsync(null, ct),
        await sales.ListReceiptsAsync(ct),
        await sales.ListCreditMemosAsync(null, ct),
        await sales.ListRefundsAsync(null, ct),
        await sales.ListWriteOffsAsync(null, ct),
        await sales.ListRetainersAsync(null, ct),
        await sales.ListDepositsAsync(ct),
        await purchases.ListBillsAsync(null, ct),
        await purchases.ListPaymentsAsync(null, ct),
        await purchases.ListVendorCreditsAsync(null, ct),
        await purchases.ListVendorRefundsAsync(null, ct),
        await banking.ListAllTransactionsAsync(ct),
        await nonprofit.ListDonationsAsync(ct),
        await OtherDocumentsAsync(ct));

    /// <summary>
    /// Everything else that posts: documents with an entry each, and rows of a record that each post their own (the
    /// entry's source id is then the record's). A new one goes here, or in <see cref="Documents"/> if the other checks use it.
    /// </summary>
    private async Task<IReadOnlyList<Document>> OtherDocumentsAsync(CancellationToken ct)
    {
        var list = new List<Document>();
        void Add(string label, string entityType, Guid id, string source, Guid? entryId, bool posted)
        {
            if (entryId is { } entry && entry != Guid.Empty)
                list.Add(new Document(label, entityType, id, source, entry, posted));
        }

        foreach (var a in await partners.ListAllocationsAsync(ct))
            Add($"Partner allocation for {a.YearEnd:yyyy}", "partner_allocation", a.Id, Domain.Accounts.PartnerAllocation.Source, a.JournalEntryId, !a.IsVoided);
        foreach (var v in await purchases.ListVendorDepositsAsync(null, ct))
            Add($"Vendor deposit of {v.Date:MMM d, yyyy}", "vendor_deposit", v.Id, VendorDeposit.Source, v.JournalEntryId, v.Status == DocumentStatus.Posted);
        foreach (var p in await salesTax.ListPaymentsAsync(null, ct))
            Add($"Sales tax payment of {p.Date:MMM d, yyyy}", "sales_tax_payment", p.Id, Domain.SalesTax.SalesTaxPayment.Source, p.JournalEntryId, p.Status == DocumentStatus.Posted);
        foreach (var a in await stock.ListAdjustmentsAsync(ct))
            Add($"Inventory adjustment of {a.Date:MMM d, yyyy}", "inventory_adjustment", a.Id, Domain.Inventory.InventoryAdjustment.Source, a.JournalEntryId, a.Status == DocumentStatus.Posted);
        foreach (var b in await stock.ListBuildsAsync(ct))
            Add($"Assembly build of {b.Date:MMM d, yyyy}", "assembly_build", b.Id, Domain.Inventory.AssemblyBuild.Source, b.JournalEntryId, b.Status == DocumentStatus.Posted);
        foreach (var l in await stock.ListLandedCostsAsync(ct))
            Add($"Landed cost of {l.Date:MMM d, yyyy}", "landed_cost", l.Id, Domain.Inventory.LandedCost.Source, l.JournalEntryId, l.Status == DocumentStatus.Posted);
        foreach (var r in await assets.ListRunsAsync(ct))
            Add($"Depreciation through {r.Date:MMM d, yyyy}", "depreciation_run", r.Id, Domain.Assets.DepreciationRun.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted);
        foreach (var a in await assets.ListAssetsAsync(ct))
            Add($"Disposal of {a.Name}", "fixed_asset", a.Id, Domain.Assets.FixedAsset.DisposalSource, a.DisposalEntryId, true);
        foreach (var l in await assets.ListLoansAsync(ct))
        {
            Add($"Proceeds of {l.Name}", "loan", l.Id, Domain.Assets.Loan.Source, l.ProceedsEntryId, !l.IsVoided);
            foreach (var p in l.PaymentsMade)
                Add($"Payment on {l.Name} of {p.Date:MMM d, yyyy}", "loan", l.Id, Domain.Assets.Loan.PaymentSource, p.JournalEntryId, !p.IsVoided);
            foreach (var w in l.Draws)
                Add($"Draw on {l.Name} of {w.Date:MMM d, yyyy}", "loan", l.Id, Domain.Assets.Loan.DrawSource, w.EntryId, !w.IsVoided);
        }

        foreach (var r in await nonprofit.ListAllocationRunsAsync(ct))
            Add($"Cost allocation of {r.To:MMM d, yyyy}", "allocation_run", r.Id, Domain.Nonprofit.AllocationRun.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted);
        foreach (var r in await nonprofit.ListReleasesAsync(ct))
            Add($"Restriction release of {r.Date:MMM d, yyyy}", "restriction_release", r.Id, Domain.Nonprofit.RestrictionRelease.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted);
        foreach (var g in await nonprofit.ListDonationsAsync(ct))
        {
            Add($"Gift {g.Number}", "donation", g.Id, Domain.Nonprofit.Donation.Source, g.JournalEntryId, g.Status == DocumentStatus.Posted);
            Add($"Gift {g.Number}, its cash-basis entry", "donation", g.Id, Domain.Nonprofit.Donation.Source, g.CashEntryId, g.Status == DocumentStatus.Posted);
        }

        foreach (var p in await nonprofit.ListPledgesAsync(ct))
        {
            Add($"Pledge {p.Number}", "pledge", p.Id, Domain.Nonprofit.Pledge.Source, p.JournalEntryId, p.Status == DocumentStatus.Posted);
            Add($"Pledge {p.Number}, the cancelled part", "pledge", p.Id, Domain.Nonprofit.Pledge.CancelSource, p.CancelEntryId, true);
        }

        foreach (var g in await nonprofit.ListGrantsAsync(ct))
        {
            var standing = g.Status != Domain.Nonprofit.GrantStatus.Voided;
            Add($"Grant {g.Name}", "grant", g.Id, Domain.Nonprofit.Grant.AwardSource, g.AwardEntryId, standing);
            foreach (var r in g.Receipts)
            {
                Add($"Receipt on grant {g.Name} of {r.Date:MMM d, yyyy}", "grant", g.Id, Domain.Nonprofit.Grant.ReceiptSource, r.EntryId, !r.IsVoided);
                Add($"Receipt on grant {g.Name} of {r.Date:MMM d, yyyy}, its cash-basis entry", "grant", g.Id, Domain.Nonprofit.Grant.ReceiptSource, r.CashEntryId, !r.IsVoided);
            }

            foreach (var r in g.Recognitions)
                Add($"Recognition on grant {g.Name} of {r.Date:MMM d, yyyy}", "grant", g.Id, Domain.Nonprofit.Grant.RecognitionSource, r.EntryId, !r.IsVoided);
            foreach (var a in g.Adjustments)
            {
                Add($"Adjustment to grant {g.Name} of {a.Date:MMM d, yyyy}", "grant", g.Id, Domain.Nonprofit.Grant.AdjustmentSource, a.EntryId, !a.IsVoided);
                Add($"Adjustment to grant {g.Name} of {a.Date:MMM d, yyyy}, its cash-basis entry", "grant", g.Id, Domain.Nonprofit.Grant.AdjustmentSource, a.CashEntryId, !a.IsVoided);
            }
        }

        foreach (var r in await sales.ListRetainersAsync(null, ct))
        {
            foreach (var a in r.Applications)
                Add($"Retainer applied on {a.AppliedOn:MMM d, yyyy}", "retainer", r.Id, Retainer.ApplicationSource, a.JournalEntryId, !a.IsVoided);
            foreach (var x in r.Refunds)
                Add($"Retainer refund of {x.Date:MMM d, yyyy}", "retainer", r.Id, Retainer.RefundSource, x.JournalEntryId, !x.IsVoided);
        }

        foreach (var o in await purchases.ListPurchaseOrdersAsync(null, ct))
        {
            foreach (var row in o.Receipts.Where(x => x.StockSourceId is not null))
                Add($"Delivery {row.ReceiptNumber} on purchase order {o.Number}", "purchase_order", row.StockSourceId!.Value, Purchases.PurchaseOrderService.ReceiptSource, row.JournalEntryId, !row.IsVoided);
        }

        foreach (var p in await channels.ListPayoutsAsync(null, ct))
            Add($"Payout of {p.Date:MMM d, yyyy}", "channel_payout", p.Id, Domain.Channels.ChannelPayout.Source, p.JournalEntryId, p.Status == Domain.Channels.PayoutStatus.Posted);
        foreach (var b in await channelOrders.ListBatchesAsync(null, ct))
        {
            Add($"Orders of {b.Date:MMM d, yyyy}", "channel_orders", b.Id, Domain.Channels.ChannelOrderBatch.Source, b.JournalEntryId, b.Status == Domain.Channels.PayoutStatus.Posted);
            foreach (var r in await channelOrders.ListReturnsAsync(b.Id, ct))
                Add($"Return of {r.Date:MMM d, yyyy}", "channel_return", r.Id, Domain.Channels.ChannelReturn.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted);
        }

        return list;
    }

    private sealed record Data(
        ChartOfAccounts Chart,
        IReadOnlyList<EntryHeader> Entries,
        IReadOnlyList<LedgerLine> Lines,
        IReadOnlyList<Invoice> Invoices,
        IReadOnlyList<CustomerPayment> Payments,
        IReadOnlyList<SalesReceipt> Receipts,
        IReadOnlyList<CreditMemo> CreditMemos,
        IReadOnlyList<CustomerRefund> Refunds,
        IReadOnlyList<BadDebtWriteOff> WriteOffs,
        IReadOnlyList<Retainer> Retainers,
        IReadOnlyList<BankDeposit> Deposits,
        IReadOnlyList<Bill> Bills,
        IReadOnlyList<BillPayment> BillPayments,
        IReadOnlyList<VendorCredit> VendorCredits,
        IReadOnlyList<VendorRefund> VendorRefunds,
        IReadOnlyList<BankTransaction> BankTransactions,
        IReadOnlyList<Domain.Nonprofit.Donation> Gifts,
        IReadOnlyList<Document> OtherDocuments)
    {
        /// <summary>Lines on the accrual basis (cash-only entries, such as a pledge's cash side, left out).</summary>
        public IEnumerable<LedgerLine> Accrual => Lines.Where(l => l.Basis.AppliesTo(ReportBasis.Accrual));
    }

    /// <summary>A document that posts one entry, as the checks see it.</summary>
    private sealed record Document(string Label, string EntityType, Guid Id, string Source, Guid EntryId, bool Posted);

    private static IEnumerable<Document> Documents(Data d) =>
    [
        .. d.Invoices.Select(i => new Document($"Invoice {i.Number}", "invoice", i.Id, Invoice.Source, i.JournalEntryId, i.Status == DocumentStatus.Posted)),
        .. d.Payments.Select(p => new Document($"Customer payment of {p.Date:MMM d, yyyy}", "payment", p.Id, CustomerPayment.Source, p.JournalEntryId, p.Status == DocumentStatus.Posted)),
        .. d.Payments.Where(p => p.ReturnEntryId is not null).Select(p => new Document($"Return of the customer payment of {p.Date:MMM d, yyyy}", "payment", p.Id, CustomerPayment.ReturnSource,
            p.ReturnEntryId!.Value, p.Status == DocumentStatus.Posted)),
        .. d.Receipts.Select(r => new Document($"Sales receipt {r.Number}", "sales_receipt", r.Id, SalesReceipt.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted)),
        .. d.CreditMemos.Select(c => new Document($"Credit memo {c.Number}", "credit_memo", c.Id, CreditMemo.Source, c.JournalEntryId, c.Status == DocumentStatus.Posted)),
        .. d.Refunds.Select(r => new Document($"Customer refund of {r.Date:MMM d, yyyy}", "refund", r.Id, CustomerRefund.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted)),
        .. d.WriteOffs.Select(w => new Document($"Write-off of {w.Date:MMM d, yyyy}", "write_off", w.Id, BadDebtWriteOff.Source, w.JournalEntryId, w.Status == DocumentStatus.Posted)),
        .. d.Retainers.Select(r => new Document($"Retainer of {r.Date:MMM d, yyyy}", "retainer", r.Id, Retainer.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted)),
        .. d.Deposits.Select(x => new Document($"Deposit of {x.Date:MMM d, yyyy}", "deposit", x.Id, BankDeposit.Source, x.JournalEntryId, x.Status == DocumentStatus.Posted)),
        .. d.Bills.Select(b => new Document(BillLabel(b), "bill", b.Id, Bill.Source, b.JournalEntryId, b.Status == DocumentStatus.Posted)),
        .. d.BillPayments.Select(p => new Document($"Bill payment of {p.Date:MMM d, yyyy}", "bill_payment", p.Id, BillPayment.Source, p.JournalEntryId, p.Status == DocumentStatus.Posted)),
        .. d.VendorCredits.Select(c => new Document($"Vendor credit of {c.Date:MMM d, yyyy}", "vendor_credit", c.Id, VendorCredit.Source, c.JournalEntryId, c.Status == DocumentStatus.Posted)),
        .. d.VendorRefunds.Select(r => new Document($"Vendor refund of {r.Date:MMM d, yyyy}", "vendor_refund", r.Id, VendorRefund.Source, r.JournalEntryId, r.Status == DocumentStatus.Posted)),
        .. d.BankTransactions.Select(t => new Document($"{Describe(t.Kind)} of {t.Date:MMM d, yyyy}", "bank_transaction", t.Id, BankTransaction.SourceFor(t.Kind), t.JournalEntryId,
            t.Status == BankTransactionStatus.Posted)),
        .. d.OtherDocuments,
    ];

    private static string BillLabel(Bill bill) => bill.Reference is { } number ? $"Bill {number}" : $"Bill of {bill.Date:MMM d, yyyy}";

    private static string Describe(BankTransactionKind kind) => kind switch
    {
        BankTransactionKind.Spend => "Expense",
        BankTransactionKind.Receive => "Bank deposit",
        _ => "Transfer",
    };

    private static CheckResult EntriesBalance(Data d)
    {
        var findings = new List<CheckFinding>();
        foreach (var entry in d.Entries.Where(e => !e.IsSealed).OrderBy(e => e.Number))
            findings.Add(new($"Entry #{entry.Number} ({entry.Date:MMM d, yyyy}) was never sealed, so reports leave it out.", "journal_entry", entry.Id));
        foreach (var g in d.Lines.GroupBy(l => l.JournalEntryId).Where(g => g.Sum(l => l.Amount) != 0m).OrderBy(g => g.First().EntryNumber))
            findings.Add(new($"Entry #{g.First().EntryNumber} is out of balance by {g.Sum(l => l.Amount):N2}.", "journal_entry", g.Key));
        var total = d.Lines.Sum(l => l.Amount);
        if (total != 0m && findings.Count == 0)
            findings.Add(new($"Debits and credits differ by {total:N2} across the ledger."));

        return Result("entries-balance", "Every entry balances",
            "Each journal entry's debits equal its credits, and every saved entry is sealed.",
            findings, $"{d.Entries.Count:N0} entries checked.");
    }

    private static CheckResult DocumentsHaveTheirEntries(Data d)
    {
        var entries = d.Entries.ToDictionary(e => e.Id);
        var documents = Documents(d).ToList();
        var findings = new List<CheckFinding>();
        foreach (var doc in documents)
        {
            if (!entries.TryGetValue(doc.EntryId, out var entry))
                findings.Add(new($"{doc.Label}: its journal entry is missing.", doc.EntityType, doc.Id));
            else if (doc.Posted && !entry.IsStanding)
                findings.Add(new($"{doc.Label} is posted, but its entry #{entry.Number} is voided.", doc.EntityType, doc.Id));
            else if (!doc.Posted && entry.IsStanding)
                findings.Add(new($"{doc.Label} is voided, but its entry #{entry.Number} still stands.", doc.EntityType, doc.Id));
        }

        // Entries that still stand for a document that doesn't (a void or correction that left one behind).
        var standingFor = documents.Where(x => x.Posted).Select(x => (x.Source, x.Id, x.EntryId)).ToHashSet();
        var sources = documents.Select(x => x.Source).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in d.Entries.Where(e => e.IsStanding && e.IsSealed && sources.Contains(e.Source)).OrderBy(e => e.Number))
        {
            if (entry.SourceId is { } sourceId && standingFor.Contains((entry.Source, sourceId, entry.Id)))
                continue;
            findings.Add(new($"Entry #{entry.Number} ({entry.Date:MMM d, yyyy}) still stands, but no posted document uses it.", "journal_entry", entry.Id));
        }

        return Result("documents-entries", "Documents and their entries agree",
            "Each posted document has its journal entry standing, each voided one has it voided, and no entry is left standing for a document that no longer uses it.",
            findings, $"{documents.Count:N0} documents checked.");
    }

    private static CheckResult InvoicesPaid(Data d)
    {
        var settled = new Dictionary<Guid, decimal>();
        void Add(Guid invoiceId, decimal amount) => settled[invoiceId] = settled.GetValueOrDefault(invoiceId) + amount;
        foreach (var p in d.Payments.Where(p => p.Status == DocumentStatus.Posted && p.ReturnedOn is null))
            foreach (var a in p.Applications)
                Add(a.InvoiceId, a.Settled);
        foreach (var c in d.CreditMemos.Where(c => c.Status == DocumentStatus.Posted))
            foreach (var a in c.Applications)
                Add(a.InvoiceId, a.Amount);
        foreach (var w in d.WriteOffs.Where(w => w.Status == DocumentStatus.Posted))
            Add(w.InvoiceId, w.Amount);
        foreach (var r in d.Retainers.Where(r => r.Status == DocumentStatus.Posted))
            foreach (var a in r.Applications.Where(a => !a.IsVoided))
                Add(a.InvoiceId, a.Amount);

        var findings = new List<CheckFinding>();
        foreach (var invoice in d.Invoices.OrderBy(i => i.Date))
        {
            var applied = settled.GetValueOrDefault(invoice.Id);
            if (invoice.Status != DocumentStatus.Posted)
            {
                if (applied != 0m)
                    findings.Add(new($"Invoice {invoice.Number} is voided but still has {applied:N2} applied to it.", "invoice", invoice.Id));
                continue;
            }

            if (invoice.AmountPaid != applied)
                findings.Add(new($"Invoice {invoice.Number} shows {invoice.AmountPaid:N2} paid; its payments, credits, retainers and write-offs add up to {applied:N2}.", "invoice", invoice.Id));
            else if (applied > invoice.Total)
                findings.Add(new($"Invoice {invoice.Number} has {applied:N2} applied to a total of {invoice.Total:N2}.", "invoice", invoice.Id));
        }

        return Result("invoices-paid", "Invoices show what's been paid",
            "What each invoice shows as paid equals the payments, credits, retainers and write-offs applied to it.",
            findings, $"{d.Invoices.Count:N0} invoices checked.");
    }

    private static CheckResult BillsPaid(Data d)
    {
        var settled = new Dictionary<Guid, decimal>();
        void Add(Guid billId, decimal amount) => settled[billId] = settled.GetValueOrDefault(billId) + amount;
        foreach (var p in d.BillPayments.Where(p => p.Status == DocumentStatus.Posted))
            foreach (var a in p.Applications)
                Add(a.BillId, a.Amount);
        foreach (var c in d.VendorCredits.Where(c => c.Status == DocumentStatus.Posted))
            foreach (var a in c.Applications)
                Add(a.BillId, a.Amount);

        var findings = new List<CheckFinding>();
        foreach (var bill in d.Bills.OrderBy(b => b.Date))
        {
            var applied = settled.GetValueOrDefault(bill.Id);
            if (bill.Status != DocumentStatus.Posted)
            {
                if (applied != 0m)
                    findings.Add(new($"{BillLabel(bill)} is voided but still has {applied:N2} applied to it.", "bill", bill.Id));
                continue;
            }

            if (bill.AmountPaid != applied)
                findings.Add(new($"{BillLabel(bill)} shows {bill.AmountPaid:N2} paid; its payments and credits add up to {applied:N2}.", "bill", bill.Id));
            else if (applied > bill.Total)
                findings.Add(new($"{BillLabel(bill)} has {applied:N2} applied to a total of {bill.Total:N2}.", "bill", bill.Id));
        }

        return Result("bills-paid", "Bills show what's been paid",
            "What each bill shows as paid equals the bill payments and vendor credits applied to it.",
            findings, $"{d.Bills.Count:N0} bills checked.");
    }

    private async Task<CheckResult> ReceivablesTieAsync(Data d, CancellationToken ct)
    {
        var aging = (await receivables.AgingAsync(Always, ct)).Totals.Total;
        return TieOut("receivables", "A/R aging matches the books",
            "Everything customers owe by the documents (A/R aging, all dates) equals the accounts receivable accounts. Journal entries to those accounts are counted separately, since aging can't list them.",
            d, AccountType.AccountsReceivable, aging, "A/R aging", l => !ReceivableSources.Contains(l.Source));
    }

    private async Task<CheckResult> PayablesTieAsync(Data d, CancellationToken ct)
    {
        // Payables are credit balances; aging counts them as positive.
        var aging = (await payables.AgingAsync(Always, ct)).Totals.Total;
        return TieOut("payables", "A/P aging matches the books",
            "Everything owed to vendors by the documents (A/P aging, all dates) equals the accounts payable accounts. Journal entries to those accounts are counted separately.",
            d, AccountType.AccountsPayable, -aging, "A/P aging", l => !PayableSources.Contains(l.Source), credit: true);
    }

    private static CheckResult TieOut(string key, string title, string description, Data d, AccountType type, decimal expected, string what,
        Func<LedgerLine, bool> other, bool credit = false)
    {
        var accountIds = d.Chart.Ordered().Where(x => x.Account.Type == type).Select(x => x.Account.Id).ToHashSet();
        var lines = d.Accrual.Where(l => accountIds.Contains(l.AccountId)).ToList();
        var others = lines.Where(other).ToList();
        return Compare(key, title, description, expected, lines.Sum(l => l.Amount), others, what, "the accounts", credit);
    }

    /// <summary>
    /// Compares a figure kept beside the ledger with the ledger. Lines from other sources explain a difference
    /// (a note); anything else left over is a problem.
    /// </summary>
    private static CheckResult Compare(string key, string title, string description, decimal expected, decimal books, IReadOnlyList<LedgerLine> others,
        string what, string where, bool credit = false)
    {
        // Shown as people read them: payables as positive amounts owed.
        decimal Shown(decimal amount) => credit ? -amount : amount;
        var otherTotal = others.Sum(l => l.Amount);
        var unexplained = books - otherTotal - expected;
        var findings = new List<CheckFinding>();
        if (unexplained != 0m)
            findings.Add(new($"{what} is {Shown(expected):N2}; {where} show {Shown(books):N2}" +
                (otherTotal == 0m ? "." : $", of which {Shown(otherTotal):N2} is from journal entries or other records.") +
                $" Difference: {Shown(unexplained):N2}."));
        var noted = others.GroupBy(l => l.JournalEntryId).Where(g => g.Sum(l => l.Amount) != 0m)
            .OrderBy(g => g.First().EntryNumber)
            .Select(g => new CheckFinding($"Entry #{g.First().EntryNumber} ({g.First().Date:MMM d, yyyy}) puts {Shown(g.Sum(l => l.Amount)):N2} there directly.", "journal_entry", g.Key))
            .ToList();

        if (unexplained != 0m)
            return new CheckResult(key, title, description, CheckOutcome.Problem, $"Off by {Shown(unexplained):N2}.", [.. findings, .. noted.Take(MaxFindings - 1)]);
        if (noted.Count > 0)
            return new CheckResult(key, title, description, CheckOutcome.Note,
                $"Matches, counting {noted.Count:N0} {(noted.Count == 1 ? "entry" : "entries")} made directly to {where} ({Shown(otherTotal):N2}).", noted.Take(MaxFindings).ToList());
        return new CheckResult(key, title, description, CheckOutcome.Ok, $"Both show {Shown(expected):N2}.", []);
    }

    private async Task<CheckResult> UndepositedTieAsync(Data d, CancellationToken ct)
    {
        // Holding accounts: any account a payment, receipt, retainer or gift of money was ever recorded to, other than a bank.
        var waiting = await deposits.UndepositedAsync(ct);
        var holding = d.Payments.Select(p => p.DepositAccountId)
            .Concat(d.Receipts.Select(r => r.DepositAccountId))
            .Concat(d.Retainers.Select(r => r.DepositAccountId))
            .Concat(d.Gifts.Where(g => g.Kind == Domain.Nonprofit.GiftKind.Cash).Select(g => g.DebitAccountId))
            .Concat(waiting.Select(w => w.AccountId))
            .Concat((await nonprofit.ListGrantsAsync(ct)).SelectMany(g => g.Receipts).Select(r => r.BankAccountId))
            .Where(id => d.Chart.Find(id)?.Type == AccountType.OtherCurrentAsset)
            .ToHashSet();
        var lines = d.Accrual.Where(l => holding.Contains(l.AccountId)).ToList();
        var others = lines.Where(l => !UndepositedSources.Contains(l.Source)).ToList();
        var names = string.Join(", ", holding.Select(id => d.Chart.Find(id)!.Name).Order(StringComparer.CurrentCultureIgnoreCase));
        return Compare("undeposited", "Undeposited funds match what's waiting",
            "The money in holding accounts such as Undeposited Funds equals the payments, receipts, retainers and gifts waiting to be deposited.",
            waiting.Sum(w => w.Amount), lines.Sum(l => l.Amount), others,
            $"Waiting to be deposited", holding.Count == 0 ? "the holding accounts" : names);
    }

    private async Task<CheckResult> InventoryTieAsync(Data d, CancellationToken ct)
    {
        var stock = await inventory.StockAsync(null, ct);
        var assetAccounts = stock.Where(s => s.Item.InventoryAssetAccountId is not null).Select(s => s.Item.InventoryAssetAccountId!.Value).ToHashSet();
        var lines = d.Accrual.Where(l => assetAccounts.Contains(l.AccountId)).ToList();

        // What reached those accounts without moving any stock: entered by hand, spent to them, or a bill's category line.
        var plain = d.Bills.SelectMany(b => b.Lines.Where(l => l.ItemId is null).Select(l => (b.Id, l.AccountId, l.Amount))).ToHashSet();
        var others = lines.Where(l => JournalEntry.IsJournalSource(l.Source) || l.Source.StartsWith("bank.", StringComparison.Ordinal)
            || (l.Source == Bill.Source && l.SourceId is { } bill && plain.Contains((bill, l.AccountId, l.Amount)))).ToList();
        return Compare("inventory", "Stock value matches the books",
            "The cost of everything in stock equals the inventory asset accounts.",
            stock.Sum(s => s.Value), lines.Sum(l => l.Amount), others, "Stock on hand at cost", "the inventory asset accounts");
    }

    /// <summary>The lines on these accounts, with those from other sources set apart (they explain a difference).</summary>
    private static (List<LedgerLine> Lines, List<LedgerLine> Others) On(Data d, HashSet<Guid> accountIds, params string[] sources)
    {
        var lines = d.Accrual.Where(l => accountIds.Contains(l.AccountId)).ToList();
        return (lines, lines.Where(l => !sources.Contains(l.Source, StringComparer.Ordinal)).ToList());
    }

    private static string Names(Data d, IEnumerable<Guid> accountIds, string none) =>
        accountIds.Select(id => d.Chart.Find(id)?.Name).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToList() is { Count: > 0 } names
            ? string.Join(", ", names) : none;

    private static CheckResult CustomerDepositsTie(Data d)
    {
        var accountIds = d.Retainers.Select(r => r.LiabilityAccountId).ToHashSet();
        var (lines, others) = On(d, accountIds, Retainer.Source, Retainer.ApplicationSource, Retainer.RefundSource);
        return Compare("customer-deposits", "Customer deposits match the retainers",
            "What customers paid ahead (retainers not yet applied or refunded) equals the account the retainers are kept in.",
            -d.Retainers.Where(r => r.Status == DocumentStatus.Posted).Sum(r => r.Balance), lines.Sum(l => l.Amount), others,
            "Retainers still held", Names(d, accountIds, "the customer deposits account"), credit: true);
    }

    /// <summary>Deliveries received into stock credit "Received Not Billed" at their value; bills from the order clear what they accrue.</summary>
    private async Task<CheckResult> ReceivedNotBilledTieAsync(Data d, CancellationToken ct)
    {
        var account = d.Chart.FindByFullName(Purchases.PurchaseOrderService.ReceivedNotBilledName);
        var accountIds = account is null ? new HashSet<Guid>() : [account.Id];
        var (lines, others) = On(d, accountIds, Purchases.PurchaseOrderService.ReceiptSource, Bill.Source);
        var received = (await purchases.ListPurchaseOrdersAsync(null, ct)).SelectMany(o => o.Receipts).Where(r => !r.IsVoided && r.JournalEntryId is not null).Sum(r => r.Value);
        var cleared = d.Bills.Where(b => b.Status == DocumentStatus.Posted).SelectMany(b => b.SourceLines).Sum(s => s.AccruedAmount);
        return Compare("received-not-billed", "Received Not Billed matches the deliveries",
            "Goods received into stock from purchase orders, less what bills from those orders cleared, equals the Received Not Billed account.",
            -(received - cleared), lines.Sum(l => l.Amount), others, "Deliveries not billed", Names(d, accountIds, "Received Not Billed"), credit: true);
    }

    private async Task<CheckResult> PledgesTieAsync(Data d, CancellationToken ct)
    {
        var pledges = await nonprofit.ListPledgesAsync(ct);
        var accountIds = pledges.Select(p => p.ReceivableAccountId).ToHashSet();
        var (lines, others) = On(d, accountIds, Domain.Nonprofit.Pledge.Source, Domain.Nonprofit.Pledge.CancelSource, Domain.Nonprofit.Donation.Source);
        var standing = pledges.Where(p => p.Status == DocumentStatus.Posted).Select(p => p.Id).ToHashSet();
        var promised = pledges.Where(p => standing.Contains(p.Id)).Sum(p => p.Amount - p.CancelledAmount);
        var paid = d.Gifts.Where(g => g.Status == DocumentStatus.Posted && g.PledgeId is { } id && standing.Contains(id)).Sum(g => g.Amount);
        return Compare("pledges", "Pledges receivable match the open pledges",
            "What's still to come on pledges (promised, less cancelled and paid) equals the pledges receivable account, on the accrual basis.",
            promised - paid, lines.Sum(l => l.Amount), others, "Open pledges", Names(d, accountIds, "the pledges receivable account"));
    }

    /// <summary>Grants receivable (debit) and refundable advances (credit) together, against each grant's own figures.</summary>
    private async Task<CheckResult> GrantsTieAsync(Data d, CancellationToken ct)
    {
        var grants = (await nonprofit.ListGrantsAsync(ct)).Where(g => g.Status != Domain.Nonprofit.GrantStatus.Voided).ToList();
        var all = await nonprofit.ListGrantsAsync(ct);
        var accountIds = all.Select(g => g.ReceivableAccountId).Concat(all.Select(g => g.AdvanceAccountId).OfType<Guid>()).ToHashSet();
        var (lines, others) = On(d, accountIds, Domain.Nonprofit.Grant.AwardSource, Domain.Nonprofit.Grant.ReceiptSource,
            Domain.Nonprofit.Grant.RecognitionSource, Domain.Nonprofit.Grant.AdjustmentSource);
        return Compare("grants", "Grants receivable and advances match the grants",
            "What funders still owe for income recognized, less money they paid ahead and isn't earned yet, equals the grants receivable and refundable advance accounts, on the accrual basis.",
            grants.Sum(g => g.Receivable) - grants.Sum(g => g.Advance), lines.Sum(l => l.Amount), others, "The grants",
            Names(d, accountIds, "the grants receivable account"));
    }

    private async Task<CheckResult> DepreciationTieAsync(Data d, CancellationToken ct)
    {
        var assetList = (await assets.ListAssetsAsync(ct)).Where(a => !a.IsRemoved).ToList();
        var runs = (await assets.ListRunsAsync(ct)).Where(r => r.Status == DocumentStatus.Posted).ToList();
        var posted = runs.SelectMany(r => r.Lines).GroupBy(l => l.AssetId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var accountIds = assetList.Select(a => a.AccumulatedAccountId).ToHashSet();
        var (lines, others) = On(d, accountIds, Domain.Assets.DepreciationRun.Source, Domain.Assets.FixedAsset.DisposalSource);
        // OpenBook's own lines: each run credits its depreciation; a disposal debits all that was accumulated, including any
        // depreciation from before OpenBook (which came in some other way, so it's counted with the other lines).
        var expected = assetList.Sum(a => a.IsDisposed ? a.PriorDepreciation : -posted.GetValueOrDefault(a.Id));
        return Compare("depreciation", "Accumulated depreciation matches the asset register",
            "The depreciation posted for each asset still held (and taken out for each one sold or disposed of) equals the accumulated depreciation accounts. Depreciation from before OpenBook, entered as a journal entry or opening balance, is counted separately.",
            expected, lines.Sum(l => l.Amount), others, "The asset register", Names(d, accountIds, "the accumulated depreciation accounts"), credit: true);
    }

    private async Task<CheckResult> LoansTieAsync(Data d, CancellationToken ct)
    {
        var loans = await assets.ListLoansAsync(ct);
        var accountIds = loans.Select(l => l.LiabilityAccountId).ToHashSet();
        var (lines, others) = On(d, accountIds, Domain.Assets.Loan.Source, Domain.Assets.Loan.PaymentSource, Domain.Assets.Loan.DrawSource);
        // A loan whose money was entered with it credits the principal; otherwise it's in the books some other way.
        var expected = loans.Where(l => !l.IsVoided)
            .Sum(l => (l.ProceedsAccountId is null ? 0m : -l.Principal) - l.Drawn + l.PaymentsMade.Where(p => !p.IsVoided).Sum(p => p.Principal));
        return Compare("loans", "Loan balances match the books",
            "What's left to repay on each loan, by its payments, equals the loan accounts. A loan entered without its money (already in the books) is counted from the entry that put it there.",
            expected, lines.Sum(l => l.Amount), others, "The loans' balances", Names(d, accountIds, "the loan accounts"), credit: true);
    }

    private async Task<CheckResult> ReconciliationsAsync(Data d, CancellationToken ct)
    {
        var lines = d.Lines.ToDictionary(l => l.JournalLineId);
        var entries = d.Entries.ToDictionary(e => e.Id);
        var findings = new List<CheckFinding>();
        var checkedCount = 0;
        foreach (var (account, _) in d.Chart.Ordered().Where(x => x.Account.Type is AccountType.Bank or AccountType.CreditCard))
        {
            var reconciled = 0m;
            var completed = (await banking.ListReconciliationsAsync(account.Id, ct))
                .Where(r => r.Status == ReconciliationStatus.Completed).OrderBy(r => r.StatementDate).ToList();
            foreach (var r in completed)
            {
                checkedCount++;
                var label = $"{account.Name} statement of {r.StatementDate:MMM d, yyyy}";
                var cleared = 0m;
                foreach (var lineId in r.ClearedLineIds)
                {
                    if (!lines.TryGetValue(lineId, out var line))
                    {
                        findings.Add(new($"{label}: a cleared line is no longer in the ledger.", "reconciliation", r.Id));
                        continue;
                    }

                    if (line.AccountId != account.Id)
                        findings.Add(new($"{label}: entry #{line.EntryNumber}'s cleared line is on another account.", "reconciliation", r.Id));
                    else if (!entries[line.JournalEntryId].IsStanding)
                        findings.Add(new($"{label}: entry #{line.EntryNumber} is cleared but voided.", "reconciliation", r.Id));
                    cleared += BankingRules.Natural(account, line.Amount);
                }

                if (r.BeginningBalance != reconciled)
                    findings.Add(new($"{label} starts from {r.BeginningBalance:N2}, but earlier statements reconciled {reconciled:N2}.", "reconciliation", r.Id));
                if (r.BeginningBalance + cleared != r.StatementEndingBalance)
                    findings.Add(new($"{label}: {r.BeginningBalance:N2} plus {cleared:N2} cleared doesn't reach the statement's {r.StatementEndingBalance:N2}.", "reconciliation", r.Id));
                reconciled += cleared;
            }
        }

        return Result("reconciliations", "Reconciliations still add up",
            "Each finished reconciliation starts where the last one ended, and its cleared transactions still reach the statement's ending balance.",
            findings, $"{checkedCount:N0} finished reconciliations checked.");
    }

    private async Task<CheckResult> StockBelowZeroAsync(CancellationToken ct)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var stock = await inventory.StockAsync(null, ct);
        var findings = stock.Where(s => s.Quantity < 0m && !s.Item.CanSellShort(company.AllowSellingShort))
            .OrderBy(s => s.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new CheckFinding($"{s.Item.Name} is at {s.Quantity:0.####} on hand, but it isn't allowed below zero.", "item", s.Item.Id))
            .ToList();
        return Result("stock-below-zero", "No stock below zero unless allowed",
            "Items are below zero on hand only when they may be sold short.",
            findings, $"{stock.Count:N0} items checked.");
    }

    private static CheckResult Result(string key, string title, string description, List<CheckFinding> findings, string whenOk) =>
        findings.Count == 0
            ? new CheckResult(key, title, description, CheckOutcome.Ok, whenOk, [])
            : new CheckResult(key, title, description, CheckOutcome.Problem,
                findings.Count == 1 ? "1 thing to look at." : $"{findings.Count:N0} things to look at.", findings.Take(MaxFindings).ToList());
}
