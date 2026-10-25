using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application;
using OpenBook.Application.Import;
using OpenBook.Application.Notes;
using OpenBook.Domain.Notes;

namespace OpenBook.Api;

public sealed record AddNoteRequest(string EntityType, Guid EntityId, string Text);

internal static class PortabilityApi
{
    public static void MapPortabilityApi(this RouteGroupBuilder api)
    {
        // Bodies are the files themselves (text/csv, or the .iif text). With dryRun=true nothing is saved.
        var import = api.MapGroup("/import");
        import.MapPost("/journal-entries", async (HttpRequest request, bool? dryRun, JournalImportService s, CancellationToken ct) =>
        {
            var csv = await ReadAsync(request, ct);
            return dryRun == true ? await s.PreviewEntriesAsync(csv, ct) : await s.ImportEntriesAsync(csv, ct);
        }).Accepts<string>("text/csv");

        import.MapPost("/opening-balances", async (HttpRequest request, DateOnly? asOf, bool? dryRun, JournalImportService s, CancellationToken ct) =>
        {
            var csv = await ReadAsync(request, ct);
            if (dryRun == true)
                return Results.Ok(await s.PreviewBalancesAsync(csv, ct));
            if (asOf is not { } date)
                return Results.BadRequest(new { code = "import.as_of_required", message = "Say the date the balances are as of (asOf=yyyy-mm-dd)." });
            var entry = await s.ImportBalancesAsync(csv, date, ct);
            return Results.Ok(new { entry.Id, entry.Number });
        }).Accepts<string>("text/csv");

        import.MapPost("/iif", async (HttpRequest request, bool? dryRun, IifImportService s, CancellationToken ct) =>
        {
            var text = await ReadAsync(request, ct);
            return dryRun == true ? await s.PreviewAsync(text, ct) : await s.ImportAsync(text, ct);
        }).Accepts<string>("text/plain");

        // Any supported program's export as the raw request body: source is a MigrationSource (QuickBooksIif, GnuCash, Xero...).
        import.MapPost("/migrate/{source}", async (string source, HttpRequest request, bool? dryRun, Application.Import.Migration.MigrationImportService s, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Application.Import.Migration.MigrationSource>(source, ignoreCase: true, out var from))
                return Results.BadRequest(new { code = "import.source_unknown", message = $"Unknown source '{source}'. Use one of: {string.Join(", ", Enum.GetNames<Application.Import.Migration.MigrationSource>())}." });
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > Application.Import.Migration.MigrationImportService.MaxBytes)
                return Results.BadRequest(new { code = "import.too_large", message = "The file is larger than 20 MB." });
            var content = buffer.ToArray();
            return Results.Ok(dryRun == true ? await s.PreviewAsync(from, content, ct) : await s.ImportAsync(from, content, ct));
        }).Accepts<byte[]>("application/octet-stream");

        // A ZIP of every table as CSV, the attachments, and README.md documenting it all.
        api.MapGet("/export", async (IDataExporter exporter, CancellationToken ct) =>
        {
            var file = await exporter.ExportAsync(ct);
            var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/zip", file.FileName);
        });

        var notes = api.MapGroup("/notes");
        notes.MapGet("/{entityType}/{entityId:guid}", async (string entityType, Guid entityId, NoteService s, CancellationToken ct) =>
            await s.ListAsync(entityType, entityId, ct));
        notes.MapPost("/", async (AddNoteRequest r, NoteService s, CancellationToken ct) =>
        {
            Note note = await s.AddAsync(r.EntityType, r.EntityId, r.Text, ct);
            return Results.Created($"/api/notes/{note.EntityType}/{note.EntityId}", note);
        });
        notes.MapDelete("/{id:guid}", async (Guid id, NoteService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(id, ct);
            return Results.NoContent();
        });
    }

    private const int MaxBytes = 5 * 1024 * 1024;

    private static async Task<string> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBytes)
            throw new Domain.Common.DomainException("import.too_large", "Files can be at most 5 MB. Split it into smaller files.");
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(ct);
    }
}
