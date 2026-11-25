using System.Globalization;
using System.Security.Claims;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;
using OpenBook.Web.Components;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the sales commission pages (accountant; antiforgery-checked, form-bound).</summary>
public static class CommissionEndpoints
{
    public static IEndpointRouteBuilder MapCommissionEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: through, rep (one per ticked person), billDate, accountId.
        app.MapPost("/sales/commissions/runs", async (IFormCollection form, ClaimsPrincipal user, CommissionService commissions, ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            // A bill is a purchases record whichever page makes it.
            if (!AuthClaims.MayWrite(user, "purchases"))
                return Results.Redirect(AccessRules.DeniedUrl(user, "/purchases/bills/new", UserRole.Standard, "/sales/commissions"));
            closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;
            var through = Date(form["through"]) ?? CompanyTime.Today;
            var back = $"/sales/commissions?through={through:yyyy-MM-dd}";
            try
            {
                var reps = form["rep"].Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
                var runs = await commissions.RunAsync(new CommissionRunCommand(through, reps, Date(form["billDate"]), Id(form["accountId"])), ct);
                return Results.Redirect($"{back}&done={runs.Count}");
            }
            catch (DomainException ex)
            {
                var password = ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"{back}&error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales/commissions/settings", async (IFormCollection form, IRecordVersions versions, CommissionService commissions, CancellationToken ct) =>
            await Back(async () =>
            {
                await SettingsVersions.EnsureUnchangedAsync<CommissionSettings>(versions, form, ct);
                await commissions.SetBasisAsync(form["basis"] == nameof(CommissionBasis.Invoiced) ? CommissionBasis.Invoiced : CommissionBasis.Paid, ct);
            })).RequireAuthorization();

        app.MapPost("/sales/commissions/rates", async (IFormCollection form, CommissionService commissions, CancellationToken ct) =>
            await Back(async () => await commissions.AddRateAsync(new CommissionRateDetails(
                Id(form["workerId"]) ?? throw new DomainException("commission.rep_required", "Choose whose rate it is."),
                FormNumbers.Number(form["percent"]) ?? throw new DomainException("commission.percent_invalid", "Enter the commission rate as a percent."),
                form["category"], Date(form["startsOn"])), ct))).RequireAuthorization();

        app.MapPost("/sales/commissions/rates/{id:guid}/delete", async (IFormCollection _, Guid id, CommissionService commissions, CancellationToken ct) =>
            await Back(() => commissions.DeleteRateAsync(id, ct))).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Back(Func<Task> change)
    {
        try
        {
            await change();
            return Results.Redirect("/sales/commissions/settings?done=saved");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/sales/commissions/settings?error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
