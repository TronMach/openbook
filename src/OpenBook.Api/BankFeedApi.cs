using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Domain.Banking;

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
