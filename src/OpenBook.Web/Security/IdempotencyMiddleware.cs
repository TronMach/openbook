using System.Security.Cryptography;
using System.Text;
using OpenBook.Application;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Security;

/// <summary>
/// <c>Idempotency-Key</c> on API writes (POST, PUT, PATCH, DELETE): the first request with a key runs and its response
/// is kept for a day; the same request sent again with the key (a retry after a timeout) gets that response back,
/// marked <c>Idempotency-Replayed: true</c>, instead of making a second invoice. The same key with a different request
/// is refused (422), and one still running answers 409. Keys belong to the caller. Server errors (5xx) and responses
/// over 1 MB aren't kept, so the request can be sent again. Without the header nothing changes. Endpoints whose
/// response shows a secret once (new API keys, invitation links, webhook secrets, capture devices) ignore it, so the
/// secret is never stored.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";

    private static readonly string[] SecretPaths = ["/api/api-keys", "/api/users/invites", "/api/webhooks", "/api/capture"];

    public async Task InvokeAsync(HttpContext http, IIdempotencyStore store)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);
        var request = http.Request;
        if (!request.Path.StartsWithSegments("/api") || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || !request.Headers.TryGetValue(Header, out var values) || http.User.Identity?.Name is not { } caller
            || SecretPaths.Any(p => request.Path.StartsWithSegments(p)))
        {
            await next(http);
            return;
        }

        var key = values.ToString().Trim();
        if (key.Length is 0 or > IdempotentRequest.MaxKeyLength || key.Any(char.IsControl))
        {
            await Refuse(http, StatusCodes.Status400BadRequest, "idempotency.key_invalid", $"An Idempotency-Key is 1 to {IdempotentRequest.MaxKeyLength} printable characters.");
            return;
        }

        var hash = await HashAsync(request, http.RequestAborted);
        var (reserved, earlier) = await store.ReserveAsync(key, caller, hash, http.RequestAborted);
        if (!reserved)
        {
            if (earlier is null || earlier.RequestHash != hash)
                await Refuse(http, StatusCodes.Status422UnprocessableEntity, "idempotency.key_reused", "That Idempotency-Key was used for a different request.");
            else if (!earlier.IsDone)
                await Refuse(http, StatusCodes.Status409Conflict, "idempotency.in_progress", "A request with that Idempotency-Key is still running. Try again shortly.");
            else
                await ReplayAsync(http, earlier);
            return;
        }

        // Run it, keeping a copy of the response to answer the same request again.
        var original = http.Response.Body;
        using var copy = new MemoryStream();
        http.Response.Body = copy;
        var kept = false;
        try
        {
            await next(http);
            // A refusal saved nothing, so sending it again (with the closing password, say) must be able to succeed.
            var status = http.Response.StatusCode;
            if (status < 400 && copy.Length <= IdempotentRequest.MaxBodyBytes)
            {
                // From here the work is done: if the answer can't be kept, the key stays taken rather than letting a repeat do it twice.
                kept = true;
                await store.CompleteAsync(key, caller, status, http.Response.ContentType, copy.ToArray(),
                    http.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
                    http.Response.Headers.ContentDisposition.ToString() is { Length: > 0 } disposition ? disposition : null, CancellationToken.None);
            }
        }
        finally
        {
            http.Response.Body = original;
            if (!kept)
                await store.ForgetAsync(key, caller, CancellationToken.None);
            copy.Position = 0;
            await copy.CopyToAsync(original, CancellationToken.None);
        }
    }

    /// <summary>SHA-256 of the method, path, query and body, so a key can't be reused for something else.</summary>
    private static async Task<string> HashAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        var buffer = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            sha.AppendData(buffer, 0, read);
        request.Body.Position = 0;
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task ReplayAsync(HttpContext http, IdempotentRequest earlier)
    {
        http.Response.StatusCode = earlier.StatusCode;
        if (earlier.ContentType is { } type)
            http.Response.ContentType = type;
        if (earlier.Location is { } location)
            http.Response.Headers.Location = location;
        if (earlier.ContentDisposition is { } disposition)
            http.Response.Headers.ContentDisposition = disposition;
        http.Response.Headers[ReplayedHeader] = "true";
        if (earlier.Body is { Length: > 0 } body)
            await http.Response.Body.WriteAsync(body, http.RequestAborted);
    }

    private static Task Refuse(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { title = message, status, code }, http.RequestAborted);
    }
}
