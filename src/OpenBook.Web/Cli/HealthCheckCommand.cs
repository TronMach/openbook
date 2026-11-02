namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web healthcheck [--url http://localhost:8080/healthz]</c>: asks a running OpenBook whether it's healthy and
/// exits 0 (it answered) or 1, for container health checks (the image has no curl). Runs before anything else starts:
/// no configuration, database or logging. The address defaults to the first of ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS,
/// on localhost.
/// </summary>
public static class HealthCheckCommand
{
    public const string Name = "healthcheck";

    public static async Task<int> RunAsync(string[] args)
    {
        var url = Option(args, "--url") ?? DefaultUrl();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
                return 0;
            await Console.Error.WriteLineAsync($"{url} answered {(int)response.StatusCode}.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            await Console.Error.WriteLineAsync($"{url} didn't answer: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Where the app listens in this container: ASPNETCORE_URLS ("http://+:8080") or ASPNETCORE_HTTP_PORTS, else 8080.</summary>
    internal static string DefaultUrl()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls?.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) is { } first)
        {
            var port = first[(first.LastIndexOf(':') + 1)..].TrimEnd('/');
            return $"http://localhost:{port}/healthz";
        }

        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"http://localhost:{ports?.FirstOrDefault() ?? "8080"}/healthz";
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
