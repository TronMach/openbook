using System.Globalization;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Segments;
using OpenBook.Domain.Common;
using OpenBook.Domain.Segments;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the nonprofit pages and a class's functional area (antiforgery-checked, form-bound).</summary>
public static class NonprofitEndpoints
{
    public static IEndpointRouteBuilder MapNonprofitEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: date, classId ("" = no class), amount, memo, ClosingPassword.
        app.MapPost("/nonprofit/releases", async (IFormCollection form, NonprofitService nonprofit, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            var date = Date(form["date"]) ?? DateOnly.FromDateTime(DateTime.Today);
            try
            {
                var release = await nonprofit.ReleaseAsync(new ReleaseCommand(
                    date,
                    Guid.TryParse(form["classId"], out var c) ? c : null,
                    Number(form["amount"]) ?? throw new DomainException("release.amount", "Enter an amount above zero to release."),
                    form["memo"]), ct);
                return Results.Redirect($"/nonprofit/releases/{release.Id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
                return Results.Redirect($"/nonprofit?asOf={date:yyyy-MM-dd}&error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/releases/{id:guid}/void", async (Guid id, IFormCollection form, NonprofitService nonprofit, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = Password(form);
            try
            {
                var reason = form["reason"].ToString().Trim() is { Length: > 0 } r ? r : throw new DomainException("void.reason_required", "Say why.");
                await nonprofit.VoidAsync(id, reason, ct);
                return Results.Redirect($"/nonprofit/releases/{id}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=1" : "";
                return Results.Redirect($"/nonprofit/releases/{id}?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/nonprofit/functional-classes", async (IFormCollection form, NonprofitService nonprofit, CancellationToken ct) =>
        {
            var changed = await nonprofit.SetUpFunctionalClassesAsync(ct);
            return Results.Redirect($"/nonprofit?classes={changed}");
        }).RequireAuthorization();

        // On a class's page; "" clears the area (the parent's applies).
        app.MapPost("/segments/{id:guid}/functional-area", async (Guid id, IFormCollection form, SegmentService segments, CancellationToken ct) =>
        {
            try
            {
                await segments.SetFunctionalAreaAsync(id, Enum.TryParse<FunctionalArea>(form["area"], out var area) ? area : null, ct);
                return Results.Redirect($"/segments/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/segments/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        return app;
    }

    private static string? Password(IFormCollection form) => form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Number(string? value) =>
        decimal.TryParse(value?.Replace(",", "", StringComparison.Ordinal).Replace("$", "", StringComparison.Ordinal).Trim(),
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}
