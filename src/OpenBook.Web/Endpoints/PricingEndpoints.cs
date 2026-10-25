using System.Globalization;
using OpenBook.Application.Pricing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Form posts from the pricing pages that are grids (a level's item prices, an item's quantity breaks), file uploads
/// or company settings. Each binds the whole form, so the antiforgery token is validated automatically.
/// </summary>
public static class PricingEndpoints
{
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: p:{itemId} for each item; blank means the item keeps its own price.
        app.MapPost("/pricing/levels/{id:guid}/prices", async (Guid id, IFormCollection form, PricingService service, CancellationToken ct) =>
        {
            var prices = new List<(Guid, decimal)>();
            foreach (var (field, values) in form)
            {
                var text = values.ToString().Trim();
                if (!field.StartsWith("p:", StringComparison.Ordinal) || text.Length == 0 || !Guid.TryParse(field[2..], out var itemId))
                    continue;
                if (!TryParse(text, out var price))
                    return Back($"/pricing/levels/{id}", $"'{text}' isn't a price.");
                prices.Add((itemId, price));
            }

            return await Run($"/pricing/levels/{id}", () => service.SetLevelPricesAsync(id, prices, ct));
        }).RequireAuthorization();

        app.MapPost("/pricing/levels/{id:guid}/import", async (Guid id, IFormCollection form, PricingService service, CancellationToken ct) =>
        {
            var csv = form["csv"].ToString();
            if (form.Files.GetFile("file") is { Length: > 0 } file)
            {
                using var reader = new StreamReader(file.OpenReadStream());
                csv = await reader.ReadToEndAsync(ct);
            }

            var result = await service.ImportLevelPricesAsync(id, csv, ct);
            return result.Errors.Count > 0
                ? Back($"/pricing/levels/{id}", string.Join(" ", result.Errors.Take(5)))
                : Results.Redirect($"/pricing/levels/{id}?saved={result.Imported}");
        }).RequireAuthorization();

        // Fields: from{n}, kind{n}, value{n}; rows with no quantity are dropped.
        app.MapPost("/pricing/items/{id:guid}/breaks", async (Guid id, IFormCollection form, PricingService service, CancellationToken ct) =>
        {
            var breaks = new List<(decimal, QuantityBreakKind, decimal)>();
            for (var n = 0; n < 50; n++)
            {
                var from = form[$"from{n}"].ToString().Trim();
                var value = form[$"value{n}"].ToString().Trim();
                if (from.Length == 0 && value.Length == 0)
                    continue;
                if (!TryParse(from, out var quantity) || !TryParse(value, out var amount))
                    return Back($"/items/{id}", "Enter a starting quantity and a value for each quantity break.");
                var kind = Enum.TryParse<QuantityBreakKind>(form[$"kind{n}"], out var k) ? k : QuantityBreakKind.PercentOff;
                breaks.Add((quantity, kind, amount));
            }

            return await Run($"/items/{id}", () => service.SetQuantityBreaksAsync(id, breaks, ct));
        }).RequireAuthorization();

        app.MapPost("/pricing/rounding", async (IFormCollection form, PricingService service, CancellationToken ct) =>
        {
            if (!Enum.TryParse<PriceRounding>(form["rounding"], out var rounding))
                return Back("/pricing", "Choose how prices are rounded.");
            return await Run("/pricing", () => service.SetRoundingAsync(rounding, ct));
        }).RequireAuthorization();

        return app;
    }

    private static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text.Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal),
            NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static async Task<IResult> Run(string back, Func<Task> action)
    {
        try
        {
            await action();
            return Results.Redirect($"{back}?saved=1");
        }
        catch (DomainException ex)
        {
            return Back(back, ex.Message);
        }
    }

    private static IResult Back(string path, string error) => Results.Redirect($"{path}?error={Uri.EscapeDataString(error)}");
}
