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
