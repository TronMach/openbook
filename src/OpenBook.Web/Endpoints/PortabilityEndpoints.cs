using OpenBook.Application;
using OpenBook.Application.Notes;
using OpenBook.Domain.Common;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>
/// Form posts for notes (from the Notes component on any record page), the full data export download and the QuickBooks
/// Desktop export helper's download.
/// </summary>
public static class PortabilityEndpoints
{
    public static IEndpointRouteBuilder MapPortabilityEndpoints(this IEndpointRouteBuilder app)
    {
        // Bound as the whole form, so the antiforgery token is validated automatically.
        app.MapPost("/notes", async (IFormCollection form, NoteService notes, CancellationToken ct) =>
        {
            var back = WebSecurity.SafeReturnUrl(form["returnUrl"]);
            try
            {
                if (!Guid.TryParse(form["entityId"], out var entityId))
                    throw new DomainException("note.entity_required", "The note isn't attached to a record.");
                await notes.AddAsync(form["entityType"].ToString(), entityId, form["text"], ct);
                return Results.Redirect($"{back}#notes");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"{back}{(back.Contains('?', StringComparison.Ordinal) ? '&' : '?')}noteError={Uri.EscapeDataString(ex.Message)}#notes");
            }
        }).RequireAuthorization();

        app.MapPost("/notes/{id:guid}/delete", async (Guid id, IFormCollection form, NoteService notes, CancellationToken ct) =>
        {
            await notes.RemoveAsync(id, ct);
            return Results.Redirect($"{WebSecurity.SafeReturnUrl(form["returnUrl"])}#notes");
        }).RequireAuthorization();

        app.MapGet("/export/download", async (IDataExporter exporter, CancellationToken ct) =>
        {
            var file = await exporter.ExportAsync(ct);
            var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/zip", file.FileName);
        }).RequireAuthorization();

        app.MapGet(Hosting.QuickBooksHelper.DownloadPath, () => Hosting.QuickBooksHelper.File is { } path
            ? Results.File(path, "application/vnd.microsoft.portable-executable", Hosting.QuickBooksHelper.FileName)
            : Results.NotFound()).RequireAuthorization();

        return app;
    }
}
