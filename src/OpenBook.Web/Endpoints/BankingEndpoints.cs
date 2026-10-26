using System.Globalization;
using Microsoft.Net.Http.Headers;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the bank connection, positive pay and petty cash pages (antiforgery-checked, form-bound).</summary>
public static class BankingEndpoints
{
    public static IEndpointRouteBuilder MapBankingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/banking/connections/connect", async (IFormCollection form, BankConnectionService connections, CancellationToken ct) =>
        {
            try
            {
                var connection = await connections.ConnectAsync(BankConnectionProvider.SimpleFin, form["setupToken"].ToString(), form["name"], TimeZoneInfo.Local, ct);
                return Results.Redirect($"/banking/connections/{connection.Id}?done=connected");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/banking/connections?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/banking/connections/{id:guid}/sync", async (Guid id, IFormCollection form, BankConnectionService connections, CancellationToken ct) =>
            await Back($"/banking/connections/{id}", async () =>
            {
                var result = await connections.SyncAsync(id, TimeZoneInfo.Local, ct);
                return result.Succeeded
                    ? FormattableString.Invariant($"done=synced&queued={result.Queued}&added={result.AutoAdded}&skipped={result.Skipped}")
                    : $"error={Uri.EscapeDataString(result.Error!)}";
            })).RequireAuthorization();

        app.MapPost("/banking/connections/{id:guid}/link", async (Guid id, IFormCollection form, BankConnectionService connections, CancellationToken ct) =>
            await Back($"/banking/connections/{id}", async () =>
            {
                await connections.LinkAsync(id, form["remoteId"].ToString(), Id(form["accountId"]), Date(form["importFrom"]), ct);
                return "done=linked";
            })).RequireAuthorization();

        app.MapPost("/banking/connections/{id:guid}/rename", async (Guid id, IFormCollection form, BankConnectionService connections, CancellationToken ct) =>
            await Back($"/banking/connections/{id}", async () =>
            {
                await connections.RenameAsync(id, form["name"].ToString(), ct);
                return "done=saved";
            })).RequireAuthorization();

        app.MapPost("/banking/connections/{id:guid}/disconnect", async (Guid id, IFormCollection form, BankConnectionService connections, CancellationToken ct) =>
            await Back($"/banking/connections/{id}", async () =>
            {
                await connections.DisconnectAsync(id, ct);
                return "done=disconnected";
            })).RequireAuthorization();

        app.MapPost("/banking/positive-pay/export", async (IFormCollection form, HttpContext http, PositivePayService service, CancellationToken ct) =>
        {
            var accountId = Id(form["accountId"]);
            var from = Date(form["from"]);
            var to = Date(form["to"]);
            var back = $"/banking/positive-pay?accountId={accountId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            try
            {
                if (accountId is null || from is null || to is null)
                    throw new DomainException("positive_pay.fields_required", "Choose the account and both dates.");
                var options = new PositivePayOptions(form["bankAccountNumber"],
                    Enum.TryParse<PositivePayDateFormat>(form["dateFormat"], out var format) ? format : PositivePayDateFormat.MonthDayYear,
                    form["includeHeader"] == "true");
                var file = await service.ExportAsync(accountId.Value, from.Value, to.Value, options, ct);
                http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = file.FileName }.ToString();
                return Results.File(file.Content, "text/csv");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/banking/petty-cash/new", async (IFormCollection form, PettyCashService service, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var fund = await service.CreateAsync(new CreatePettyCashFundCommand(
                    Id(form["accountId"]), form["newAccountName"], Amount(form["float"]) ?? 0m, form["custodian"], Id(form["fundFromAccountId"]), Date(form["date"])), ct);
                return Results.Redirect($"/banking/petty-cash/{fund.Id}?done=created");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
                return Results.Redirect($"/banking/petty-cash?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/banking/petty-cash/{id:guid}/count", async (Guid id, IFormCollection form, PettyCashService service, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/banking/petty-cash/{id}", async () =>
            {
                var counted = Amount(form["counted"]) ?? throw new DomainException("petty_cash.count_required", "Enter the amount counted.");
                var count = await service.CountAsync(id, new CountCashCommand(Date(form["date"]) ?? Today(), counted, form["breakdown"], form["note"]), ct);
                return FormattableString.Invariant($"done=counted&difference={count.Difference:0.00}");
            });
        }).RequireAuthorization();

        app.MapPost("/banking/petty-cash/{id:guid}/replenish", async (Guid id, IFormCollection form, PettyCashService service, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            return await Back($"/banking/petty-cash/{id}", async () =>
            {
                var from = Id(form["fromAccountId"]) ?? throw new DomainException("petty_cash.source_required", "Choose the bank account to replenish from.");
                var transfer = await service.ReplenishAsync(id, from, Date(form["date"]) ?? Today(), ct);
                return FormattableString.Invariant($"done=replenished&amount={transfer.Amount:0.00}");
            });
        }).RequireAuthorization();

        app.MapPost("/banking/petty-cash/{id:guid}/edit", async (Guid id, IFormCollection form, PettyCashService service, CancellationToken ct) =>
            await Back($"/banking/petty-cash/{id}", async () =>
            {
                await service.UpdateAsync(id, Amount(form["float"]) ?? 0m, form["custodian"], form["active"] == "true", ct);
                return "done=saved";
            })).RequireAuthorization();

        return app;
    }

    /// <summary>Runs the change and goes back to the page with its outcome, or the refusal (asking for the closing password when that would help).</summary>
    private static async Task<IResult> Back(string page, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}");
        }
        catch (DomainException ex)
        {
            var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}{password}");
        }
    }

    private static string? Password(IFormCollection form) => form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Amount(string? value) =>
        StatementValues.TryParseAmount(value, out var amount) ? amount : null;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
}
