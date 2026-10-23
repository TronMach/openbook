using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Application.Search;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

public sealed record GeneralLedgerLineResponse(
    DateOnly Date, long EntryNumber, Guid JournalEntryId, string Source, Guid? SourceId, string? Memo, decimal Debit, decimal Credit, decimal Balance);

public sealed record GeneralLedgerAccountResponse(
    Guid AccountId, string FullName, decimal OpeningBalance, decimal ClosingBalance, IReadOnlyList<GeneralLedgerLineResponse> Lines);

internal static class ReportsApi
{
    public static void MapReportsApi(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports");

        // Either a preset (thisMonth, lastFiscalYear, ...) or explicit from/to.
        reports.MapGet("/profit-and-loss", async (DatePreset? preset, DateOnly? from, DateOnly? to, ReportBasis? basis,
            FinancialReports r, CompanySettingsService settings, CancellationToken ct) =>
        {
            var range = await Range(preset ?? DatePreset.ThisFiscalYearToDate, from, to, settings, ct);
            return await r.ProfitAndLossAsync(range.From ?? DateRanges.Beginning, range.To, basis, ct);
        });

        reports.MapGet("/balance-sheet", async (DateOnly? asOf, ReportBasis? basis, FinancialReports r, CancellationToken ct) =>
            await r.BalanceSheetAsync(asOf ?? DateOnly.FromDateTime(DateTime.Today), basis, ct));

        reports.MapGet("/general-ledger", async (DatePreset? preset, DateOnly? from, DateOnly? to, ReportBasis? basis, Guid? accountId,
            FinancialReports r, CompanySettingsService settings, CancellationToken ct) =>
        {
            var range = await Range(preset ?? DatePreset.ThisMonth, from, to, settings, ct);
            var gl = await r.GeneralLedgerAsync(range.From, range.To, basis, accountId, ct);
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

        api.MapGet("/search", async (string? q, SearchService s, CancellationToken ct) => await s.SearchAsync(q, ct));
    }

    private static async Task<DateRange> Range(DatePreset preset, DateOnly? from, DateOnly? to, CompanySettingsService settings, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return from is not null || to is not null
            ? DateRanges.For(DatePreset.Custom, today, 1, from, to)
            : DateRanges.For(preset, today, (await settings.GetAsync(ct)).FiscalYearStartMonth);
    }
}
