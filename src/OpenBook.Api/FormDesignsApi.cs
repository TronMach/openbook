using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Api;

public sealed record FormDesignTextResponse(FormDocuments Document, string? Title, string? Message);

/// <param name="Key">A built-in column (number, item, description, sku, lots, class, taxable, quantity, rate, amount) or "field:{item custom field id}".</param>
/// <param name="Label">The heading; null = the usual one (a custom field's name).</param>
/// <param name="Width">A relative weight, 1–100; null = the column's usual width.</param>
/// <param name="AutoWidth">As wide as its heading and values need; the other columns share what's left by width.</param>
/// <param name="Align">Left, Center or Right; null = the usual (numbers on the right).</param>
public sealed record FormColumnRequest(string Key, string? Label = null, int? Width = null, bool AutoWidth = false, ColumnAlign? Align = null);

/// <param name="Show">What prints besides the lines (FormParts names).</param>
/// <param name="ShowShipTo">Kept for older clients: the same as ShipTo in <paramref name="Show"/>.</param>
/// <param name="ShowQuantityAndRate">Kept for older clients: whether <paramref name="Columns"/> has quantity and rate.</param>
/// <param name="CompanyFontKey">The font of the company's name and details: null = the same as <paramref name="FontKey"/>, "built-in" = the built-in sans-serif.</param>
public sealed record FormDesignResponse(
    Guid Id, string Name, bool IsDefault, bool IsActive, IReadOnlyList<FormDocuments> DefaultFor, FormTemplate Template, string AccentColor, string? Footer,
    string? FontKey, FormPaperSize PaperSize, bool HasLogo, FormLogoPosition LogoPosition, FormLogoSize LogoSize, IReadOnlyList<FormParts> Show,
    IReadOnlyList<FormColumnRequest> Columns, bool AutoFieldColumns, bool Landscape, FormMargins Margins, FormTextSize TextSize,
    bool ShowShipTo, bool ShowTerms, bool ShowQuantityAndRate, FormColumnLabels ColumnLabels, IReadOnlyList<FormDesignTextResponse> Texts,
    string? CompanyFontKey, OpenBook.Domain.Email.EmailStyle EmailStyle = OpenBook.Domain.Email.EmailStyle.Branded)
{
    public static FormDesignResponse From(FormDesign d) => new(
        d.Id, d.Name, d.IsDefault, d.IsActive, [.. FormDesign.Each(d.DefaultFor)], d.Template, d.AccentColor, d.Footer, d.FontKey, d.PaperSize,
        d.Logo is not null, d.LogoPosition, d.LogoSize, [.. FormDesign.EachPart(d.Show)], [.. d.Columns.Select(c => new FormColumnRequest(c.Key, c.Label, c.Width, c.AutoWidth, c.Align))],
        d.AutoFieldColumns, d.Landscape, d.Margins, d.TextSize, d.ShowShipTo, d.ShowTerms, d.ShowQuantityAndRate, d.ColumnLabels,
        [.. d.Texts.Select(t => new FormDesignTextResponse(t.Document, t.Title, t.Message))], d.CompanyFontKey, d.EmailStyle);
}

/// <param name="Show">What prints besides the lines (FormParts names); null = the usual parts, with <paramref name="ShowShipTo"/> and <paramref name="ShowTerms"/>.</param>
/// <param name="Columns">The line table's columns in order; null = description, (quantity, rate,) amount from <paramref name="ShowQuantityAndRate"/> and <paramref name="ColumnLabels"/>.</param>
/// <param name="CompanyFontKey">The font of the company's name and details: null = the same as <paramref name="FontKey"/>, "built-in" = the built-in sans-serif, else a font's key.</param>
public sealed record FormDesignRequest(
    string Name,
    FormTemplate Template = FormTemplate.Classic,
    string? AccentColor = null,
    string? Footer = null,
    string? FontKey = null,
    FormPaperSize PaperSize = FormPaperSize.Letter,
    FormLogoPosition LogoPosition = FormLogoPosition.Left,
    FormLogoSize LogoSize = FormLogoSize.Medium,
    IReadOnlyList<FormParts>? Show = null,
    IReadOnlyList<FormColumnRequest>? Columns = null,
    bool AutoFieldColumns = true,
    bool Landscape = false,
    FormMargins Margins = FormMargins.Normal,
    FormTextSize TextSize = FormTextSize.Medium,
    bool ShowShipTo = true,
    bool ShowTerms = true,
    bool ShowQuantityAndRate = true,
    FormColumnLabels? ColumnLabels = null,
    IReadOnlyList<FormDesignTextResponse>? Texts = null,
    string? CompanyFontKey = null,
    OpenBook.Domain.Email.EmailStyle? EmailStyle = null)
{
    public FormDesignDetails Details()
    {
        var show = Show is { } parts
            ? parts.Aggregate(FormParts.None, (all, p) => all | p)
            : FormParts.Default & ~(ShowShipTo ? FormParts.None : FormParts.ShipTo) & ~(ShowTerms ? FormParts.None : FormParts.Terms);
        var columns = Columns is { } chosen
            ? chosen.Select(c => new FormColumnDraft(c.Key, c.Label, c.Width, c.AutoWidth, c.Align)).ToList()
            : FormColumns.FromLegacy(ShowQuantityAndRate, ColumnLabels);
        return new FormDesignDetails(
            Name, Template, AccentColor, Footer, FontKey, PaperSize, LogoPosition, LogoSize, show, columns, AutoFieldColumns, Landscape, Margins, TextSize,
            (Texts ?? []).ToDictionary(t => t.Document, t => (t.Title, t.Message)), CompanyFontKey,
            EmailStyle: EmailStyle ?? OpenBook.Domain.Email.EmailStyle.Branded);
    }
}

public sealed record FormDesignPresetResponse(
    string Key, string Name, string Description, FormTemplate Template, string? AccentColor, string? FontKey, string? CompanyFontKey);

/// <param name="Name">The new design's name; null = the ready-made name (numbered when taken).</param>
public sealed record FormDesignFromPresetRequest(string? Name = null);

public sealed record FormDesignDocumentsRequest(IReadOnlyList<FormDocuments> Documents);

public sealed record CustomerFormDesignRequest(Guid? DesignId);

public sealed record GoogleFontRequest(string Family);

public sealed record PrintDocumentRequest(FormDocuments Document, Guid Id);

public sealed record PrintStatementsRequest(IReadOnlyList<Guid> CustomerIds, DateOnly From, DateOnly To);

/// <param name="Documents">In print order.</param>
public sealed record PrintRequest(IReadOnlyList<PrintDocumentRequest>? Documents = null, PrintStatementsRequest? Statements = null);

public sealed record PrintQueueResponse(Guid Id, FormDocuments Document, Guid DocumentId, string? Title, string? Party, DateTimeOffset AddedAt, string AddedBy, DateTimeOffset? PrintedAt)
{
    public static PrintQueueResponse From(PrintQueueEntry e) =>
        new(e.Item.Id, e.Item.Document, e.Item.DocumentId, e.Document?.Title, e.Document?.Party, e.Item.AddedAt, e.Item.AddedBy, e.Item.PrintedAt);
}

/// <param name="Regular">The regular face's .ttf file, Base64.</param>
/// <param name="Bold">The bold face's .ttf file, Base64 (optional).</param>
public sealed record UploadFontRequest(string Family, string Regular, string? Bold = null);

internal static class FormDesignsApi
{
    public static void MapFormDesignsApi(this RouteGroupBuilder api)
    {
        var designs = api.MapGroup("/settings/form-designs");
        designs.MapGet("/", async (FormDesignService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(FormDesignResponse.From));
        designs.MapGet("/{id:guid}", async (Guid id, FormDesignService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } d ? Results.Ok(FormDesignResponse.From(d)) : Results.NotFound());
        designs.MapPost("/", async (FormDesignRequest r, FormDesignService s, CancellationToken ct) =>
        {
            var d = await s.CreateAsync(r.Details(), ct: ct);
            return Results.Created($"/api/settings/form-designs/{d.Id}", FormDesignResponse.From(d));
        });
        designs.MapGet("/presets", () => FormDesignPresets.All.Select(p =>
            new FormDesignPresetResponse(p.Key, p.Details.Name, p.Description, p.Details.Template, p.Details.AccentColor, p.Details.FontKey, p.Details.CompanyFontKey)));
        designs.MapGet("/presets/{key}/preview", async (string key, FormDocuments? document, FormDesignService s, CancellationToken ct) =>
            FormDesignPresets.Find(key) is { } preset
                ? Results.File(await s.PreviewAsync(preset.Details, document ?? FormDocuments.Invoice, ct: ct), "application/pdf")
                : Results.NotFound());
        designs.MapPost("/presets/{key}", async (string key, FormDesignFromPresetRequest? r, FormDesignService s, CancellationToken ct) =>
        {
            var d = await s.CreateFromPresetAsync(key, r?.Name, ct);
            return Results.Created($"/api/settings/form-designs/{d.Id}", FormDesignResponse.From(d));
        });
        designs.MapPut("/{id:guid}", async (Guid id, FormDesignRequest r, FormDesignService s, CancellationToken ct) =>
            // Left out, the email look stays as it is.
            FormDesignResponse.From(await s.UpdateAsync(id, (r with { EmailStyle = r.EmailStyle ?? (await s.GetAsync(id, ct))?.EmailStyle }).Details(), ct)));

        // The logo is the raw request body (PNG or JPEG, at most 1 MB).
        designs.MapPut("/{id:guid}/logo", async (Guid id, HttpRequest request, FormDesignService s, CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > FormDesign.MaxLogoBytes)
                throw new DomainException("formdesign.logo_too_large", "The logo can be at most 1 MB.");
            return FormDesignResponse.From(await s.SetLogoAsync(id, buffer.ToArray(), ct));
        }).Accepts<byte[]>("image/png", "image/jpeg");
        designs.MapDelete("/{id:guid}/logo", async (Guid id, FormDesignService s, CancellationToken ct) => FormDesignResponse.From(await s.RemoveLogoAsync(id, ct)));
        designs.MapPost("/{id:guid}/default", async (Guid id, FormDesignService s, CancellationToken ct) => FormDesignResponse.From(await s.MakeDefaultAsync(id, ct)));
        designs.MapPut("/{id:guid}/documents", async (Guid id, FormDesignDocumentsRequest r, FormDesignService s, CancellationToken ct) =>
            FormDesignResponse.From(await s.SetDefaultForAsync(id, (r.Documents ?? []).Aggregate(FormDocuments.None, (all, d) => all | d), ct)));
        designs.MapPost("/{id:guid}/deactivate", async (Guid id, FormDesignService s, CancellationToken ct) => FormDesignResponse.From(await s.SetActiveAsync(id, false, ct)));
        designs.MapPost("/{id:guid}/reactivate", async (Guid id, FormDesignService s, CancellationToken ct) => FormDesignResponse.From(await s.SetActiveAsync(id, true, ct)));

        // A sample document printed with the design.
        designs.MapGet("/{id:guid}/preview", async (Guid id, FormDocuments? document, FormDesignService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } d
                ? Results.File(await s.PreviewAsync(d.ToDetails(), document ?? FormDocuments.Invoice, d.Id, ct), "application/pdf")
                : Results.NotFound());

        // Fonts designs can use (fontKey): built in, shipped with OpenBook, and added.
        var fonts = api.MapGroup("/settings/form-fonts");
        fonts.MapGet("/", async (FormFontService s, CancellationToken ct) => await s.ChoicesAsync(ct));
        fonts.MapPost("/google", async (GoogleFontRequest r, FormFontService s, CancellationToken ct) =>
        {
            var f = await s.AddFromGoogleAsync(r.Family, ct);
            return new { f.Key, f.Family, Origin = f.Origin.ToString(), HasBold = f.Bold is not null };
        });
        fonts.MapPost("/upload", async (UploadFontRequest r, FormFontService s, CancellationToken ct) =>
        {
            static byte[] Decode(string base64, string face)
            {
                try
                {
                    return Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    throw new DomainException("font.not_base64", $"The {face} file isn't Base64.");
                }
            }

            var f = await s.UploadAsync(r.Family, Decode(r.Regular ?? "", "regular"), r.Bold is { Length: > 0 } bold ? Decode(bold, "bold") : null, ct);
            return new { f.Key, f.Family, Origin = f.Origin.ToString(), HasBold = f.Bold is not null };
        });
        fonts.MapDelete("/{key}", async (string key, FormFontService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(key, ct);
            return Results.NoContent();
        });

        // The print queue, and printing a batch as one PDF (queued documents in it are marked printed).
        var print = api.MapGroup("/print");
        print.MapGet("/queue", async (PrintService s, CancellationToken ct) => (await s.QueueAsync(ct)).Select(PrintQueueResponse.From));
        print.MapGet("/printed", async (PrintService s, CancellationToken ct) => (await s.PrintedAsync(ct)).Select(PrintQueueResponse.From));
        print.MapPost("/queue", async (PrintDocumentRequest r, PrintService s, CancellationToken ct) =>
        {
            var item = await s.AddAsync(r.Document, r.Id, ct);
            return new { item.Id, item.Document, item.DocumentId };
        });
        print.MapDelete("/queue/{id:guid}", async (Guid id, PrintService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(id, ct);
            return Results.NoContent();
        });
        print.MapPost("/queue/{id:guid}/put-back", async (Guid id, PrintService s, CancellationToken ct) =>
        {
            await s.PutBackAsync(id, ct);
            return Results.NoContent();
        });
        print.MapPost("/pdf", async (PrintRequest r, PrintService s, CancellationToken ct) =>
        {
            var rendered = await s.PrintAsync([.. (r.Documents ?? []).Select(d => (d.Document, d.Id))],
                r.Statements is { } st ? new StatementRun(st.CustomerIds ?? [], st.From, st.To) : null, ct);
            return Results.File(rendered.Pdf, "application/pdf", rendered.FileName);
        });

        api.MapPut("/customers/{id:guid}/form-design", async (Guid id, CustomerFormDesignRequest r, FormDesignService s, CancellationToken ct) =>
        {
            var c = await s.SetForCustomerAsync(id, r.DesignId, ct);
            return new { c.Id, c.FormDesignId };
        });
    }
}
