using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Reporting;

namespace OpenBook.Application.Reporting;

public enum BudgetSeed
{
    /// <summary>Start with no amounts.</summary>
    Blank,

    /// <summary>Each month's actual amounts from the fiscal year before.</summary>
    PriorYearActuals,

    /// <summary>The amounts of another budget, month for month.</summary>
    CopyBudget,
}

/// <param name="FiscalYear">The calendar year the budget's fiscal year starts in.</param>
/// <param name="ClassId">A budget for one class (with its subclasses); actuals and prior-year seeding follow it.</param>
/// <param name="LocationId">A budget for one location (with those under it).</param>
public sealed record CreateBudgetCommand(
    string Name, int FiscalYear, BudgetSeed Seed = BudgetSeed.Blank, Guid? CopyFromBudgetId = null, Guid? ClassId = null, Guid? LocationId = null);

/// <summary>Budgets for profit and loss accounts, by month of a fiscal year.</summary>
public sealed class BudgetService(IReportingStore store, AccountService accounts, ICompanyStore companies, ReportLedger ledger, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "budget";

    public async Task<IReadOnlyList<Budget>> ListAsync(CancellationToken ct = default) =>
        (await store.ListBudgetsAsync(ct)).OrderByDescending(b => b.FiscalYearStart).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<Budget?> GetAsync(Guid id, CancellationToken ct = default) => store.GetBudgetAsync(id, ct);

    public async Task<Budget> CreateAsync(CreateBudgetCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.FiscalYear is < 2000 or > 2100)
            throw new DomainException("budget.year_invalid", "Choose a year between 2000 and 2100.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var budget = Budget.Create(command.Name, new DateOnly(command.FiscalYear, company.FiscalYearStartMonth, 1), clock.UtcNow);
        await EnsureUniqueAsync(budget.Name, null, ct);
        budget.SetSegments(command.ClassId, command.LocationId);

        switch (command.Seed)
        {
            case BudgetSeed.PriorYearActuals:
                budget.SetAmounts(await ActualsAsync(budget.FiscalYearStart.AddYears(-1), company.DefaultReportBasis, new SegmentFilter(budget.ClassId, budget.LocationId), ct));
                break;
            case BudgetSeed.CopyBudget:
                var source = command.CopyFromBudgetId is { } id ? await store.GetBudgetAsync(id, ct) : null;
                if (source is null)
                    throw new DomainException("budget.copy_source_required", "Choose the budget to copy.");
                budget.SetAmounts(source.Lines.Select(l => new BudgetAmount(l.AccountId, l.Period, l.Amount)));
                break;
        }

        store.Add(budget);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, budget.Id, null, Snapshot(budget)));
        await store.SaveChangesAsync(ct);
        return budget;
    }

    /// <summary>Renames the budget and replaces all its amounts.</summary>
    public async Task<Budget> UpdateAsync(Guid id, string name, IReadOnlyCollection<BudgetAmount> amounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var budget = await store.GetBudgetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var normalized = Budget.NormalizeName(name);
        await EnsureUniqueAsync(normalized, id, ct);

        var chart = await accounts.GetChartAsync(ct);
        foreach (var accountId in amounts.Where(a => a.Amount != 0m).Select(a => a.AccountId).Distinct())
        {
            if (chart.Find(accountId) is not { } account || account.Type.IsBalanceSheet())
                throw new DomainException("budget.account_invalid", "Budgets are for income and expense accounts.");
        }

        var before = Snapshot(budget);
        budget.Rename(normalized);
        budget.SetAmounts(amounts);
        var after = Snapshot(budget);
        if (after != before)
        {
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, budget.Id, before, after));
            await store.SaveChangesAsync(ct);
        }

        return budget;
    }

    /// <summary>The budget as a CSV: an account's full name, then its twelve months in the budget's order.</summary>
    public async Task<string> ToCsvAsync(Guid id, CancellationToken ct = default)
    {
        var budget = await store.GetBudgetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var chart = await accounts.GetChartAsync(ct);
        var text = new System.Text.StringBuilder("Account");
        for (var month = 1; month <= Budget.Periods; month++)
            text.Append(',').Append(budget.PeriodStart(month).ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        text.Append("\r\n");
        var amounts = budget.Lines.ToLookup(l => l.AccountId);
        foreach (var (account, _) in chart.Ordered(includeInactive: false).Where(x => !x.Account.Type.IsBalanceSheet()))
        {
            text.Append('"').Append(chart.FullName(account.Id).Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            for (var month = 1; month <= Budget.Periods; month++)
                text.Append(',').Append((amounts[account.Id].FirstOrDefault(l => l.Period == month)?.Amount ?? 0m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            text.Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Replaces the amounts of the accounts a CSV lists, all or nothing: an Account column by full name or number, then
    /// twelve columns of months in the budget's order. Accounts it doesn't list keep theirs.
    /// </summary>
    public async Task<Budget> ImportCsvAsync(Guid id, string csv, CancellationToken ct = default)
    {
        var budget = await store.GetBudgetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var records = Sales.SalesImport.Records(csv, 2000);
        var cols = new Import.CsvColumns(records[0]);
        var accountColumn = cols.Find("account", "accountname", "name")
            ?? throw new DomainException("budget.csv_no_account", "The file needs an Account column.");
        var chart = await accounts.GetChartAsync(ct);
        var byName = chart.Ordered(includeInactive: true).ToDictionary(x => chart.FullName(x.Account.Id), x => x.Account, StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<Guid>();
        var amounts = new List<BudgetAmount>();
        foreach (var record in records.Skip(1))
        {
            var name = Import.CsvColumns.Field(record, accountColumn)?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            var account = byName.GetValueOrDefault(name) ?? chart.Ordered(includeInactive: true).Select(x => x.Account).FirstOrDefault(a => a.Number == name)
                ?? throw new DomainException("budget.csv_account", $"Line {record.Line}: there's no account \"{name}\".");
            if (account.Type.IsBalanceSheet())
                throw new DomainException("budget.account_invalid", "Budgets are for income and expense accounts.");
            if (!listed.Add(account.Id))
                throw new DomainException("budget.csv_repeat", $"Line {record.Line}: {name} is listed twice.");
            for (var month = 1; month <= Budget.Periods; month++)
            {
                var text = Import.CsvColumns.Field(record, accountColumn + month);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (!Import.StatementValues.TryParseAmount(text, out var amount))
                    throw new DomainException("budget.csv_amount", $"Line {record.Line}: \"{text}\" isn't an amount.");
                if (amount != 0m)
                    amounts.Add(new BudgetAmount(account.Id, month, amount));
            }
        }

        if (listed.Count == 0)
            throw new DomainException("budget.csv_empty", "The file has no accounts in it.");
        var kept = budget.Lines.Where(l => !listed.Contains(l.AccountId)).Select(l => new BudgetAmount(l.AccountId, l.Period, l.Amount));
        return await UpdateAsync(id, budget.Name, [.. kept, .. amounts], ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var budget = await store.GetBudgetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, budget.Id, Snapshot(budget), null));
        store.Remove(budget);
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Profit and loss amounts by month of the fiscal year starting <paramref name="start"/>, in natural sign.</summary>
    private async Task<IReadOnlyList<BudgetAmount>> ActualsAsync(DateOnly start, ReportBasis basis, SegmentFilter filter, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(basis, start, start.AddYears(1).AddDays(-1), filter, ct);
        return lines
            .Where(l => chart.Find(l.AccountId) is { } a && !a.Type.IsBalanceSheet())
            .GroupBy(l => (l.AccountId, Period: (l.Date.Year - start.Year) * 12 + l.Date.Month - start.Month + 1))
            .Select(g => new BudgetAmount(g.Key.AccountId, g.Key.Period, (chart.Get(g.Key.AccountId).Type.IsDebitNormal() ? 1m : -1m) * g.Sum(l => l.Amount)))
            .Where(a => a.Amount != 0m)
            .ToList();
    }

    private async Task EnsureUniqueAsync(string name, Guid? except, CancellationToken ct)
    {
        if ((await store.ListBudgetsAsync(ct)).Any(b => b.Id != except && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("budget.name_taken", $"There's already a budget named \"{name}\".");
    }

    private static string Snapshot(Budget b) => JsonSerializer.Serialize(new
    {
        b.Name,
        b.FiscalYearStart,
        b.ClassId,
        b.LocationId,
        Amounts = b.Lines
            .GroupBy(l => l.AccountId)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => Enumerable.Range(1, Budget.Periods).Select(p => g.Where(l => l.Period == p).Sum(l => l.Amount)).ToArray()),
    });
}
