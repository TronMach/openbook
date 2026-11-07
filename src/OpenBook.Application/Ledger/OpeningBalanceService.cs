using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Ledger;

/// <param name="Balance">In the account's natural sign: positive is money in the bank, value of an asset, or amount owed.</param>
/// <param name="ClassId">The class the amount belongs to (a program, a grant), for history by class; null for none.</param>
public sealed record OpeningBalance(Guid AccountId, decimal Balance, Guid? ClassId = null);

/// <summary>
/// Starting balances for a company moving to OpenBook: one entry dated the day before it starts, with the
/// difference going to "Opening Balance Equity". Receivables and payables are left out; enter unpaid
/// invoices and bills individually so they can be paid.
/// </summary>
public sealed class OpeningBalanceService(AccountService accounts, ILedgerStore ledger, PostingEngine engine)
{
    public const string EquityAccountName = "Opening Balance Equity";

    /// <summary>Balance sheet accounts that take an opening balance here, in chart order.</summary>
    public async Task<IReadOnlyList<Account>> EligibleAccountsAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        return chart.Ordered(includeInactive: false)
            .Select(x => x.Account)
            .Where(IsEligible)
            .ToList();
    }

    /// <summary>Opening balance entries already posted (not voided), oldest first.</summary>
    public async Task<IReadOnlyList<JournalEntry>> ExistingAsync(CancellationToken ct = default)
    {
        var (entries, _) = await ledger.ListEntriesAsync(new JournalEntryFilter(Take: int.MaxValue, Source: JournalEntry.OpeningBalanceSource), ct);
        return entries
            .Where(e => e.Status == JournalEntryStatus.Posted && e.Kind == JournalEntryKind.Standard)
            .OrderBy(e => e.Date)
            .ToList();
    }

    public async Task<JournalEntry> PostAsync(DateOnly asOf, IReadOnlyCollection<OpeningBalance> balances, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(balances);
        var chart = await accounts.GetChartAsync(ct);
        var lines = new List<JournalLineDraft>();
        foreach (var balance in balances.Where(b => b.Balance != 0m))
        {
            var account = chart.Get(balance.AccountId);
            if (!IsEligible(account))
                throw new DomainException("opening.account_not_eligible", $"'{account.Name}' can't take an opening balance here.");
            // Natural-sign balance to debit/credit: a debit-normal account with a positive balance is a debit.
            var debit = account.Type.IsDebitNormal() == (balance.Balance > 0m);
            var amount = Math.Abs(balance.Balance);
            lines.Add(debit ? JournalLineDraft.Dr(account.Id, amount, null, balance.ClassId) : JournalLineDraft.Cr(account.Id, amount, null, balance.ClassId));
        }

        if (lines.Count == 0)
            throw new DomainException("opening.nothing_entered", "Enter at least one opening balance.");

        var difference = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        if (difference != 0m)
        {
            var equity = await GetOrCreateEquityAccountAsync(chart, ct);
            lines.Add(difference > 0m ? JournalLineDraft.Cr(equity.Id, difference) : JournalLineDraft.Dr(equity.Id, -difference));
        }

        return await engine.PostAsync(new PostJournalEntryCommand(asOf, lines, "Opening balances", JournalEntry.OpeningBalanceSource), ct);
    }

    /// <summary>Income and expense accounts, for the year so far, in chart order.</summary>
    public async Task<IReadOnlyList<Account>> YearToDateAccountsAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        return chart.Ordered(includeInactive: false).Select(x => x.Account).Where(a => !a.Type.IsBalanceSheet()).ToList();
    }

    /// <summary>
    /// For a business starting OpenBook part-way through its year: what each income and expense account came to in the
    /// old books this year, posted on <paramref name="through"/> (the day before starting) against "Opening Balance
    /// Equity", so the year's profit and loss and tax summary are whole. Amounts are positive for income earned and
    /// expenses spent (a minus sign for a refund or a credit). The opening balances, as of the same day, already hold
    /// that profit in what the business owns and owes, so equity comes out right.
    /// </summary>
    public async Task<JournalEntry> PostYearToDateAsync(DateOnly through, IReadOnlyCollection<OpeningBalance> amounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var chart = await accounts.GetChartAsync(ct);
        var lines = new List<JournalLineDraft>();
        foreach (var amount in amounts.Where(a => a.Balance != 0m))
        {
            var account = chart.Get(amount.AccountId);
            if (account.Type.IsBalanceSheet())
                throw new DomainException("opening.year_to_date_account", $"'{account.Name}' is a balance sheet account; its balance goes with the opening balances.");
            var debit = account.Type.IsDebitNormal() == (amount.Balance > 0m);
            var value = Math.Abs(amount.Balance);
            lines.Add(debit ? JournalLineDraft.Dr(account.Id, value, null, amount.ClassId) : JournalLineDraft.Cr(account.Id, value, null, amount.ClassId));
        }

        if (lines.Count == 0)
            throw new DomainException("opening.nothing_entered", "Enter at least one amount.");

        var difference = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        if (difference != 0m)
        {
            var equity = await GetOrCreateEquityAccountAsync(chart, ct);
            lines.Add(difference > 0m ? JournalLineDraft.Cr(equity.Id, difference) : JournalLineDraft.Dr(equity.Id, -difference));
        }

        return await engine.PostAsync(new PostJournalEntryCommand(through, lines, "Year to date before OpenBook", JournalEntry.OpeningBalanceSource), ct);
    }

    internal static bool IsEligible(Account account) =>
        account.Type.IsBalanceSheet()
        && account.Type is not (AccountType.AccountsReceivable or AccountType.AccountsPayable)
        && !string.Equals(account.Name, EquityAccountName, StringComparison.OrdinalIgnoreCase);

    private async Task<Account> GetOrCreateEquityAccountAsync(ChartOfAccounts chart, CancellationToken ct) =>
        chart.FindByFullName(EquityAccountName)
        ?? await accounts.CreateAsync(new CreateAccountCommand(EquityAccountName, AccountType.Equity,
            Description: "Balances brought in when the books were started; move them to owner's equity once confirmed."), ct);
}
