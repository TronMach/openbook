using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

/// <summary>Profit and loss, balance sheet and general ledger, all built from <see cref="ReportLedger"/>.</summary>
public sealed class FinancialReports(ReportLedger ledger, AccountService accounts, ICompanyStore companies)
{
    public async Task<ProfitAndLoss> ProfitAndLossAsync(DateOnly from, DateOnly to, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var reportBasis = basis ?? (await companies.GetRequiredCompanyAsync(ct)).DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        return BuildProfitAndLoss(from, to, reportBasis, chart, Sum(await ledger.LinesAsync(reportBasis, from, to, ct)));
    }

    /// <param name="amounts">Debit-positive amount per account.</param>
    /// <param name="show">Accounts to list even with no amount, so several columns get the same rows.</param>
    internal static ProfitAndLoss BuildProfitAndLoss(
        DateOnly from, DateOnly to, ReportBasis basis, ChartOfAccounts chart, IReadOnlyDictionary<Guid, decimal> amounts, IReadOnlySet<Guid>? show = null)
    {
        ReportSection Section(string title, AccountType type) => BuildSection(title, chart, amounts, [type], show);
        return new ProfitAndLoss(
            from,
            to,
            basis,
            Section("Income", AccountType.Income),
            Section("Cost of goods sold", AccountType.CostOfGoodsSold),
            Section("Expenses", AccountType.Expense),
            Section("Other income", AccountType.OtherIncome),
            Section("Other expenses", AccountType.OtherExpense));
    }

    /// <summary>
    /// No closing entries are posted at year end, so equity shows net income before the current fiscal year
    /// ("retained earnings") and net income since it started, both computed.
    /// </summary>
    public async Task<BalanceSheet> BalanceSheetAsync(DateOnly asOf, ReportBasis? basis = null, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var reportBasis = basis ?? company.DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        return BuildBalanceSheet(asOf, reportBasis, company.FiscalYearStart(asOf), chart, await ledger.LinesAsync(reportBasis, null, asOf, ct));
    }

    /// <param name="lines">Every line up to <paramref name="asOf"/>.</param>
    /// <param name="show">Accounts to list even with no balance, so several columns get the same rows.</param>
    internal static BalanceSheet BuildBalanceSheet(
        DateOnly asOf, ReportBasis basis, DateOnly fiscalStart, ChartOfAccounts chart, IReadOnlyList<LedgerLine> lines, IReadOnlySet<Guid>? show = null)
    {
        var amounts = Sum(lines);
        var types = chart.All.ToDictionary(a => a.Id, a => a.Type);
        decimal NetIncome(IEnumerable<LedgerLine> ls) =>
            -ls.Where(l => types.TryGetValue(l.AccountId, out var t) && !t.IsBalanceSheet()).Sum(l => l.Amount);
        var retained = NetIncome(lines.Where(l => l.Date < fiscalStart));
        var thisYear = NetIncome(lines.Where(l => l.Date >= fiscalStart));

        var equity = BuildSection("Equity", chart, amounts, [AccountType.Equity], show);
        var equityRows = equity.Rows.ToList();

        // With a top-level "Retained Earnings" account (as in the starter charts), prior years' income shows
        // on it, combined with anything posted to it directly; otherwise as a computed line.
        var retainedAccount = chart.Ordered(includeInactive: false).Select(x => x.Account).FirstOrDefault(a =>
            a.Type == AccountType.Equity && a.ParentId is null && string.Equals(a.Name, "Retained Earnings", StringComparison.OrdinalIgnoreCase)
            && !chart.ChildrenOf(a.Id).Any());
        var retainedRow = retainedAccount is null ? -1 : equityRows.FindIndex(r => r.AccountId == retainedAccount.Id);
        if (retainedRow >= 0)
            equityRows[retainedRow] = equityRows[retainedRow] with { Amount = (equityRows[retainedRow].Amount ?? 0m) + retained };
        else if (retainedAccount is not null)
            equityRows.Add(new ReportRow(retainedAccount.Id, retainedAccount.Name, 1, retained, RowKind.Account));
        else
            equityRows.Add(new ReportRow(null, "Retained earnings", 1, retained, RowKind.Computed));
        equityRows.Add(new ReportRow(null, "Net income", 1, thisYear, RowKind.Computed));

        return new BalanceSheet(
            asOf,
            basis,
            BuildSection("Current assets", chart, amounts, [AccountType.Bank, AccountType.AccountsReceivable, AccountType.OtherCurrentAsset], show),
            BuildSection("Fixed assets", chart, amounts, [AccountType.FixedAsset], show),
            BuildSection("Other assets", chart, amounts, [AccountType.OtherAsset], show),
            BuildSection("Current liabilities", chart, amounts, [AccountType.AccountsPayable, AccountType.CreditCard, AccountType.OtherCurrentLiability], show),
            BuildSection("Long-term liabilities", chart, amounts, [AccountType.LongTermLiability], show),
            new ReportSection("Equity", equityRows, equity.Total + retained + thisYear));
    }

    /// <param name="accountId">Only this account (and its subaccounts); null for every account with activity.</param>
    /// <param name="filter">Only lines of this class, location or tag (opening balances too).</param>
    public async Task<GeneralLedger> GeneralLedgerAsync(
        DateOnly? from, DateOnly to, ReportBasis? basis = null, Guid? accountId = null, SegmentFilter? filter = null, CancellationToken ct = default)
    {
        var reportBasis = basis ?? (await companies.GetRequiredCompanyAsync(ct)).DefaultReportBasis;
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(reportBasis, null, to, filter, ct);
        var include = accountId is { } id ? Subtree(chart, id).ToHashSet() : null;

        var result = new List<GeneralLedgerAccount>();
        var byAccount = lines.ToLookup(l => l.AccountId);
        foreach (var (account, _) in chart.Ordered())
        {
            if (include is not null && !include.Contains(account.Id))
                continue;

            var sign = account.Type.IsDebitNormal() ? 1m : -1m;
            var accountLines = byAccount[account.Id].ToList();
            var opening = from is { } f ? sign * accountLines.Where(l => l.Date < f).Sum(l => l.Amount) : 0m;
            var balance = opening;
            var period = new List<GeneralLedgerLine>();
            foreach (var line in accountLines.Where(l => from is null || l.Date >= from))
            {
                balance += sign * line.Amount;
                period.Add(new GeneralLedgerLine(line, line.Amount, balance));
            }

            if (period.Count > 0 || opening != 0m)
                result.Add(new GeneralLedgerAccount(account, chart.FullName(account.Id), opening, period));
        }

        return new GeneralLedger(from, to, reportBasis, result);
    }

    internal static Dictionary<Guid, decimal> Sum(IEnumerable<LedgerLine> lines) =>
        lines.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));

    /// <summary>
    /// Rows for the accounts of <paramref name="types"/>, in chart order: an account appears when it or a
    /// subaccount has an amount (or is in <paramref name="show"/>); a parent with active subaccounts also gets a "Total ..." row.
    /// Amounts are in the section's natural sign.
    /// </summary>
    private static ReportSection BuildSection(
        string title, ChartOfAccounts chart, IReadOnlyDictionary<Guid, decimal> amounts, AccountType[] types, IReadOnlySet<Guid>? show)
    {
        var rows = new List<ReportRow>();

        decimal Natural(Account a) => (a.Type.IsDebitNormal() ? 1m : -1m) * amounts.GetValueOrDefault(a.Id);
        decimal SubtreeTotal(Account a) => Natural(a) + chart.ChildrenOf(a.Id).Sum(SubtreeTotal);
        bool HasActivity(Account a) =>
            amounts.GetValueOrDefault(a.Id) != 0m || show?.Contains(a.Id) == true || chart.ChildrenOf(a.Id).Any(HasActivity);

        void Add(Account account, int depth)
        {
            if (!HasActivity(account))
                return;
            var children = chart.Ordered().Where(x => x.Account.ParentId == account.Id).Select(x => x.Account).Where(HasActivity).ToList();
            var own = Natural(account);
            if (children.Count == 0)
            {
                rows.Add(new ReportRow(account.Id, account.Name, depth, own, RowKind.Account));
                return;
            }

            rows.Add(new ReportRow(account.Id, account.Name, depth, own == 0m ? null : own, RowKind.Account));
            foreach (var child in children)
                Add(child, depth + 1);
            rows.Add(new ReportRow(account.Id, $"Total {account.Name}", depth, SubtreeTotal(account), RowKind.AccountTotal));
        }

        var roots = chart.Ordered().Where(x => x.Depth == 1 && types.Contains(x.Account.Type)).Select(x => x.Account).ToList();
        foreach (var root in roots)
            Add(root, 1);

        return new ReportSection(title, rows, roots.Sum(SubtreeTotal));
    }

    internal static IEnumerable<Guid> Subtree(ChartOfAccounts chart, Guid id) =>
        chart.ChildrenOf(id).SelectMany(c => Subtree(chart, c.Id)).Prepend(id);
}
