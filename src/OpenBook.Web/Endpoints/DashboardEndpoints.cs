using System.Globalization;
using OpenBook.Application.Purchases;
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

        // Change a schedule in place: its recipients, timing and format, or paused and resumed (the saved report stays).
        app.MapPost("/reports/schedules/{id:guid}", async (Guid id, IFormCollection form, ReportSubscriptionService subscriptions, CancellationToken ct) =>
            await Back(async () =>
            {
                var current = (await subscriptions.ListAsync(ct)).FirstOrDefault(s => s.Id == id) ?? throw new EntityNotFoundException("report_subscription", id);
                var frequency = Enum.TryParse<ReportFrequency>(form["frequency"], out var f) ? f : current.Frequency;
                var day = (frequency == ReportFrequency.Weekly ? form["weekday"] : form["monthday"]).ToString();
                await subscriptions.UpdateAsync(id, new ReportSubscriptionDetails(
                    current.SavedReportId,
                    form.ContainsKey("recipients") ? form["recipients"].ToString() : current.Recipients,
                    frequency,
                    day.Length > 0 ? Number(day) : current.Day,
                    form.ContainsKey("hour") ? Number(form["hour"]) : current.Hour,
                    Enum.TryParse<ReportFileFormat>(form["format"], out var format) ? format : current.Format,
                    form.ContainsKey("active") ? form["active"] == "true" : current.IsActive), ct);
                return form.ContainsKey("active") && !form.ContainsKey("recipients") ? (form["active"] == "true" ? "Resumed." : "Paused.") : "Saved.";
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

        app.MapGet("/accountant/package", async (DateOnly? from, DateOnly? to, string? basis, AccountantPackageService packages, CancellationToken ct) =>
        {
            try
            {
                var end = to ?? CompanyTime.Today;
                var file = await packages.BuildAsync(from ?? new DateOnly(end.Year, 1, 1), end,
                    Enum.TryParse<OpenBook.Domain.Ledger.ReportBasis>(basis, out var chosen) ? chosen : null, ct);
                var bytes = await File.ReadAllBytesAsync(file.Path, ct);
                File.Delete(file.Path);
                return Results.File(bytes, "application/zip", file.FileName);
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/accountant?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapGet("/accountant/1099/copies", async (int year, decimal? threshold, Guid? vendor, Filing1099Service filing, CancellationToken ct) =>
        {
            try
            {
                var copy = await filing.CopiesAsync(year, threshold, vendor, ct);
                return Results.File(copy.Pdf, "application/pdf", copy.FileName);
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/reports/1099?year={year}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/accountant/1099/email-copies", async (IFormCollection form, Filing1099Service filing, CancellationToken ct) =>
        {
            var year = Year(form);
            try
            {
                var run = await filing.EmailCopiesAsync(year, FormNumbers.Amount(form["threshold"]), ct);
                var notes = $"{run.Sent} sent, {run.NoEmail.Count} with no email address, {run.Failed.Count} failed";
                return Results.Redirect($"/reports/1099?year={year}&note={Uri.EscapeDataString(notes)}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/reports/1099?year={year}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/accountant/1099/request-w9/{id:guid}", async (Guid id, IFormCollection form, Filing1099Service filing, CancellationToken ct) =>
        {
            var year = Year(form);
            try
            {
                await filing.RequestW9Async(id, ct);
                return Results.Redirect($"/reports/1099?year={year}&note={Uri.EscapeDataString("W-9 request sent.")}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/reports/1099?year={year}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/accountant/1099/filed", async (IFormCollection form, Filing1099Service filing, CancellationToken ct) =>
        {
            var year = Year(form);
            var on = DateOnly.TryParse(form["on"], CultureInfo.InvariantCulture, out var date) ? date : CompanyTime.Today;
            await filing.MarkFiledAsync(year, on, FormNumbers.Amount(form["threshold"]), ct);
            return Results.Redirect($"/reports/1099?year={year}");
        }).RequireAuthorization();

        app.MapPost("/accountant/1099/prior", async (IFormCollection form, Filing1099Service filing, CancellationToken ct) =>
        {
            var year = Year(form);
            try
            {
                if (!Guid.TryParse(form["vendorId"], out var vendorId))
                    throw new DomainException("prior1099.vendor_required", "Choose a vendor.");
                await filing.SetPriorAsync(vendorId, year, FormNumbers.Amount(form["amount"]), form["note"], ct);
                return Results.Redirect($"/reports/1099?year={year}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/reports/1099?year={year}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/accountant/1099/filed/clear", async (IFormCollection form, Filing1099Service filing, CancellationToken ct) =>
        {
            var year = Year(form);
            await filing.ClearFiledAsync(year, ct);
            return Results.Redirect($"/reports/1099?year={year}");
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

    private static int Year(IFormCollection form) =>
        int.TryParse(form["year"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : CompanyTime.Today.Year - 1;

    private static ForecastScenarioDetails Scenario(IFormCollection form) =>
        new(form["name"].ToString(), Delay(form["collectionDelay"]), Delay(form["paymentDelay"]), form["notes"].ToString());

    /// <summary>Blank is no delay; anything else goes to the scenario's own check.</summary>
    private static int Delay(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

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
