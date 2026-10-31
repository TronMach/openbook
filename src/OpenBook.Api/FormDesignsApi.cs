using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Api;

public sealed record FormDesignTextResponse(FormDocuments Document, string? Title, string? Message);

public sealed record FormDesignResponse(
    Guid Id, string Name, bool IsDefault, bool IsActive, IReadOnlyList<FormDocuments> DefaultFor, FormTemplate Template, string AccentColor, string? Footer,
    string? FontKey, FormPaperSize PaperSize, bool HasLogo, FormLogoPosition LogoPosition, FormLogoSize LogoSize, bool ShowShipTo, bool ShowTerms,
    bool ShowQuantityAndRate, FormColumnLabels ColumnLabels, IReadOnlyList<FormDesignTextResponse> Texts)
{
    public static FormDesignResponse From(FormDesign d) => new(
        d.Id, d.Name, d.IsDefault, d.IsActive, [.. FormDesign.Each(d.DefaultFor)], d.Template, d.AccentColor, d.Footer, d.FontKey, d.PaperSize,
        d.Logo is not null, d.LogoPosition, d.LogoSize, d.ShowShipTo, d.ShowTerms, d.ShowQuantityAndRate, d.ColumnLabels,
        [.. d.Texts.Select(t => new FormDesignTextResponse(t.Document, t.Title, t.Message))]);
}

public sealed record FormDesignRequest(
    string Name,
    FormTemplate Template = FormTemplate.Classic,
    string? AccentColor = null,
    string? Footer = null,
    string? FontKey = null,
    FormPaperSize PaperSize = FormPaperSize.Letter,
    FormLogoPosition LogoPosition = FormLogoPosition.Left,
    FormLogoSize LogoSize = FormLogoSize.Medium,
    bool ShowShipTo = true,
    bool ShowTerms = true,
    bool ShowQuantityAndRate = true,
    FormColumnLabels? ColumnLabels = null,
    IReadOnlyList<FormDesignTextResponse>? Texts = null)
{
    public FormDesignDetails Details() => new(
        Name, Template, AccentColor, Footer, FontKey, PaperSize, LogoPosition, LogoSize, ShowShipTo, ShowTerms, ShowQuantityAndRate, ColumnLabels,
        (Texts ?? []).ToDictionary(t => t.Document, t => (t.Title, t.Message)));
}

public sealed record FormDesignDocumentsRequest(IReadOnlyList<FormDocuments> Documents);

public sealed record CustomerFormDesignRequest(Guid? DesignId);

public sealed record GoogleFontRequest(string Family);

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
        designs.MapPut("/{id:guid}", async (Guid id, FormDesignRequest r, FormDesignService s, CancellationToken ct) =>
            FormDesignResponse.From(await s.UpdateAsync(id, r.Details(), ct)));

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

        api.MapPut("/customers/{id:guid}/form-design", async (Guid id, CustomerFormDesignRequest r, FormDesignService s, CancellationToken ct) =>
        {
            var c = await s.SetForCustomerAsync(id, r.DesignId, ct);
            return new { c.Id, c.FormDesignId };
        });
    }
}
