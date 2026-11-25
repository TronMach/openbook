using System.Globalization;
using OpenBook.Application;
using OpenBook.Application.Projects;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Form posts for expense claims (C345). Handing a claim in or changing it is under /time/claims (standard); approving
/// and returning are under /time/review/claims (accountant).
/// </summary>
public static class ExpenseClaimEndpoints
{
    public static IEndpointRouteBuilder MapExpenseClaimEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/time/claims", async (IFormCollection form, ExpenseClaimService claims, CancellationToken ct) =>
        {
            try
            {
                var claim = await claims.SubmitAsync(Id(form["workerId"]), Text(form["memo"]), Lines(form), ct);
                return Results.Redirect($"/time/claims/{claim.Id}?submitted=true");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/claims/new?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/claims/{id:guid}", async (Guid id, IFormCollection form, ExpenseClaimService claims, IRecordVersions versions, CancellationToken ct) =>
        {
            try
            {
                await versions.EnsureUnchangedAsync<ExpenseClaim>(id, Version(form), ct);
                await claims.ChangeAsync(id, Text(form["memo"]), Lines(form), ct);
                return Results.Redirect($"/time/claims/{id}?submitted=true");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/claims/{id}/edit?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/review/claims/{id:guid}/approve", async (Guid id, IFormCollection form, ExpenseClaimService claims, IRecordVersions versions,
            OpenBook.Application.Ledger.ClosedPeriodOverride closing, CancellationToken ct) =>
        {
            closing.Password = form["ClosingPassword"].ToString() is { Length: > 0 } p ? p : null;
            try
            {
                await versions.EnsureUnchangedAsync<ExpenseClaim>(id, Version(form), ct);
                var bill = await claims.ApproveAsync(id, Date(form["billDate"]) ?? CompanyTime.Today, Text(form["note"]), ct);
                return Results.Redirect($"/purchases/bills/{bill.Id}");
            }
            catch (DomainException ex)
            {
                var password = OpenBook.Web.Security.ClosedPeriod.NeedsPassword(ex) ? "&password=true" : "";
                return Results.Redirect($"/time/claims/{id}?error={Uri.EscapeDataString(ex.Message)}{password}");
            }
        }).RequireAuthorization();

        app.MapPost("/time/review/claims/{id:guid}/return", async (Guid id, IFormCollection form, ExpenseClaimService claims, IRecordVersions versions, CancellationToken ct) =>
        {
            try
            {
                await versions.EnsureUnchangedAsync<ExpenseClaim>(id, Version(form), ct);
                await claims.ReturnAsync(id, form["note"].ToString(), ct);
                return Results.Redirect($"/time/claims/{id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/time/claims/{id}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Rows r = 0, 1, … with date:r, account:r, desc:r, amount:r and customer:r; a row with nothing typed in it is left
    /// out, and one with something but no date takes today.
    /// </summary>
    internal static IReadOnlyList<ExpenseClaimLineDraft> Lines(IFormCollection form)
    {
        var lines = new List<ExpenseClaimLineDraft>();
        for (var r = 0; r < ExpenseClaim.MaxLines + 20 && form.ContainsKey($"amount:{r}"); r++)
        {
            var amountText = form[$"amount:{r}"].ToString();
            var description = Text(form[$"desc:{r}"]);
            var account = Id(form[$"account:{r}"]);
            if (string.IsNullOrWhiteSpace(amountText) && description is null)
                continue;
            var amount = FormNumbers.Amount(amountText)
                ?? throw new DomainException("expense_claim.amount", $"Line {lines.Count + 1}: enter the amount.");
            lines.Add(new ExpenseClaimLineDraft(Date(form[$"date:{r}"]) ?? CompanyTime.Today, account ?? Guid.Empty, description, amount, Id(form[$"customer:{r}"])));
        }

        return lines;
    }

    /// <summary>The claim's version when the page was opened.</summary>
    private static int? Version(IFormCollection form) =>
        int.TryParse(form["version"], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
