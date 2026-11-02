using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Assets;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Reporting;

namespace OpenBook.Application.Reporting;

/// <summary>One week of the cash-flow forecast. Money in is positive, money out negative.</summary>
public sealed record ForecastWeek(DateOnly Start, DateOnly End, decimal Opening, decimal Collections, decimal Bills, decimal Loans, decimal Other)
{
    public decimal Closing => Opening + Collections + Bills + Loans + Other;
}

public sealed record CashForecast(DateOnly AsOf, decimal Opening, ForecastScenario? Scenario, IReadOnlyList<ForecastWeek> Weeks)
{
    public decimal LowestClosing => Weeks.Count == 0 ? Opening : Weeks.Min(w => w.Closing);
}

/// <summary>
/// Cash-flow forecast by week: today's bank balances, plus open invoices collected on their due dates, less open
/// bills paid on theirs (overdue ones in the first week) and scheduled loan payments, adjusted by a scenario.
/// </summary>
public sealed class ForecastService(
    IReportingStore store, ReportLedger ledger, AccountService accounts, ReceivablesService receivables, PayablesService payables,
    LoanService loans, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string EntityType = "forecast_scenario";
    public const int MaxWeeks = 52;

    public async Task<IReadOnlyList<ForecastScenario>> ListScenariosAsync(CancellationToken ct = default) =>
        (await store.ListScenariosAsync(ct)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<ForecastScenario?> GetScenarioAsync(Guid id, CancellationToken ct = default) => store.GetScenarioAsync(id, ct);

    public async Task<ForecastScenario> CreateScenarioAsync(ForecastScenarioDetails details, CancellationToken ct = default)
    {
        var scenario = ForecastScenario.Create(details);
        store.Add(scenario);
        store.Add(Audit(AuditAction.Created, scenario, null));
        await store.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task<ForecastScenario> UpdateScenarioAsync(Guid id, ForecastScenarioDetails details, CancellationToken ct = default)
    {
        var scenario = await store.GetScenarioAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(scenario);
        scenario.Update(details);
        store.Add(Audit(AuditAction.Updated, scenario, before));
        await store.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task<ForecastScenario> AddItemAsync(Guid id, ScenarioItemDetails details, CancellationToken ct = default)
    {
        var scenario = await store.GetScenarioAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(scenario);
        scenario.AddItem(details);
        store.Add(Audit(AuditAction.Updated, scenario, before));
        await store.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task<ForecastScenario> RemoveItemAsync(Guid id, Guid itemId, CancellationToken ct = default)
    {
        var scenario = await store.GetScenarioAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(scenario);
        scenario.RemoveItem(itemId);
        store.Add(Audit(AuditAction.Updated, scenario, before));
        await store.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task DeleteScenarioAsync(Guid id, CancellationToken ct = default)
    {
        var scenario = await store.GetScenarioAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        store.Remove(scenario);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, scenario.Id, Snapshot(scenario), null));
        await store.SaveChangesAsync(ct);
    }

    public async Task<CashForecast> ForecastAsync(DateOnly asOf, int weeks, Guid? scenarioId = null, CancellationToken ct = default)
    {
        weeks = Math.Clamp(weeks, 1, MaxWeeks);
        var scenario = scenarioId is { } id ? await store.GetScenarioAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id) : null;
        var chart = await accounts.GetChartAsync(ct);
        var opening = (await ledger.TotalsAsync(ReportBasis.Accrual, asOf, asOf, ct))
            .Where(l => chart.Find(l.AccountId)?.Type == AccountType.Bank).Sum(l => l.Amount);
        var end = asOf.AddDays(7 * weeks);
        int WeekOf(DateOnly date) => date <= asOf ? 0 : Math.Min(weeks - 1, (date.DayNumber - asOf.DayNumber - 1) / 7);

        var collections = new decimal[weeks];
        var bills = new decimal[weeks];
        var loanPayments = new decimal[weeks];
        var other = new decimal[weeks];

        foreach (var invoice in (await receivables.AgingAsync(asOf, ct)).Customers.SelectMany(c => c.Invoices))
        {
            var expected = invoice.Invoice.DueDate.AddDays(scenario?.CollectionDelayDays ?? 0);
            if (expected <= end)
                collections[WeekOf(expected)] += invoice.Balance;
        }

        foreach (var bill in (await payables.AgingAsync(asOf, ct)).Vendors.SelectMany(v => v.Bills))
        {
            var expected = bill.Bill.DueDate.AddDays(scenario?.PaymentDelayDays ?? 0);
            if (expected <= end)
                bills[WeekOf(expected)] -= bill.Balance;
        }

        foreach (var loan in await loans.ListAsync(ct))
        {
            foreach (var installment in LoanService.RemainingSchedule(loan).Where(i => i.Date <= end))
                loanPayments[WeekOf(installment.Date)] -= installment.Payment;
        }

        foreach (var item in scenario?.Items ?? [])
        {
            foreach (var date in item.Dates(asOf.AddDays(1), end))
                other[WeekOf(date)] += item.Amount;
        }

        var result = new List<ForecastWeek>();
        var balance = opening;
        for (var w = 0; w < weeks; w++)
        {
            var week = new ForecastWeek(asOf.AddDays(7 * w + 1), asOf.AddDays(7 * (w + 1)), balance, collections[w], bills[w], loanPayments[w], other[w]);
            result.Add(week);
            balance = week.Closing;
        }

        return new CashForecast(asOf, opening, scenario, result);
    }

    public async Task<ReportTable> TableAsync(DateOnly asOf, int weeks, Guid? scenarioId, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var f = await ForecastAsync(asOf, weeks, scenarioId, ct);
        var rows = f.Weeks.Select(w => new ReportTableRow(
        [
            new ReportCell(w.Start), w.Opening, w.Collections, w.Bills, w.Loans, w.Other, w.Closing,
        ])).ToList();
        rows.Add(new ReportTableRow(
            ["Total", f.Opening, f.Weeks.Sum(w => w.Collections), f.Weeks.Sum(w => w.Bills), f.Weeks.Sum(w => w.Loans), f.Weeks.Sum(w => w.Other),
                f.Weeks.Count > 0 ? f.Weeks[^1].Closing : f.Opening], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Cash-flow forecast",
            [company.Name, $"{f.Weeks.Count} weeks from {asOf:MMM d, yyyy}" + (f.Scenario is { } s ? $" · scenario: {s.Name}" : "")],
            [
                new("Week starting", ColumnFormat.Date), new("Opening", ColumnFormat.Money), new("Customers pay", ColumnFormat.Money),
                new("Bills", ColumnFormat.Money), new("Loans", ColumnFormat.Money), new("Scenario", ColumnFormat.Money), new("Closing", ColumnFormat.Money),
            ],
            rows,
            Note: "Open invoices are collected on their due dates and open bills paid on theirs (overdue ones in the first week), shifted by the scenario's delays. Recurring templates aren't included; add them to a scenario.");
    }

    private AuditRecord Audit(AuditAction action, ForecastScenario s, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, s.Id, before, Snapshot(s));

    private static string Snapshot(ForecastScenario s) => JsonSerializer.Serialize(new
    {
        s.Name,
        s.CollectionDelayDays,
        s.PaymentDelayDays,
        Items = s.Items.Select(i => new { i.Description, i.Amount, i.StartDate, Recurrence = i.Recurrence.ToString(), i.EndDate }),
    });
}

/// <summary>A line of the adjusted trial balance worksheet, debit-positive.</summary>
public sealed record WorksheetLine(Guid AccountId, string Name, decimal Unadjusted, decimal Adjustments)
{
    public decimal Adjusted => Unadjusted + Adjustments;
}

/// <summary>
/// Adjusting entries: journal entries an accountant marks as year-end adjustments, kept beside the ledger so the
/// worksheet can show balances before and after them. Voided entries don't count as adjustments.
/// </summary>
public sealed class AdjustingEntryService(IReportingStore store, ILedgerStore ledgerStore, ReportLedger ledger, AccountService accounts, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public async Task<IReadOnlySet<Guid>> MarkedAsync(CancellationToken ct = default) =>
        (await store.ListAdjustingEntriesAsync(ct)).Select(a => a.JournalEntryId).ToHashSet();

    /// <summary>Marks or unmarks a journal entry (manual entries only); audited on the entry.</summary>
    public async Task SetAsync(Guid journalEntryId, bool adjusting, CancellationToken ct = default)
    {
        var entry = await ledgerStore.GetEntryAsync(journalEntryId, ct) ?? throw new EntityNotFoundException("journal_entry", journalEntryId);
        if (!JournalEntry.IsJournalSource(entry.Source) || entry.Source == JournalEntry.AutoReversalSource)
            throw new DomainException("adjusting.not_journal", "Only journal entries can be adjusting entries, not documents or reversals.");
        var existing = (await store.ListAdjustingEntriesAsync(ct)).FirstOrDefault(a => a.JournalEntryId == journalEntryId);
        if (adjusting == existing is not null)
            return;
        if (adjusting)
            store.Add(AdjustingEntry.Create(journalEntryId, user.Actor, clock.UtcNow));
        else
            store.Remove(existing!);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "journal_entry", journalEntryId,
            JsonSerializer.Serialize(new { Adjusting = !adjusting }), JsonSerializer.Serialize(new { Adjusting = adjusting })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>The marked entries that are posted (not voided), dated from <paramref name="from"/> through <paramref name="to"/>.</summary>
    public async Task<IReadOnlyList<JournalEntry>> ListAsync(DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var result = new List<JournalEntry>();
        foreach (var id in await MarkedAsync(ct))
        {
            if (await ledgerStore.GetEntryAsync(id, ct) is { Status: JournalEntryStatus.Posted } entry && (from is null || entry.Date >= from) && entry.Date <= to)
                result.Add(entry);
        }

        return result.OrderBy(e => e.Date).ThenBy(e => e.Number).ToList();
    }

    /// <summary>Every account's balance on a date before adjustments, the adjusting entries (this fiscal year), and after.</summary>
    public async Task<IReadOnlyList<WorksheetLine>> WorksheetAsync(DateOnly asOf, ReportBasis basis, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var fiscal = DateRanges.For(DatePreset.ThisFiscalYearToDate, asOf, company.FiscalYearStartMonth);
        var adjusting = (await ListAsync(fiscal.From, asOf, ct)).Select(e => e.Id).ToHashSet();
        // Adjusting entries are this fiscal year's, so history before it comes folded.
        return (await ledger.LinesSinceAsync(basis, fiscal.From ?? asOf, asOf, ct: ct))
            .GroupBy(l => l.AccountId)
            .Select(g =>
            {
                var adjustments = g.Where(l => adjusting.Contains(l.JournalEntryId)).Sum(l => l.Amount);
                return new WorksheetLine(g.Key, chart.Find(g.Key) is { } a ? chart.FullName(a.Id) : "", g.Sum(l => l.Amount) - adjustments, adjustments);
            })
            .Where(l => l.Unadjusted != 0m || l.Adjustments != 0m)
            .OrderBy(l => chart.Find(l.AccountId)?.Number ?? "￿", StringComparer.Ordinal).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ReportTable> WorksheetTableAsync(DateOnly asOf, ReportBasis basis, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var lines = await WorksheetAsync(asOf, basis, ct);
        static ReportCell Dr(decimal v) => v > 0m ? new ReportCell(v) : ReportCell.Empty;
        static ReportCell Cr(decimal v) => v < 0m ? new ReportCell(-v) : ReportCell.Empty;
        var rows = lines.Select(l => new ReportTableRow(
        [
            new ReportCell(l.Name, new ReportLink("account", l.AccountId) { To = asOf }),
            Dr(l.Unadjusted), Cr(l.Unadjusted), Dr(l.Adjustments), Cr(l.Adjustments), Dr(l.Adjusted), Cr(l.Adjusted),
        ])).ToList();
        static decimal D(IEnumerable<decimal> v) => v.Where(x => x > 0m).Sum();
        static decimal C(IEnumerable<decimal> v) => -v.Where(x => x < 0m).Sum();
        rows.Add(new ReportTableRow(
        [
            "Total", D(lines.Select(l => l.Unadjusted)), C(lines.Select(l => l.Unadjusted)), D(lines.Select(l => l.Adjustments)), C(lines.Select(l => l.Adjustments)),
            D(lines.Select(l => l.Adjusted)), C(lines.Select(l => l.Adjusted)),
        ], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Trial balance worksheet",
            [company.Name, $"As of {asOf:MMM d, yyyy} · {StatementReports.Describe(basis)}"],
            [
                new("Account"), new("Unadjusted debit", ColumnFormat.Money), new("Unadjusted credit", ColumnFormat.Money),
                new("Adjustment debit", ColumnFormat.Money), new("Adjustment credit", ColumnFormat.Money),
                new("Adjusted debit", ColumnFormat.Money), new("Adjusted credit", ColumnFormat.Money),
            ],
            rows,
            basis,
            "Adjustments are the journal entries marked as adjusting entries in this fiscal year.");
    }

    public async Task<ReportTable> EntriesTableAsync(DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var chart = await accounts.GetChartAsync(ct);
        var rows = new List<ReportTableRow>();
        var entries = await ListAsync(from, to, ct);
        foreach (var entry in entries)
        {
            var first = true;
            foreach (var line in entry.Lines.OrderBy(l => l.LineNumber))
            {
                rows.Add(new ReportTableRow(
                [
                    first ? new ReportCell(entry.Date) : ReportCell.Empty,
                    first ? new ReportCell((int)entry.Number, new ReportLink("entry", entry.Id) { Source = entry.Source, SourceId = entry.SourceId }) : ReportCell.Empty,
                    chart.Find(line.AccountId) is { } a ? chart.FullName(a.Id) : "",
                    first ? entry.Memo : line.Memo,
                    line.Debit == 0m ? ReportCell.Empty : new ReportCell(line.Debit),
                    line.Credit == 0m ? ReportCell.Empty : new ReportCell(line.Credit),
                ]));
                first = false;
            }
        }

        rows.Add(new ReportTableRow(["Total", null, null, null, entries.Sum(e => e.TotalDebits.Amount), entries.Sum(e => e.TotalCredits.Amount)], 0, ReportRowStyle.Total));
        return new ReportTable(
            "Adjusting journal entries",
            [company.Name, from is { } f ? Comparisons.Describe(f, to) : $"All dates to {to:MMM d, yyyy}"],
            [new("Date", ColumnFormat.Date), new("No.", ColumnFormat.Number), new("Account"), new("Memo"), new("Debit", ColumnFormat.Money), new("Credit", ColumnFormat.Money)],
            rows);
    }
}

/// <summary>
/// Year-end files for the accountant: the reports they ask for as Excel files, the 1099 filing data, and a README,
/// in one ZIP. The 1099 file carries full taxpayer IDs, so building the package is audited.
/// </summary>
public sealed class AccountantPackageService(
    ReportCatalog catalog, IReportExporter exporter, PayablesService payables, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    /// <summary>The reports in the package, with the query each runs with (from/to/asOf/year filled in).</summary>
    public static IReadOnlyList<(string Key, string File)> Reports { get; } =
    [
        ("trial-balance", "01 Trial balance"),
        ("trial-balance-worksheet", "02 Trial balance worksheet"),
        ("adjusting-entries", "03 Adjusting journal entries"),
        ("profit-and-loss", "04 Profit and loss"),
        ("balance-sheet", "05 Balance sheet"),
        ("cash-flow", "06 Statement of cash flows"),
        ("general-ledger", "07 General ledger"),
        ("journal", "08 Journal"),
        ("ar-aging", "09 AR aging"),
        ("ap-aging", "10 AP aging"),
        ("fixed-assets", "11 Fixed asset register"),
        ("depreciation-schedule", "12 Depreciation schedule"),
        ("loans", "13 Loans"),
        ("1099", "14 1099 summary"),
        ("tax-summary", "15 Tax summary"),
    ];

    public async Task<ExportFile> BuildAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("package.dates_invalid", "The end date can't be before the start.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        var query = string.Create(CultureInfo.InvariantCulture, $"preset=Custom&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&asOf={to:yyyy-MM-dd}&year={to.Year}&detail=true");
        var path = Path.Combine(Path.GetTempPath(), $"openbook-package-{Guid.NewGuid():N}.zip");
        await using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var (key, name) in Reports)
            {
                var table = await catalog.RunAsync(key, ReportParameters.Parse(query), ct);
                var entry = zip.CreateEntry($"{name}.xlsx", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await stream.WriteAsync(exporter.ToXlsx(table), ct);
            }

            var filing = zip.CreateEntry($"16 1099 filing data {to.Year}.csv", CompressionLevel.Optimal);
            await using (var stream = filing.Open())
                await stream.WriteAsync(await Filing1099CsvAsync(to.Year, ct: ct), ct);

            var readme = zip.CreateEntry("README.txt");
            await using (var stream = readme.Open())
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(
                    $"{company.Name}: accountant's package for {from:MMM d, yyyy} to {to:MMM d, yyyy}, made {clock.UtcNow.InCompanyTime():g} by {user.Actor}.\r\n\r\n"
                    + "Reports are on the company's default basis. The 1099 filing data has full taxpayer IDs: keep it private.\r\n"), ct);
            }
        }

        companies.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Exported, "company", company.Id, null,
            JsonSerializer.Serialize(new { Exported = "Accountant package", From = from, To = to })));
        await companies.SaveChangesAsync(ct);
        return new ExportFile(path, $"{Safe(company.Name)} accountant package {to:yyyy-MM-dd}.zip");
    }

    /// <summary>
    /// The 1099 filing data for a year: one row per vendor that meets the threshold, with the fields the IRS forms ask
    /// for (full TIN, address, form and box, amount), to copy into the IRIS upload template or a filing service.
    /// </summary>
    /// <param name="audit">True when the file leaves OpenBook on its own (downloaded), so the export is audited.</param>
    public async Task<byte[]> Filing1099CsvAsync(int year, bool audit = false, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (audit)
        {
            companies.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Exported, "company", company.Id, null,
                JsonSerializer.Serialize(new { Exported = "1099 filing data", Year = year })));
            await companies.SaveChangesAsync(ct);
        }

        var report = await payables.Report1099Async(year, null, ct);
        var csv = new StringBuilder();
        csv.Append("Form,Box,Recipient name,Recipient business name,Recipient TIN,TIN type,Recipient address,Amount,Payer name,Payer address,Tax year\r\n");
        foreach (var v in report.Vendors.Where(v => v.MeetsThreshold).OrderBy(v => v.Vendor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var (form, box) = FormAndBox(v.Vendor.Form1099Box);
            var tin = v.Vendor.TaxId ?? "";
            csv.AppendJoin(',', [
                form, box, Field(v.Vendor.DisplayName), Field(v.Vendor.CompanyName), Field(tin), TinType(tin), Field(v.Vendor.Address),
                (v.Total - v.CardPayments).ToString("0.00", CultureInfo.InvariantCulture), Field(company.Name), Field(company.Address),
                year.ToString(CultureInfo.InvariantCulture),
            ]).Append("\r\n");
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    public static (string Form, string Box) FormAndBox(Form1099Box box) => box switch
    {
        Form1099Box.MiscRents => ("1099-MISC", "1"),
        Form1099Box.MiscOtherIncome => ("1099-MISC", "3"),
        Form1099Box.MiscMedical => ("1099-MISC", "6"),
        Form1099Box.MiscAttorney => ("1099-MISC", "10"),
        _ => ("1099-NEC", "1"),
    };

    /// <summary>EIN for a TIN written 12-3456789, SSN for 123-45-6789; blank when it can't be told.</summary>
    private static string TinType(string tin) => tin.Length == 10 && tin[2] == '-' ? "EIN" : tin.Length == 11 && tin[3] == '-' ? "SSN" : "";

    /// <summary>CSV-quoted, with formula-starting text neutralised like the report exports.</summary>
    private static string Field(string? value)
    {
        var text = (value ?? "").Replace("\r", " ", StringComparison.Ordinal).Replace("\n", ", ", StringComparison.Ordinal);
        if (text.Length > 0 && text[0] is '=' or '+' or '@' or '-')
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_').ToArray());
}
