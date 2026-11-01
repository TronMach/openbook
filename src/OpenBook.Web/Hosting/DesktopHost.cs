using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBook.Web.Hosting;

/// <summary>
/// Running as OpenBook Desktop: one copy per company folder, listening on this computer only, opening the browser once
/// started. A second start finds the first through <see cref="Installation.RunningFile"/> and only opens the browser.
/// </summary>
public static class DesktopHost
{
    private sealed record Running(string Url);

    /// <summary>Held while this copy runs, so two starts at once don't both open the company file.</summary>
    private static Mutex? _instance;

    /// <summary>True when another copy already serves this company: its page was opened and this process should end.</summary>
    public static async Task<bool> HandOverAsync(Installation installation)
    {
        var name = "OpenBook.Desktop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.DataFolder!.ToUpperInvariant())))[..16];
        _instance = new Mutex(initiallyOwned: true, name, out var first);
        if (first)
            return false;

        // Another copy is starting or running: wait for it to answer. (A file left by a copy that crashed never answers.)
        for (var i = 0; i < 60; i++)
        {
            if (await AnsweringAsync(installation) is { } url)
            {
                Open(installation, url);
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        await Console.Error.WriteLineAsync("OpenBook is already starting for this company but doesn't answer. Try again in a minute.");
        return true;
    }

    /// <summary>Listens on localhost only, whatever the URL settings say; on <see cref="Installation.Port"/> unless it's taken.</summary>
    public static void Listen(WebApplicationBuilder builder, Installation installation)
    {
        var port = FreePort(installation.Port);
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));
    }

    /// <summary>Records the address once listening, opens the browser, and removes the record when stopping.</summary>
    public static void OnStarted(WebApplication app, Installation installation)
    {
        var lifetime = app.Lifetime;
        lifetime.ApplicationStarted.Register(() =>
        {
            // The addresses Kestrel bound (none under a test server).
            var bound = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
            var url = bound?.FirstOrDefault() ?? $"http://localhost:{installation.Port}";
            Directory.CreateDirectory(installation.AppFolder!);
            File.WriteAllText(installation.RunningFile!, JsonSerializer.Serialize(new Running(url)));
            Console.WriteLine($"OpenBook Desktop is running at {url}. Quit it from Settings > System.");
            Open(installation, url);
        });
        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                File.Delete(installation.RunningFile!);
            }
            catch (IOException)
            {
                // The next start checks whether the address answers anyway.
            }
        });
    }

    public static int FreePort(int preferred)
    {
        if (preferred is > 0 and <= IPEndPoint.MaxPort && Available(preferred))
            return preferred;
        using var any = new TcpListener(IPAddress.Loopback, 0);
        any.Start();
        return ((IPEndPoint)any.LocalEndpoint).Port;
    }

    private static bool Available(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<string?> AnsweringAsync(Installation installation)
    {
        Running? running;
        try
        {
            running = File.Exists(installation.RunningFile) ? JsonSerializer.Deserialize<Running>(await File.ReadAllTextAsync(installation.RunningFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }

        if (running is null || !Uri.TryCreate(running.Url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            return null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return (await client.GetAsync(new Uri(uri, "/healthz"))).IsSuccessStatusCode ? running.Url : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static void Open(Installation installation, string url)
    {
        if (!installation.OpenBrowser)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Open {url} in your browser.");
        }
    }
}
