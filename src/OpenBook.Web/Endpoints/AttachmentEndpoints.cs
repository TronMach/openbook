using OpenBook.Application.Companies;
using OpenBook.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using OpenBook.Application.Attachments;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>Adding, opening and removing files attached to records, from the plain forms on detail pages.</summary>
public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Form-bound, so the antiforgery token from the page is validated automatically.
        app.MapPost("/attachments", async (
            IFormFile? file,
            [FromForm] string? entityType,
            [FromForm] Guid? entityId,
            [FromForm] string? returnUrl,
            AttachmentService attachments,
            CancellationToken ct) =>
        {
            var back = LocalUrl(returnUrl);
            if (file is null || file.Length == 0 || entityType is null || entityId is null)
                return Results.Redirect(WithError(back, "attachment.empty"));
            try
            {
                await using var stream = file.OpenReadStream();
                await attachments.AddAsync(entityType, entityId.Value, file.FileName, stream, ct);
                return Results.Redirect(back + "#attachments");
            }
            catch (DomainException ex)
            {
                return Results.Redirect(WithError(back, ex.Code));
            }
        })
        .RequireAuthorization()
        // Twice the file limit, so an oversized file reaches the check that says why.
        .WithMetadata(new RequestSizeLimitAttribute(Attachment.MaxBytes * 2));

        app.MapPost("/attachments/{id:guid}/remove", async (Guid id, [FromForm] string? returnUrl, AttachmentService attachments, CancellationToken ct) =>
        {
            try
            {
                await attachments.RemoveAsync(id, ct);
            }
            catch (DomainException)
            {
                // Already gone: nothing to do.
            }

            return Results.Redirect(LocalUrl(returnUrl) + "#attachments");
        }).RequireAuthorization();

        app.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext http, AttachmentService attachments, CancellationToken ct) =>
        {
            var attachment = await attachments.GetAsync(id, ct);
            var content = attachment is null ? null : await attachments.GetContentAsync(id, ct);
            if (attachment is null || content is null)
                return Results.NotFound();
            // A file is read with its record: AccessRules lets this request through for that.
            if (AuthClaims.LevelIn(http.User, RecordAreas.Of(attachment.EntityType)) is null)
                return Results.Forbid();

            // The type comes from the stored extension, never sniffed; anything but images and PDFs downloads,
            // and inline files can't run scripts.
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
            headers.ContentDisposition = new ContentDispositionHeaderValue(attachment.ShowsInline ? "inline" : "attachment")
            {
                FileNameStar = attachment.FileName,
            }.ToString();
            return Results.File(content, attachment.ContentType);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Only paths on this site, so a crafted form can't redirect elsewhere.</summary>
    private static string LocalUrl(string? url) => (AccessRules.LocalPath(url) ?? "/").Split('#')[0];

    private static string WithError(string url, string code) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "attachError=" + Uri.EscapeDataString(code) + "#attachments";
}
