using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Infrastructure;
using OpenBook.Web.Security;

namespace OpenBook.Web.Hosting;

/// <summary>
/// Running as OpenBook Server for Windows: a Windows service (<see cref="ServiceName"/>) for the people on an office network,
/// put there by the installer (<see cref="Cli.WindowsServerCommands"/>). One copy per folder, listening on every address of the
/// computer, with its settings in <see cref="Installation.SettingsFile"/>.
/// </summary>
public static partial class WindowsServerHost
{
    /// <summary>The Windows service's name, and so its account's: <c>NT SERVICE\OpenBook</c>.</summary>
    public const string ServiceName = "OpenBook";

    /// <summary>Not the desktop's 5180, so one computer can have both.</summary>
    public const int DefaultPort = 5190;

    public const string PortSetting = "OpenBook:WindowsServer:Port";

    /// <summary>The address other computers use (<c>https://books.example.com</c>), when it isn't the computer's name and port.</summary>
    public const string AddressSetting = "OpenBook:WindowsServer:Address";

    /// <summary>What <see cref="Installation.RunningFile"/> holds.</summary>
    internal sealed record Running(string Url);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Adds the folder's settings file, under environment variables and arguments, which still win. A file that isn't JSON
    /// stops OpenBook with what's wrong rather than being passed over.
    /// </summary>
    public static void AddSettings(IConfigurationBuilder configuration, Installation installation)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.SettingsFile is not { } file)
            return;
        var last = configuration.Sources.ToList().FindLastIndex(s => s is JsonConfigurationSource);
        var source = new JsonConfigurationSource { Path = file, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();
        configuration.Sources.Insert(last + 1, source);
    }

    /// <summary>The port in the settings; the default when there's none, or a nonsensical one.</summary>
    public static int Port(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return int.TryParse(configuration[PortSetting], out var port) && port is > 0 and <= IPEndPoint.MaxPort ? port : DefaultPort;
    }

    /// <summary>Listens on every address, unless the settings give Kestrel endpoints of their own (HTTPS with a certificate, say).</summary>
    public static void Listen(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            return;
        var port = Port(builder.Configuration);
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
    }

    /// <summary>
    /// The folder's lock, held (and given to <see cref="OnStarted"/>) while this copy runs; the system lets go of it whatever way
    /// the process ends. Null, with why, when another copy already runs on the folder or it can't be written to.
    /// </summary>
    public static FileStream? TakeLock(Installation installation, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(installation);
        problem = null;
        try
        {
            Directory.CreateDirectory(installation.DataFolder!);
            return OpenLock(installation);
        }
        catch (UnauthorizedAccessException)
        {
            problem = $"OpenBook Server runs as a Windows service, and only it and administrators can open {installation.DataFolder}. Open it in your browser from the Start menu.";
        }
        catch (IOException)
        {
            problem = "OpenBook Server is already running on this computer, as a Windows service. Open it in your browser from the Start menu.";
        }

        return null;
    }

    /// <summary>Whether a copy runs on this folder; one that can't be opened counts as running, so commands stay away from it.</summary>
    public static bool IsRunning(Installation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!File.Exists(installation.LockFile))
            return false;
        try
        {
            using var taken = OpenLock(installation);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static FileStream OpenLock(Installation installation) =>
        new(installation.LockFile!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>Why a command can't run here (the folder is the administrators'); null when it can.</summary>
    public static string? NoAccess(Installation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!installation.IsWindowsServer || !Directory.Exists(installation.DataFolder))
            return null;
        try
        {
            _ = Directory.EnumerateFileSystemEntries(installation.DataFolder).Any();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"OpenBook Server runs as a Windows service: open it in your browser from the Start menu. Only administrators can open {installation.DataFolder}, so for its commands open the command prompt with \"Run as administrator\".";
        }
    }

    /// <summary>Once started: writes the setup code (while there's no company) and then the address, which tells the installer it's up.</summary>
    public static void OnStarted(WebApplication app, Installation installation, FileStream folderLock)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(folderLock);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var code = app.Services.GetRequiredService<SetupCode>();
            using var scope = app.Services.CreateScope();
            var isSetUp = app.Services.GetRequiredService<SetupState>()
                .CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), CancellationToken.None).GetAwaiter().GetResult();
            if (isSetUp || code.File is null)
                File.Delete(installation.SetupCodeFile!);
            else
                File.WriteAllText(installation.SetupCodeFile!, code.Display);
            File.WriteAllText(installation.RunningFile!, JsonSerializer.Serialize(new Running(Address(app.Services, app.Configuration)), Json));
        });
        app.Lifetime.ApplicationStopping.Register(() => TryDelete(installation.RunningFile!));
        app.Lifetime.ApplicationStopped.Register(folderLock.Dispose);
    }

    /// <summary>The address people type on other computers: the one in the settings, else this computer's name with the port it listens on.</summary>
    public static string Address(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration[AddressSetting] is { Length: > 0 } chosen)
            return chosen.TrimEnd('/');
        var (scheme, port) = Bound(services, configuration);
        return $"{scheme}://{HostName}:{port}";
    }

    /// <summary>The same by number, for devices that don't find the computer by its name.</summary>
    public static IReadOnlyList<string> AddressesByNumber(IServiceProvider services, IConfiguration configuration)
    {
        var (scheme, port) = Bound(services, configuration);
        return [.. NetworkAddresses().Select(a => $"{scheme}://{a}:{port}")];
    }

    /// <summary>Windows computer names are in capitals; addresses read better without.</summary>
    internal static string HostName => Environment.MachineName.ToLowerInvariant();

    // What Kestrel bound; under a test server, which binds nothing, what it would.
    private static (string Scheme, int Port) Bound(IServiceProvider services, IConfiguration configuration)
    {
        var bound = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var parsed = bound.Select(a => BindingAddress.Parse(a)).Where(a => !a.IsUnixPipe && a.Port != 0).ToList();
        var best = parsed.FirstOrDefault(a => string.Equals(a.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ?? parsed.FirstOrDefault();
        return best is null ? (Uri.UriSchemeHttp, Port(configuration)) : (best.Scheme, best.Port);
    }

    // This computer's addresses on a network with a router: not virtual machines' or disconnected ones.
    private static IReadOnlyList<string> NetworkAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The start of links meant for other people: the request's own address, except on Windows Server for someone at the
    /// server itself, whose "localhost" would open on nobody else's computer.
    /// </summary>
    public static string LinkBase(HttpContext http, Installation installation, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(installation);
        return installation.IsWindowsServer && DesktopHost.LocalHosts.Contains(http.Request.Host.Host, StringComparer.OrdinalIgnoreCase)
            ? Address(http.RequestServices, configuration) + http.Request.PathBase
            : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
    }

    /// <summary>The address a running copy wrote, or null when none has.</summary>
    internal static string? RunningUrl(Installation installation)
    {
        try
        {
            return File.Exists(installation.RunningFile) ? JsonSerializer.Deserialize<Running>(File.ReadAllText(installation.RunningFile))?.Url : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Written again by the next start.
        }
    }

    /// <summary>Migrates the database and takes the company's time zone, before anything is served.</summary>
    public static async Task PrepareDatabaseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await services.MigrateOpenBookDatabaseAsync(ct);
        await using var scope = services.CreateAsyncScope();
        CompanyTime.Use(await scope.ServiceProvider.GetRequiredService<ICompanyStore>().GetCompanyAsync(ct));
    }

    /// <summary>
    /// Before the rest of a Windows service starts: brings the database up to date. Not before the host runs, as elsewhere,
    /// because Windows gives up on a service that hasn't reported in 30 seconds and a large company's upgrade can take longer;
    /// by now it has reported (<see cref="WindowsServiceLifetime"/> does before any hosted service starts).
    /// </summary>
    internal sealed partial class Startup(IServiceProvider services, ILogger<Startup> logger) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            try
            {
                await PrepareDatabaseAsync(services, cancellationToken);
            }
            catch (Exception ex)
            {
                LogFailed(ex);
                throw;
            }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Critical, Message = "OpenBook couldn't open or update the company file, so it stopped.")]
        private partial void LogFailed(Exception error);
    }
}
