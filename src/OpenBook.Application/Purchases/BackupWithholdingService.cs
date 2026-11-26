using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <summary>How much of a payment is for what's kept in accounts marked not reportable on 1099s.</summary>
internal static class NotReportable1099
{
    public static HashSet<Guid> Accounts(ChartOfAccounts chart) => chart.All.Where(a => a.Excluded1099).Select(a => a.Id).ToHashSet();

    /// <summary>The share of a bill on those accounts, 0 to 1.</summary>
    public static decimal Share(Bill bill, IReadOnlySet<Guid> excluded)
    {
        var total = bill.Lines.Sum(l => l.Amount);
        var part = bill.Lines.Where(l => excluded.Contains(l.AccountId)).Sum(l => l.Amount);
        return total > 0m && part > 0m ? Math.Min(1m, part / total) : 0m;
    }

    /// <summary>What's not reportable of what a payment paid on each bill.</summary>
    public static decimal Of(IReadOnlyCollection<(Guid BillId, decimal Amount)> paid, IReadOnlyDictionary<Guid, decimal> shares) =>
        Math.Min(paid.Sum(a => a.Amount), decimal.Round(paid.Sum(a => a.Amount * shares.GetValueOrDefault(a.BillId)), 2, MidpointRounding.AwayFromZero));
}

/// <summary>A line of the backup withholding report: money held back from a vendor, paid over to the IRS, or moved some other way.</summary>
/// <param name="Gross">The whole payment it was held back from.</param>
public sealed record WithholdingLine(
    DateOnly Date, string Type, string? Payee, string? Reference, decimal? Gross, decimal Withheld, decimal PaidOver, decimal Other, string EntityType, Guid EntityId);

/// <param name="OwedAtStart">Held back and not paid over before the year.</param>
public sealed record WithholdingReport(int Year, decimal OwedAtStart, IReadOnlyList<WithholdingLine> Lines, Guid? AccountId)
{
    public decimal Withheld => Lines.Sum(l => l.Withheld);
    public decimal PaidOver => Lines.Sum(l => l.PaidOver);
    public decimal Other => Lines.Sum(l => l.Other);
    public decimal OwedAtEnd => OwedAtStart + Withheld - PaidOver + Other;
}

/// <summary>
/// Backup withholding on vendors marked for it. What's held back from a bill payment or an expense is credited to
/// "Backup Withholding Payable" (created on first use); an expense to that account pays it over to the IRS.
/// </summary>
public sealed class BackupWithholdingService(
    IPurchasesStore store, AccountService accounts, IBankingStore bank, ICompanyStore companies, IClock clock, ICurrentUser user, Settlements.IOwnerStore owners)
{
    public const string AccountName = "Backup Withholding Payable";
    public const string LineMemo = "Backup withholding";

    /// <summary>The account what's held back is owed on; null until the first use.</summary>
    public async Task<Account?> AccountAsync(CancellationToken ct = default) =>
        (await store.GetPurchasingSettingsAsync(ct))?.BackupWithholdingAccountId is { } id ? (await accounts.GetChartAsync(ct)).Find(id) : null;

    /// <summary>Found or created, and remembered (so renaming it changes nothing). Saves right away: call it before staging.</summary>
    internal async Task<Account> GetOrCreateAccountAsync(CancellationToken ct)
    {
        if (await AccountAsync(ct) is { } known)
        {
            return known.IsActive
                ? known
                : throw new DomainException("withholding.account_inactive", $"The account \"{known.Name}\" is inactive. Make it active again on the chart of accounts.");
        }

        var account = (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentLiability && string.Equals(a.Name, AccountName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateUsualAsync(new CreateAccountCommand(AccountName, AccountType.OtherCurrentLiability,
                Description: "Federal tax held back from vendors, owed to the IRS."), ct);
        var settings = await store.GetPurchasingSettingsAsync(ct);
        if (settings is null)
        {
            settings = PurchasingSettings.Create();
            store.Add(settings);
        }

        settings.SetBackupWithholdingAccount(account.Id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, BillApprovalService.SettingsEntity,
            (await companies.GetRequiredCompanyAsync(ct)).Id, null, JsonSerializer.Serialize(new { BackupWithholdingAccount = account.Name })));
        await store.SaveChangesAsync(ct);
        return account;
    }

    /// <summary>Marks a vendor from a date on, with the reason; a null date stops it. Audited on the vendor.</summary>
    public async Task<Vendor> SetAsync(Guid vendorId, DateOnly? from, string? note, CancellationToken ct = default)
    {
        var vendor = await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
        var before = Snapshot(vendor);
        vendor.SetBackupWithholding(from, note);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "vendor", vendor.Id, before, Snapshot(vendor)));
        await store.SaveChangesAsync(ct);
        return vendor;
    }

    /// <summary>
    /// What's held back when a vendor's bills are paid: the rate of what's reportable on a 1099 (its share of each bill,
    /// as the 1099 summary counts it). Nothing from a payment by card, which the card company reports.
    /// </summary>
    /// <param name="always">The payment holds back already and is being corrected, though the vendor's mark may have gone since.</param>
    public static decimal OnBills(
        ChartOfAccounts chart, Vendor vendor, DateOnly date, Guid paidFromAccountId, IReadOnlyCollection<(Bill Bill, decimal Amount)> paid, bool always = false)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(paid);
        // An owner-operator's settlement held it back from the gross already.
        paid = [.. paid.Where(p => p.Bill.OwnerSettlementId is null)];
        if ((!always && !vendor.WithholdsOn(date)) || chart.Find(paidFromAccountId)?.Type == AccountType.CreditCard || paid.Count == 0)
            return 0m;
        var excluded = NotReportable1099.Accounts(chart);
        var shares = paid.Select(p => p.Bill).DistinctBy(b => b.Id).ToDictionary(b => b.Id, b => NotReportable1099.Share(b, excluded));
        var applied = paid.Select(p => (p.Bill.Id, p.Amount)).ToList();
        return BackupWithholding.On(applied.Sum(a => a.Amount) - NotReportable1099.Of(applied, shares), date);
    }

    /// <summary>What's held back from an expense: the rate of its lines that are reportable on a 1099.</summary>
    public static decimal OnExpense(ChartOfAccounts chart, DateOnly date, IReadOnlyCollection<BankSplit> splits)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(splits);
        var excluded = NotReportable1099.Accounts(chart);
        return BackupWithholding.On(Math.Min(splits.Sum(s => s.Amount), splits.Where(s => !excluded.Contains(s.AccountId)).Sum(s => s.Amount)), date);
    }

    /// <summary>The line that holds backup withholding back from an expense, checked. May create the account: call it before staging.</summary>
    internal async Task<BankSplit> SplitAsync(
        Vendor? vendor, BankTransactionKind kind, Account paidFrom, DateOnly date, IReadOnlyCollection<BankSplit> splits, CancellationToken ct)
    {
        if (kind != BankTransactionKind.Spend || paidFrom.Type != AccountType.Bank)
            throw new DomainException("withholding.bank_only", "Backup withholding is held back only from money paid from a bank account.");
        if (vendor is null || !vendor.WithholdsOn(date))
            throw new DomainException("withholding.not_marked", "Backup withholding doesn't apply on that date: it's marked on the vendor's page, from a date on.");
        var held = OnExpense(await accounts.GetChartAsync(ct), date, splits);
        if (held <= 0m)
            throw new DomainException("withholding.nothing", "Nothing in this payment is reportable on a 1099, so there's nothing to hold back.");
        return new BankSplit((await GetOrCreateAccountAsync(ct)).Id, -held, LineMemo);
    }

    /// <summary>What each posted expense held back, by its id.</summary>
    internal async Task<IReadOnlyDictionary<Guid, decimal>> OnExpensesAsync(CancellationToken ct) =>
        await AccountAsync(ct) is not { } account
            ? new Dictionary<Guid, decimal>()
            : (await bank.GetAccountLinesAsync(account.Id, ct))
                .Where(l => !l.IsVoidPair && l.Source == BankTransaction.SpendSource && l.SourceId is not null)
                .GroupBy(l => l.SourceId!.Value)
                .Select(g => (g.Key, Held: -g.Sum(l => l.Amount)))
                .Where(x => x.Held > 0m)
                .ToDictionary(x => x.Key, x => x.Held);

    /// <summary>
    /// A calendar year's withholding from the account itself, so it ties to the books: held back on bill payments and
    /// expenses, paid over by expenses to the account, and anything else that moved it.
    /// </summary>
    public async Task<WithholdingReport> ReportAsync(int year, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100)
            throw new DomainException("report.year_invalid", "Choose a year between 2000 and 2100.");
        if (await AccountAsync(ct) is not { } account)
            return new WithholdingReport(year, 0m, [], null);
        var lines = (await bank.GetAccountLinesAsync(account.Id, ct)).Where(l => !l.IsVoidPair).ToList();
        var payments = (await store.ListPaymentsAsync(null, ct)).ToDictionary(p => p.Id);
        var expenses = (await store.ListExpensesAsync(null, ct)).ToDictionary(e => e.Id);
        var vendors = (await store.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var settlements = await owners.ListSettlementsAsync(null, ct);
        var byBill = settlements.Where(s => s.BillId is not null).ToDictionary(s => s.BillId!.Value);
        var byId = settlements.ToDictionary(s => s.Id);
        var ownerVendors = (await owners.ListOwnersAsync(ct)).ToDictionary(o => o.Id, o => o.VendorId);
        var result = new List<WithholdingLine>();
        // An entry is one line here, however many of its own are on the account.
        foreach (var entry in lines.Where(l => l.Date.Year == year).GroupBy(l => l.JournalEntryId))
        {
            var first = entry.First();
            var amount = -entry.Sum(l => l.Amount);
            if (amount == 0m)
                continue;
            var settlement = first.SourceId is not { } sourceId ? null
                : first.Source == Bill.Source ? byBill.GetValueOrDefault(sourceId)
                : first.Source == Domain.Settlements.OwnerSettlement.Source ? byId.GetValueOrDefault(sourceId) : null;
            if (settlement is not null)
            {
                // Its gross, and what it held back (a void gives it back on the same day).
                var payee = ownerVendors.TryGetValue(settlement.OwnerId, out var vendorId) ? vendors.GetValueOrDefault(vendorId) : null;
                result.Add(new WithholdingLine(first.Date, "Owner settlement", payee, $"Settlement {settlement.Number}", amount > 0m ? settlement.ReportableGross : null,
                    amount, 0m, 0m, "owner_settlement", settlement.Id));
            }
            else if (first.Source == BillPayment.Source && first.SourceId is { } paymentId && payments.GetValueOrDefault(paymentId) is { } payment)
            {
                result.Add(new WithholdingLine(first.Date, "Bill payment", vendors.GetValueOrDefault(payment.VendorId), payment.Reference, payment.Amount, amount, 0m, 0m,
                    "bill_payment", payment.Id));
            }
            else if (first.Source == BankTransaction.SpendSource && first.SourceId is { } spendId && expenses.GetValueOrDefault(spendId) is { } expense)
            {
                var payee = expense.Payee ?? (expense.VendorId is { } v ? vendors.GetValueOrDefault(v) : null);
                result.Add(amount > 0m
                    ? new WithholdingLine(first.Date, "Expense", payee, expense.Reference, expense.Amount + amount, amount, 0m, 0m, "bank_transaction", expense.Id)
                    : new WithholdingLine(first.Date, "Paid to the IRS", payee, expense.Reference, null, 0m, -amount, 0m, "bank_transaction", expense.Id));
            }
            else
            {
                result.Add(new WithholdingLine(first.Date, ReportCatalog.DocumentType(first.Source), first.EntryMemo, null, null, 0m, 0m, amount, "journal_entry", first.JournalEntryId));
            }
        }

        return new WithholdingReport(year, -lines.Where(l => l.Date.Year < year).Sum(l => l.Amount), [.. result.OrderBy(l => l.Date)], account.Id);
    }

    /// <summary>The report as a table, by month as Form 945 asks.</summary>
    public async Task<ReportTable> TableAsync(int year, CancellationToken ct = default)
    {
        var report = await ReportAsync(year, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        static ReportCell Amount(decimal amount) => amount == 0m ? ReportCell.Empty : new ReportCell(amount);
        static ReportTableRow Totals(string label, IReadOnlyCollection<WithholdingLine> of, ReportRowStyle style) => new(
            [label, null, null, null, new ReportCell(of.Sum(l => l.Gross ?? 0m)), of.Sum(l => l.Withheld), of.Sum(l => l.PaidOver), Amount(of.Sum(l => l.Other))], 0, style);

        var rows = new List<ReportTableRow> { new(["Held back before the year and not paid over yet", null, null, null, null, report.OwedAtStart, null, null]) };
        foreach (var month in report.Lines.GroupBy(l => l.Date.Month))
        {
            var name = new DateOnly(year, month.Key, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            rows.Add(new ReportTableRow([name, null, null, null, null, null, null, null], 0, ReportRowStyle.Section));
            rows.AddRange(month.Select(l => new ReportTableRow(
            [
                new ReportCell(l.Type, new ReportLink(l.EntityType, l.EntityId)), new ReportCell(l.Date), l.Payee, l.Reference, new ReportCell(l.Gross),
                Amount(l.Withheld), Amount(l.PaidOver), Amount(l.Other),
            ], 1)));
            rows.Add(Totals($"Total {name}", [.. month], ReportRowStyle.Subtotal));
        }

        rows.Add(Totals($"Total {year.ToString(CultureInfo.InvariantCulture)}", report.Lines, ReportRowStyle.Total));
        var still = report.AccountId is { } account
            ? new ReportCell("Still to pay over at the end of the year", new ReportLink("account", account) { From = new DateOnly(year, 1, 1), To = new DateOnly(year, 12, 31) })
            : new ReportCell("Still to pay over at the end of the year");
        rows.Add(new ReportTableRow([still, null, null, null, null, report.OwedAtEnd, null, null], 0, ReportRowStyle.Total));
        return new ReportTable(
            $"Backup withholding for {year.ToString(CultureInfo.InvariantCulture)}",
            [company.Name, "Held back from vendors and paid over to the IRS, for Form 945"],
            [new("Payment"), new("Date", ColumnFormat.Date), new("Vendor or payee"), new("Reference"), new("Payment before withholding", ColumnFormat.Money),
                new("Withheld", ColumnFormat.Money), new("Paid to the IRS", ColumnFormat.Money), new("Other", ColumnFormat.Money)],
            rows,
            Note: "From the Backup Withholding Payable account. Record a payment to the IRS as an expense with that account as its category. OpenBook doesn't file Form 945.");
    }

    private static string Snapshot(Vendor v) => JsonSerializer.Serialize(new { BackupWithholding = new { From = v.BackupWithholdingFrom, Note = v.BackupWithholdingNote } });
}
