using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>
/// A project's figures over a period, from the documents (accrual): income is its invoices and sales receipts less
/// credit memos, without sales tax; costs are bill and expense lines marked to it, labor (hours × the cost rate when
/// entered) and mileage at the year's standard rate.
/// </summary>
public sealed record ProjectFigures(
    ProjectSummary Project,
    decimal Income,
    decimal ExpenseCost,
    decimal LaborCost,
    decimal MileageCost,
    decimal Hours,
    decimal UnbilledTime,
    decimal UnbilledExpenses,
    decimal UnbilledMileage)
{
    public decimal Cost => ExpenseCost + LaborCost + MileageCost;
    public decimal Profit => Income - Cost;

    /// <summary>Profit as a percentage of income; null without income.</summary>
    public decimal? Margin => Income == 0m ? null : decimal.Round(Profit / Income * 100m, 1, MidpointRounding.AwayFromZero);

    public decimal Unbilled => UnbilledTime + UnbilledExpenses + UnbilledMileage;
}

public sealed class ProjectReports(
    ProjectService projects, IProjectStore store, ISalesStore sales, MileageService mileage, ICompanyStore companies)
{
    public async Task<IReadOnlyList<ProjectFigures>> ProfitabilityAsync(DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var list = await projects.ListAsync(includeClosed: true, ct);
        var result = new List<ProjectFigures>();
        foreach (var project in list)
            result.Add(await FiguresAsync(project, from, to, ct));
        return result;
    }

    public async Task<ProjectFigures> FiguresAsync(ProjectSummary project, DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var customerId = project.Customer.Id;
        bool InRange(DateOnly d) => (from is null || d >= from) && d <= to;
        static decimal Net(IEnumerable<SalesLine> lines) => lines.Where(l => l.Kind != SalesLineKind.Tax).Sum(l => l.Amount);

        var income =
            (await sales.ListInvoicesAsync(customerId, ct)).Where(i => i.Status == DocumentStatus.Posted && InRange(i.Date)).Sum(i => Net(i.Lines))
            + (await sales.ListReceiptsAsync(ct)).Where(r => r.CustomerId == customerId && r.Status == DocumentStatus.Posted && InRange(r.Date)).Sum(r => Net(r.Lines))
            - (await sales.ListCreditMemosAsync(customerId, ct)).Where(c => c.Status == DocumentStatus.Posted && InRange(c.Date)).Sum(c => Net(c.Lines));

        var expenses = await sales.ListBillableExpensesAsync(customerId, ct);
        var time = (await store.ListTimeAsync(new TimeFilter(CustomerId: customerId), ct)).Where(t => !t.IsRunning).ToList();
        var trips = await store.ListMileageAsync(null, null, customerId, ct);
        var rates = await mileage.RatesAsync(ct);

        return new ProjectFigures(
            project,
            income,
            expenses.Where(e => InRange(e.Date)).Sum(e => e.Cost),
            time.Where(t => InRange(t.Date)).Sum(t => t.Cost),
            trips.Where(t => InRange(t.Date)).Sum(t => decimal.Round(t.Miles * rates.GetValueOrDefault(t.Date.Year), 2, MidpointRounding.AwayFromZero)),
            time.Where(t => InRange(t.Date)).Sum(t => t.Hours),
            time.Where(t => t.Billable && t.InvoiceId is null).Sum(t => t.Amount),
            expenses.Where(e => e.IsBillable && e.InvoiceId is null).Sum(e => e.Amount),
            trips.Where(t => t.Billable && t.InvoiceId is null).Sum(t => t.Amount));
    }

    public async Task<ReportTable> ProfitabilityTableAsync(DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var figures = await ProfitabilityAsync(from, to, ct);
        var rows = figures.Select(f => new ReportTableRow(
        [
            new ReportCell(f.Project.FullName, new ReportLink("project", f.Project.Project.Id)),
            Status(f.Project.Project.Status),
            f.Income,
            f.ExpenseCost,
            f.LaborCost,
            f.MileageCost,
            f.Profit,
            f.Margin is { } m ? new ReportCell(m) : ReportCell.Empty,
            new ReportCell(f.Hours),
            f.Project.Project.BudgetCost is { } budget ? new ReportCell(budget) : ReportCell.Empty,
            f.Unbilled,
        ])).ToList();
        rows.Add(new ReportTableRow(
        [
            "Total", null, figures.Sum(f => f.Income), figures.Sum(f => f.ExpenseCost), figures.Sum(f => f.LaborCost), figures.Sum(f => f.MileageCost),
            figures.Sum(f => f.Profit), null, new ReportCell(figures.Sum(f => f.Hours)), null, figures.Sum(f => f.Unbilled),
        ], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Project profitability",
            [company.Name, Describe(from, to)],
            [
                new("Project"), new("Status"), new("Income", ColumnFormat.Money), new("Expenses", ColumnFormat.Money), new("Labor", ColumnFormat.Money),
                new("Mileage", ColumnFormat.Money), new("Profit", ColumnFormat.Money), new("Margin", ColumnFormat.Percent), new("Hours", ColumnFormat.Number),
                new("Cost budget", ColumnFormat.Money), new("Unbilled", ColumnFormat.Money),
            ],
            rows,
            Note: "Income is invoices and sales receipts less credit memos, without sales tax. Labor is hours at each person's cost rate; mileage at the standard rate. Unbilled is billable time, expenses and mileage not yet invoiced, whatever their date.");
    }

    public async Task<ReportTable> TimeDetailAsync(DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var entries = (await store.ListTimeAsync(new TimeFilter(from, to), ct)).Where(t => !t.IsRunning).ToList();
        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var customers = CustomerTree.FullNames(await sales.ListCustomersAsync(ct));
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Id);
        var rows = new List<ReportTableRow>();
        foreach (var group in entries.GroupBy(e => e.CustomerId).OrderBy(g => g.Key is { } c ? customers[c] : "￿", StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Key is { } id ? customers[id] : "No customer";
            rows.Add(new ReportTableRow(group.Key is { } cid ? [new ReportCell(name, new ReportLink("customer", cid))] : [name], 0, ReportRowStyle.Section));
            rows.AddRange(group.OrderBy(e => e.Date).ThenBy(e => e.CreatedAt).Select(e => new ReportTableRow(
            [
                new ReportCell(e.Date),
                workers[e.WorkerId].Name,
                e.ItemId is { } item ? items[item].Name : null,
                e.Notes,
                new ReportCell(e.Hours),
                e.Billable ? new ReportCell(e.Amount) : ReportCell.Empty,
                e.Billable ? (e.InvoiceId is null ? "Not yet" : "Yes") : "Not billable",
            ], 1)));
            rows.Add(new ReportTableRow(
                [$"Total for {name}", null, null, null, new ReportCell(group.Sum(e => e.Hours)), group.Sum(e => e.Amount), null], 0, ReportRowStyle.Subtotal));
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, new ReportCell(entries.Sum(e => e.Hours)), entries.Sum(e => e.Amount), null], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Time by customer",
            [company.Name, Describe(from, to)],
            [
                new("Date", ColumnFormat.Date), new("Person"), new("Service"), new("Notes"), new("Hours", ColumnFormat.Number),
                new("Billable amount", ColumnFormat.Money), new("Invoiced"),
            ],
            rows);
    }

    public async Task<ReportTable> MileageLogAsync(int year, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var log = await mileage.YearAsync(year, ct);
        var customers = CustomerTree.FullNames(await sales.ListCustomersAsync(ct));
        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var rows = log.Trips.Select(t => new ReportTableRow(
        [
            new ReportCell(t.Date),
            t.Purpose,
            string.Join(" → ", new[] { t.FromPlace, t.ToPlace }.Where(p => p is not null)),
            t.Vehicle,
            t.WorkerId is { } w ? workers[w].Name : null,
            t.CustomerId is { } c ? customers[c] : null,
            new ReportCell(t.Miles),
            decimal.Round(t.Miles * log.Rate, 2, MidpointRounding.AwayFromZero),
        ])).ToList();
        rows.Add(new ReportTableRow(["Total", null, null, null, null, null, new ReportCell(log.Miles), log.Deduction], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Mileage log",
            [company.Name, $"{year} · standard rate {log.Rate:0.###} per mile"],
            [
                new("Date", ColumnFormat.Date), new("Purpose"), new("Route"), new("Vehicle"), new("Driver"), new("Customer"),
                new("Miles", ColumnFormat.Number), new("At standard rate", ColumnFormat.Money),
            ],
            rows,
            Note: "The standard mileage rate covers the cost of running the car; parking and tolls are deducted separately. Check the rate for the year before filing.");
    }

    /// <summary>
    /// Work in progress: billable time, mileage and expenses not invoiced on <paramref name="asOf"/> (an invoice dated
    /// later counts as not yet), by client and project, with the oldest work waiting. Detail lists each piece.
    /// </summary>
    public async Task<ReportTable> UnbilledWorkAsync(DateOnly asOf, bool detail, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var time = (await store.ListTimeAsync(new TimeFilter(To: asOf), ct)).Where(t => t.Billable && !t.IsRunning && t.CustomerId is not null).ToList();
        var trips = (await store.ListMileageAsync(null, asOf, null, ct)).Where(t => t.Billable && t.CustomerId is not null).ToList();
        var expenses = (await sales.ListBillableExpensesAsync(null, ct)).Where(e => e.IsBillable && e.Date <= asOf).ToList();
        var invoiceIds = time.Select(t => t.InvoiceId).Concat(trips.Select(t => t.InvoiceId)).Concat(expenses.Select(e => e.InvoiceId)).OfType<Guid>().Distinct().ToList();
        var invoiceDates = (await sales.GetInvoicesAsync(invoiceIds, ct)).ToDictionary(i => i.Id, i => i.Date);
        bool Waiting(Guid? invoiceId) => invoiceId is not { } id || invoiceDates.GetValueOrDefault(id, DateOnly.MaxValue) > asOf;

        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var customers = CustomerTree.FullNames(await sales.ListCustomersAsync(ct));
        var pieces = time.Where(t => Waiting(t.InvoiceId))
            .Select(t => new UnbilledPiece(t.CustomerId!.Value, t.Date, $"{workers[t.WorkerId].Name}{(t.Notes is null ? "" : ": " + t.Notes)}", t.Hours, t.Amount, 0m, 0m))
            .Concat(trips.Where(t => Waiting(t.InvoiceId))
                .Select(t => new UnbilledPiece(t.CustomerId!.Value, t.Date, $"{t.Miles:0.#} miles: {t.Purpose}", 0m, 0m, t.Amount, 0m)))
            .Concat(expenses.Where(e => Waiting(e.InvoiceId))
                .Select(e => new UnbilledPiece(e.CustomerId, e.Date, e.Description, 0m, 0m, 0m, e.Amount)))
            .ToList();

        var rows = new List<ReportTableRow>();
        foreach (var group in pieces.GroupBy(x => x.CustomerId).OrderBy(g => customers.GetValueOrDefault(g.Key, ""), StringComparer.OrdinalIgnoreCase))
        {
            var link = new ReportLink("customer", group.Key);
            var name = customers.GetValueOrDefault(group.Key, "Unknown client");
            if (detail)
            {
                rows.Add(new ReportTableRow([new ReportCell(name, link)], 0, ReportRowStyle.Section));
                rows.AddRange(group.OrderBy(x => x.Date).Select(x => new ReportTableRow(
                [
                    x.Description, x.Hours == 0m ? ReportCell.Empty : new ReportCell(x.Hours), Money(x.Time), Money(x.Mileage), Money(x.Expenses), x.Total, new ReportCell(x.Date),
                ], 1)));
            }

            rows.Add(new ReportTableRow(
            [
                detail ? $"Total for {name}" : new ReportCell(name, link), new ReportCell(group.Sum(x => x.Hours)), group.Sum(x => x.Time), group.Sum(x => x.Mileage),
                group.Sum(x => x.Expenses), group.Sum(x => x.Total), new ReportCell(group.Min(x => x.Date)),
            ], 0, detail ? ReportRowStyle.Subtotal : ReportRowStyle.Normal));
        }

        rows.Add(new ReportTableRow(
        [
            "Total", new ReportCell(pieces.Sum(x => x.Hours)), pieces.Sum(x => x.Time), pieces.Sum(x => x.Mileage), pieces.Sum(x => x.Expenses), pieces.Sum(x => x.Total),
            pieces.Count == 0 ? ReportCell.Empty : new ReportCell(pieces.Min(x => x.Date)),
        ], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Unbilled work",
            [company.Name, $"As of {asOf:MMM d, yyyy}"],
            [
                new(detail ? "Client and work" : "Client"), new("Hours", ColumnFormat.Number), new("Time", ColumnFormat.Money), new("Mileage", ColumnFormat.Money),
                new("Expenses", ColumnFormat.Money), new("Total", ColumnFormat.Money), new(detail ? "Date" : "Oldest", ColumnFormat.Date),
            ],
            rows,
            Note: "Billable time, mileage and expenses (with markup) not on an invoice dated on or before this date, at the rates they'll be billed at. Retainers held aren't subtracted.");

        static ReportCell Money(decimal amount) => amount == 0m ? ReportCell.Empty : new ReportCell(amount);
    }

    /// <summary>
    /// Each person's hours over a period: billable and not, the billable share, and their utilization (billable hours
    /// against their weekly hours over the period's weekdays), with what the billable time is worth and how much of
    /// it is on invoices.
    /// </summary>
    public async Task<ReportTable> UtilizationAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var time = (await store.ListTimeAsync(new TimeFilter(from, to), ct)).Where(t => !t.IsRunning).ToLookup(t => t.WorkerId);
        var weekdays = Weekdays(from, to);
        var people = (await store.ListWorkersAsync(ct))
            .Where(w => time[w.Id].Any() || (w.IsActive && w.WeeklyHours is not null))
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<ReportTableRow>();
        decimal totalCapacity = 0m, totalHours = 0m, totalBillable = 0m, totalValue = 0m, totalInvoiced = 0m;
        foreach (var w in people)
        {
            var entries = time[w.Id].ToList();
            var hours = entries.Sum(t => t.Hours);
            var billable = entries.Where(t => t.Billable).Sum(t => t.Hours);
            var capacity = w.WeeklyHours is { } weekly ? decimal.Round(weekly * weekdays / 5m, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
            var value = entries.Sum(t => t.Amount);
            var invoiced = entries.Where(t => t.InvoiceId is not null).Sum(t => t.Amount);
            rows.Add(new ReportTableRow(
            [
                w.Name, w.Role, capacity is { } c ? new ReportCell(c) : ReportCell.Empty, new ReportCell(hours), new ReportCell(billable), new ReportCell(hours - billable),
                Percent(billable, hours), capacity is { } cap ? Percent(billable, cap) : ReportCell.Empty, value, invoiced,
            ]));
            totalCapacity += capacity ?? 0m;
            totalHours += hours;
            totalBillable += billable;
            totalValue += value;
            totalInvoiced += invoiced;
        }

        var withCapacity = people.Where(w => w.WeeklyHours is not null).Sum(w => time[w.Id].Where(t => t.Billable).Sum(t => t.Hours));
        rows.Add(new ReportTableRow(
        [
            "Total", null, new ReportCell(totalCapacity), new ReportCell(totalHours), new ReportCell(totalBillable), new ReportCell(totalHours - totalBillable),
            Percent(totalBillable, totalHours), Percent(withCapacity, totalCapacity), totalValue, totalInvoiced,
        ], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Utilization",
            [company.Name, Comparisons.Describe(from, to), $"{weekdays} weekdays"],
            [
                new("Person"), new("Role"), new("Available hours", ColumnFormat.Number), new("Hours", ColumnFormat.Number), new("Billable hours", ColumnFormat.Number),
                new("Other hours", ColumnFormat.Number), new("Billable share", ColumnFormat.Percent), new("Utilization", ColumnFormat.Percent),
                new("Billable value", ColumnFormat.Money), new("Invoiced", ColumnFormat.Money),
            ],
            rows,
            Note: "Available hours are each person's hours a week spread over the period's weekdays (holidays aren't taken out). Utilization is billable hours against available hours; people without hours a week are listed without it.");

        static ReportCell Percent(decimal part, decimal whole) =>
            whole == 0m ? ReportCell.Empty : new ReportCell(decimal.Round(part / whole * 100m, 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>Monday to Friday days from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static int Weekdays(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        return count;
    }

    private sealed record UnbilledPiece(Guid CustomerId, DateOnly Date, string Description, decimal Hours, decimal Time, decimal Mileage, decimal Expenses)
    {
        public decimal Total => Time + Mileage + Expenses;
    }

    public static string Status(ProjectStatus status) => status switch
    {
        ProjectStatus.NotStarted => "Not started",
        ProjectStatus.InProgress => "In progress",
        ProjectStatus.Completed => "Completed",
        _ => "Cancelled",
    };

    private static string Describe(DateOnly? from, DateOnly to) => from is { } f ? Comparisons.Describe(f, to) : $"All dates to {to:MMM d, yyyy}";
}
