using OpenBook.Application.Accounts;
using OpenBook.Application.Assets;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Segments;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

public enum FixedCostSource
{
    RecurringExpense,
    RecurringBill,
    RecurringEntry,
    Loan,
    Depreciation,
    Settlement,
}

/// <summary>
/// One cost that comes whether or not the truck hauls: a template's lines on one account, a loan's payment, an asset's
/// depreciation, or a deduction the same on a carrier's last settlements. <paramref name="Month"/> is what it costs a
/// month (null when it isn't a cost at all, such as loan principal); <paramref name="CashMonth"/> what leaves the bank.
/// </summary>
/// <param name="Id">The template, loan, asset or carrier.</param>
/// <param name="From">A carrier's name, for a settlement's deduction.</param>
public sealed record FixedCost(
    FixedCostSource Source, Guid Id, string Name, string? From, Guid? AccountId, Guid? ClassId, string Schedule, decimal EachTime, decimal? Month, decimal CashMonth)
{
    public decimal? Week => Month is { } m ? FixedCostService.Weekly(m) : null;
}

/// <summary>A truck's fixed costs: those on one class (null: on none), and its miles a month lately.</summary>
public sealed record TruckFixedCosts(Guid? ClassId, string Name, Guid? TruckId, IReadOnlyList<FixedCost> Costs, decimal? MilesMonth)
{
    public decimal Month => Costs.Sum(c => c.Month ?? 0m);
    public decimal Week => FixedCostService.Weekly(Month);
    public decimal CashMonth => Costs.Sum(c => c.CashMonth);
    public decimal? PerMile => MilesMonth is > 0m and { } miles ? Month / miles : null;
}

/// <summary>
/// What each truck costs before it hauls a load: the recurring expenses, bills and journal entries on its class, loan
/// payments and depreciation on it, and deductions the same on the latest settlements of a carrier that still pays.
/// </summary>
public sealed class FixedCostService(
    ICompanyStore companies,
    AccountService accounts,
    ILedgerStore ledger,
    IBankingStore banking,
    IPurchasesStore purchases,
    IAssetStore assets,
    ISettlementStore settlements,
    IOwnerStore owners,
    IEnumerable<IMileageSource> mileage)
{
    /// <summary>A month is a twelfth of 365.25 days.</summary>
    public static decimal Weekly(decimal month) => month * 84m / 365.25m;

    /// <summary>Miles a month are those of the last 13 weeks.</summary>
    public const int MilesDays = 91;

    /// <summary>A carrier whose latest posted settlement is older than this no longer pays: its deductions aren't counted.</summary>
    public const int CarrierStoppedAfterDays = 35;

    public async Task<IReadOnlyList<TruckFixedCosts>> ListAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var costs = new List<FixedCost>();
        var expenses = (await banking.ListRecurringExpensesAsync(ct)).Where(t => t.IsActive && t.NextDate is not null).ToList();
        var bills = (await purchases.ListRecurringBillsAsync(ct)).Where(t => t.IsActive && t.NextDate is not null).ToList();

        foreach (var t in expenses)
        {
            costs.AddRange(FromLines(FixedCostSource.RecurringExpense, t.Id, t.Name, t.Frequency, t.Interval,
                t.Lines.Select(l => (l.AccountId, l.ClassId, l.Amount)), chart));
        }

        foreach (var t in bills)
        {
            costs.AddRange(FromLines(FixedCostSource.RecurringBill, t.Id, t.Name, t.Frequency, t.Interval,
                t.Lines.Select(l => (l.AccountId, l.ClassId, l.Amount)), chart));
        }

        // A journal entry moves no money of its own here: only what it puts on income and expense accounts counts, as a cost.
        foreach (var t in (await ledger.ListRecurringAsync(ct)).Where(t => t.IsActive && t.NextDate is not null))
        {
            foreach (var g in t.Lines.Where(l => IsCost(chart, l.AccountId)).GroupBy(l => (l.AccountId, l.ClassId)))
            {
                var each = g.Sum(l => l.Debit - l.Credit);
                if (each != 0m)
                    costs.Add(new FixedCost(FixedCostSource.RecurringEntry, t.Id, t.Name, null, g.Key.AccountId, g.Key.ClassId, Schedule(t.Frequency, t.Interval), each, Monthly(each, t.Frequency, t.Interval), 0m));
            }
        }

        // A loan whose payments a template makes is counted there, not twice.
        var paidByTemplate = expenses.SelectMany(t => t.Lines.Select(l => l.AccountId)).Concat(bills.SelectMany(t => t.Lines.Select(l => l.AccountId))).ToHashSet();
        foreach (var loan in (await assets.ListLoansAsync(ct)).Where(l => !l.IsVoided && !l.IsPaidOff && !paidByTemplate.Contains(l.LiabilityAccountId)))
        {
            var payment = loan.NextPaymentOn(asOf);
            if (payment <= 0m)
                continue;
            var (interest, _) = loan.Split(payment, asOf);
            costs.Add(new FixedCost(FixedCostSource.Loan, loan.Id, loan.Name, null, loan.InterestAccountId, loan.ClassId, "Every month", payment, interest, payment));
        }

        var monthEnd = new DateOnly(asOf.Year, asOf.Month, 1).AddMonths(1).AddDays(-1);
        foreach (var asset in (await assets.ListAssetsAsync(ct)).Where(a => a.Status == FixedAssetStatus.Active && a.Method != DepreciationMethod.None))
        {
            var schedule = Depreciation.Schedule(asset);
            var index = schedule.ToList().FindIndex(p => p.PeriodEnd == monthEnd);
            if (index < 0)
                continue;
            // A first-year write-off is once, not every month.
            var amount = schedule[index].Amount - (index == 0 ? asset.FirstYearAmount : 0m);
            if (amount > 0m)
                costs.Add(new FixedCost(FixedCostSource.Depreciation, asset.Id, asset.Name, null, asset.ExpenseAccountId, asset.ClassId, "Every month", amount, amount, 0m));
        }

        costs.AddRange(await SettlementDeductionsAsync(asOf, chart, ct));

        var classes = (await ledger.ListSegmentsAsync(ct)).Where(s => s.Kind == SegmentKind.Class).ToDictionary(s => s.Id);
        var trucks = (await owners.ListTrucksAsync(ct)).Where(t => t.ClassId is not null).GroupBy(t => t.ClassId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.IsActive).First());
        var miles = await MilesAsync(asOf, ct);

        return [.. costs.GroupBy(c => c.ClassId is { } id && classes.ContainsKey(id) ? id : (Guid?)null)
            .Select(g =>
            {
                var truck = g.Key is { } id ? trucks.GetValueOrDefault(id) : null;
                var name = g.Key is { } classId ? ClassName(classes, classId) : "Not on a truck";
                if (truck is not null && !string.Equals(truck.Unit, name, StringComparison.OrdinalIgnoreCase))
                    name = $"{name} (unit {truck.Unit})";
                var ordered = g.OrderBy(c => c.Source).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                return new TruckFixedCosts(g.Key, name, truck?.Id, ordered, g.Key is { } c && miles.TryGetValue(c, out var m) ? m : null);
            })
            .OrderBy(t => t.ClassId is null).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<ReportTable> TableAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var trucks = await ListAsync(asOf, ct);
        var allMiles = (await MilesAsync(asOf, ct)).Values.Sum() + await UnclassedMilesAsync(asOf, ct);
        const int columns = 10;
        var rows = new List<ReportTableRow>();

        static ReportCell Amount(decimal? value) => value is { } v ? new ReportCell(decimal.Round(v, 2, MidpointRounding.AwayFromZero)) : ReportCell.Empty;
        static ReportCell PerMile(decimal? amount, decimal? miles) =>
            amount is { } a && miles is > 0m and { } m ? new ReportCell(decimal.Round(a / m, 3, MidpointRounding.AwayFromZero)) : ReportCell.Empty;
        static ReportCell Miles(decimal? miles) => miles is { } m ? new ReportCell(decimal.Round(m, 0, MidpointRounding.AwayFromZero)) : ReportCell.Empty;

        foreach (var truck in trucks)
        {
            var heading = new ReportCell(truck.Name, truck.TruckId is { } id ? new ReportLink("truck", id) : null);
            rows.Add(new ReportTableRow([heading, .. Enumerable.Repeat(ReportCell.Empty, columns - 1)], 0, ReportRowStyle.Section));
            foreach (var cost in truck.Costs)
            {
                var account = cost.AccountId is { } a && chart.Find(a) is not null
                    ? new ReportCell(chart.FullName(a), new ReportLink("account", a) { From = asOf.AddMonths(-12).AddDays(1), To = asOf })
                    : ReportCell.Empty;
                rows.Add(new ReportTableRow(
                    [
                        new ReportCell(cost.Name, new ReportLink(LinkKind(cost.Source), cost.Id)), cost.From ?? Label(cost.Source), account, cost.Schedule,
                        Amount(cost.EachTime), Amount(cost.Week), Amount(cost.Month), Amount(cost.CashMonth), ReportCell.Empty, PerMile(cost.Month, truck.MilesMonth),
                    ], 1));
            }

            rows.Add(new ReportTableRow(
                [truck.ClassId is null ? "Total not on a truck" : $"Total for {truck.Name}", null, null, null, null, Amount(truck.Week), Amount(truck.Month), Amount(truck.CashMonth), Miles(truck.MilesMonth), PerMile(truck.Month, truck.MilesMonth)],
                0, ReportRowStyle.Subtotal));
        }

        var month = trucks.Sum(t => t.Month);
        var milesMonth = allMiles > 0m ? allMiles : (decimal?)null;
        rows.Add(new ReportTableRow(
            ["Total", null, null, null, null, Amount(Weekly(month)), Amount(month), Amount(trucks.Sum(t => t.CashMonth)), Miles(milesMonth), PerMile(month, milesMonth)],
            0, ReportRowStyle.Total));

        return new ReportTable(
            "Fixed costs by truck",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [
                new("What"), new("From"), new("Account"), new("How often"), new("Each time", ColumnFormat.Money), new("A week", ColumnFormat.Money),
                new("A month", ColumnFormat.Money), new("Cash a month", ColumnFormat.Money), new("Miles a month", ColumnFormat.Number), new("Per mile", ColumnFormat.Number),
            ],
            rows,
            Note: "What each truck costs whether it hauls or not: active recurring expenses, bills and journal entries on its class, loans, depreciation, and deductions the same on a carrier's last two settlements. "
                + "A week and a month are costs; cash a month is what leaves the bank, so it counts a loan's whole payment but not depreciation, and a recurring journal entry moves none. "
                + "A loan paid by a recurring expense or bill is counted there. Miles a month are the last 13 weeks'. The total per mile spreads the costs on no truck over every mile.");
    }

    private static IEnumerable<FixedCost> FromLines(
        FixedCostSource source, Guid id, string name, RecurrenceFrequency frequency, int interval, IEnumerable<(Guid AccountId, Guid? ClassId, decimal Amount)> lines, ChartOfAccounts chart)
    {
        foreach (var g in lines.GroupBy(l => (l.AccountId, l.ClassId)))
        {
            var each = g.Sum(l => l.Amount);
            if (each == 0m)
                continue;
            // A line on a balance sheet account (a loan's principal, a prepayment) is cash, not a cost.
            yield return new FixedCost(source, id, name, null, g.Key.AccountId, g.Key.ClassId, Schedule(frequency, interval), each,
                IsCost(chart, g.Key.AccountId) ? Monthly(each, frequency, interval) : null, Monthly(each, frequency, interval));
        }
    }

    /// <summary>
    /// Deductions and wages that came to the same amount, on the same account and class, on a carrier's latest two posted
    /// settlements (the truck payment, insurance, a plate program): the amount every settlement takes, spread by the days
    /// between them. Fuel and the like vary, so they aren't fixed.
    /// </summary>
    private async Task<IReadOnlyList<FixedCost>> SettlementDeductionsAsync(DateOnly asOf, ChartOfAccounts chart, CancellationToken ct)
    {
        var carriers = (await settlements.ListCarriersAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var result = new List<FixedCost>();
        foreach (var carrier in (await settlements.ListSettlementsAsync(null, ct)).Where(s => s.Status == SettlementStatus.Posted && s.Date <= asOf).GroupBy(s => s.CarrierId))
        {
            var latest = carrier.OrderByDescending(s => s.Date).ThenByDescending(s => s.CreatedAt).Take(SettlementReview.RegularAfter).ToList();
            if (latest.Count < SettlementReview.RegularAfter || latest[0].Date < asOf.AddDays(-CarrierStoppedAfterDays))
                continue;
            var days = latest[0].Date.DayNumber - latest[1].Date.DayNumber;
            if (days <= 0)
                days = 7;
            var schedule = days == 7 ? "Every settlement, weekly" : $"Every settlement, {days} days apart";
            var amounts = latest.Select(s => s.Lines.Where(l => l.Section is SettlementSection.Deduction or SettlementSection.Wage && l.PostedAccountId is not null)
                .GroupBy(l => (l.Section, l.Code, Account: l.PostedAccountId!.Value, l.PostedClassId))
                .ToDictionary(g => g.Key, g => (Amount: -g.Sum(l => l.Amount), Name: g.First().Description))).ToList();
            foreach (var (key, now) in amounts[0])
            {
                if (now.Amount <= 0m || amounts.Skip(1).Any(b => !b.TryGetValue(key, out var before) || before.Amount != now.Amount))
                    continue;
                var name = string.IsNullOrWhiteSpace(now.Name) ? key.Code : $"{key.Code} {now.Name}";
                result.Add(new FixedCost(FixedCostSource.Settlement, carrier.Key, name, carriers.GetValueOrDefault(carrier.Key), key.Account, key.PostedClassId, schedule,
                    now.Amount, IsCost(chart, key.Account) ? now.Amount * 365.25m / (12m * days) : null, now.Amount * 365.25m / (12m * days)));
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, decimal>> MilesAsync(DateOnly asOf, CancellationToken ct) =>
        (await RecentMilesAsync(asOf, ct)).Where(m => m.ClassId is not null).GroupBy(m => m.ClassId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Loaded + m.Empty) * (365.25m / 12m) / MilesDays);

    private async Task<decimal> UnclassedMilesAsync(DateOnly asOf, CancellationToken ct) =>
        (await RecentMilesAsync(asOf, ct)).Where(m => m.ClassId is null).Sum(m => m.Loaded + m.Empty) * (365.25m / 12m) / MilesDays;

    private async Task<IReadOnlyList<MilesDriven>> RecentMilesAsync(DateOnly asOf, CancellationToken ct)
    {
        var from = asOf.AddDays(-MilesDays + 1);
        var miles = new List<MilesDriven>();
        foreach (var source in mileage)
            miles.AddRange((await source.ListAsync(ct)).Where(m => m.Date >= from && m.Date <= asOf));
        return miles;
    }

    private static bool IsCost(ChartOfAccounts chart, Guid accountId) =>
        chart.Find(accountId) is { } account && account.Type.ClassOf() is AccountClass.Expense or AccountClass.Income;

    /// <summary>What an amount every so many weeks, months, quarters or years comes to a month.</summary>
    public static decimal Monthly(decimal each, RecurrenceFrequency frequency, int interval) => frequency switch
    {
        RecurrenceFrequency.Weekly => each * 365.25m / (84m * interval),
        RecurrenceFrequency.Monthly => each / interval,
        RecurrenceFrequency.Quarterly => each / (3m * interval),
        _ => each / (12m * interval),
    };

    private static string Schedule(RecurrenceFrequency frequency, int interval)
    {
        var unit = frequency switch
        {
            RecurrenceFrequency.Weekly => "week",
            RecurrenceFrequency.Monthly => "month",
            RecurrenceFrequency.Quarterly => "quarter",
            _ => "year",
        };
        return interval == 1 ? $"Every {unit}" : $"Every {interval} {unit}s";
    }

    private static string ClassName(Dictionary<Guid, Segment> classes, Guid id)
    {
        var names = new List<string>();
        for (Guid? at = id; at is { } current && classes.TryGetValue(current, out var segment) && names.Count <= Segment.MaxDepth; at = segment.ParentId)
            names.Insert(0, segment.Name);
        return string.Join(':', names);
    }

    private static string Label(FixedCostSource source) => source switch
    {
        FixedCostSource.RecurringExpense => "Recurring expense",
        FixedCostSource.RecurringBill => "Recurring bill",
        FixedCostSource.RecurringEntry => "Recurring entry",
        FixedCostSource.Loan => "Loan",
        FixedCostSource.Depreciation => "Depreciation",
        _ => "Settlements",
    };

    private static string LinkKind(FixedCostSource source) => source switch
    {
        FixedCostSource.RecurringExpense => "recurring_expense",
        FixedCostSource.RecurringBill => "recurring_bill",
        FixedCostSource.RecurringEntry => "recurring_entry",
        FixedCostSource.Loan => "loan",
        FixedCostSource.Depreciation => "fixed_asset",
        _ => "carrier",
    };
}
