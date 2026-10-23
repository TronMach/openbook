using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Form posts from the static report pages: saving and deleting saved reports, the budget grid and the tax line
/// mapping. Each binds the whole form, so the antiforgery token is validated automatically.
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/reports/saved", async (IFormCollection form, SavedReportService service, CancellationToken ct) =>
        {
            var key = form["report"].ToString();
            var path = ReportCatalog.Find(key)?.Path ?? "/reports";
            var query = form["query"].ToString();
            var back = query.Length > 0 ? $"{path}?{query}&" : $"{path}?";
            try
            {
                var saved = await service.SaveAsync(form["name"].ToString(), key, query, ct);
                return Results.Redirect($"{back}saved={Uri.EscapeDataString(saved.Name)}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}saveError={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/reports/saved/{id:guid}/delete", async (Guid id, IFormCollection form, SavedReportService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.Redirect("/reports");
        }).RequireAuthorization();

        // Fields: name, a:{accountId}:{month} for each cell, and annual:{accountId}, which when filled replaces
        // the row with the amount spread evenly (any rounding cents in the last month).
        app.MapPost("/reports/budgets/{id:guid}", async (Guid id, IFormCollection form, BudgetService service, CancellationToken ct) =>
        {
            var amounts = new Dictionary<(Guid, int), decimal>();
            var bad = new List<string>();
            foreach (var (field, values) in form)
            {
                var parts = field.Split(':');
                var text = values.ToString().Trim();
                if (text.Length == 0 || parts.Length < 2 || !Guid.TryParse(parts[1], out var accountId))
                    continue;
                if (!StatementValues.TryParseAmount(text, out var amount))
                {
                    bad.Add(text);
                    continue;
                }

                if (parts[0] == "a" && parts.Length == 3 && int.TryParse(parts[2], out var month))
                    amounts.TryAdd((accountId, month), amount);
            }

            // Annual amounts win over the row's month cells, whatever order the fields arrive in.
            foreach (var (field, values) in form.Where(f => f.Key.StartsWith("annual:", StringComparison.Ordinal) && f.Value.ToString().Trim().Length > 0))
            {
                if (Guid.TryParse(field["annual:".Length..], out var accountId) && StatementValues.TryParseAmount(values.ToString(), out var amount))
                {
                    var each = decimal.Round(amount / Budget.Periods, 2, MidpointRounding.AwayFromZero);
                    for (var m = 1; m <= Budget.Periods; m++)
                        amounts[(accountId, m)] = m == Budget.Periods ? amount - each * (Budget.Periods - 1) : each;
                }
            }

            if (bad.Count > 0)
                return Results.Redirect($"/reports/budgets/{id}?error={Uri.EscapeDataString($"\"{bad[0]}\" isn't an amount. Nothing was saved.")}");
            try
            {
                await service.UpdateAsync(id, form["name"].ToString(), amounts.Select(kv => new BudgetAmount(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList(), ct);
                return Results.Redirect($"/reports/budgets/{id}?saved=true");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/reports/budgets/{id}?error={Uri.EscapeDataString(ex.Message + " Nothing was saved.")}");
            }
        }).RequireAuthorization();

        app.MapPost("/reports/budgets/{id:guid}/delete", async (Guid id, IFormCollection form, BudgetService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.Redirect("/reports/budgets");
        }).RequireAuthorization();

        // Fields: line:{accountId} = tax line key, or empty for none.
        app.MapPost("/reports/tax-lines", async (IFormCollection form, AccountService accounts, CancellationToken ct) =>
        {
            var map = new Dictionary<Guid, string?>();
            foreach (var (field, value) in form)
            {
                if (field.StartsWith("line:", StringComparison.Ordinal) && Guid.TryParse(field["line:".Length..], out var accountId))
                    map[accountId] = value.ToString();
            }

            try
            {
                var changed = await accounts.MapTaxLinesAsync(map, ct);
                return Results.Redirect($"/reports/tax-lines?saved={changed}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/reports/tax-lines?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        return app;
    }
}
