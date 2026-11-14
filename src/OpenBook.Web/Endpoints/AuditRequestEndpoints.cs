using System.Globalization;
using OpenBook.Application;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;

namespace OpenBook.Web.Endpoints;

/// <summary>Form posts from the audit request (PBC) list at /accountant/requests (C306).</summary>
public static class AuditRequestEndpoints
{
    public static IEndpointRouteBuilder MapAuditRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/accountant/requests.csv", async (int? year, AuditRequestService requests, CancellationToken ct) =>
        {
            var y = year ?? CompanyTime.Today.Year;
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(await requests.CsvAsync(y, ct))).ToArray(),
                "text/csv", $"Audit requests {y}.csv");
        }).RequireAuthorization();

        app.MapPost("/accountant/requests", async (IFormCollection form, AuditRequestService requests, CancellationToken ct) =>
        {
            var year = Year(form);
            return await Back(year, () => requests.CreateAsync(Details(form, year), ct));
        }).RequireAuthorization();

        app.MapPost("/accountant/requests/{id:guid}", async (Guid id, IFormCollection form, AuditRequestService requests, CancellationToken ct) =>
        {
            var year = Year(form);
            return await Back(year, () => requests.UpdateAsync(id, Details(form, year), Status(form["status"]), ct));
        }).RequireAuthorization();

        app.MapPost("/accountant/requests/{id:guid}/status", async (Guid id, IFormCollection form, AuditRequestService requests, CancellationToken ct) =>
            await Back(Year(form), () => requests.SetStatusAsync(id, Status(form["status"]), ct))).RequireAuthorization();

        app.MapPost("/accountant/requests/{id:guid}/delete", async (Guid id, IFormCollection form, AuditRequestService requests, CancellationToken ct) =>
            await Back(Year(form), () => requests.DeleteAsync(id, ct))).RequireAuthorization();

        app.MapPost("/accountant/requests/copy", async (IFormCollection form, AuditRequestService requests, CancellationToken ct) =>
        {
            var year = Year(form);
            var from = int.TryParse(form["fromYear"], NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : year - 1;
            return await Back(year, () => requests.CopyYearAsync(from, year, ct), count => $"&added={count}");
        }).RequireAuthorization();

        // Binding the file (with the form) makes minimal APIs check the antiforgery token.
        app.MapPost("/accountant/requests/import", async (IFormFile? file, IFormCollection form, AuditRequestService requests, CancellationToken ct) =>
        {
            var year = Year(form);
            if (file is null || file.Length == 0)
                return Results.Redirect($"/accountant/requests?year={year}&error={Uri.EscapeDataString("Choose a CSV file.")}");
            if (file.Length > ChannelEndpoints.MaxReportBytes)
                return Results.Redirect($"/accountant/requests?year={year}&error={Uri.EscapeDataString("The file is larger than 20 MB.")}");
            using var reader = new StreamReader(file.OpenReadStream());
            var csv = await reader.ReadToEndAsync(ct);
            return await Back(year, () => requests.ImportCsvAsync(year, csv, ct), count => $"&added={count}");
        }).RequireAuthorization();

        return app;
    }

    private static int Year(IFormCollection form) =>
        int.TryParse(form["year"], NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 9999 ? y : CompanyTime.Today.Year;

    private static AuditRequestDetails Details(IFormCollection form, int year) => new(
        year, form["item"].ToString(), form["owner"].ToString(),
        DateOnly.TryParseExact(form["dueOn"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) ? due : null,
        form["notes"].ToString());

    private static AuditRequestStatus Status(string? value) =>
        Enum.TryParse<AuditRequestStatus>(value, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s
            : throw new DomainException("audit_request.status", "Choose open, received or not needed.");

    private static Task<IResult> Back<T>(int year, Func<Task<T>> change) => Back(year, change, _ => "");

    private static async Task<IResult> Back<T>(int year, Func<Task<T>> change, Func<T, string> extra)
    {
        var page = $"/accountant/requests?year={year}";
        try
        {
            var result = await change();
            return Results.Redirect(page + extra(result));
        }
        catch (DomainException ex)
        {
            return Results.Redirect($"{page}&error={Uri.EscapeDataString(ex.Message)}");
        }
    }

    private static Task<IResult> Back(int year, Func<Task> change) => Back(year, async () =>
    {
        await change();
        return true;
    });
}
