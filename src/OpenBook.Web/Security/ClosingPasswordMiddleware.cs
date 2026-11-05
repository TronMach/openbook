using OpenBook.Application.Ledger;

namespace OpenBook.Web.Security;

/// <summary>
/// The closing date password from any plain form post, so an endpoint that posts to a closed date needs nothing of its
/// own: <c>ClosingPasswordBox</c> offers the field on the page that was refused, and shortcuts.js sends it with the next form.
/// </summary>
public sealed class ClosingPasswordMiddleware(RequestDelegate next)
{
    public const string Field = "ClosingPassword";

    public async Task InvokeAsync(HttpContext http)
    {
        var request = http.Request;
        if (HttpMethods.IsPost(request.Method) && !request.Path.StartsWithSegments("/api") && !request.Path.StartsWithSegments("/_blazor")
            && request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            var form = await request.ReadFormAsync(http.RequestAborted);
            if (form[Field].ToString() is { Length: > 0 } password)
                http.RequestServices.GetRequiredService<ClosedPeriodOverride>().Password = password;
        }

        await next(http);
    }
}
