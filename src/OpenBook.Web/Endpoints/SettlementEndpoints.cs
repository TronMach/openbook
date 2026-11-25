using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;
using OpenBook.Web.Components;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts for carriers and their settlements.</summary>
public static class SettlementEndpoints
{
    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/settlements/carriers", async (IFormCollection form, SettlementService settlements, CancellationToken ct) =>
        {
            try
            {
                var carrier = await settlements.CreateCarrierAsync(Details(form), ct);
                return Results.Redirect($"/settlements/carriers/{carrier.Id}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/settlements/carriers?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // The trucking accounts the chart doesn't have.
        app.MapPost("/settlements/carriers/accounts", async (IFormCollection _, ChartTemplates templates, CancellationToken ct) =>
        {
            try
            {
                var result = await templates.AddTruckingAsync(ct);
                return Results.Redirect($"/settlements/carriers?added={result.NewCount}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/settlements/carriers?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/settlements/carriers/{id:guid}", async (Guid id, IFormCollection form, SettlementService settlements, IRecordVersions versions, CancellationToken ct) =>
            await Back($"/settlements/carriers/{id}", async () =>
            {
                await SettingsVersions.EnsureUnchangedAsync<Carrier>(versions, id, form, ct);
                await settlements.UpdateCarrierAsync(id, Details(form), ct);
                await settlements.SetCarrierActiveAsync(id, form["active"] == "true", ct);
            })).RequireAuthorization();

        // Codes by row: section:i, code:i, name:i, account:i and class:i; a ticked forget:i drops one, a row with no account stays unassigned.
        app.MapPost("/settlements/carriers/{id:guid}/codes", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
        {
            var back = AccessRules.LocalPath(form["back"]) ?? $"/settlements/carriers/{id}";
            var codes = new List<CarrierCodeDraft>();
            var forget = new List<(SettlementSection, string)>();
            for (var i = 0; form.ContainsKey($"code:{i}"); i++)
            {
                if (!Enum.TryParse<SettlementSection>(form[$"section:{i}"], out var section))
                    continue;
                var code = form[$"code:{i}"].ToString().Trim();
                if (form[$"forget:{i}"] == "true")
                    forget.Add((section, code));
                else if (code.Length > 0 && Guid.TryParse(form[$"account:{i}"], out var account))
                    codes.Add(new CarrierCodeDraft(section, code, account, form[$"name:{i}"].ToString(), Id(form, $"class:{i}")));
            }

            return await Back(back, () => settlements.SetCodesAsync(id, codes, forget, ct));
        }).RequireAuthorization();

        app.MapPost("/settlements/{id:guid}/post", async (IFormCollection _, Guid id, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () => settlements.PostAsync(id, ct))).RequireAuthorization();

        app.MapPost("/settlements/{id:guid}/void", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () => settlements.VoidAsync(id, form["reason"].ToString().Trim() is { Length: > 0 } r ? r : "Settlement voided", ct))).RequireAuthorization();

        app.MapPost("/settlements/{id:guid}/dismiss", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () => settlements.SetDismissedAsync(id, form["restore"] != "true", ct))).RequireAuthorization();

        return app;
    }


    private static Guid? Id(IFormCollection form, string name) => Guid.TryParse(form[name], out var id) ? id : null;

    private static CarrierDetails Details(IFormCollection form) => new(
        form["name"].ToString(), Id(form, "depositAccountId") ?? Guid.Empty, Id(form, "vendorId"), Id(form, "dueAccountId"), Id(form, "classId"), Id(form, "locationId"), form["unit"].ToString());

    private static async Task<IResult> Back(string page, Func<Task> change)
    {
        try
        {
            await change();
            return Results.Redirect(page);
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}{(page.Contains('?', StringComparison.Ordinal) ? '&' : '?')}error={Uri.EscapeDataString(ex.Message)}");
        }
    }
}
