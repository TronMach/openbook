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

        return app;
    }
}
