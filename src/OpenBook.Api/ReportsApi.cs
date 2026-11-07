using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Search;
using OpenBook.Application;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Reporting;

namespace OpenBook.Api;

public sealed record GeneralLedgerLineResponse(
    DateOnly Date, long EntryNumber, Guid JournalEntryId, string Source, Guid? SourceId, string? Memo, decimal Debit, decimal Credit, decimal Balance);

public sealed record UpdateBudgetRequest(string Name, IReadOnlyList<BudgetAmount> Amounts);

public sealed record SaveReportRequest(string Name, string Report, string? Query);

public sealed record SavedReportResponse(Guid Id, string Name, string Report, string Query, DateTimeOffset CreatedAt, string CreatedBy)
{
    public static SavedReportResponse From(SavedReport r) => new(r.Id, r.Name, r.ReportKey, r.Query, r.CreatedAt, r.CreatedBy);
}

public sealed record GeneralLedgerAccountResponse(
    Guid AccountId, string FullName, decimal OpeningBalance, decimal ClosingBalance, IReadOnlyList<GeneralLedgerLineResponse> Lines);

internal static class ReportsApi
{
    public static void MapReportsApi(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports");

        reports.MapGet("/kpis", async (DateOnly? asOf, KpiService s, CancellationToken ct) =>
            await s.SnapshotAsync(asOf ?? CompanyTime.Today, ct));
        reports.MapGet("/schedules", async (ReportSubscriptionService s, CancellationToken ct) => await s.ListAsync(ct));
        reports.MapPost("/schedules", async (ReportSubscriptionDetails r, ReportSubscriptionService s, CancellationToken ct) =>
        {
            var subscription = await s.CreateAsync(r, ct);
            return Results.Created($"/api/reports/schedules/{subscription.Id}", subscription);
        });
        reports.MapPut("/schedules/{id:guid}", async (Guid id, ReportSubscriptionDetails r, ReportSubscriptionService s, CancellationToken ct) =>
            await s.UpdateAsync(id, r, ct));
        reports.MapDelete("/schedules/{id:guid}", async (Guid id, ReportSubscriptionService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        reports.MapPost("/schedules/{id:guid}/send", async (Guid id, ReportSubscriptionService s, CancellationToken ct) =>
        {
            var run = await s.SendNowAsync(id, ct);
            return run.Error is { } error ? Results.BadRequest(new { code = "subscription.send_failed", message = error }) : Results.Ok(run.Subscription);
        });

        // Either a preset (thisMonth, lastFiscalYear, ...) or explicit from/to.
        reports.MapGet("/profit-and-loss", async (DatePreset? preset, DateOnly? from, DateOnly? to, ReportBasis? basis,
            FinancialReports r, CompanySettingsService settings, CancellationToken ct) =>
        {
            var range = await Range(preset ?? DatePreset.ThisFiscalYearToDate, from, to, settings, ct);
            return await r.ProfitAndLossAsync(range.From ?? DateRanges.Beginning, range.To, basis, ct);
        });

        reports.MapGet("/balance-sheet", async (DateOnly? asOf, ReportBasis? basis, FinancialReports r, CancellationToken ct) =>
            await r.BalanceSheetAsync(asOf ?? CompanyTime.Today, basis, ct));

        reports.MapGet("/general-ledger", async (DatePreset? preset, DateOnly? from, DateOnly? to, ReportBasis? basis, Guid? accountId,
            FinancialReports r, CompanySettingsService settings, CancellationToken ct) =>
        {
            var range = await Range(preset ?? DatePreset.ThisMonth, from, to, settings, ct);
            var gl = await r.GeneralLedgerAsync(range.From, range.To, basis, accountId, ct: ct);
            return new
            {
                gl.From,
                gl.To,
                gl.Basis,
                Accounts = gl.Accounts.Select(a => new GeneralLedgerAccountResponse(
                    a.Account.Id, a.FullName, a.OpeningBalance, a.ClosingBalance,
                    a.Lines.Select(l => new GeneralLedgerLineResponse(
                        l.Line.Date, l.Line.EntryNumber, l.Line.JournalEntryId, l.Line.Source, l.Line.SourceId,
                        l.Line.EntryMemo ?? l.Line.LineMemo, l.Debit, l.Credit, l.Balance)).ToList())).ToList(),
            };
        });

        reports.MapGet("/cash-flow", async (DatePreset? preset, DateOnly? from, DateOnly? to, ReportBasis? basis,
            StatementReports r, CompanySettingsService settings, CancellationToken ct) =>
        {
            var range = await Range(preset ?? DatePreset.ThisFiscalYearToDate, from, to, settings, ct);
            return await r.CashFlowAsync(range.From ?? DateRanges.Beginning, range.To, basis, ct);
        });

        reports.MapGet("/tax-summary", async (DatePreset? preset, DateOnly? from, DateOnly? to, ReportBasis? basis,
            StatementReports r, CompanySettingsService settings, CancellationToken ct) =>
        {
            var range = await Range(preset ?? DatePreset.LastFiscalYear, from, to, settings, ct);
            return await r.TaxSummaryAsync(range.From ?? DateRanges.Beginning, range.To, basis, ct);
        });

        // Every report in the catalog, as a table or a file. Query parameters are the report page's own.
        reports.MapGet("/catalog", (ReportCatalog catalog) => catalog.All);

        reports.MapGet("/{key}/table", async (string key, HttpContext http, ReportCatalog catalog, CancellationToken ct) =>
            await catalog.RunAsync(key, Parameters(http), ct));

        reports.MapGet("/{key}/export", async (string key, string? format, HttpContext http, ReportCatalog catalog, IReportExporter exporter, CancellationToken ct) =>
        {
            var table = await catalog.RunAsync(key, Parameters(http), ct);
            var name = $"{catalog.Find(key)!.Key}-{CompanyTime.Today:yyyyMMdd}";
            return (format ?? "csv").ToLowerInvariant() switch
            {
                "xlsx" => Results.File(exporter.ToXlsx(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx"),
                "pdf" => InlinePdf(http, exporter.ToPdf(table), $"{name}.pdf"),
                "csv" => Results.File(ReportCsv.Write(table), "text/csv; charset=utf-8", $"{name}.csv"),
                _ => Results.BadRequest(new { code = "report.format_invalid", message = "Export as csv, xlsx or pdf." }),
            };
        });

        var accountant = api.MapGroup("/accountant");
        accountant.MapGet("/check", async (BooksCheckService s, CancellationToken ct) => await s.RunAsync(ct));
        accountant.MapGet("/forecast", async (DateOnly? asOf, int? weeks, Guid? scenarioId, ForecastService s, CancellationToken ct) =>
            await s.ForecastAsync(asOf ?? CompanyTime.Today, weeks ?? 13, scenarioId, ct));
        accountant.MapGet("/scenarios", async (ForecastService s, CancellationToken ct) => await s.ListScenariosAsync(ct));
        accountant.MapPost("/scenarios", async (ForecastScenarioDetails r, ForecastService s, CancellationToken ct) =>
        {
            var scenario = await s.CreateScenarioAsync(r, ct);
            return Results.Created($"/api/accountant/scenarios/{scenario.Id}", scenario);
        });
        accountant.MapPut("/scenarios/{id:guid}", async (Guid id, ForecastScenarioDetails r, ForecastService s, CancellationToken ct) => await s.UpdateScenarioAsync(id, r, ct));
        accountant.MapPost("/scenarios/{id:guid}/items", async (Guid id, ScenarioItemDetails r, ForecastService s, CancellationToken ct) => await s.AddItemAsync(id, r, ct));
        accountant.MapDelete("/scenarios/{id:guid}/items/{itemId:guid}", async (Guid id, Guid itemId, ForecastService s, CancellationToken ct) => await s.RemoveItemAsync(id, itemId, ct));
        accountant.MapDelete("/scenarios/{id:guid}", async (Guid id, ForecastService s, CancellationToken ct) =>
        {
            await s.DeleteScenarioAsync(id, ct);
            return Results.NoContent();
        });
        accountant.MapGet("/adjusting-entries", async (DateOnly? from, DateOnly? to, AdjustingEntryService s, CancellationToken ct) =>
            (await s.ListAsync(from, to ?? DateOnly.MaxValue, ct)).Select(e => new { e.Id, e.Number, e.Date, e.Memo }));
        accountant.MapPut("/adjusting-entries/{entryId:guid}", async (Guid entryId, bool adjusting, AdjustingEntryService s, CancellationToken ct) =>
        {
            await s.SetAsync(entryId, adjusting, ct);
            return Results.NoContent();
        });
        accountant.MapGet("/worksheet", async (DateOnly? asOf, ReportBasis? basis, AdjustingEntryService s, CompanySettingsService settings, CancellationToken ct) =>
            await s.WorksheetAsync(asOf ?? CompanyTime.Today, basis ?? (await settings.GetAsync(ct)).DefaultReportBasis, ct));
        accountant.MapGet("/1099-filing", async (int year, decimal? threshold, AccountantPackageService s, CancellationToken ct) =>
            Results.File(await s.Filing1099CsvAsync(year, audit: true, threshold: threshold, ct: ct), "text/csv", $"1099 filing data {year}.csv"));

        accountant.MapGet("/1099-copies", async (int year, decimal? threshold, Guid? vendor, Filing1099Service s, CancellationToken ct) =>
        {
            var copy = await s.CopiesAsync(year, threshold, vendor, ct);
            return Results.File(copy.Pdf, "application/pdf", copy.FileName);
        });
        accountant.MapPost("/1099-copies/email", async (int year, decimal? threshold, Filing1099Service s, CancellationToken ct) =>
            await s.EmailCopiesAsync(year, threshold, ct));
        accountant.MapPost("/1099-filed", async (int year, DateOnly? on, decimal? threshold, Filing1099Service s, CancellationToken ct) =>
            new { Added = await s.MarkFiledAsync(year, on ?? CompanyTime.Today, threshold, ct) });
        accountant.MapDelete("/1099-filed", async (int year, Filing1099Service s, CancellationToken ct) =>
        {
            await s.ClearFiledAsync(year, ct);
            return Results.NoContent();
        });
        accountant.MapPost("/vendors/{id:guid}/w9-request", async (Guid id, Filing1099Service s, CancellationToken ct) =>
        {
            await s.RequestW9Async(id, ct);
            return Results.NoContent();
        });

        var saved = api.MapGroup("/saved-reports");
        saved.MapGet("/", async (SavedReportService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(SavedReportResponse.From));
        saved.MapPost("/", async (SaveReportRequest r, SavedReportService s, CancellationToken ct) =>
            SavedReportResponse.From(await s.SaveAsync(r.Name, r.Report, r.Query, ct)));
        saved.MapDelete("/{id:guid}", async (Guid id, SavedReportService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        var budgets = api.MapGroup("/budgets");
        budgets.MapGet("/", async (BudgetService s, CancellationToken ct) => await s.ListAsync(ct));
        budgets.MapGet("/{id:guid}", async (Guid id, BudgetService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } budget ? Results.Ok(budget) : Results.NotFound());
        budgets.MapPost("/", async (CreateBudgetCommand c, BudgetService s, CancellationToken ct) =>
        {
            var budget = await s.CreateAsync(c, ct);
            return Results.Created($"/api/budgets/{budget.Id}", budget);
        });
        budgets.MapPut("/{id:guid}", async (Guid id, UpdateBudgetRequest r, BudgetService s, CancellationToken ct) =>
            await s.UpdateAsync(id, r.Name, r.Amounts, ct));
        budgets.MapDelete("/{id:guid}", async (Guid id, BudgetService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        budgets.MapGet("/{id:guid}/vs-actual", async (Guid id, int? fromMonth, int? toMonth, ReportBasis? basis, StatementReports r, CancellationToken ct) =>
            await r.BudgetVsActualAsync(id, fromMonth ?? 1, toMonth ?? Budget.Periods, basis, ct));

        // The company's form's lines (all forms with all=true), and setting accounts' lines: { "accountId": "c.8" | null }.
        api.MapGet("/tax-lines", async (bool? all, CompanySettingsService settings, CancellationToken ct) =>
            all == true ? TaxLines.All : TaxLines.For(TaxLines.FormFor((await settings.GetAsync(ct)).BusinessType)));
        api.MapPut("/accounts/tax-lines", async (Dictionary<Guid, string?> map, AccountService accounts, CancellationToken ct) =>
            new { Changed = await accounts.MapTaxLinesAsync(map, ct) });

        // People with a permission set see only results in the areas they may view.
        api.MapGet("/search", async (string? q, ClaimsPrincipal user, SearchService s, CancellationToken ct) =>
        {
            var results = await s.SearchAsync(q, ct);
            return AuthClaims.HasPermissionSet(user)
                ? results with { Hits = results.Hits.Where(h => AuthClaims.LevelIn(user, SearchAreas.Of(h.Kind)) is not null).ToList() }
                : results;
        });
    }

    /// <summary>Opens in the browser's viewer (the page links it in a new tab) and still saves under a sensible name.</summary>
    private static IResult InlinePdf(HttpContext http, byte[] pdf, string fileName)
    {
        http.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileNameStar = fileName }.ToString();
        return Results.File(pdf, "application/pdf");
    }

    private static readonly string[] DateParameters = ["from", "to", "asOf"];
    private static readonly string[] NumberParameters = ["year", "weeks", "fromMonth", "toMonth"];

    /// <summary>A date or number that can't be read is refused: left out, the report would answer for a different period.</summary>
    private static ReportParameters Parameters(HttpContext http)
    {
        var query = http.Request.Query;
        foreach (var name in DateParameters)
        {
            if (query[name].ToString() is { Length: > 0 } text && !DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                throw new OpenBook.Domain.Common.DomainException("report.parameter_invalid", $"{name} must be a date written yyyy-MM-dd.");
        }

        foreach (var name in NumberParameters)
        {
            if (query[name].ToString() is { Length: > 0 } text && !int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new OpenBook.Domain.Common.DomainException("report.parameter_invalid", $"{name} must be a whole number.");
        }

        return new(query.Select(q => new KeyValuePair<string, string?>(q.Key, q.Value.ToString())));
    }

    private static async Task<DateRange> Range(DatePreset preset, DateOnly? from, DateOnly? to, CompanySettingsService settings, CancellationToken ct)
    {
        var today = CompanyTime.Today;
        return from is not null || to is not null
            ? DateRanges.For(DatePreset.Custom, today, 1, from, to)
            : DateRanges.For(preset, today, (await settings.GetAsync(ct)).FiscalYearStartMonth);
    }
}
