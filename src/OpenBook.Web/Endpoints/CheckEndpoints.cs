using Microsoft.Net.Http.Headers;
using OpenBook.Application.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Web.Endpoints;

/// <summary>Printing checks from the checks page: a plain form post that opens the PDF in a new tab.</summary>
public static class CheckEndpoints
{
    public static IEndpointRouteBuilder MapCheckEndpoints(this IEndpointRouteBuilder app)
    {
        // Form-bound, so the antiforgery token from the page is validated automatically.
        // Bound as the whole form: a string[] parameter trips .NET 10's TryParse lookup for string.
        app.MapPost("/checks/print", async (IFormCollection form, HttpContext http, CheckService service, CancellationToken ct) =>
        {
            var chosen = form["check"].OfType<string>()
                .Select(c => c.Split(':', 2))
                .Where(p => p.Length == 2 && Enum.TryParse<CheckKind>(p[0], out _) && Guid.TryParse(p[1], out _))
                .Select(p => (Enum.Parse<CheckKind>(p[0]), Guid.Parse(p[1])))
                .ToList();
            try
            {
                var printed = await service.PrintAsync(chosen, form["firstNumber"].ToString(), ct);
                http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = printed.FileName }.ToString();
                return Results.File(printed.Pdf, "application/pdf");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/banking/checks?error={Uri.EscapeDataString(ex.Code)}");
            }
        }).RequireAuthorization();

        return app;
    }
}
