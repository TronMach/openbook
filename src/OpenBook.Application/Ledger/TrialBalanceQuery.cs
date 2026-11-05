using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Ledger;

public sealed record TrialBalanceLine(Guid AccountId, string? Number, string FullName, AccountType Type, Money Debit, Money Credit);

/// <param name="FiscalStart">Income and expense accounts' balances run from here; earlier years are in retained earnings.</param>
public sealed record TrialBalance(DateOnly AsOf, ReportBasis Basis, IReadOnlyList<TrialBalanceLine> Lines, Money TotalDebits, Money TotalCredits, DateOnly FiscalStart)
{
    public bool IsBalanced => TotalDebits == TotalCredits;
}

/// <summary>
/// Every account's balance as of a date, in chart order, on either basis. Income and expense accounts show this fiscal
/// year; earlier years are in retained earnings, as on the balance sheet (a computed row, with an empty account id,
/// when the chart has no Retained Earnings account).
/// </summary>
public sealed class TrialBalanceQuery(ReportLedger ledger, AccountService accounts, ICompanyStore companies)
{
    /// <param name="basis">Defaults to the company's default report basis.</param>
    public async Task<TrialBalance> RunAsync(DateOnly asOf, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var currency = company.HomeCurrency;

        var fiscalStart = company.FiscalYearStart(asOf);
        var chart = await accounts.GetChartAsync(ct);
        var totals = await ledger.TotalsAsync(reportBasis, fiscalStart, asOf, ct);
        var prior = FinancialReports.PriorYears(totals, fiscalStart, chart);
        var balances = totals.Except(prior)
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var retained = prior.Sum(l => l.Amount);
        var retainedAccount = FinancialReports.RetainedEarningsAccount(chart);
        if (retainedAccount is not null)
            balances[retainedAccount.Id] = balances.GetValueOrDefault(retainedAccount.Id) + retained;

        var lines = chart.Ordered()
            .Where(x => balances.GetValueOrDefault(x.Account.Id) != 0m)
            .Select(x =>
            {
                var balance = balances[x.Account.Id];
                return new TrialBalanceLine(
                    x.Account.Id,
                    x.Account.Number,
                    chart.FullName(x.Account.Id),
                    x.Account.Type,
                    new Money(Math.Max(balance, 0m), currency),
                    new Money(Math.Max(-balance, 0m), currency));
            })
            .ToList();
        if (retainedAccount is null && retained != 0m)
        {
            var afterEquity = lines.FindIndex(l => !l.Type.IsBalanceSheet());
            lines.Insert(afterEquity < 0 ? lines.Count : afterEquity, new TrialBalanceLine(
                Guid.Empty, null, "Retained earnings", AccountType.Equity, new Money(Math.Max(retained, 0m), currency), new Money(Math.Max(-retained, 0m), currency)));
        }

        return new TrialBalance(
            asOf,
            reportBasis,
            lines,
            new Money(lines.Sum(l => l.Debit.Amount), currency),
            new Money(lines.Sum(l => l.Credit.Amount), currency),
            fiscalStart);
    }
}
