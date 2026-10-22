using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

/// <summary>REST surface over the application layer. The Blazor UI calls the same services directly.</summary>
public static class OpenBookApi
{
    public static IServiceCollection AddOpenBookApi(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    public static IEndpointRouteBuilder MapOpenBookApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(MapDomainErrors);

        api.MapGet("/accounts", async (AccountService accounts, CancellationToken ct) =>
            (await accounts.ListAsync(ct)).Select(AccountResponse.From));

        api.MapPost("/accounts", async (CreateAccountRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var account = await accounts.CreateAsync(new CreateAccountCommand(request.Name, request.Type, request.Number, request.ParentId), ct);
            return Results.Created($"/api/accounts/{account.Id}", AccountResponse.From(account));
        });

        api.MapPost("/journal-entries", async (PostJournalEntryRequest request, PostingEngine engine, CancellationToken ct) =>
        {
            var lines = (request.Lines ?? []).Select(l => new JournalLineDraft(l.AccountId, l.Debit, l.Credit, l.Memo)).ToList();
            var entry = await engine.PostAsync(new PostJournalEntryCommand(request.Date, lines, request.Memo), ct);
            return Results.Created($"/api/journal-entries/{entry.Id}", JournalEntryResponse.From(entry));
        });

        api.MapGet("/journal-entries/{id:guid}", async (Guid id, PostingEngine engine, CancellationToken ct) =>
            await engine.GetAsync(id, ct) is { } entry ? Results.Ok(JournalEntryResponse.From(entry)) : Results.NotFound());

        api.MapPost("/journal-entries/{id:guid}/void", async (Guid id, VoidJournalEntryRequest request, PostingEngine engine, CancellationToken ct) =>
            JournalEntryResponse.From(await engine.VoidAsync(id, request.Reason, ct)));

        api.MapGet("/reports/trial-balance", async (DateOnly? asOf, TrialBalanceQuery query, CancellationToken ct) =>
            TrialBalanceResponse.From(await query.RunAsync(asOf ?? DateOnly.FromDateTime(DateTime.Today), ct)));

        return app;
    }

    private static async ValueTask<object?> MapDomainErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DomainException ex)
        {
            return Results.Problem(
                title: ex.Message,
                statusCode: ex is EntityNotFoundException ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }
}
