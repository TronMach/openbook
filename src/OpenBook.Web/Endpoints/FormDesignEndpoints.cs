using Microsoft.Extensions.Primitives;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Endpoints;

/// <summary>Form designs (Settings → Invoice style): create, save, logo, default, documents, switch off, preview; a customer's design.</summary>
public static class FormDesignEndpoints
{
    /// <summary>"Start from" values naming a ready-made design.</summary>
    public const string PresetPrefix = "preset:";

    public static IEndpointRouteBuilder MapFormDesignEndpoints(this IEndpointRouteBuilder app)
    {
        // Fields: name (optional with a ready-made design), copyFrom (a design whose settings and logo to start from, or
        // "preset:{key}" for a ready-made design).
        app.MapPost("/settings/forms/new", async (IFormCollection form, FormDesignService designs, CancellationToken ct) =>
        {
            try
            {
                if (form["copyFrom"].ToString() is var copy && copy.StartsWith(PresetPrefix, StringComparison.Ordinal))
                {
                    var made = await designs.CreateFromPresetAsync(copy[PresetPrefix.Length..], form["name"].ToString(), ct);
                    return Results.Redirect($"/settings/forms/{made.Id}?done=created");
                }

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
            await Back(id, async () => await designs.UpdateAsync(id, Details(form), ct))).RequireAuthorization();

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

        // A saved design (design) or a ready-made one (preset, its key) on a sample document (document).
        app.MapGet("/settings/forms/preview", async (Guid? design, string? preset, string? document, FormDesignService designs, CancellationToken ct) =>
        {
            if (FormDesignPresets.Find(preset) is { } ready)
                return await Preview(() => designs.PreviewAsync(ready.Details, Document(document), ct: ct));
            var saved = design is { } id ? await designs.GetAsync(id, ct) : null;
            return saved is null ? Results.NotFound() : await Preview(() => designs.PreviewAsync(saved.ToDetails(), Document(document), saved.Id, ct));
        }).RequireAuthorization();

        // The editor's unsaved settings on a sample document: the editor's fields plus document and logoFrom (a design whose logo to show).
        app.MapPost("/settings/forms/preview", async (IFormCollection form, FormDesignService designs, CancellationToken ct) =>
            await Preview(() => designs.PreviewAsync(Details(form) with { Name = "Preview" }, Document(form["document"]),
                Guid.TryParse(form["logoFrom"], out var logoFrom) ? logoFrom : null, ct))).RequireAuthorization();

        // Field: family (as Google Fonts names it).
        app.MapPost("/settings/forms/fonts/google", async (IFormCollection form, FormFontService fonts, CancellationToken ct) =>
            await Fonts(async () => $"Added {(await fonts.AddFromGoogleAsync(form["family"].ToString(), ct)).Family}.")).RequireAuthorization();

        // Fields: family, regular and bold (.ttf files; bold optional).
        app.MapPost("/settings/forms/fonts/upload", async (IFormFile? regular, IFormFile? bold, HttpRequest request, FormFontService fonts, CancellationToken ct) =>
            await Fonts(async () =>
            {
                var form = await request.ReadFormAsync(ct);
                var regularBytes = await ReadAsync(regular, ct) ?? throw new DomainException("font.empty", "Choose the regular font file.");
                var font = await fonts.UploadAsync(form["family"].ToString(), regularBytes, await ReadAsync(bold, ct), ct);
                return $"Added {font.Family}.";
            })).RequireAuthorization();

        app.MapPost("/settings/forms/fonts/{key}/remove", async (string key, IFormCollection form, FormFontService fonts, CancellationToken ct) =>
            await Fonts(async () =>
            {
                await fonts.RemoveAsync(key, ct);
                return "Removed.";
            })).RequireAuthorization();

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
    /// The editor's fields: name, template, accentColor, footer, fontKey, companyFontKey (empty = the same font),
    /// paperSize, logoPosition, logoSize, margins, textSize, landscape / autoFieldColumns ("true" when ticked), show (each
    /// part ticked), the column table (columnKey for every row in the table's order, and per key col:{key} = "true" when it
    /// prints, order:{key}, label:{key}, width:{key}, auto:{key} = "true" to fit its contents, align:{key} = Left / Center /
    /// Right), and title:{document} / message:{document}.
    /// </summary>
    private static FormDesignDetails Details(IFormCollection form)
    {
        T Pick<T>(string name, T fallback) where T : struct, Enum => Enum.TryParse<T>(form[name], out var value) && Enum.IsDefined(value) ? value : fallback;
        int? Number(string name) => int.TryParse(form[name], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        var texts = FormDesign.Each(FormDesign.AllDocuments).ToDictionary(
            d => d, d => ((string?)form[$"title:{d}"].ToString(), (string?)form[$"message:{d}"].ToString()));
        // Ticked columns by their order number; rows without one keep their place in the table.
        var columns = form["columnKey"].OfType<string>()
            .Select((key, index) => (Key: key, Index: index))
            .Where(c => form[$"col:{c.Key}"] == "true")
            .OrderBy(c => Number($"order:{c.Key}") ?? int.MaxValue).ThenBy(c => c.Index)
            .Select(c => new FormColumnDraft(c.Key, form[$"label:{c.Key}"].ToString(), Number($"width:{c.Key}"), form[$"auto:{c.Key}"] == "true",
                Enum.TryParse<ColumnAlign>(form[$"align:{c.Key}"], out var align) && Enum.IsDefined(align) ? align : null))
            .ToList();
        return new FormDesignDetails(
            form["name"].ToString(),
            Pick("template", FormTemplate.Classic),
            form["accentColor"],
            form["footer"],
            form["fontKey"],
            Pick("paperSize", FormPaperSize.Letter),
            Pick("logoPosition", FormLogoPosition.Left),
            Pick("logoSize", FormLogoSize.Medium),
            form["show"].OfType<string>().Aggregate(FormParts.None, (all, p) => Enum.TryParse<FormParts>(p, out var part) ? all | part : all),
            columns,
            form["autoFieldColumns"] == "true",
            form["landscape"] == "true",
            Pick("margins", FormMargins.Normal),
            Pick("textSize", FormTextSize.Medium),
            texts,
            form["companyFontKey"]);
    }

    private static async Task<IResult> Fonts(Func<Task<string>> change)
    {
        try
        {
            return Results.Redirect($"/settings/forms?fonts={Uri.EscapeDataString(await change())}#fonts");
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"/settings/forms?fontError={Uri.EscapeDataString(ex.Message)}#fonts");
        }
    }

    private static async Task<byte[]?> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return null;
        if (file.Length > FormFont.MaxFileBytes)
            throw new DomainException("font.too_large", "Font files can be at most 10 MB.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
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
