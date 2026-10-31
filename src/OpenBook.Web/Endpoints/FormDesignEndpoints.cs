using Microsoft.Extensions.Primitives;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Endpoints;

/// <summary>Form designs (Settings → Invoice style): create, save, logo, default, documents, switch off, preview; a customer's design.</summary>
public static class FormDesignEndpoints
{
    public static IEndpointRouteBuilder MapFormDesignEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: name, copyFrom (a design whose settings and logo to start from).
        app.MapPost("/settings/forms/new", async (IFormCollection form, FormDesignService designs, CancellationToken ct) =>
        {
            try
            {
                var from = Guid.TryParse(form["copyFrom"], out var id) ? await designs.GetAsync(id, ct) : null;
                var details = (from?.ToDetails() ?? new FormDesignDetails("")) with { Name = form["name"].ToString() };
                var design = await designs.CreateAsync(details, from?.Id, ct);
                return Results.Redirect($"/settings/forms/{design.Id}?done=created");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/settings/forms?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        // Fields: see Details.
        app.MapPost("/settings/forms/{id:guid}", async (Guid id, IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Back(id, async () => await designs.UpdateAsync(id, Details(k => form[k]), ct))).RequireAuthorization();

        // Field: logo (a PNG or JPEG file).
        app.MapPost("/settings/forms/{id:guid}/logo", async (Guid id, IFormFile? logo, FormDesignService designs, CancellationToken ct) =>
            await Back(id, async () =>
            {
                if (logo is null || logo.Length == 0)
                    throw new DomainException("formdesign.logo_empty", "Choose a logo file.");
                if (logo.Length > FormDesign.MaxLogoBytes)
                    throw new DomainException("formdesign.logo_too_large", "The logo can be at most 1 MB.");
                using var buffer = new MemoryStream();
                await logo.CopyToAsync(buffer, ct);
                await designs.SetLogoAsync(id, buffer.ToArray(), ct);
            })).RequireAuthorization();

        app.MapPost("/settings/forms/{id:guid}/logo/remove", async (Guid id, IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Back(id, async () => await designs.RemoveLogoAsync(id, ct))).RequireAuthorization();

        app.MapPost("/settings/forms/{id:guid}/default", async (Guid id, IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Back(id, async () => await designs.MakeDefaultAsync(id, ct))).RequireAuthorization();

        // Fields: doc (each document this design prints by default).
        app.MapPost("/settings/forms/{id:guid}/documents", async (Guid id, IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Back(id, async () => await designs.SetDefaultForAsync(id, Documents(form["doc"]), ct))).RequireAuthorization();

        // Field: active = true | false.
        app.MapPost("/settings/forms/{id:guid}/active", async (Guid id, IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Back(id, async () => await designs.SetActiveAsync(id, form["active"] == "true", ct))).RequireAuthorization();

        // A saved design on a sample document: design, document.
        app.MapGet("/settings/forms/preview", async (Guid design, string? document, FormDesignService designs, CancellationToken ct) =>
        {
            var saved = await designs.GetAsync(design, ct);
            return saved is null ? Results.NotFound() : await Preview(() => designs.PreviewAsync(saved.ToDetails(), Document(document), saved.Id, ct));
        }).RequireAuthorization();

        // The editor's unsaved settings on a sample document: the editor's fields plus document and logoFrom (a design whose logo to show).
        app.MapPost("/settings/forms/preview", async (IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Preview(() => designs.PreviewAsync(Details(k => form[k]) with { Name = "Preview" }, Document(form["document"]),
                Guid.TryParse(form["logoFrom"], out var logoFrom) ? logoFrom : null, ct))).RequireAuthorization();

        // Field: designId (empty = the usual design).
        app.MapPost("/customers/{id:guid}/form-design", async (Guid id, IFormCollection form, FormDesignService designs, CancellationToken ct) =>
        {
            try
            {
                await designs.SetForCustomerAsync(id, Guid.TryParse(form["designId"], out var design) ? design : null, ct);
                return Results.Redirect($"/customers/{id}#form-design");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/customers/{id}?designError={Uri.EscapeDataString(ex.Message)}#form-design");
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// The editor's fields: name, template, accentColor, footer, fontKey, paperSize, logoPosition, logoSize,
    /// showShipTo / showTerms / showQuantityAndRate ("true" when ticked), labelDescription / labelQuantity / labelRate /
    /// labelAmount, and title:{document} / message:{document}.
    /// </summary>
    private static FormDesignDetails Details(Func<string, StringValues> field)
    {
        T Pick<T>(string name, T fallback) where T : struct, Enum => Enum.TryParse<T>(field(name), out var value) && Enum.IsDefined(value) ? value : fallback;
        var texts = FormDesign.Each(FormDesign.AllDocuments).ToDictionary(
            d => d, d => ((string?)field($"title:{d}").ToString(), (string?)field($"message:{d}").ToString()));
        return new FormDesignDetails(
            field("name").ToString(),
            Pick("template", FormTemplate.Classic),
            field("accentColor"),
            field("footer"),
            field("fontKey"),
            Pick("paperSize", FormPaperSize.Letter),
            Pick("logoPosition", FormLogoPosition.Left),
            Pick("logoSize", FormLogoSize.Medium),
            field("showShipTo") == "true",
            field("showTerms") == "true",
            field("showQuantityAndRate") == "true",
            new FormColumnLabels(field("labelDescription"), field("labelQuantity"), field("labelRate"), field("labelAmount")),
            texts);
    }

    private static FormDocuments Document(string? value) =>
        Enum.TryParse<FormDocuments>(value, out var d) && FormDesign.Each(FormDesign.AllDocuments).Contains(d) ? d : FormDocuments.Invoice;

    /// <summary>The PDF, or the reason as a small page (shown in the preview frame).</summary>
    private static async Task<IResult> Preview(Func<Task<byte[]>> render)
    {
        try
        {
            return Results.File(await render(), "application/pdf");
        }
        catch (DomainException ex)
        {
            return Results.Content($"<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;padding:1rem\">{System.Net.WebUtility.HtmlEncode(ex.Message)}</body>", "text/html", statusCode: 400);
        }
    }

    private static FormDocuments Documents(StringValues values) =>
        values.OfType<string>().Aggregate(FormDocuments.None, (all, d) => Enum.TryParse<FormDocuments>(d, out var one) ? all | one : all);

    private static async Task<IResult> Back(Guid id, Func<Task> change)
    {
        try
        {
            await change();
            return Results.Redirect($"/settings/forms/{id}?done=saved");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/settings/forms/{id}?error={Uri.EscapeDataString(ex.Message)}");
        }
    }
}
