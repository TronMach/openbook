using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Application;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

public sealed record QueueFeedRequest(IReadOnlyList<FeedLine> Lines);

public sealed record MatchFeedItemRequest(Guid JournalEntryId);

public sealed record FeedItemResponse(
    Guid Id, Guid AccountId, DateOnly Date, decimal Amount, string? Payee, string? Reference, string? Memo, string? FitId,
    BankFeedStatus Status, Guid? BankTransactionId, Guid? MatchedJournalEntryId, Guid? RuleId)
{
    public static FeedItemResponse From(BankFeedItem i) => new(
        i.Id, i.AccountId, i.Date, i.Amount, i.Payee, i.Reference, i.Memo, i.FitId, i.Status, i.BankTransactionId, i.MatchedJournalEntryId, i.RuleId);
}

public sealed record BankRuleResponse(
    Guid Id, string Name, int Priority, Guid? AccountId, RuleDirection Direction, string Contains, decimal? MinAmount, decimal? MaxAmount,
    Guid CategoryAccountId, string? Payee, Guid? VendorId, bool AutoAdd, bool IsActive)
{
    public static BankRuleResponse From(BankRule r) => new(
        r.Id, r.Name, r.Priority, r.AccountId, r.Direction, r.Contains, r.MinAmount, r.MaxAmount, r.CategoryAccountId, r.Payee, r.VendorId, r.AutoAdd, r.IsActive);
}

public sealed record PrintChecksRequest(IReadOnlyList<CheckRef> Checks);

public sealed record CheckRef(CheckKind Kind, Guid Id);

internal static class BankFeedApi
{
    public static void MapBankFeedApi(this RouteGroupBuilder api)
    {
        var banking = api.MapGroup("/banking");

        // Preview: POST the statement (CSV, OFX, QFX or QBO) as text. Then queue the chosen lines as JSON.
        banking.MapPost("/accounts/{id:guid}/feed/preview", async (Guid id, HttpRequest request, DateOrder? dateOrder, AmountSign? amountSign,
            BankFeedService s, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            return await s.PreviewAsync(id, await reader.ReadToEndAsync(ct), new BankImportOptions(dateOrder, amountSign ?? AmountSign.PositiveIsMoneyIn), ct);
        }).Accepts<string>("text/plain");
        banking.MapPost("/accounts/{id:guid}/feed", async (Guid id, QueueFeedRequest r, BankFeedService s, CancellationToken ct) =>
            await s.QueueAsync(id, r.Lines ?? [], ct));
        banking.MapGet("/accounts/{id:guid}/feed", async (Guid id, BankFeedStatus? status, BankFeedService s, CancellationToken ct) =>
            (await s.ReviewAsync(id, status ?? BankFeedStatus.Pending, ct)).Select(r => new
            {
                Item = FeedItemResponse.From(r.Item),
                Suggestion = r.Suggestion is { } x ? new { x.CategoryAccountId, x.Payee, x.VendorId, RuleId = x.Rule.Id, RuleName = x.Rule.Name } : null,
                r.Matches,
            }));
        banking.MapPost("/feed/{id:guid}/add", async (Guid id, AddFeedItemCommand r, BankFeedService s, CancellationToken ct) =>
            BankTransactionResponse.From(await s.AddAsync(id, r, ct)));
        banking.MapPost("/feed/{id:guid}/match", async (Guid id, MatchFeedItemRequest r, BankFeedService s, CancellationToken ct) =>
        {
            await s.MatchAsync(id, r.JournalEntryId, ct);
            return Results.NoContent();
        });
        banking.MapPost("/feed/{id:guid}/exclude", async (Guid id, BankFeedService s, CancellationToken ct) =>
        {
            await s.ExcludeAsync(id, ct);
            return Results.NoContent();
        });
        banking.MapPost("/feed/{id:guid}/reopen", async (Guid id, BankFeedService s, CancellationToken ct) =>
        {
            await s.ReopenAsync(id, ct);
            return Results.NoContent();
        });

        banking.MapGet("/rules", async (BankRuleService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(BankRuleResponse.From));
        banking.MapGet("/rules/{id:guid}", async (Guid id, BankRuleService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(BankRuleResponse.From(r)) : Results.NotFound());
        banking.MapPost("/rules", async (BankRuleDetails r, BankRuleService s, CancellationToken ct) =>
        {
            var rule = await s.CreateAsync(r, ct);
            return Results.Created($"/api/banking/rules/{rule.Id}", BankRuleResponse.From(rule));
        });
        banking.MapPut("/rules/{id:guid}", async (Guid id, BankRuleDetails r, BankRuleService s, CancellationToken ct) =>
            BankRuleResponse.From(await s.UpdateAsync(id, r, ct)));
        banking.MapPut("/rules/{id:guid}/active", async (Guid id, SetActiveRequest r, BankRuleService s, CancellationToken ct) =>
            BankRuleResponse.From(await s.SetActiveAsync(id, r.Active, ct)));

        banking.MapPost("/reconciliations/{id:guid}/undo", async (Guid id, ReconciliationService s, CancellationToken ct) =>
            WorksheetResponse.From(await s.GetWorksheetAsync((await s.UndoAsync(id, ct)).Id, ct)));

        banking.MapGet("/checks", async (Guid? accountId, bool? unprinted, CheckService s, CancellationToken ct) =>
            await s.ListAsync(accountId, unprinted ?? false, ct));
        banking.MapPost("/checks/print", async (PrintChecksRequest r, HttpContext http, CheckService s, CancellationToken ct) =>
        {
            var printed = await s.PrintAsync((r.Checks ?? []).Select(c => (c.Kind, c.Id)).ToList(), ct);
            http.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileNameStar = printed.FileName }.ToString();
            return Results.File(printed.Pdf, "application/pdf");
        });
    }
}

public sealed record ConnectRequest(string SetupToken, string? Name);

public sealed record LinkConnectionAccountRequest(Guid? AccountId, DateOnly? ImportFrom);

public sealed record BankConnectionResponse(
    Guid Id, BankConnectionProvider Provider, string Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSuccessAt, bool LastSyncFailed, string? LastError, IReadOnlyList<BankConnectionAccountResponse> Accounts)
{
    /// <summary>Never includes the access credential.</summary>
    public static BankConnectionResponse From(BankConnection c) => new(
        c.Id, c.Provider, c.Name, c.IsActive, c.CreatedAt, c.LastSyncAt, c.LastSuccessAt, c.LastSyncFailed, c.LastError,
        c.Accounts.Select(a => new BankConnectionAccountResponse(a.RemoteId, a.Name, a.Institution, a.Currency, a.Balance, a.BalanceDate, a.AccountId, a.ImportFrom, a.FetchedThrough)).ToList());
}

public sealed record BankConnectionAccountResponse(
    string RemoteId, string Name, string? Institution, string? Currency, decimal? Balance, DateOnly? BalanceDate, Guid? AccountId, DateOnly? ImportFrom,
    DateOnly? FetchedThrough);

public sealed record PositivePayExportRequest(Guid AccountId, DateOnly From, DateOnly To, string? BankAccountNumber, PositivePayDateFormat DateFormat, bool IncludeHeader = true);

public sealed record UpdatePettyCashFundRequest(decimal FloatAmount, string? Custodian, bool Active = true);

public sealed record ReplenishRequest(Guid FromAccountId, DateOnly Date);

internal static class BankingProApi
{
    public static void MapBankingProApi(this RouteGroupBuilder api)
    {
        var banking = api.MapGroup("/banking");

        banking.MapGet("/connections", async (BankConnectionService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(BankConnectionResponse.From));
        banking.MapGet("/connections/{id:guid}", async (Guid id, BankConnectionService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } c ? Results.Ok(BankConnectionResponse.From(c)) : Results.NotFound());
        banking.MapPost("/connections", async (ConnectRequest r, BankConnectionService s, CancellationToken ct) =>
        {
            var connection = await s.ConnectAsync(BankConnectionProvider.SimpleFin, r.SetupToken, r.Name, CompanyTime.Zone, ct);
            return Results.Created($"/api/banking/connections/{connection.Id}", BankConnectionResponse.From(connection));
        });
        banking.MapPost("/connections/{id:guid}/sync", async (Guid id, BankConnectionService s, CancellationToken ct) =>
            await s.SyncAsync(id, CompanyTime.Zone, ct));
        banking.MapPut("/connections/{id:guid}/accounts/{remoteId}", async (Guid id, string remoteId, LinkConnectionAccountRequest r, BankConnectionService s, CancellationToken ct) =>
            BankConnectionResponse.From(await s.LinkAsync(id, remoteId, r.AccountId, r.ImportFrom, ct)));
        banking.MapPost("/connections/{id:guid}/disconnect", async (Guid id, BankConnectionService s, CancellationToken ct) =>
        {
            await s.DisconnectAsync(id, ct);
            return Results.NoContent();
        });

        banking.MapGet("/positive-pay", async (Guid accountId, DateOnly from, DateOnly to, PositivePayService s, CancellationToken ct) =>
            await s.ListAsync(accountId, from, to, ct));
        banking.MapPost("/positive-pay/export", async (PositivePayExportRequest r, HttpContext http, PositivePayService s, CancellationToken ct) =>
        {
            var file = await s.ExportAsync(r.AccountId, r.From, r.To, new PositivePayOptions(r.BankAccountNumber, r.DateFormat, r.IncludeHeader), ct);
            http.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = file.FileName }.ToString();
            return Results.File(file.Content, "text/csv");
        });

        banking.MapGet("/recurring-expenses", async (RecurringExpenseService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(RecurringExpenseResponse.From));
        banking.MapGet("/recurring-expenses/{id:guid}", async (Guid id, RecurringExpenseService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } t ? Results.Ok(RecurringExpenseResponse.From(t)) : Results.NotFound());
        banking.MapPost("/recurring-expenses", async (RecurringExpenseDetails r, RecurringExpenseService s, CancellationToken ct) =>
        {
            var t = await s.CreateAsync(r, ct);
            return Results.Created($"/api/banking/recurring-expenses/{t.Id}", RecurringExpenseResponse.From(t));
        });
        banking.MapPut("/recurring-expenses/{id:guid}", async (Guid id, RecurringExpenseDetails r, RecurringExpenseService s, CancellationToken ct) =>
            RecurringExpenseResponse.From(await s.UpdateAsync(id, r, ct)));
        banking.MapPut("/recurring-expenses/{id:guid}/active", async (Guid id, SetActiveRequest r, bool? catchUp, RecurringExpenseService s, CancellationToken ct) =>
            RecurringExpenseResponse.From(await s.SetActiveAsync(id, r.Active, catchUp == true, ct)));
        // Records every recurring expense due today (automatic or not).
        banking.MapPost("/recurring-expenses/record-due", async (RecurringExpenseService s, CancellationToken ct) =>
            (await s.CreateDueAsync(CompanyTime.Today, automaticOnly: false, ct)).Select(r => new { TemplateId = r.Template.Id, Created = r.Created.Select(t => t.Id).ToList(), r.Error }));

        banking.MapGet("/petty-cash", async (PettyCashService s, CancellationToken ct) =>
            (await s.ListAsync(ct)).Select(f => new { f.Fund, f.AccountName, f.Balance, f.ToReplenish, f.LastCount }));
        banking.MapGet("/petty-cash/{id:guid}", async (Guid id, PettyCashService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } f
                ? Results.Ok(new { f.Fund, f.AccountName, f.Balance, f.ToReplenish, Counts = await s.CountsAsync(id, ct) })
                : Results.NotFound());
        banking.MapPost("/petty-cash", async (CreatePettyCashFundCommand r, PettyCashService s, CancellationToken ct) =>
        {
            var fund = await s.CreateAsync(r, ct);
            return Results.Created($"/api/banking/petty-cash/{fund.Id}", fund);
        });
        banking.MapPut("/petty-cash/{id:guid}", async (Guid id, UpdatePettyCashFundRequest r, PettyCashService s, CancellationToken ct) =>
            await s.UpdateAsync(id, r.FloatAmount, r.Custodian, r.Active, ct));
        banking.MapPost("/petty-cash/{id:guid}/counts", async (Guid id, CountCashCommand r, PettyCashService s, CancellationToken ct) =>
            await s.CountAsync(id, r, ct));
        banking.MapPost("/petty-cash/{id:guid}/replenish", async (Guid id, ReplenishRequest r, PettyCashService s, CancellationToken ct) =>
            BankTransactionResponse.From(await s.ReplenishAsync(id, r.FromAccountId, r.Date, ct)));
    }
}

public sealed record RecurringExpenseResponse(
    Guid Id, string Name, Guid AccountId, Guid? VendorId, string? Payee, string? Memo, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate,
    DateOnly? EndDate, int? MaxOccurrences, int OccurrencesPosted, bool AutoCreate, bool IsActive, DateOnly? NextDate, decimal Total,
    IReadOnlyList<BankSplit> Lines, Guid? LocationId, IReadOnlyList<Guid> TagIds)
{
    public static RecurringExpenseResponse From(RecurringExpense t) => new(
        t.Id, t.Name, t.AccountId, t.VendorId, t.Payee, t.Memo, t.Frequency, t.Interval, t.StartDate, t.EndDate, t.MaxOccurrences, t.OccurrencesPosted,
        t.AutoCreate, t.IsActive, t.NextDate, t.Total, t.Lines.OrderBy(l => l.LineNumber).Select(l => l.ToSplit()).ToList(), t.LocationId, t.TagIds);
}
