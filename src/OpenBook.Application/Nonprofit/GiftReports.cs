using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;

namespace OpenBook.Application.Nonprofit;

/// <summary>Reports on donors and gifts, from the gifts and pledges themselves (posted only).</summary>
public sealed class GiftReports(INonprofitStore store, ISalesStore sales, GiftService gifts, ICompanyStore companies)
{
    /// <summary>Each donor's gifts in a period: count, total, largest and latest; with detail, each gift under its donor.</summary>
    public async Task<ReportTable> GivingByDonorAsync(DateOnly from, DateOnly to, bool detail, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var names = await NamesAsync(ct);
        var posted = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Date >= from && d.Date <= to).ToList();
        var rows = new List<ReportTableRow>();
        foreach (var donor in posted.GroupBy(d => d.DonorId).OrderBy(g => names.GetValueOrDefault(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var name = names.GetValueOrDefault(donor.Key, "?");
            rows.Add(new ReportTableRow([new ReportCell(name, new ReportLink("customer", donor.Key)), (decimal)donor.Count(), donor.Sum(d => d.Amount),
                donor.Max(d => d.Amount), donor.Max(d => d.Date).ToString("d", System.Globalization.CultureInfo.CurrentCulture)], 0, detail ? ReportRowStyle.Subtotal : ReportRowStyle.Normal));
            if (detail)
            {
                foreach (var gift in donor.OrderBy(d => d.Date).ThenBy(d => d.Number))
                    rows.Add(new ReportTableRow([new ReportCell($"{gift.Number} · {(gift.Kind == GiftKind.InKind ? "In kind" : gift.PledgeId is null ? "Gift" : "Pledge payment")}", new ReportLink("donation", gift.Id)),
                        null, gift.Amount, null, gift.Date.ToString("d", System.Globalization.CultureInfo.CurrentCulture)], 1));
            }
        }

        rows.Add(new ReportTableRow(["Total", (decimal)posted.Count, posted.Sum(d => d.Amount), null, null], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Giving by donor",
            [company.Name, Comparisons.Describe(from, to)],
            [new("Donor"), new("Gifts", ColumnFormat.Number), new("Total", ColumnFormat.Money), new("Largest", ColumnFormat.Money), new("Latest")],
            rows,
            Note: "Gifts received (money and in kind, including pledge payments); pledges themselves aren't counted until paid.");
    }

    /// <summary>
    /// Donors who haven't given yet this year: "last year but not this year" (LYBUNT) and "some earlier year but not
    /// last year or this year" (SYBUNT), with their latest gift and lifetime giving.
    /// </summary>
    public async Task<ReportTable> LapsedDonorsAsync(int year, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var names = await NamesAsync(ct);
        var posted = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Date.Year <= year).ToList();
        var byDonor = posted.GroupBy(d => d.DonorId).Where(g => g.All(d => d.Date.Year < year)).ToList();
        var rows = new List<ReportTableRow>();
        foreach (var (title, lastYear) in new[] { ($"Gave in {year - 1}, not yet in {year}", true), ($"Gave before {year - 1}, not since", false) })
        {
            var part = byDonor.Where(g => g.Any(d => d.Date.Year == year - 1) == lastYear)
                .OrderByDescending(g => g.Max(d => d.Date)).ToList();
            rows.Add(Row.Section(title, 5));
            foreach (var donor in part)
            {
                rows.Add(new ReportTableRow([new ReportCell(names.GetValueOrDefault(donor.Key, "?"), new ReportLink("customer", donor.Key)),
                    donor.Max(d => d.Date).ToString("d", System.Globalization.CultureInfo.CurrentCulture),
                    donor.Where(d => d.Date.Year == year - 1).Sum(d => d.Amount), donor.Sum(d => d.Amount), (decimal)donor.Count()], 1));
            }

            rows.Add(new ReportTableRow([$"{part.Count} donor(s)", null, part.Sum(g => g.Where(d => d.Date.Year == year - 1).Sum(d => d.Amount)),
                part.Sum(g => g.Sum(d => d.Amount)), null], 0, ReportRowStyle.Subtotal));
        }

        return new ReportTable(
            "Lapsed donors",
            [company.Name, $"For {year}"],
            [new("Donor"), new("Latest gift"), new($"Given in {year - 1}", ColumnFormat.Money), new("Lifetime giving", ColumnFormat.Money), new("Gifts", ColumnFormat.Number)],
            rows,
            Note: "Donors with no gift dated in the year, by how recently they gave: the people to ask first.");
    }

    /// <summary>Pledges as of a date: promised, paid by then and what's left, oldest first.</summary>
    public async Task<ReportTable> PledgesAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var donations = await store.ListDonationsAsync(ct);
        var rows = new List<ReportTableRow>();
        decimal promised = 0m, paid = 0m;
        foreach (var p in (await gifts.ListPledgesAsync(null, ct)).Where(p => p.Pledge.Status == DocumentStatus.Posted && p.Pledge.Date <= asOf)
                     .OrderBy(p => p.Pledge.Date).ThenBy(p => p.Pledge.Number))
        {
            var paidBy = donations.Where(d => d.PledgeId == p.Pledge.Id && d.Status == DocumentStatus.Posted && d.Date <= asOf).Sum(d => d.Amount);
            var cancelled = p.Pledge.CancelledOn <= asOf ? p.Pledge.CancelledAmount : 0m;
            if (paidBy + cancelled >= p.Pledge.Amount)
                continue;
            promised += p.Pledge.Amount;
            paid += paidBy;
            rows.Add(new ReportTableRow([new ReportCell($"{p.Pledge.Number} · {p.Donor}", new ReportLink("pledge", p.Pledge.Id)),
                p.Pledge.Date.ToString("d", System.Globalization.CultureInfo.CurrentCulture),
                p.Pledge.DueDate?.ToString("d", System.Globalization.CultureInfo.CurrentCulture), p.Pledge.Amount, paidBy, p.Pledge.Amount - paidBy]));
        }

        rows.Add(new ReportTableRow(["Total", null, null, promised, paid, promised - paid], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Open pledges",
            [company.Name, $"As of {asOf.ToString("d", System.Globalization.CultureInfo.CurrentCulture)}"],
            [new("Pledge"), new("Date"), new("Due"), new("Promised", ColumnFormat.Money), new("Paid", ColumnFormat.Money), new("Still to come", ColumnFormat.Money)],
            rows,
            Note: "Pledges not fully paid by the date. Their balance is in Pledges Receivable on accrual-basis reports.");
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(CancellationToken ct) =>
        (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
}
