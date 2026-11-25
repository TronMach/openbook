using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Posted documents with a sales rep and what settled their invoices, untracked, whatever their dates.</summary>
/// <param name="Payments">Posted payments applied to any of the invoices.</param>
/// <param name="Retainers">Posted retainers applied to any of the invoices.</param>
public sealed record RepDocuments(
    IReadOnlyList<Invoice> Invoices, IReadOnlyList<SalesReceipt> Receipts, IReadOnlyList<CreditMemo> CreditMemos,
    IReadOnlyList<CustomerPayment> Payments, IReadOnlyList<Retainer> Retainers);

public interface ICommissionStore
{
    /// <summary>Tracked; null until first saved.</summary>
    Task<CommissionSettings?> GetSettingsAsync(CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<CommissionRate>> ListRatesAsync(CancellationToken ct);

    /// <summary>Untracked, with lines, oldest first.</summary>
    Task<IReadOnlyList<CommissionRun>> ListRunsAsync(CancellationToken ct);

    /// <summary>The highest run number, 0 before the first.</summary>
    Task<int> LastRunNumberAsync(CancellationToken ct);

    Task<RepDocuments> ListDocumentsAsync(CancellationToken ct);

    void Add(CommissionSettings settings);
    void Add(CommissionRate rate);
    void Add(CommissionRun run);
    void Add(AuditRecord record);
    void Remove(CommissionRate rate);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>One amount of commission: what earned it, the amount counted and what it comes to. Negative when it takes one back.</summary>
/// <param name="Document">"Invoice 1001".</param>
public sealed record CommissionLine(CommissionKey Key, DateOnly Date, string Document, Guid? CustomerId, decimal Counted, decimal Commission)
{
    public decimal? Percent => CommissionRun.Rate(Counted, Commission);
}

/// <summary>A rep's commissions not in a run yet. <see cref="Total"/> can be negative: then they wait for what's earned next.</summary>
public sealed record RepCommission(Worker Worker, IReadOnlyList<CommissionLine> Lines)
{
    public decimal Total => Lines.Sum(l => l.Commission);

    /// <summary>Whether a run can bill it: there's something to pay and a vendor record to bill.</summary>
    public bool CanPay => Worker.VendorId is not null && Total > 0m;
}

/// <summary>A run with its rep and bill. Once the bill is voided its lines are free for the next run.</summary>
public sealed record CommissionRunView(CommissionRun Run, Worker? Worker, Bill? Bill)
{
    public bool Stands => Bill?.Status == DocumentStatus.Posted;
}

/// <param name="WorkerIds">The reps to pay; null pays everyone who can be.</param>
/// <param name="BillDate">Defaults to today.</param>
/// <param name="ExpenseAccountId">Null takes the account of the last run, else "Commissions" (made on first use).</param>
public sealed record CommissionRunCommand(DateOnly Through, IReadOnlyCollection<Guid>? WorkerIds = null, DateOnly? BillDate = null, Guid? ExpenseAccountId = null);

/// <summary>
/// Sales rep commissions: a percent of what a rep's invoices, sales receipts and credit memos come to before tax and
/// after discounts, counting only lines on income accounts (not gift cards, deposits or retainage), earned as invoices
/// are paid or when they're invoiced. A run makes what isn't in a run yet into a bill to the rep's vendor record.
/// Commissions post nothing themselves.
/// </summary>
public sealed class CommissionService(
    ICommissionStore store, ISalesStore sales, IProjectStore projects, IPurchasesStore purchases, BillService bills, AccountService accounts,
    ICompanyStore companies, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public const string SettingsType = "commission_settings";
    public const string RateType = "commission_rate";
    public const string RunType = "commission_run";
    public const string AccountName = "Commissions";

    /// <summary>The saved settings, or the defaults (earned when paid) before the first save.</summary>
    public async Task<CommissionSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.GetSettingsAsync(ct) ?? CommissionSettings.Create();

    /// <summary>
    /// Changes when commission is earned. Runs already made stay; the next one brings each document to what it comes to
    /// under the new setting.
    /// </summary>
    public async Task<CommissionSettings> SetBasisAsync(CommissionBasis basis, CancellationToken ct = default)
    {
        var settings = await EnsureSettingsAsync(ct);
        var before = Snapshot(settings);
        settings.SetBasis(basis);
        store.Add(Audit(AuditAction.Updated, SettingsType, settings.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>Every rate: by person, the general ones first, each from its first date.</summary>
    public async Task<IReadOnlyList<CommissionRate>> ListRatesAsync(CancellationToken ct = default)
    {
        var names = (await projects.ListWorkersAsync(ct)).ToDictionary(w => w.Id, w => w.Name);
        return [.. (await store.ListRatesAsync(ct)).OrderBy(r => names.GetValueOrDefault(r.WorkerId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Category, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.StartsOn ?? DateOnly.MinValue)];
    }

    /// <summary>The item categories in use, for choosing on the rates page.</summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default) =>
        [.. (await sales.ListItemsAsync(ct)).Select(i => i.Category).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public async Task<CommissionRate> AddRateAsync(CommissionRateDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        _ = await projects.GetWorkerAsync(details.WorkerId, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, details.WorkerId);
        var rate = CommissionRate.Create(details);
        if ((await store.ListRatesAsync(ct)).Any(r => r.SameSlot(rate)))
            throw new DomainException("commission.rate_duplicate", "There's already a rate for that, starting on the same date. Delete that one first.");
        store.Add(rate);
        store.Add(Audit(AuditAction.Created, RateType, rate.Id, null, Snapshot(rate)));
        await store.SaveChangesAsync(ct);
        return rate;
    }

    /// <summary>Deletes a rate (audited). Runs already made keep what they paid.</summary>
    public async Task DeleteRateAsync(Guid id, CancellationToken ct = default)
    {
        var rate = (await store.ListRatesAsync(ct)).FirstOrDefault(r => r.Id == id) ?? throw new EntityNotFoundException(RateType, id);
        store.Remove(rate);
        store.Add(Audit(AuditAction.Deleted, RateType, rate.Id, Snapshot(rate), null));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Every run, newest first.</summary>
    public async Task<IReadOnlyList<CommissionRunView>> ListRunsAsync(CancellationToken ct = default)
    {
        var runs = await store.ListRunsAsync(ct);
        var workers = (await projects.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var billed = (await purchases.GetBillsAsync([.. runs.Select(r => r.BillId).Distinct()], ct)).ToDictionary(b => b.Id);
        return [.. runs.OrderByDescending(r => r.Number).Select(r => new CommissionRunView(r, workers.GetValueOrDefault(r.WorkerId), billed.GetValueOrDefault(r.BillId)))];
    }

    /// <summary>
    /// What each rep has earned through the date and isn't in a run whose bill stands, by name. Something in a run whose
    /// amount changed since (a corrected or voided invoice, a payment taken back) shows the difference, whatever its date.
    /// </summary>
    public async Task<IReadOnlyList<RepCommission>> PendingAsync(DateOnly through, CancellationToken ct = default)
    {
        var inRuns = (await ListRunsAsync(ct)).Where(r => r.Stands)
            .SelectMany(r => r.Run.Lines.Select(l => (Key: r.Run.KeyOf(l), Line: l)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => (Counted: g.Sum(x => x.Line.Counted), Commission: g.Sum(x => x.Line.Commission), g.First().Line));

        var lines = new List<CommissionLine>();
        foreach (var line in await EarnedAsync(ct))
        {
            if (inRuns.Remove(line.Key, out var before))
            {
                // The rate a run paid stays: only a change in the amount counted is worked out again.
                if (line.Counted != before.Counted)
                    lines.Add(line with { Counted = line.Counted - before.Counted, Commission = line.Commission - before.Commission });
            }
            else if (line.Date <= through)
            {
                lines.Add(line);
            }
        }

        lines.AddRange(inRuns.Select(x => new CommissionLine(x.Key, x.Value.Line.Date, x.Value.Line.Document, null, -x.Value.Counted, -x.Value.Commission)));

        var workers = (await projects.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        return [.. lines.Where(l => l.Commission != 0m).GroupBy(l => l.Key.WorkerId).Where(g => workers.ContainsKey(g.Key))
            .Select(g => new RepCommission(workers[g.Key], [.. g.OrderBy(l => l.Date).ThenBy(l => l.Document, StringComparer.OrdinalIgnoreCase)]))
            .OrderBy(r => r.Worker.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Bills each chosen rep's commissions through the date to their vendor record, in one transaction: a run and a bill
    /// each. What's taken back comes off; someone left with nothing to pay is refused, or left out when nobody is chosen,
    /// and their lines wait for the next run.
    /// </summary>
    public async Task<IReadOnlyList<CommissionRun>> RunAsync(CommissionRunCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await unitOfWork.BeginAsync(ct);

        // Read before what's pending: a run made by someone else in between then clashes on the number.
        var number = await store.LastRunNumberAsync(ct);
        var pending = await PendingAsync(command.Through, ct);
        var chosen = pending.Where(p => command.WorkerIds is null && p.CanPay).ToList();
        foreach (var id in command.WorkerIds?.Distinct() ?? [])
        {
            var worker = await projects.GetWorkerAsync(id, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, id);
            if (worker.VendorId is null)
                throw new DomainException("commission.worker_vendor", $"Link {worker.Name} to a vendor record on People and rates first: the bill for their commissions goes to it.");
            chosen.Add(pending.FirstOrDefault(p => p.Worker.Id == id && p.CanPay)
                ?? throw new DomainException("commission.nothing_to_pay", $"{worker.Name} has no commission to pay through {command.Through:MMM d, yyyy}."));
        }

        if (chosen.Count == 0)
            throw new DomainException("commission.nothing_to_pay", "There's no commission to pay. Choose at least one rep who has some.");

        var settings = await EnsureSettingsAsync(ct);
        var before = Snapshot(settings);
        var account = await ExpenseAccountAsync(command.ExpenseAccountId, settings, ct);
        if (settings.ExpenseAccountId != account.Id)
        {
            settings.SetExpenseAccount(account.Id);
            store.Add(Audit(AuditAction.Updated, SettingsType, settings.Id, before, Snapshot(settings)));
        }

        var runs = new List<CommissionRun>();
        foreach (var rep in chosen)
        {
            number++;
            var memo = $"Commissions for {rep.Worker.Name} through {command.Through:MMM d, yyyy}";
            var bill = await bills.CreateAsync(new CreateBillCommand(rep.Worker.VendorId!.Value, command.BillDate ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone),
                [new BillLineDraft(account.Id, rep.Total, memo, ClassId: rep.Worker.ClassId)], Reference: $"Commission {number}", Memo: memo), ct);
            var run = CommissionRun.Create(number, rep.Worker.Id, command.Through, bill.Id, [.. rep.Lines.Select(l => new CommissionRunLineDraft(
                l.Key.Source, l.Key.DocumentId, l.Key.Kind, l.Key.SettlementId, l.Date, l.Document, l.Counted, l.Commission))], clock.UtcNow);
            store.Add(run);
            store.Add(Audit(AuditAction.Created, RunType, run.Id, null, Snapshot(run)));
            runs.Add(run);
        }

        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return runs;
    }

    /// <summary>
    /// Each rep's commissions dated in the period, as they stand now, whether or not they're in a run. Someone below the
    /// accountant level sees only the person they're linked to.
    /// </summary>
    public async Task<ReportTable> ReportAsync(DateOnly from, DateOnly to, Guid? workerId, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var workers = (await projects.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var own = await SeesEveryoneAsync(ct) ? null
            : workers.Values.FirstOrDefault(w => w.IsActive && string.Equals(w.Username, user.Actor, StringComparison.OrdinalIgnoreCase))?.Id ?? (Guid?)Guid.Empty;
        var names = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
        var runs = (await ListRunsAsync(ct)).Where(r => r.Stands).OrderBy(r => r.Run.Number)
            .SelectMany(r => r.Run.Lines.Select(l => (Key: r.Run.KeyOf(l), r.Run.Number))).ToLookup(x => x.Key, x => x.Number);

        var rows = new List<ReportTableRow>();
        var (counted, commission) = (0m, 0m);
        foreach (var rep in (await EarnedAsync(ct))
                     .Where(l => l.Date >= from && l.Date <= to && (workerId is null || l.Key.WorkerId == workerId) && (own is null || l.Key.WorkerId == own))
                     .GroupBy(l => l.Key.WorkerId).Where(g => workers.ContainsKey(g.Key))
                     .OrderBy(g => workers[g.Key].Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(Row.Section(workers[rep.Key].Name, 8));
            foreach (var line in rep.OrderBy(l => l.Date).ThenBy(l => l.Document, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new ReportTableRow(
                [
                    new ReportCell(line.Document, new ReportLink(EntityTypeOf(line.Key.Source), line.Key.DocumentId)),
                    Label(line.Key.Kind),
                    new ReportCell(line.Date),
                    line.CustomerId is { } customer ? new ReportCell(names.GetValueOrDefault(customer), new ReportLink("customer", customer)) : ReportCell.Empty,
                    line.Counted,
                    new ReportCell(line.Percent),
                    line.Commission,
                    string.Join(", ", runs[line.Key].Distinct()),
                ], 1));
            }

            var (repCounted, repCommission) = (rep.Sum(l => l.Counted), rep.Sum(l => l.Commission));
            rows.Add(new ReportTableRow([$"Total for {workers[rep.Key].Name}", ReportCell.Empty, ReportCell.Empty, ReportCell.Empty, repCounted, ReportCell.Empty, repCommission, ReportCell.Empty], 0, ReportRowStyle.Subtotal));
            counted += repCounted;
            commission += repCommission;
        }

        rows.Add(new ReportTableRow(["Total", ReportCell.Empty, ReportCell.Empty, ReportCell.Empty, counted, ReportCell.Empty, commission, ReportCell.Empty], 0, ReportRowStyle.Total));
        var paid = (await GetSettingsAsync(ct)).Basis == CommissionBasis.Paid;
        return new ReportTable(
            "Sales commissions",
            [company.Name, $"{Comparisons.Describe(from, to)} · {(paid ? "Earned when paid" : "Earned when invoiced")}"],
            [new("Document"), new("For"), new("Date", ColumnFormat.Date), new("Customer"), new("Amount counted", ColumnFormat.Money), new("Rate", ColumnFormat.Percent),
                new("Commission", ColumnFormat.Money), new("Run")],
            rows,
            Note: (paid ? "Each payment earns its share of the invoice; a returned payment or credit memo takes its share back. "
                    : "Invoices and sales receipts count on their dates; credit memos take theirs back. ")
                + "Amounts are lines on income accounts, before tax and after discounts. Commission is worked out as the documents stand now; what a run paid is on the run."
                + (own is null ? "" : " Only your own commissions are shown."));
    }

    public static string Label(CommissionEvent kind) => kind switch
    {
        CommissionEvent.Payment => "Payment",
        CommissionEvent.Returned => "Payment returned",
        CommissionEvent.Retainer => "Retainer applied",
        CommissionEvent.Credit => "Credit memo applied",
        _ => "",
    };

    /// <summary>The audit entity type of the document, which links go by.</summary>
    public static string EntityTypeOf(CommissionSource source) => source switch
    {
        CommissionSource.SalesReceipt => "sales_receipt",
        CommissionSource.CreditMemo => "credit_memo",
        _ => "invoice",
    };

    /// <summary>Everything that has earned or taken back commission, whatever its date, as the documents stand now.</summary>
    private async Task<IReadOnlyList<CommissionLine>> EarnedAsync(CancellationToken ct)
    {
        var documents = await store.ListDocumentsAsync(ct);
        var rates = (await store.ListRatesAsync(ct)).ToLookup(r => r.WorkerId);
        var basis = (await GetSettingsAsync(ct)).Basis;
        var chart = await accounts.GetChartAsync(ct);
        var categories = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id, i => i.Category);
        var lines = new List<CommissionLine>();

        (decimal Counted, decimal Commission) Worth(Guid rep, DateOnly date, IReadOnlyList<SalesLine> sold)
        {
            var items = sold.Where(l => l.Kind == SalesLineKind.Item && l.IncomeAccountId is not null).ToList();
            var income = items.Where(l => chart.Find(l.IncomeAccountId!.Value)?.Type == AccountType.Income).ToList();
            var gross = income.Sum(l => l.Amount);
            var earned = income.Sum(l => l.Amount * CommissionRate.For(rates[rep], rep, categories.GetValueOrDefault(l.ItemId ?? Guid.Empty), date) / 100m);
            var discounts = sold.Where(l => l.Kind is SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount).Sum(l => l.Amount);
            if (gross <= 0m || discounts == 0m)
                return (gross, earned);

            // A discount comes off a gift card or deposit sold with it too, so income bears only its share.
            var all = gross + items.Except(income).Where(l => l.Amount > 0m).Sum(l => l.Amount);
            var left = Math.Max(0m, gross + discounts * gross / all);
            return (left, earned * left / gross);
        }

        void Add(Guid rep, CommissionSource source, Guid id, CommissionEvent kind, Guid settlement, DateOnly date, string document, Guid? customer, decimal counted, decimal commission)
        {
            if (counted != 0m || commission != 0m)
                lines.Add(new CommissionLine(new CommissionKey(rep, source, id, kind, settlement), date, document, customer, counted, commission));
        }

        foreach (var r in documents.Receipts)
        {
            var (counted, commission) = Worth(r.SalesRepId!.Value, r.Date, r.Lines);
            Add(r.SalesRepId.Value, CommissionSource.SalesReceipt, r.Id, CommissionEvent.Sale, Guid.Empty, r.Date, $"Sales receipt {r.Number}", r.CustomerId, Cents(counted), Cents(commission));
        }

        foreach (var c in documents.CreditMemos)
        {
            var (counted, commission) = Worth(c.SalesRepId!.Value, c.Date, c.Lines);
            Add(c.SalesRepId.Value, CommissionSource.CreditMemo, c.Id, CommissionEvent.Sale, Guid.Empty, c.Date, $"Credit memo {c.Number}", c.CustomerId, -Cents(counted), -Cents(commission));
        }

        // A credit memo is taken back from its rep in full, so applying it pays the invoice; one without a rep pays for nothing here.
        var settlements = documents.Payments.SelectMany(p => p.Applications.Select(a => (a.InvoiceId, Kind: CommissionEvent.Payment, p.Id, Date: a.AppliedOn, a.Amount)))
            .Concat(documents.Payments.Where(p => p.ReturnedOn is not null).SelectMany(p => p.Applications.Select(a => (a.InvoiceId, Kind: CommissionEvent.Returned, p.Id, Date: p.ReturnedOn!.Value, Amount: -a.Amount))))
            .Concat(documents.Retainers.SelectMany(r => r.Applications.Where(a => !a.IsVoided).Select(a => (a.InvoiceId, Kind: CommissionEvent.Retainer, a.Id, Date: a.AppliedOn, a.Amount))))
            .Concat(documents.CreditMemos.SelectMany(c => c.Applications.Select(a => (a.InvoiceId, Kind: CommissionEvent.Credit, c.Id, Date: a.AppliedOn, a.Amount))))
            .ToLookup(s => s.InvoiceId);
        foreach (var i in documents.Invoices)
        {
            var rep = i.SalesRepId!.Value;
            var (counted, commission) = Worth(rep, i.Date, i.Lines);
            // A gift card redeemed on it paid for all of it: there's no payment to wait for.
            if (basis == CommissionBasis.Invoiced || i.Total == 0m)
            {
                Add(rep, CommissionSource.Invoice, i.Id, CommissionEvent.Sale, Guid.Empty, i.Date, $"Invoice {i.Number}", i.CustomerId, Cents(counted), Cents(commission));
                continue;
            }

            // Each settlement takes the difference between the shares up to and before it, so the last lands on the whole.
            var (settled, countedBefore, commissionBefore) = (0m, 0m, 0m);
            foreach (var s in settlements[i.Id].OrderBy(s => s.Date).ThenBy(s => s.Kind).ThenBy(s => s.Id))
            {
                settled += s.Amount;
                var (countedNow, commissionNow) = (Cents(counted * settled / i.Total), Cents(commission * settled / i.Total));
                Add(rep, CommissionSource.Invoice, i.Id, s.Kind, s.Id, s.Date, $"Invoice {i.Number}", i.CustomerId, countedNow - countedBefore, commissionNow - commissionBefore);
                (countedBefore, commissionBefore) = (countedNow, commissionNow);
            }
        }

        return lines;
    }

    private static decimal Cents(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Whether the person asking may see everyone's commissions: an accountant in sales, or a job of OpenBook's own.</summary>
    private async Task<bool> SeesEveryoneAsync(CancellationToken ct)
    {
        if (await companies.FindUserAsync(user.Actor, ct) is not { } account)
            return true;
        if (account.PermissionSetId is not { } setId)
            return account.Role >= UserRole.Accountant;
        return await companies.GetPermissionSetAsync(setId, ct) is { } set && set.LevelMap.TryGetValue("sales", out var level) && level >= UserRole.Accountant;
    }

    private async Task<CommissionSettings> EnsureSettingsAsync(CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(ct);
        if (settings is null)
        {
            settings = CommissionSettings.Create();
            store.Add(settings);
        }

        return settings;
    }

    private async Task<Account> ExpenseAccountAsync(Guid? chosen, CommissionSettings settings, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        if (chosen is { } id && id != Guid.Empty)
        {
            var account = chart.Get(id);
            if (!IsExpense(account))
                throw new DomainException("commission.account_type", $"'{account.Name}' isn't an expense account.");
            return account.IsActive ? account : throw new DomainException("commission.account_inactive", $"'{account.Name}' is inactive.");
        }

        if (settings.ExpenseAccountId is { } last && chart.Find(last) is { IsActive: true } remembered && IsExpense(remembered))
            return remembered;
        return chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.Expense && string.Equals(a.Name, AccountName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateUsualAsync(new CreateAccountCommand(AccountName, AccountType.Expense, Description: "Commissions paid to sales reps."), ct);
    }

    /// <summary>The accounts a run can be billed to.</summary>
    public static bool IsExpense(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Type is AccountType.Expense or AccountType.OtherExpense or AccountType.CostOfGoodsSold;
    }

    private AuditRecord Audit(AuditAction action, string type, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, type, id, before, after);

    private static string Snapshot(CommissionSettings s) => JsonSerializer.Serialize(new { Basis = s.Basis.ToString(), s.ExpenseAccountId });

    private static string Snapshot(CommissionRate r) => JsonSerializer.Serialize(new { r.WorkerId, r.Category, r.Percent, r.StartsOn });

    private static string Snapshot(CommissionRun r) => JsonSerializer.Serialize(new { r.Number, r.WorkerId, r.Through, r.BillId, r.Total, Lines = r.Lines.Count });
}
