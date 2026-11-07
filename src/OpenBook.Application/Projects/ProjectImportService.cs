using System.Globalization;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

public enum ProjectImportKind
{
    People,
    Rates,
    Time,
}

public sealed record ProjectImportRow(int Line, string Text, ImportRowStatus Status, string? Message);

public sealed record ProjectImportResult(IReadOnlyList<ProjectImportRow> Rows, int NewCount, int ExistingCount, int ErrorCount, IReadOnlyList<string> Warnings)
{
    public bool CanImport => ErrorCount == 0 && NewCount > 0;
}

/// <summary>
/// A firm's people, rate card and unbilled time from CSV files, each all or nothing, for moving in. Import people
/// first: rates and time name them. Customers and services must be in the books already, by name
/// ("Client:Project" reaches a sub-customer).
/// </summary>
public sealed class ProjectImportService(
    WorkerService workers,
    BillingRateService rates,
    TimeService time,
    CustomerService customers,
    ISalesStore sales,
    IUnitOfWork unitOfWork)
{
    public const int MaxRows = 10000;

    public Task<ProjectImportResult> PreviewAsync(ProjectImportKind kind, string csv, CancellationToken ct = default) => RunAsync(kind, csv, save: false, ct);

    public async Task<ProjectImportResult> ImportAsync(ProjectImportKind kind, string csv, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var result = await RunAsync(kind, csv, save: true, ct);
        SalesImport.EnsureImportable(result.ErrorCount, result.NewCount);
        await transaction.CommitAsync(ct);
        return result;
    }

    private Task<ProjectImportResult> RunAsync(ProjectImportKind kind, string csv, bool save, CancellationToken ct) => kind switch
    {
        ProjectImportKind.People => PeopleAsync(csv, save, ct),
        ProjectImportKind.Rates => RatesAsync(csv, save, ct),
        _ => TimeAsync(csv, save, ct),
    };

    private static ProjectImportResult Result(List<ProjectImportRow> rows, IReadOnlyList<string> warnings) => new(
        rows, rows.Count(r => r.Status == ImportRowStatus.New), rows.Count(r => r.Status == ImportRowStatus.Exists), rows.Count(r => r.Status == ImportRowStatus.Error), warnings);

    private static decimal? Amount(string? text) => StatementValues.TryParseAmount(text, out var amount) ? amount : null;

    private async Task<ProjectImportResult> PeopleAsync(string csv, bool save, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var name = cols.Find("name", "person", "employee", "worker", "fullname") ?? throw new DomainException("import.no_name_column", "The file needs a Name column.");
        var role = cols.Find("role", "title", "level");
        var cost = cols.Find("costrate", "cost", "hourlycost");
        var bill = cols.Find("billrate", "rate", "billingrate");
        var hours = cols.Find("weeklyhours", "hoursperweek", "hours");

        var existing = (await workers.ListAsync(includeInactive: true, ct)).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProjectImportRow>();
        foreach (var r in records.Skip(1))
        {
            var who = CsvColumns.Field(r, name)?.Trim();
            if (string.IsNullOrEmpty(who))
            {
                rows.Add(new ProjectImportRow(r.Line, "", ImportRowStatus.Error, "There's no name."));
                continue;
            }

            if (!existing.Add(who))
            {
                rows.Add(new ProjectImportRow(r.Line, who, ImportRowStatus.Exists, "Already on the list."));
                continue;
            }

            try
            {
                var details = new WorkerDetails(who, Amount(CsvColumns.Field(r, cost)) ?? 0m, Amount(CsvColumns.Field(r, bill)), null, null, CsvColumns.Field(r, role)?.Trim(), Amount(CsvColumns.Field(r, hours)));
                if (save)
                    await workers.CreateAsync(details, ct);
                else
                    Worker.Create(details);
                rows.Add(new ProjectImportRow(r.Line, who, ImportRowStatus.New, null));
            }
            catch (DomainException ex) when (!save)
            {
                rows.Add(new ProjectImportRow(r.Line, who, ImportRowStatus.Error, ex.Message));
            }
        }

        return Result(rows, []);
    }

    private async Task<ProjectImportResult> RatesAsync(string csv, bool save, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var rate = cols.Find("rate", "billrate", "hourlyrate", "amount") ?? throw new DomainException("import.no_amount_column", "The file needs a Rate column.");
        var client = cols.Find("client", "customer", "project");
        var person = cols.Find("person", "worker", "employee", "name");
        var role = cols.Find("role", "level", "title");
        var service = cols.Find("service", "item", "product");
        var starts = cols.Find("starts", "startson", "from", "date", "effective");

        var (order, warnings) = Order(records.Skip(1).Select(r => CsvColumns.Field(r, starts)));
        var clients = await ClientsAsync(ct);
        var people = (await workers.ListAsync(includeInactive: true, ct)).ToDictionary(w => w.Name, w => w.Id, StringComparer.OrdinalIgnoreCase);
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Name, i => i.Id, StringComparer.OrdinalIgnoreCase);
        var card = await rates.ListAsync(ct);
        var rows = new List<ProjectImportRow>();
        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c)?.Trim();
            var label = string.Join(" · ", new[] { F(client), F(person), F(role), F(service), F(rate) }.Where(t => !string.IsNullOrEmpty(t)));
            Guid? clientId = null, personId = null, itemId = null;
            var error = !StatementValues.TryParseAmount(F(rate), out var amount) ? "The rate can't be read."
                : F(client) is { Length: > 0 } c && !clients.TryGetValue(c, out var foundClient) ? $"There's no customer \"{c}\"."
                : F(person) is { Length: > 0 } p && !people.TryGetValue(p, out var foundPerson) ? $"There's no person \"{p}\". Import people first."
                : F(service) is { Length: > 0 } s && !items.TryGetValue(s, out var foundItem) ? $"There's no service \"{s}\"."
                : null;
            if (error is null)
            {
                clientId = F(client) is { Length: > 0 } name ? clients[name] : null;
                personId = F(person) is { Length: > 0 } who ? people[who] : null;
                itemId = F(service) is { Length: > 0 } what ? items[what] : null;
            }

            if (error is not null)
            {
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.Error, error));
                continue;
            }

            DateOnly? startsOn = StatementValues.TryParseDate(F(starts), order, out var date) ? date : null;
            var details = new BillingRateDetails(amount, clientId, personId, F(role) is { Length: > 0 } level ? level : null, itemId, startsOn);
            if (card.Any(b => b.CustomerId == clientId && b.WorkerId == personId && b.Role == details.Role && b.ItemId == itemId && b.StartsOn == startsOn && b.Rate == amount))
            {
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.Exists, "Already on the rate card."));
                continue;
            }

            try
            {
                if (save)
                    await rates.CreateAsync(details, ct);
                else
                    BillingRate.Create(details, DateTimeOffset.UtcNow);
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.New, null));
            }
            catch (DomainException ex) when (!save)
            {
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.Error, ex.Message));
            }
        }

        return Result(rows, warnings);
    }

    private async Task<ProjectImportResult> TimeAsync(string csv, bool save, CancellationToken ct)
    {
        var records = SalesImport.Records(csv, MaxRows);
        var cols = new CsvColumns(records[0]);
        var dateColumn = cols.Find("date", "workdate", "day") ?? throw new DomainException("import.no_date_column", "The file needs a date column.");
        var person = cols.Find("person", "worker", "employee", "name") ?? throw new DomainException("import.no_name_column", "The file needs a Person column.");
        var hoursColumn = cols.Find("hours", "duration", "qty", "quantity") ?? throw new DomainException("import.no_amount_column", "The file needs an Hours column.");
        var client = cols.Find("client", "customer", "project", "customerjob");
        var service = cols.Find("service", "item", "serviceitem");
        var billable = cols.Find("billable", "billingstatus");
        var rate = cols.Find("rate", "billrate", "hourlyrate");
        var notes = cols.Find("notes", "note", "memo", "description");

        var (order, warnings) = Order(records.Skip(1).Select(r => CsvColumns.Field(r, dateColumn)));
        var clients = await ClientsAsync(ct);
        var people = (await workers.ListAsync(includeInactive: true, ct)).ToDictionary(w => w.Name, w => w.Id, StringComparer.OrdinalIgnoreCase);
        var items = (await sales.ListItemsAsync(ct)).ToDictionary(i => i.Name, i => i.Id, StringComparer.OrdinalIgnoreCase);
        var entered = (await time.ListAsync(new TimeFilter(), ct)).GroupBy(t => (t.WorkerId, t.Date, t.CustomerId, t.Hours)).ToDictionary(g => g.Key, g => g.Count());
        var rows = new List<ProjectImportRow>();
        foreach (var r in records.Skip(1))
        {
            string? F(int? c) => CsvColumns.Field(r, c)?.Trim();
            var label = string.Join(" · ", new[] { F(dateColumn), F(person), F(client), F(hoursColumn) }.Where(t => !string.IsNullOrEmpty(t)));
            var hasDate = StatementValues.TryParseDate(F(dateColumn), order, out var date);
            var hasHours = StatementValues.TryParseAmount(F(hoursColumn), out var hours);
            var error = !hasDate ? "The date can't be read."
                : !hasHours ? "The hours can't be read."
                : !people.ContainsKey(F(person) ?? "") ? $"There's no person \"{F(person)}\". Import people first."
                : F(client) is { Length: > 0 } c && !clients.ContainsKey(c) ? $"There's no customer \"{c}\"."
                : F(service) is { Length: > 0 } s && !items.ContainsKey(s) ? $"There's no service \"{s}\"."
                : null;
            if (error is not null)
            {
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.Error, error));
                continue;
            }

            var workerId = people[F(person)!];
            Guid? customerId = F(client) is { Length: > 0 } name ? clients[name] : null;
            Guid? itemId = F(service) is { Length: > 0 } what ? items[what] : null;
            var key = (workerId, date, customerId, hours);
            if (entered.TryGetValue(key, out var held) && held > 0)
            {
                entered[key] = held - 1;
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.Exists, "Already entered."));
                continue;
            }

            // Billable unless the file says otherwise, when there's someone to bill and something to bill as.
            var marked = F(billable)?.ToUpperInvariant();
            var bill = marked is "N" or "NO" or "FALSE" or "0" or "NOT BILLABLE" or "NONBILLABLE" ? false : customerId is not null && itemId is not null;
            try
            {
                var details = new TimeEntryDetails(workerId, date, hours, customerId, itemId, bill, Amount(F(rate)), F(notes));
                if (save)
                    await time.CreateAsync(details, ct);
                else if (hours <= 0m)
                    throw new DomainException("time.hours", "Hours are above zero.");
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.New, null));
            }
            catch (DomainException ex) when (!save)
            {
                rows.Add(new ProjectImportRow(r.Line, label, ImportRowStatus.Error, ex.Message));
            }
        }

        return Result(rows, warnings);
    }

    private async Task<Dictionary<string, Guid>> ClientsAsync(CancellationToken ct)
    {
        var all = await sales.ListCustomersAsync(ct);
        var clients = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var customer in all)
            clients[customer.DisplayName] = customer.Id;
        foreach (var (id, name) in await customers.FullNamesAsync(ct))
            clients[name] = id;
        return clients;
    }

    private static (DateOrder Order, IReadOnlyList<string> Warnings) Order(IEnumerable<string?> dates)
    {
        var order = StatementValues.DetectDateOrder(dates.OfType<string>().ToList(), CultureInfo.CurrentCulture, out var ambiguous);
        return (order, ambiguous
            ? [$"Every date could be read either day-first or month-first; they were read {(order == DateOrder.DayFirst ? "day-first" : "month-first")}. Check them."]
            : []);
    }
}
