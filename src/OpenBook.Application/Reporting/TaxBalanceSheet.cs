using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Reporting;

/// <summary>
/// The return's balance sheet from the books (C347, C074): Schedule L of Forms 1065, 1120-S and 1120, or Part X of
/// Form 990, at the start and end of the tax year. Each balance sheet account goes on the line it's mapped to (Reports →
/// Tax lines; a subaccount takes its parent's), else on the usual line for its type and name. All profit and loss to date
/// is the owners' equity: partners' capital, retained earnings, or net assets with or without donor restrictions.
/// A worksheet for the preparer, on the accrual basis.
/// </summary>
public sealed class TaxBalanceSheet(ReportLedger ledger, AccountService accounts, ICompanyStore companies)
{
    public async Task<ReportTable> TableAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var form = TaxLines.FormFor(company.BusinessType);
        var title = form == TaxForm.Form990 ? "Form 990 Part X: balance sheet" : $"{TaxLines.Describe(form)} Schedule L: balance sheets per books";
        if (form == TaxForm.ScheduleC)
            return new ReportTable("Tax balance sheet", [company.Name], [new("")],
                [new(["Schedule C has no balance sheet. Set the business type (Settings → Company details) to a partnership, a corporation or a nonprofit to see Schedule L or Part X."])],
                ReportBasis.Accrual);

        var chart = await accounts.GetChartAsync(ct);
        var lines = TaxLines.BalanceSheetFor(form);
        var start = FinancialReports.Sum(await ledger.LinesAsync(ReportBasis.Accrual, null, from.AddDays(-1), ct));
        var end = FinancialReports.Sum(await ledger.LinesAsync(ReportBasis.Accrual, null, to, ct));

        // Each line's accounts with their balances at both dates (debit positive), in print order.
        var byLine = lines.ToDictionary(l => l.Key, _ => new List<(string Name, decimal Start, decimal End)>());
        foreach (var (account, _) in chart.Ordered())
        {
            var (s, e) = (start.GetValueOrDefault(account.Id), end.GetValueOrDefault(account.Id));
            if (s == 0m && e == 0m)
                continue;
            var line = LineFor(account, chart, form, lines);
            byLine[line.Key].Add((account.Type.IsBalanceSheet() ? chart.FullName(account.Id) : "Profit and loss to date", s, e));
        }

        var rows = new List<ReportTableRow>();
        decimal assetsStart = 0m, assetsEnd = 0m, othersStart = 0m, othersEnd = 0m;
        foreach (var credit in new[] { false, true })
        {
            rows.Add(new ReportTableRow([credit ? (form == TaxForm.Form990 ? "Liabilities and net assets" : "Liabilities and capital") : "Assets", null, null, null], 0, ReportRowStyle.Section));
            foreach (var line in lines.Where(l => l.IsIncome == credit && byLine[l.Key].Count > 0))
            {
                var items = byLine[line.Key];
                // Assets read debit-positive, the rest credit-positive; a "Less ..." line comes out negative on its own.
                decimal Shown(decimal amount) => credit ? -amount : amount;
                var (lineStart, lineEnd) = (items.Sum(i => Shown(i.Start)), items.Sum(i => Shown(i.End)));
                rows.Add(new ReportTableRow([line.Line, line.Name, lineStart, lineEnd], 0, ReportRowStyle.Subtotal));
                rows.AddRange(items.Select(i => new ReportTableRow([null, i.Name, Shown(i.Start), Shown(i.End)], 1)));
                if (credit)
                    (othersStart, othersEnd) = (othersStart + lineStart, othersEnd + lineEnd);
                else
                    (assetsStart, assetsEnd) = (assetsStart + lineStart, assetsEnd + lineEnd);
            }

            rows.Add(credit
                ? new ReportTableRow([null, form == TaxForm.Form990 ? "Total liabilities and net assets" : "Total liabilities and capital", othersStart, othersEnd], 0, ReportRowStyle.Total)
                : new ReportTableRow([null, "Total assets", assetsStart, assetsEnd], 0, ReportRowStyle.Total));
        }

        if (assetsStart != othersStart || assetsEnd != othersEnd)
            rows.Add(new ReportTableRow([null, "Doesn't balance: an asset account is on a liability or equity line, or the other way round", assetsStart - othersStart, assetsEnd - othersEnd]));
        return new ReportTable(title,
            [company.Name, $"Beginning of tax year {from:MMM d, yyyy}, end of tax year {to:MMM d, yyyy}"],
            [new("Line"), new("Description"), new("Beginning of year", ColumnFormat.Money), new("End of year", ColumnFormat.Money)],
            rows, ReportBasis.Accrual,
            "From the books on the accrual basis. Accounts go on the line chosen under Reports, Tax lines, else the usual line for their type and name. A worksheet for the return, not the return.");
    }

    /// <summary>The account's mapped balance sheet line (its own, else its nearest parent's), else the usual one.</summary>
    public static TaxLine LineFor(Account account, ChartOfAccounts chart, TaxForm form, IReadOnlyList<TaxLine> lines)
    {
        for (var at = account; at is not null; at = at.ParentId is { } p ? chart.Find(p) : null)
        {
            if (TaxLines.Find(at.TaxLineKey) is { BalanceSheet: true } mapped && mapped.Form == form)
                return mapped;
        }

        TaxLine Named(string name) => lines.First(l => l.Name == name);
        var n = account.Name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => n.Contains(w, StringComparison.Ordinal));
        var nonprofit = form == TaxForm.Form990;
        if (!account.Type.IsBalanceSheet())
            return nonprofit
                ? Named(account.DonorRestricted ? "Net assets with donor restrictions" : "Net assets without donor restrictions")
                : Earnings(form, lines);

        return account.Type switch
        {
            AccountType.Bank => nonprofit && Has("saving", "money market", "investment", "certificate") ? Named("Savings and temporary cash investments")
                : Named(nonprofit ? "Cash, non-interest-bearing" : "Cash"),
            AccountType.AccountsReceivable => Named(nonprofit ? "Accounts receivable, net" : "Trade notes and accounts receivable"),
            AccountType.OtherCurrentAsset when Has("undeposited") => Named(nonprofit ? "Cash, non-interest-bearing" : "Cash"),
            AccountType.OtherCurrentAsset when Has("allowance", "doubtful") => Named(nonprofit ? "Accounts receivable, net" : "Less allowance for bad debts"),
            AccountType.OtherCurrentAsset when nonprofit && Has("pledge", "grant") => Named("Pledges and grants receivable, net"),
            AccountType.OtherCurrentAsset when Has("inventor") => Named(nonprofit ? "Inventories for sale or use" : "Inventories"),
            AccountType.OtherCurrentAsset when nonprofit && Has("prepaid", "deferred") => Named("Prepaid expenses and deferred charges"),
            AccountType.OtherCurrentAsset => Named(nonprofit ? "Other assets" : "Other current assets"),
            AccountType.FixedAsset when Has("amortiz") => Named(nonprofit ? "Intangible assets" : "Less accumulated amortization"),
            AccountType.FixedAsset when Has("depreciation", "accumulated") => Named(nonprofit ? "Less accumulated depreciation" : "Less accumulated depreciation"),
            AccountType.FixedAsset when !nonprofit && (n == "land" || Has("land ")) => Named("Land (net of any amortization)"),
            AccountType.FixedAsset when Has("intangible", "goodwill") => Named(nonprofit ? "Intangible assets" : "Intangible assets (amortizable only)"),
            AccountType.FixedAsset => Named(nonprofit ? "Land, buildings and equipment: cost or other basis" : "Buildings and other depreciable assets"),
            AccountType.OtherAsset when Has("intangible", "goodwill") => Named(nonprofit ? "Intangible assets" : "Intangible assets (amortizable only)"),
            AccountType.OtherAsset when Has("invest", "securit") => Named(nonprofit ? "Investments: other securities" : "Other investments"),
            AccountType.OtherAsset => Named("Other assets"),
            AccountType.AccountsPayable => Named(nonprofit ? "Accounts payable and accrued expenses" : "Accounts payable"),
            AccountType.CreditCard => Named(nonprofit ? "Accounts payable and accrued expenses" : "Other current liabilities"),
            AccountType.OtherCurrentLiability when nonprofit && Has("deferred", "unearned", "advance") => Named("Deferred revenue"),
            AccountType.OtherCurrentLiability when nonprofit && Has("grants payable") => Named("Grants payable"),
            AccountType.OtherCurrentLiability => Named(nonprofit ? "Accounts payable and accrued expenses" : "Other current liabilities"),
            AccountType.LongTermLiability when nonprofit => Named(Has("mortgage", "secured") ? "Secured mortgages and notes payable to unrelated third parties"
                : "Unsecured notes and loans payable to unrelated third parties"),
            AccountType.LongTermLiability when Has("partner") && form == TaxForm.Form1065 => Named("Loans from partners (or persons related to partners)"),
            AccountType.LongTermLiability when Has("shareholder", "officer") && form != TaxForm.Form1065 => Named("Loans from shareholders"),
            AccountType.LongTermLiability => Named("Mortgages, notes, bonds payable in 1 year or more"),
            AccountType.Equity when nonprofit => Named(account.DonorRestricted ? "Net assets with donor restrictions" : "Net assets without donor restrictions"),
            AccountType.Equity when form == TaxForm.Form1065 => Named("Partners' capital accounts"),
            AccountType.Equity when Has("treasury") => Named("Less cost of treasury stock"),
            AccountType.Equity when Has("paid-in", "paid in", "apic") => Named("Additional paid-in capital"),
            AccountType.Equity when Has("stock") => Named(form == TaxForm.Form1120 ? "Capital stock: common stock" : "Capital stock"),
            _ => Earnings(form, lines),
        };
    }

    private static TaxLine Earnings(TaxForm form, IReadOnlyList<TaxLine> lines) => lines.First(l => l.Name == form switch
    {
        TaxForm.Form1065 => "Partners' capital accounts",
        TaxForm.Form1120 => "Retained earnings, unappropriated",
        _ => "Retained earnings",
    });
}
