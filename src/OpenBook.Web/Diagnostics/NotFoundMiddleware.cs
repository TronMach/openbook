using OpenBook.Domain.Common;

namespace OpenBook.Web.Diagnostics;

/// <summary>
/// A page or form post about a record that isn't there (deleted in another tab, a form sent twice, an old link)
/// answers with the "not found" page rather than an error. The API maps the same exception to 404 itself. So does
/// an address with a value that won't read (a hand-edited or cut-off link), wherever a page takes it from the query.
/// A form post to a form the page no longer draws (a second click on Void) goes back to the page, which shows what happened.
/// </summary>
internal sealed class NotFoundMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // The framework writes its own words after the status, so this has to happen as the response starts.
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status400BadRequest && IsPagePost(context))
                    context.Response.Redirect(context.Request.PathBase + context.Request.Path + context.Request.QueryString);
                return Task.CompletedTask;
            });
            await next(context);
        }
        catch (EntityNotFoundException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (InvalidOperationException ex) when (IsUnreadableQueryValue(ex) && !context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <summary>The framework's words for a <c>[SupplyParameterFromQuery]</c> value that doesn't parse; <c>PageSmokeTests</c> pins them.</summary>
    private static bool IsUnreadableQueryValue(InvalidOperationException ex) =>
        ex.Message.StartsWith("Cannot parse the value", StringComparison.Ordinal);

    private static bool IsPagePost(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>()?.IsValid != false
        && context.Request.HasFormContentType
        && context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() is not null;
}
