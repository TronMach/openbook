using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Reporting;

/// <summary>
/// Each partner's (or shareholder's) share of the year for their Schedule K-1 (C330): ordinary business income from
/// the tax summary's page 1 (box 1, meals at half) and each Schedule K line, split by the active partners' profit shares
/// (Accountant → Partners). Equal splits only: special allocations, guaranteed payments to one partner and the capital
/// account analysis are the preparer's.
/// </summary>
public sealed class K1Worksheet(StatementReports statements, PartnerService partners, ICompanyStore companies)
{
    public async Task<ReportTable> TableAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var form = TaxLines.FormFor(company.BusinessType);
        const string Title = "Schedule K-1 worksheet";
        if (form is not (TaxForm.Form1065 or TaxForm.Form1120S))
            return new ReportTable(Title, [company.Name], [new("")],
                [new(["K-1s are for partnerships, multi-member LLCs and S corporations. Set the business type under Settings → Company details."])]);

        var owners = (await partners.ListAsync(ct)).Where(p => p.IsActive).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (owners.Count == 0)
            return new ReportTable(Title, [company.Name], [new("")],
                [new(["Add the partners (or shareholders) and their shares under Accountant → Partners and their capital first."])]);

        var summary = await statements.TaxSummaryAsync(from, to, Domain.Ledger.ReportBasis.Accrual, ct);
        var meals = summary.Lines.Where(l => l.Line!.HalfDeductible).Sum(l => l.Total);
        var ordinary = summary.Income - summary.Deductions + decimal.Round(meals / 2m, 2, MidpointRounding.AwayFromZero);
        var items = new List<(string Label, decimal Amount)> { ("Box 1: Ordinary business income (loss)", ordinary) };
        // Income items are kept as shown (credit-positive); deductions as positive amounts, as the K-1 lists them.
        items.AddRange(summary.Lines.Where(l => l.Line!.SeparatelyStated && l.Total != 0m).Select(l => ($"Box {l.Line!.Line}: {l.Line.Name}", l.Total)));

        var weights = owners.Select(p => p.SharePercent).ToList();
        var rows = items.Select(item =>
        {
            var split = Shares.ByWeight(item.Amount, weights);
            return new ReportTableRow([item.Label, item.Amount, .. split.Select(s => (ReportCell)s)]);
        }).ToList();
        var total = weights.Sum();
        if (total != 100m)
            rows.Add(new ReportTableRow([$"The active partners' shares add up to {total:0.####}%, not 100%: check them under Accountant → Partners.", null, .. owners.Select(_ => ReportCell.Empty)]));
        var owner = form == TaxForm.Form1065 ? "partner" : "shareholder";
        return new ReportTable(Title,
            [company.Name, $"{TaxLines.Describe(form)} · {Comparisons.Describe(from, to)} · accrual basis"],
            [new("Item"), new("Total", ColumnFormat.Money), .. owners.Select(p => new ReportColumn($"{p.Name} ({p.SharePercent:0.####}%)", ColumnFormat.Money))],
            rows, Domain.Ledger.ReportBasis.Accrual,
            $"Each {owner}'s share of page 1's ordinary income and of the Schedule K lines your accounts are mapped to (Reports → Tax lines), by profit share. A worksheet: special allocations, guaranteed payments and the capital account analysis are the preparer's.");
    }
}
