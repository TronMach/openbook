using OpenBook.Domain.Common;

namespace OpenBook.Web.Diagnostics;

/// <summary>
/// A page or form post about a record that isn't there (deleted in another tab, a form sent twice, an old link)
/// answers with the "not found" page rather than an error. The API maps the same exception to 404 itself.
/// </summary>
internal sealed class NotFoundMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (EntityNotFoundException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }
}
