using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenBook.Domain.Common;

namespace OpenBook.Api;

/// <summary>
/// A correction through the API sends what it knows of; what it leaves out stays as it was, or a program that reads a
/// document and sends it back would clear what the response doesn't carry. <see cref="Guid.Empty"/> clears.
/// </summary>
internal static class ApiKeep
{
    public static Guid? Id(Guid? sent, Guid? current) => sent == Guid.Empty ? null : sent ?? current;

    /// <summary>The body with the names of the fields it has, for a request whose left-out fields stay though null clears them.</summary>
    public static async Task<(T Value, IReadOnlySet<string> Sent)> ReadAsync<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = request.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        // Read here, not bound: a retry after a conflict reads it again.
        request.EnableBuffering();
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.Deserialize<T>(options) is { } value)
                return (value, body.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
        }

        throw new DomainException("request.unreadable", "The request body isn't JSON OpenBook can read.");
    }
}
