using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Ledger;

public sealed record TrialBalanceLine(Guid AccountId, string? Number, string Name, AccountType Type, Money Debit, Money Credit);

public sealed record TrialBalance(DateOnly AsOf, IReadOnlyList<TrialBalanceLine> Lines, Money TotalDebits, Money TotalCredits)
{
    public bool IsBalanced => TotalDebits == TotalCredits;
}

/// <summary>Minimal trial balance used to verify the engine. Full reporting arrives in M5.</summary>
public sealed class TrialBalanceQuery(ILedgerStore store, ICompanyStore companies)
{
    public async Task<TrialBalance> RunAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var currency = (await companies.GetRequiredCompanyAsync(ct)).HomeCurrency;
        var balances = await store.GetBalancesAsync(asOf, ct);
        var accounts = await store.GetAccountsAsync(balances.Select(b => b.AccountId).ToList(), ct);

        var lines = balances
            .Where(b => b.Balance != 0m)
            .Select(b =>
            {
                var a = accounts[b.AccountId];
                return new TrialBalanceLine(
                    a.Id,
                    a.Number,
                    a.Name,
                    a.Type,
                    new Money(Math.Max(b.Balance, 0m), currency),
                    new Money(Math.Max(-b.Balance, 0m), currency));
            })
            .OrderBy(l => l.Type)
            .ThenBy(l => l.Number, StringComparer.Ordinal)
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new TrialBalance(
            asOf,
            lines,
            new Money(lines.Sum(l => l.Debit.Amount), currency),
            new Money(lines.Sum(l => l.Credit.Amount), currency));
    }
}
