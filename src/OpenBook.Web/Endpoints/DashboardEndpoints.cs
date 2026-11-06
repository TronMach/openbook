using System.Globalization;
using OpenBook.Application.Reporting;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;
using OpenBook.Web.Components.Pages.Reports;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts and downloads from the dashboard, scheduled reports, cash-flow forecast and accountant pages (antiforgery-checked).</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dashboard/panels", async (IFormCollection form, DashboardService preferences, CancellationToken ct) =>
        {
            var shown = form["show"].Select(v => v ?? "").ToHashSet(StringComparer.Ordinal);
            await preferences.SetHiddenAsync(Dashboard.Panels.Select(p => p.Key).Where(k => !shown.Contains(k)), ct);
            return Results.Redirect("/dashboard");
        }).RequireAuthorization();

        app.MapPost("/reports/schedules", async (IFormCollection form, ReportSubscriptionService subscriptions, CancellationToken ct) =>
            await Back(async () =>
            {
                var frequency = Enum.TryParse<ReportFrequency>(form["frequency"], out var f) ? f : ReportFrequency.Monthly;
                await subscriptions.CreateAsync(new ReportSubscriptionDetails(
                    Guid.TryParse(form["savedReportId"], out var id) ? id : Guid.Empty,
                    form["recipients"].ToString(),
                    frequency,
                    Number(frequency == ReportFrequency.Weekly ? form["weekday"] : form["monthday"]),
                    Number(form["hour"]),
                    Enum.TryParse<ReportFileFormat>(form["format"], out var format) ? format : ReportFileFormat.Pdf), ct);
                return "Scheduled.";
            })).RequireAuthorization();

        app.MapPost("/reports/schedules/{id:guid}/send", async (IFormCollection _, Guid id, ReportSubscriptionService subscriptions, CancellationToken ct) =>
            await Back(async () =>
            {
                var run = await subscriptions.SendNowAsync(id, ct);
                return run.Error is { } error ? throw new DomainException("subscription.send_failed", error) : $"Sent to {run.Subscription.Recipients}.";
            })).RequireAuthorization();

        app.MapPost("/reports/schedules/{id:guid}/delete", async (IFormCollection _, Guid id, ReportSubscriptionService subscriptions, CancellationToken ct) =>
            await Back(async () =>
            {
                await subscriptions.DeleteAsync(id, ct);
                return "Deleted.";
            })).RequireAuthorization();

        app.MapPost("/reports/cash-forecast/scenarios", async (IFormCollection form, ForecastService forecasts, CancellationToken ct) =>
            await Forecast(async () => (await forecasts.CreateScenarioAsync(Scenario(form), ct)).Id)).RequireAuthorization();

        app.MapPost("/reports/cash-forecast/scenarios/{id:guid}", async (Guid id, IFormCollection form, ForecastService forecasts, CancellationToken ct) =>
            await Forecast(async () => (await forecasts.UpdateScenarioAsync(id, Scenario(form), ct)).Id)).RequireAuthorization();

        app.MapPost("/reports/cash-forecast/scenarios/{id:guid}/items", async (Guid id, IFormCollection form, ForecastService forecasts, CancellationToken ct) =>
            await Forecast(async () => (await forecasts.AddItemAsync(id, new ScenarioItemDetails(
                form["description"].ToString(),
                decimal.TryParse(form["amount"].ToString().Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                    ? amount : throw new DomainException("scenario.amount_zero", "Enter an amount: positive for money in, negative for money out."),
                Date(form["startDate"]) ?? CompanyTime.Today,
                Enum.TryParse<ScenarioRecurrence>(form["recurrence"], out var recurrence) ? recurrence : ScenarioRecurrence.Once,
                Date(form["endDate"])), ct)).Id)).RequireAuthorization();

        app.MapPost("/reports/cash-forecast/scenarios/{id:guid}/items/{itemId:guid}/remove", async (IFormCollection _, Guid id, Guid itemId, ForecastService forecasts, CancellationToken ct) =>
            await Forecast(async () => (await forecasts.RemoveItemAsync(id, itemId, ct)).Id)).RequireAuthorization();

        app.MapPost("/reports/cash-forecast/scenarios/{id:guid}/delete", async (IFormCollection _, Guid id, ForecastService forecasts, CancellationToken ct) =>
            await Forecast(async () =>
            {
                await forecasts.DeleteScenarioAsync(id, ct);
                return (Guid?)null;
            })).RequireAuthorization();

        app.MapPost("/journal/{id:guid}/adjusting", async (Guid id, IFormCollection form, AdjustingEntryService adjusting, CancellationToken ct) =>
        {
            try
            {
                await adjusting.SetAsync(id, form["adjusting"] == "true", ct);
                return Results.Redirect($"/journal/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/journal/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapGet("/accountant/package", async (DateOnly? from, DateOnly? to, AccountantPackageService packages, CancellationToken ct) =>
        {
            try
            {
                var end = to ?? CompanyTime.Today;
                var file = await packages.BuildAsync(from ?? new DateOnly(end.Year, 1, 1), end, ct);
                var bytes = await File.ReadAllBytesAsync(file.Path, ct);
                File.Delete(file.Path);
                return Results.File(bytes, "application/zip", file.FileName);
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/accountant?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapGet("/accountant/1099", async (int? year, decimal? threshold, AccountantPackageService packages, CancellationToken ct) =>
        {
            var y = year ?? CompanyTime.Today.Year - 1;
            try
            {
                return Results.File(await packages.Filing1099CsvAsync(y, audit: true, threshold: threshold, ct: ct), "text/csv", $"1099 filing data {y}.csv");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/accountant?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        return app;
    }

    private static ForecastScenarioDetails Scenario(IFormCollection form) =>
        new(form["name"].ToString(), Number(form["collectionDelay"]) is var c and >= -90 ? c : 0, Number(form["paymentDelay"]) is var d and >= -90 ? d : 0);

    private static async Task<IResult> Forecast(Func<Task<Guid?>> change)
    {
        try
        {
            var id = await change();
            return Results.Redirect(id is { } scenario ? $"/reports/cash-forecast?scenarioId={scenario}" : "/reports/cash-forecast");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/reports/cash-forecast?error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static async Task<IResult> Back(Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"/reports/schedules?done={Uri.EscapeDataString(await change())}");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/reports/schedules?error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static int Number(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
}
