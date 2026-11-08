using System.Globalization;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the economic nexus thresholds page (antiforgery-checked, form-bound).</summary>
public static class NexusEndpoints
{
    public static IEndpointRouteBuilder MapNexusEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: region, amount, transactions (optional).
        app.MapPost("/sales-tax/nexus", async (IFormCollection form, NexusService nexus, CancellationToken ct) =>
        {
            try
            {
                var transactions = form["transactions"].ToString().Trim() is { Length: > 0 } text
                    ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new DomainException("nexus.transactions_invalid", "The number of transactions is a whole number, or blank.")
                    : (int?)null;
                await nexus.SaveAsync(form["region"].ToString(), FormNumbers.Amount(form["amount"]) ?? 0m, transactions, ct);
                return Results.Redirect("/sales-tax/nexus");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/sales-tax/nexus?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/sales-tax/nexus/remove", async (IFormCollection form, NexusService nexus, CancellationToken ct) =>
        {
            try
            {
                await nexus.RemoveAsync(form["region"].ToString(), ct);
                return Results.Redirect("/sales-tax/nexus");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/sales-tax/nexus?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // A rate's percentage from a date on: effectiveFrom, percent; remove=true takes the change on that date away.
        app.MapPost("/sales-tax/rates/{id:guid}/changes", async (Guid id, IFormCollection form, SalesTaxService taxes, CancellationToken ct) =>
        {
            try
            {
                var from = DateOnly.TryParseExact(form["effectiveFrom"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d : throw new DomainException("tax.rate_change_date", "Choose the date the new rate starts.");
                var percent = form["remove"] == "true" ? (decimal?)null
                    : FormNumbers.Number(form["percent"]) ?? throw new DomainException("tax.rate_change_percent", "Enter the rate from that date.");
                await taxes.SetRateChangeAsync(id, from, percent, ct);
                return Results.Redirect($"/sales-tax/rates/{id}#changes");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/sales-tax/rates/{id}?changeError={Uri.EscapeDataString(ex.Message)}#changes");
            }
        }).RequireAuthorization();

        return app;
    }
}
