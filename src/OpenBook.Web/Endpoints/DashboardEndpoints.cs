using System.Globalization;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;
using OpenBook.Web.Components.Pages.Reports;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the dashboard and the scheduled reports page (antiforgery-checked, form-bound).</summary>
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

        app.MapPost("/reports/schedules/{id:guid}/send", async (Guid id, ReportSubscriptionService subscriptions, CancellationToken ct) =>
            await Back(async () =>
            {
                var run = await subscriptions.SendNowAsync(id, ct);
                return run.Error is { } error ? throw new DomainException("subscription.send_failed", error) : $"Sent to {run.Subscription.Recipients}.";
            })).RequireAuthorization();

        app.MapPost("/reports/schedules/{id:guid}/delete", async (Guid id, ReportSubscriptionService subscriptions, CancellationToken ct) =>
            await Back(async () =>
            {
                await subscriptions.DeleteAsync(id, ct);
                return "Deleted.";
            })).RequireAuthorization();

        return app;
    }

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
