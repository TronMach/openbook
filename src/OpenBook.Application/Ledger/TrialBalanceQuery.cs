using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Ledger;

public sealed record TrialBalanceLine(Guid AccountId, string? Number, string FullName, AccountType Type, Money Debit, Money Credit);

public sealed record TrialBalance(DateOnly AsOf, ReportBasis Basis, IReadOnlyList<TrialBalanceLine> Lines, Money TotalDebits, Money TotalCredits)
{
    public bool IsBalanced => TotalDebits == TotalCredits;
}

/// <summary>Every account's balance as of a date, in chart order, on either basis.</summary>
public sealed class TrialBalanceQuery(ReportLedger ledger, AccountService accounts, ICompanyStore companies)
{
    /// <param name="basis">Defaults to the company's default report basis.</param>
    public async Task<TrialBalance> RunAsync(DateOnly asOf, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var currency = company.HomeCurrency;

        var balances = (await ledger.LinesAsync(reportBasis, null, asOf, ct))
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var chart = await accounts.GetChartAsync(ct);

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

        return new TrialBalance(
            asOf,
            reportBasis,
            lines,
            new Money(lines.Sum(l => l.Debit.Amount), currency),
            new Money(lines.Sum(l => l.Credit.Amount), currency));
    }
}
