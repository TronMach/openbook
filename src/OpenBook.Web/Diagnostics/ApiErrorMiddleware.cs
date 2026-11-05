using Microsoft.AspNetCore.Diagnostics;

namespace OpenBook.Web.Diagnostics;

/// <summary>
/// The API answers in JSON whatever happens: the not-found page is for people, and a program reading a 401 or a 404
/// mustn't get a page of HTML. An error with no body of its own gets a small one with a code.
/// </summary>
internal sealed class ApiErrorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }

        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
            pages.Enabled = false;
        await next(http);
        if (http.Response.StatusCode >= 400 && !http.Response.HasStarted && http.Response.ContentLength is null or 0)
        {
            var status = http.Response.StatusCode;
            await http.Response.WriteAsJsonAsync(new { code = $"http.{status}", title = Title(status) });
        }
    }

    private static string Title(int status) => status switch
    {
        400 => "The request couldn't be read.",
        401 => "Sign in, or send an API key.",
        403 => "You don't have access to this.",
        404 => "There's nothing there.",
        405 => "That method isn't allowed here.",
        413 => "The request is too large.",
        415 => "That content type isn't accepted.",
        _ => "The request failed.",
    };
}
