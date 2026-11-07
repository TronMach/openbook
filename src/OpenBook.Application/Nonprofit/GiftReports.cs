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
    /// <summary>
    /// Donors with no gift in a fiscal year (the one starting in <paramref name="year"/>; null = this one) who gave before it.
    /// With a fiscal year starting in January these are calendar years.
    /// </summary>
    public async Task<ReportTable> LapsedDonorsAsync(int? year, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var startMonth = company.FiscalYearStartMonth;
        int Fiscal(DateOnly date) => date.Month >= startMonth ? date.Year : date.Year - 1;
        string Label(int y) => startMonth == 1 ? y.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : $"{new DateOnly(y, startMonth, 1):MMM yyyy}–{new DateOnly(y, startMonth, 1).AddYears(1).AddDays(-1):MMM yyyy}";
        var fy = year ?? Fiscal(CompanyTime.Today);
        var names = await NamesAsync(ct);
        var posted = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && Fiscal(d.Date) <= fy).ToList();
        var byDonor = posted.GroupBy(d => d.DonorId).Where(g => g.All(d => Fiscal(d.Date) < fy)).ToList();
        var rows = new List<ReportTableRow>();
        foreach (var (title, lastYear) in new[] { ($"Gave in {Label(fy - 1)}, not yet in {Label(fy)}", true), ($"Gave before {Label(fy - 1)}, not since", false) })
        {
            var part = byDonor.Where(g => g.Any(d => Fiscal(d.Date) == fy - 1) == lastYear)
                .OrderByDescending(g => g.Max(d => d.Date)).ToList();
            rows.Add(Row.Section(title, 5));
            foreach (var donor in part)
            {
                rows.Add(new ReportTableRow([new ReportCell(names.GetValueOrDefault(donor.Key, "?"), new ReportLink("customer", donor.Key)),
                    donor.Max(d => d.Date).ToString("d", System.Globalization.CultureInfo.CurrentCulture),
                    donor.Where(d => Fiscal(d.Date) == fy - 1).Sum(d => d.Amount), donor.Sum(d => d.Amount), (decimal)donor.Count()], 1));
            }

            rows.Add(new ReportTableRow([$"{part.Count} donor(s)", null, part.Sum(g => g.Where(d => Fiscal(d.Date) == fy - 1).Sum(d => d.Amount)),
                part.Sum(g => g.Sum(d => d.Amount)), null], 0, ReportRowStyle.Subtotal));
        }

        return new ReportTable(
            "Lapsed donors",
            [company.Name, startMonth == 1 ? $"For {fy}" : $"For the fiscal year {Label(fy)}"],
            [new("Donor"), new("Latest gift"), new($"Given in {Label(fy - 1)}", ColumnFormat.Money), new("Lifetime giving", ColumnFormat.Money), new("Gifts", ColumnFormat.Number)],
            rows,
            Note: "Donors with no gift dated in the fiscal year, by how recently they gave: the people to ask first. A year is the fiscal year starting in it.");
    }

    /// <summary>Gifts in a period by the campaign they answered: how many, from how many donors, the total and the average; gifts with none last.</summary>
    public async Task<ReportTable> GivingByCampaignAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var gifts = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Date >= from && d.Date <= to).ToList();
        var rows = new List<ReportTableRow>();
        foreach (var campaign in gifts.GroupBy(d => d.Campaign ?? "", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key.Length == 0).ThenByDescending(g => g.Sum(d => d.Amount)))
        {
            var total = campaign.Sum(d => d.Amount);
            rows.Add(new ReportTableRow([campaign.Key.Length == 0 ? "No campaign" : campaign.First().Campaign, (decimal)campaign.Count(),
                (decimal)campaign.Select(d => d.DonorId).Distinct().Count(), total, decimal.Round(total / campaign.Count(), 2, MidpointRounding.AwayFromZero),
                campaign.Max(d => d.Date).ToString("d", System.Globalization.CultureInfo.CurrentCulture)]));
        }

        var all = gifts.Sum(d => d.Amount);
        rows.Add(new ReportTableRow(["Total", (decimal)gifts.Count, (decimal)gifts.Select(d => d.DonorId).Distinct().Count(), all,
            gifts.Count == 0 ? 0m : decimal.Round(all / gifts.Count, 2, MidpointRounding.AwayFromZero), null], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Giving by campaign",
            [company.Name, Comparisons.Describe(from, to)],
            [new("Campaign"), new("Gifts", ColumnFormat.Number), new("Donors", ColumnFormat.Number), new("Total", ColumnFormat.Money),
                new("Average gift", ColumnFormat.Money), new("Latest gift")],
            rows,
            Note: "Posted gifts dated in the period, by the campaign or appeal on each. A donor who answered two campaigns counts in each.");
    }

    /// <summary>
    /// Form 990 Schedule B, the general rule: each donor whose gifts in the calendar year came to $5,000 or more, with
    /// their address and the money and in-kind parts. The schedule itself, and which rule applies, are for the preparer.
    /// </summary>
    public async Task<ReportTable> ScheduleBAsync(int year, CancellationToken ct = default)
    {
        const decimal threshold = 5_000m;
        var company = await companies.GetRequiredCompanyAsync(ct);
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var rows = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Date.Year == year)
            .GroupBy(d => d.DonorId)
            .Select(g => (Donor: customers.GetValueOrDefault(g.Key), Cash: g.Where(d => d.Kind == GiftKind.Cash).Sum(d => d.Amount), InKind: g.Where(d => d.Kind == GiftKind.InKind).Sum(d => d.Amount), Id: g.Key))
            .Where(x => x.Cash + x.InKind >= threshold)
            .OrderByDescending(x => x.Cash + x.InKind)
            .Select(x => new ReportTableRow([new ReportCell(x.Donor?.DisplayName ?? "?", new ReportLink("customer", x.Id)), x.Donor?.BillingAddress?.Replace("\n", ", ", StringComparison.Ordinal), x.Cash, x.InKind, x.Cash + x.InKind]))
            .ToList();
        rows.Add(new ReportTableRow([$"{rows.Count} contributor(s)", null, rows.Sum(r => (decimal)r.Cells[2].Value!), rows.Sum(r => (decimal)r.Cells[3].Value!), rows.Sum(r => (decimal)r.Cells[4].Value!)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Schedule B contributors",
            [company.Name, $"Gifts dated in {year} · $5,000 or more"],
            [new("Contributor"), new("Address"), new("Money", ColumnFormat.Money), new("In kind", ColumnFormat.Money), new("Total", ColumnFormat.Money)],
            rows,
            Note: "The general rule of Form 990 Schedule B. Some organizations use the 2% special rule instead, and the schedule's names aren't made public for most: check with your preparer.");
    }

    /// <summary>In-kind gifts in a period, for the auditor and Form 990 Schedule M: donor, what was given, its recorded value and where it went.</summary>
    public async Task<ReportTable> InKindGiftsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var names = await NamesAsync(ct);
        var gifts = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Kind == GiftKind.InKind && d.Date >= from && d.Date <= to)
            .OrderBy(d => d.Date).ThenBy(d => d.Number).ToList();
        var rows = gifts.Select(d => new ReportTableRow([new ReportCell($"{d.Number} · {names.GetValueOrDefault(d.DonorId, "?")}", new ReportLink("donation", d.Id)),
            d.Date.ToString("d", System.Globalization.CultureInfo.CurrentCulture), d.Description, d.Amount])).ToList();
        rows.Add(new ReportTableRow([$"{gifts.Count} gift(s)", null, null, gifts.Sum(d => d.Amount)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "In-kind gifts",
            [company.Name, Comparisons.Describe(from, to)],
            [new("Gift"), new("Date"), new("What was given"), new("Recorded value", ColumnFormat.Money)],
            rows,
            Note: "Goods and services given instead of money, at the fair value recorded. Donated services count only when they meet the rules for recognizing them.");
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
            var standing = p.Pledge.Amount - cancelled;
            promised += standing;
            paid += paidBy;
            rows.Add(new ReportTableRow([new ReportCell($"{p.Pledge.Number} · {p.Donor}", new ReportLink("pledge", p.Pledge.Id)),
                p.Pledge.Date.ToString("d", System.Globalization.CultureInfo.CurrentCulture),
                p.Pledge.DueDate?.ToString("d", System.Globalization.CultureInfo.CurrentCulture), standing, paidBy, standing - paidBy]));
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
