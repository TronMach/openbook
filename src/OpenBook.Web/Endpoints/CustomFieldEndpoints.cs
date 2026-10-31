using System.Globalization;
using OpenBook.Application.CustomFields;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Custom fields: values typed on a record's page, and the fields themselves (Settings).</summary>
public static class CustomFieldEndpoints
{
    public static IEndpointRouteBuilder MapCustomFieldEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: entityType, entityId, returnUrl and f:{fieldId} per field shown (empty clears it).
        app.MapPost("/custom-fields", async (IFormCollection form, CustomFieldService fields, CancellationToken ct) =>
        {
            var back = WebSecurity.SafeReturnUrl(form["returnUrl"]);
            var join = back.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            try
            {
                if (!Guid.TryParse(form["entityId"], out var entityId))
                    throw new DomainException("customfield.entity_required", "The fields aren't on a record.");
                var values = form.Keys.Where(k => k.StartsWith("f:", StringComparison.Ordinal))
                    .Select(k => (Ok: Guid.TryParse(k[2..], out var id), Id: id, Value: (string?)form[k].ToString()))
                    .Where(x => x.Ok)
                    .ToDictionary(x => x.Id, x => x.Value);
                await fields.SaveAsync(form["entityType"].ToString(), entityId, values, ct);
                return Results.Redirect($"{back}{join}fields=saved#custom-fields");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}{join}fieldError={Uri.EscapeDataString(ex.Message)}#custom-fields");
            }
        }).RequireAuthorization();

        // Fields: target, name, type, helpText, options (one per line), required, defaultValue, doc (each document),
        // placement, showInList, printOnForms, maxLength, minimum, maximum, position.
        app.MapPost("/settings/custom-fields", async (IFormCollection form, CustomFieldService fields, CancellationToken ct) =>
        {
            var target = Enum.TryParse<CustomFieldTarget>(form["target"], out var parsed) ? parsed : (CustomFieldTarget)(-1);
            try
            {
                var field = await fields.CreateAsync(target, Details(form), ct);
                return Results.Redirect($"/settings/custom-fields/{field.Id}?done=saved");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/settings/custom-fields/new?target={target}&error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/settings/custom-fields/{id:guid}", async (Guid id, IFormCollection form, CustomFieldService fields, CancellationToken ct) =>
            await Back($"/settings/custom-fields/{id}", async () =>
            {
                await fields.UpdateAsync(id, Details(form), ct);
                return "done=saved";
            })).RequireAuthorization();

        // Field: active = true | false.
        app.MapPost("/settings/custom-fields/{id:guid}/active", async (Guid id, IFormCollection form, CustomFieldService fields, CancellationToken ct) =>
            await Back($"/settings/custom-fields/{id}", async () =>
            {
                await fields.SetActiveAsync(id, form["active"] == "true", ct);
                return "done=saved";
            })).RequireAuthorization();

        // Fields: key (each ready-made field ticked), or all = a target to add every one left for it.
        app.MapPost("/settings/custom-fields/presets", async (IFormCollection form, CustomFieldService fields, CancellationToken ct) =>
            await Back("/settings/custom-fields", async () =>
            {
                IReadOnlyCollection<string> keys = Enum.TryParse<CustomFieldTarget>(form["all"], out var target)
                    ? [.. (await fields.AvailablePresetsAsync(ct)).Where(p => p.Target == target).Select(p => p.Key)]
                    : [.. form["key"].OfType<string>()];
                if (keys.Count == 0)
                    throw new DomainException("customfield.preset_none", "Tick the fields to add.");
                var added = await fields.AddPresetsAsync(keys, ct);
                return $"added={added.Count}";
            })).RequireAuthorization();

        return app;
    }

    private static CustomFieldDetails Details(IFormCollection form)
    {
        var type = Enum.TryParse<CustomFieldType>(form["type"], out var parsedType) ? parsedType : (CustomFieldType)(-1);
        var placement = Enum.TryParse<CustomFieldPlacement>(form["placement"], out var parsedPlacement) ? parsedPlacement : CustomFieldPlacement.UnderDescription;
        var documents = form["doc"].OfType<string>()
            .Aggregate(CustomFieldDocuments.None, (all, d) => Enum.TryParse<CustomFieldDocuments>(d, out var one) ? all | one : all);
        return new CustomFieldDetails(
            form["name"].ToString(),
            type,
            form["helpText"],
            form["options"].ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            form["required"] == "true",
            form["defaultValue"],
            documents,
            placement,
            form["showInList"] == "true",
            form["printOnForms"] == "true",
            Whole(form["maxLength"], "Longest text allowed"),
            Number(form["minimum"], "Smallest value"),
            Number(form["maximum"], "Largest value"),
            Whole(form["position"], "Order") ?? 0);
    }

    private static async Task<IResult> Back(string page, Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"{page}?{await change()}");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static int? Whole(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null
        : int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number
        : throw new DomainException("customfield.number_invalid", $"{name} isn't a whole number.");

    private static decimal? Number(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null
        : decimal.TryParse(value.Trim().Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ? number
        : throw new DomainException("customfield.number_invalid", $"{name} isn't a number.");
}
