using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Accounts;
using OpenBook.Application.Auditing;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

public sealed record SetClosingRequest(DateOnly? ClosingDate, string? NewPassword = null, bool RemovePassword = false);

public sealed record RecurringEntryResponse(
    Guid Id, string Name, string? Memo, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate,
    int? MaxOccurrences, int OccurrencesPosted, DateOnly? NextDate, bool AutoPost, bool IsActive, IReadOnlyList<JournalLineDraft> Lines)
{
    public static RecurringEntryResponse From(RecurringEntry r) => new(
        r.Id, r.Name, r.Memo, r.Frequency, r.Interval, r.StartDate, r.EndDate, r.MaxOccurrences, r.OccurrencesPosted, r.NextDate,
        r.AutoPost, r.IsActive, r.Lines.Select(l => l.ToDraft()).ToList());
}

public sealed record OpeningBalancesRequest(DateOnly AsOf, IReadOnlyList<OpeningBalance> Balances);

internal static class LedgerToolsApi
{
    public static void MapLedgerToolsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/closing", async (CloseBooksService s, CancellationToken ct) => await s.GetAsync(ct));
        api.MapPut("/closing", async (SetClosingRequest r, CloseBooksService s, CancellationToken ct) =>
            await s.SetAsync(r.ClosingDate, r.NewPassword, r.RemovePassword, ct));
        api.MapGet("/closing/fiscal-years", async (CloseBooksService s, CancellationToken ct) =>
            await s.FiscalYearsAsync(DateOnly.FromDateTime(DateTime.Today), ct));
        api.MapPost("/closing/fiscal-years/{end}/close", async (DateOnly end, CloseBooksService s, CancellationToken ct) =>
            await s.CloseYearAsync(end, ct));

        api.MapGet("/recurring-entries", async (RecurringEntryService s, CancellationToken ct) =>
            (await s.ListAsync(ct)).Select(RecurringEntryResponse.From));
        api.MapGet("/recurring-entries/{id:guid}", async (Guid id, RecurringEntryService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(RecurringEntryResponse.From(r)) : Results.NotFound());
        api.MapPost("/recurring-entries", async (RecurringEntryDetails r, RecurringEntryService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(r, ct);
            return Results.Created($"/api/recurring-entries/{created.Id}", RecurringEntryResponse.From(created));
        });
        api.MapPut("/recurring-entries/{id:guid}", async (Guid id, RecurringEntryDetails r, RecurringEntryService s, CancellationToken ct) =>
            RecurringEntryResponse.From(await s.UpdateAsync(id, r, ct)));
        api.MapPut("/recurring-entries/{id:guid}/active", async (Guid id, SetActiveRequest r, RecurringEntryService s, CancellationToken ct) =>
            RecurringEntryResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
        api.MapPost("/recurring-entries/post-due", async (RecurringEntryService s, CancellationToken ct) =>
            (await s.PostDueAsync(DateOnly.FromDateTime(DateTime.Today), autoPostOnly: false, ct))
                .Select(x => new { x.Template.Id, x.Template.Name, Posted = x.Posted.Select(e => new { e.Id, e.Number, e.Date }), x.Error }));

        api.MapGet("/opening-balances/accounts", async (OpeningBalanceService s, CancellationToken ct) =>
            (await s.EligibleAccountsAsync(ct)).Select(a => new { a.Id, a.Name, a.Number, Type = a.Type.ToString() }));
        api.MapPost("/opening-balances", async (OpeningBalancesRequest r, OpeningBalanceService s, CancellationToken ct) =>
        {
            var entry = await s.PostAsync(r.AsOf, r.Balances ?? [], ct);
            return Results.Created($"/api/journal-entries/{entry.Id}", JournalEntryResponse.From(entry));
        });

        api.MapGet("/chart-templates", () => ChartTemplates.All);
        api.MapPost("/chart-templates/{key}/apply", async (string key, bool? dryRun, ChartTemplates t, CancellationToken ct) =>
            dryRun == true ? await t.PreviewAsync(key, ct) : await t.ApplyAsync(key, ct));

        api.MapGet("/audit", async (string? entityType, Guid? entityId, string? actor, DateOnly? from, DateOnly? to, int? skip, int? take,
            AuditLogQuery q, CancellationToken ct) =>
            await q.QueryAsync(new AuditFilter(entityType, entityId, actor, from, to, skip ?? 0, take ?? 100), ct));
    }
}
