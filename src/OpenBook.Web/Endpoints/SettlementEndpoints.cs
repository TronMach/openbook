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
    public const long MaxFileBytes = 5L * 1024 * 1024;

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
            var current = await settlements.GetCarrierAsync(id, ct);
            var forget = new List<(SettlementSection, string)>();
            for (var i = 0; form.ContainsKey($"code:{i}"); i++)
            {
                if (!Enum.TryParse<SettlementSection>(form[$"section:{i}"], out var section))
                    continue;
                var code = form[$"code:{i}"].ToString().Trim();
                if (form[$"forget:{i}"] == "true")
                    forget.Add((section, code));
                else if (code.Length > 0 && Guid.TryParse(form[$"account:{i}"], out var account))
                    codes.Add(new CarrierCodeDraft(section, code, account, form[$"name:{i}"].ToString(), Id(form, $"class:{i}"),
                        Enum.TryParse<LoadCharge>(form[$"charge:{i}"], out var charge) ? charge : form.ContainsKey($"charge:{i}") ? null : Kept(current, section, code)));
            }

            return await Back(back, () => settlements.SetCodesAsync(id, codes, forget, ct));
        }).RequireAuthorization();

        // Form-bound (the file), so the page's antiforgery token is checked automatically.
        app.MapPost("/settlements/import", async (IFormFile? file, [Microsoft.AspNetCore.Mvc.FromForm] Guid carrierId, [Microsoft.AspNetCore.Mvc.FromForm] DateOnly? date, SettlementService settlements, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.Redirect($"/settlements?error={Uri.EscapeDataString("Choose the CSV file to import.")}#import");
            if (file.Length > MaxFileBytes)
                return Results.Redirect($"/settlements?error={Uri.EscapeDataString("The file is larger than 5 MB.")}#import");
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory, ct);
            try
            {
                var result = await settlements.ImportAsync(carrierId, memory.ToArray(), date ?? CompanyTime.Today, save: true, ct);
                var made = result.Settlements.Where(s => s.Id is not null).ToList();
                var note = result.Warnings.Count > 0 ? $"note={Uri.EscapeDataString(string.Join(" ", result.Warnings))}" : "";
                return made.Count == 1
                    ? Results.Redirect($"/settlements/{made[0].Id}?{note}")
                    : Results.Redirect($"/settlements?imported={made.Count}&skipped={result.Settlements.Count - made.Count}&{note}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/settlements?error={Uri.EscapeDataString(ex.Message)}#import");
            }
        }).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFileBytes * 2));

        // The owner's share of each kind of charge: share:{charge}, blank for one that isn't checked.
        app.MapPost("/settlements/carriers/{id:guid}/shares", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/carriers/{id}", () =>
            {
                var shares = new List<(LoadCharge, decimal)>();
                foreach (var charge in Enum.GetValues<LoadCharge>())
                {
                    var text = form[$"share:{charge}"].ToString().Trim().TrimEnd('%');
                    if (text.Length == 0)
                        continue;
                    shares.Add((charge, decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var percent) ? percent
                        : throw new DomainException("settlement.percent_range", $"{LoadCharges.Label(charge)}: type the share as a percentage, like 72.")));
                }

                return settlements.SetSharesAsync(id, shares, ct);
            })).RequireAuthorization();

        app.MapPost("/settlements/{id:guid}/post", async (IFormCollection _, Guid id, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () => settlements.PostAsync(id, ct))).RequireAuthorization();

        // A scan checked against its statement: ticked lines (line:n), the net typed from it, and that all of it was looked at.
        app.MapPost("/settlements/{id:guid}/confirm", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () =>
            {
                if (!OpenBook.Application.Import.StatementValues.TryParseAmount(form["netPaid"], out var net))
                    throw new DomainException("settlement.net_unreadable", "Type the net paid from the statement.");
                var lines = form.Keys.Where(k => k.StartsWith("line:", StringComparison.Ordinal) && form[k] == "true")
                    .Select(k => int.TryParse(k[5..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0).ToList();
                return settlements.ConfirmAsync(id, net, lines, form["all"] == "true", ct);
            })).RequireAuthorization();

        app.MapPost("/settlements/{id:guid}/void", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () => settlements.VoidAsync(id, form["reason"].ToString().Trim() is { Length: > 0 } r ? r : "Settlement voided", ct))).RequireAuthorization();

        app.MapPost("/settlements/{id:guid}/dismiss", async (Guid id, IFormCollection form, SettlementService settlements, CancellationToken ct) =>
            await Back($"/settlements/{id}", () => settlements.SetDismissedAsync(id, form["restore"] != "true", ct))).RequireAuthorization();

        return app;
    }


    private static Guid? Id(IFormCollection form, string name) => Guid.TryParse(form[name], out var id) ? id : null;

    /// <summary>A form that doesn't ask about a code's charge leaves it as it is.</summary>
    private static LoadCharge? Kept(Carrier? carrier, SettlementSection section, string code) => carrier?.CodeFor(section, code)?.Charge;

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
