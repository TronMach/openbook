namespace OpenBook.Web.Security;

/// <summary>
/// Headers every response gets unless it set its own (attachments send a sandboxing policy): no MIME sniffing, no
/// framing by other sites (clickjacking), and no paths or tokens (customer portal links) in the Referer sent to
/// other sites. Not a full content security policy: the pages use inline script (theme, Blazor).
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("Referrer-Policy", "same-origin");
            headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
            headers.TryAdd("Content-Security-Policy", "frame-ancestors 'self'");
            return Task.CompletedTask;
        });
        return next(context);
    }
}
