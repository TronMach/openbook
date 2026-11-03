using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

/// <summary>REST surface over the application layer. The Blazor UI calls the same services directly.</summary>
public static class OpenBookApi
{
    public static IServiceCollection AddOpenBookApi(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "OpenBook API",
                Version = "v1",
                Description = "Everything the app can do. Authenticate with an API key (Settings › API keys) as `Authorization: Bearer obk_…`; " +
                    "it acts as its user with the user's role. Errors are 400 with a stable `code`, 403 when the role doesn't allow it.",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "An OpenBook API key.",
            };
            return Task.CompletedTask;
        }));
        return services;
    }

    public static IEndpointRouteBuilder MapOpenBookApi(this IEndpointRouteBuilder app)
    {
        // The OpenAPI description itself is public: it describes endpoints, not data.
        app.MapOpenApi("/api/openapi/{documentName}.json").AllowAnonymous();
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(MapDomainErrors);
        api.MapBankingApi();
        api.MapSalesApi();
        api.MapPurchasesApi();
        api.MapReportsApi();
        api.MapLedgerToolsApi();
        api.MapReceivablesApi();
        api.MapEmailApi();
        api.MapPayablesApi();
        api.MapBankFeedApi();
        api.MapBankingProApi();
        api.MapSalesTaxApi();
        api.MapPricingApi();
        api.MapSegmentsApi();
        api.MapInventoryApi();
        api.MapAccessApi();
        api.MapPortabilityApi();
        api.MapProjectsApi();
        api.MapAssetsApi();
        api.MapChannelsApi();
        api.MapNonprofitApi();
        api.MapCaptureApi();
        api.MapDocumentsApi();
        api.MapCustomFieldsApi();
        api.MapFormDesignsApi();

        api.MapGet("/company", async (CompanySettingsService settings, CancellationToken ct) =>
            CompanyResponse.From(await settings.GetAsync(ct)));

        api.MapPut("/company", async (CompanySettingsRequest r, CompanySettingsService settings, CancellationToken ct) =>
            CompanyResponse.From(await settings.UpdateAsync(new UpdateCompanySettingsCommand(
                r.Name, r.BusinessType, r.LegalName, r.Email, r.Phone, r.Address, r.FiscalYearStartMonth, r.UseAccountNumbers, r.DefaultReportBasis, r.Edition, r.TimeZoneId, r.DefaultViewMode, r.AllowSellingShort), ct)));

        api.MapGet("/accounts", async (AccountService accounts, CancellationToken ct) =>
        {
            var chart = await accounts.GetChartAsync(ct);
            return chart.Ordered().Select(x => AccountResponse.From(x.Account, chart));
        });

        api.MapPost("/accounts", async (CreateAccountRequest r, AccountService accounts, CancellationToken ct) =>
        {
            var account = await accounts.CreateAsync(new CreateAccountCommand(r.Name, r.Type, r.Number, r.ParentId, r.Description, r.DonorRestricted), ct);
            return Results.Created($"/api/accounts/{account.Id}", AccountResponse.From(account, await accounts.GetChartAsync(ct)));
        });

        api.MapPut("/accounts/{id:guid}", async (Guid id, UpdateAccountRequest r, AccountService accounts, CancellationToken ct) =>
        {
            var account = await accounts.UpdateAsync(new UpdateAccountCommand(id, r.Name, r.Type, r.Number, r.ParentId, r.Description, r.DonorRestricted), ct);
            return AccountResponse.From(account, await accounts.GetChartAsync(ct));
        });

        api.MapPost("/accounts/{id:guid}/deactivate", async (Guid id, AccountService accounts, CancellationToken ct) =>
            AccountResponse.From(await accounts.DeactivateAsync(id, ct), await accounts.GetChartAsync(ct)));

        api.MapPost("/accounts/{id:guid}/reactivate", async (Guid id, AccountService accounts, CancellationToken ct) =>
            AccountResponse.From(await accounts.ReactivateAsync(id, ct), await accounts.GetChartAsync(ct)));

        // Body is the CSV itself (text/csv). With dryRun=true nothing is saved.
        api.MapPost("/accounts/import", async (HttpRequest request, bool? dryRun, ChartImportService import, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var csv = await reader.ReadToEndAsync(ct);
            return dryRun == true
                ? ChartImportResponse.From(await import.PreviewAsync(csv, ct), imported: false)
                : ChartImportResponse.From(await import.ImportAsync(csv, ct), imported: true);
        }).Accepts<string>("text/csv");

        api.MapGet("/journal-entries", async (DateOnly? from, DateOnly? to, int? skip, int? take, PostingEngine engine, CancellationToken ct) =>
        {
            var filter = new JournalEntryFilter(from, to, skip ?? 0, take ?? 50);
            var (entries, total) = await engine.ListAsync(filter, ct);
            return new JournalEntryPageResponse(total, filter.Skip, filter.Take, entries.Select(JournalEntryResponse.From).ToList());
        });

        api.MapPost("/journal-entries", async (PostJournalEntryRequest request, PostingEngine engine, CancellationToken ct) =>
        {
            var lines = (request.Lines ?? []).Select(l => new JournalLineDraft(l.AccountId, l.Debit, l.Credit, l.Memo, l.ClassId)).ToList();
            var entry = await engine.PostAsync(new PostJournalEntryCommand(request.Date, lines, request.Memo, Basis: request.Basis, ReverseOn: request.ReverseOn,
                LocationId: request.LocationId, TagIds: request.TagIds), ct);
            return Results.Created($"/api/journal-entries/{entry.Id}", JournalEntryResponse.From(entry));
        });

        api.MapGet("/journal-entries/{id:guid}", async (Guid id, PostingEngine engine, CancellationToken ct) =>
            await engine.GetAsync(id, ct) is { } entry ? Results.Ok(JournalEntryResponse.From(entry)) : Results.NotFound());

        // Correct a hand-entered entry: it's voided and the replacement posted in one save (the response is the replacement).
        api.MapPut("/journal-entries/{id:guid}", async (Guid id, ChangeJournalEntryRequest request, PostingEngine engine,
            OpenBook.Application.Reporting.AdjustingEntryService adjusting, CancellationToken ct) =>
        {
            var lines = (request.Lines ?? []).Select(l => new JournalLineDraft(l.AccountId, l.Debit, l.Credit, l.Memo, l.ClassId)).ToList();
            var wasAdjusting = (await adjusting.MarkedAsync(ct)).Contains(id);
            var changed = await engine.ChangeAsync(id, new PostJournalEntryCommand(request.Date, lines, request.Memo, Basis: request.Basis,
                LocationId: request.LocationId, TagIds: request.TagIds), request.Reason, ct);
            // The replacement stays an adjusting entry, as the change page keeps it.
            if (wasAdjusting)
                await adjusting.SetAsync(changed.Id, true, ct);
            return JournalEntryResponse.From(changed);
        });
        api.MapPost("/journal-entries/{id:guid}/void", async (Guid id, VoidJournalEntryRequest request, PostingEngine engine, CancellationToken ct) =>
            JournalEntryResponse.From(await engine.VoidAsync(id, request.Reason, ct)));

        // Undo an import (journal entry CSV or move-in file): the batch id is the imported entries' sourceId.
        api.MapPost("/journal-entries/imports/{batchId:guid}/void", async (Guid batchId, VoidJournalEntryRequest request,
            OpenBook.Application.Import.JournalImportService imports, CancellationToken ct) =>
            new { Voided = await imports.VoidBatchAsync(batchId, request.Reason, ct) });

        api.MapGet("/reports/trial-balance", async (DateOnly? asOf, ReportBasis? basis, TrialBalanceQuery query, CancellationToken ct) =>
            TrialBalanceResponse.From(await query.RunAsync(asOf ?? CompanyTime.Today, basis, ct)));

        return app;
    }

    private static async ValueTask<object?> MapDomainErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Changes dated in a closed period need the closing date password, sent as a header.
        if (context.HttpContext.Request.Headers.TryGetValue("X-Closing-Password", out var closingPassword))
            context.HttpContext.RequestServices.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword.ToString();

        // Endpoints that read the raw body themselves (file uploads) can read it again on a retry.
        var request = context.HttpContext.Request;
        if (!request.HasJsonContentType())
            request.EnableBuffering();
        var work = context.HttpContext.RequestServices.GetRequiredService<IUnitOfWork>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await next(context);
            }
            catch (ConcurrencyConflictException ex) when (ConflictRetry.ShouldRetry(ex, attempt, work) && !context.HttpContext.Response.HasStarted)
            {
                work.DiscardPending();
                if (request.Body.CanSeek)
                    request.Body.Position = 0;
            }
            catch (DomainException ex)
            {
                return Results.Problem(
                    title: ex.Message,
                    statusCode: ex switch
                    {
                        EntityNotFoundException => StatusCodes.Status404NotFound,
                        ConcurrencyConflictException => StatusCodes.Status409Conflict,
                        _ => StatusCodes.Status400BadRequest,
                    },
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
            }
            catch (ArgumentNullException ex) when (ex.ParamName is { } name && !context.HttpContext.Response.HasStarted)
            {
                // A body without a list the service needs ("lines", "applications"): the caller's mistake, not the server's.
                var field = name[(name.LastIndexOf('.') + 1)..];
                return Results.Problem(
                    title: $"The request is missing '{char.ToLowerInvariant(field[0])}{field[1..]}'.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "request.value_missing" });
            }
        }
    }
}
