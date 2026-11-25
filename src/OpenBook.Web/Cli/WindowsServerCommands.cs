using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using OpenBook.Web.Hosting;

namespace OpenBook.Web.Cli;

/// <summary>
/// What the Windows installer runs for "several people" (administrators only): <c>install-service</c> makes the folder, the
/// settings, the service and the firewall rule, and starts it; <c>stop-service</c> before its files are replaced;
/// <c>remove-service</c> when it's uninstalled (the folder with the books stays). They can be run by hand, and again: what's
/// already there is kept or put right.
/// </summary>
public static class WindowsServerCommands
{
    public const string Install = "install-service";
    public const string Remove = "remove-service";
    public const string Stop = "stop-service";

    public const string StopFirst = "OpenBook Server is running. Stop it first (net stop OpenBook), and start it again afterwards (net start OpenBook). Nothing was changed.";

    internal const string FirewallRule = "OpenBook Server";

    // A large company's upgrade takes minutes; nothing else does.
    private static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// <paramref name="args"/>: the command, then <c>--port n</c> (a first install's; a free one otherwise), <c>--no-start</c>, and <c>--result file</c>,
    /// where the installer reads how it went: "ok" and the address to open, or "error" and why.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = Option(args, "--result");
        if (!OperatingSystem.IsWindows())
            return Finish(result, "This is for Windows; on other systems use the Docker image (docs/admin/server.md).");
        if (!Environment.IsPrivilegedProcess)
            return Finish(result, "Only administrators can do this: open the command prompt with \"Run as administrator\".");

        // The same settings the app reads, without building it: the service may not exist yet.
        var settings = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        var installation = Installation.Read(settings);
        if (!installation.IsWindowsServer)
            return Finish(result, $"This copy isn't OpenBook Server for Windows: {Installation.InstallFile} beside it doesn't say \"WindowsServer\".");

        try
        {
            switch (args[0])
            {
                case Stop:
                    StopService();
                    return Finish(result, null, "Stopped.");
                case Remove:
                    RemoveService();
                    return Finish(result, null, $"The service is gone. Your books are still in {installation.DataFolder}.");
                default:
                    InstallService(installation, Option(args, "--port"));
                    return args.Contains("--no-start", StringComparer.OrdinalIgnoreCase)
                        ? Finish(result, null, "Ready to start.")
                        : Finish(result, null, await StartAsync(installation));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception
            or System.ServiceProcess.TimeoutException or IdentityNotMappedException or JsonException)
        {
            return Finish(result, ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}");
        }
    }

    /// <summary>Says how a command went, on the console and, for the installer, in the file it asked for.</summary>
    internal static int Finish(string? result, string? error, string? done = null)
    {
        if (result is not null)
            File.WriteAllLines(result, error is null ? ["ok", done ?? ""] : ["error", error.ReplaceLineEndings(" ")], new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (error is null)
            Console.WriteLine(done);
        else
            Console.Error.WriteLine(error);
        return error is null ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static void InstallService(Installation installation, string? port)
    {
        if (port is not null && (!int.TryParse(port, out var number) || number is < 1 or > 65535))
            throw new InvalidOperationException($"{port} isn't a port: choose a number from 1 to 65535.");

        StopService();
        var folder = MakeFolder(installation);
        var settings = WriteSettings(installation, port);

        var program = Path.Combine(AppContext.BaseDirectory, "OpenBook.Web.exe");
        // A virtual account: no password, no rights of its own beyond its folder.
        if (Service() is null)
        {
            Run("sc.exe", "create", WindowsServerHost.ServiceName, "binPath=", $"\"{program}\"", "start=", "auto",
                "obj=", $@"NT SERVICE\{WindowsServerHost.ServiceName}", "DisplayName=", "OpenBook Server");
        }
        else
        {
            Run("sc.exe", "config", WindowsServerHost.ServiceName, "binPath=", $"\"{program}\"", "start=", "auto");
        }

        Run("sc.exe", "description", WindowsServerHost.ServiceName, "OpenBook bookkeeping for the people on this network, in their browsers.");
        // After a crash, started again in 10 seconds, then 30, then every minute; a quiet day starts the count over.
        Run("sc.exe", "failure", WindowsServerHost.ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000");

        Protect(folder);

        // Reachable from a home, office or domain network, never from one Windows calls public.
        RunAnyway("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        Run("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", "protocol=TCP",
            $"localport={WindowsServerHost.Port(settings)}", "profile=private,domain", $"program={program}");
    }

    /// <summary>
    /// The folder, refused when someone who isn't an administrator made it: anyone may make folders in ProgramData, and
    /// what they left there (add-ins, say) would run as the service.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static DirectoryInfo MakeFolder(Installation installation)
    {
        var folder = new DirectoryInfo(installation.DataFolder!);
        if (!folder.Exists)
        {
            folder.Create();
        }
        else if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || !TrustedOwner(folder))
        {
            throw new InvalidOperationException($"{folder.FullName} is already there and wasn't made by OpenBook or an administrator. Look at what's in it, rename or delete it, and try again.");
        }

        return folder;
    }

    [SupportedOSPlatform("windows")]
    private static bool TrustedOwner(DirectoryInfo folder) =>
        folder.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
        && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));

    /// <summary>Only administrators, Windows and the service: the books, their backups and the keys to stored secrets are in it.</summary>
    [SupportedOSPlatform("windows")]
    private static void Protect(DirectoryInfo folder)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        foreach (var (who, rights) in new (IdentityReference, FileSystemRights)[]
        {
            (administrators, FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
            (new NTAccount("NT SERVICE", WindowsServerHost.ServiceName).Translate(typeof(SecurityIdentifier)), FileSystemRights.Modify),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        folder.SetAccessControl(security);
    }

    /// <summary>The settings file is written once, with the port; after that it's whoever runs the server's to change.</summary>
    internal static IConfiguration WriteSettings(Installation installation, string? port)
    {
        var file = installation.SettingsFile!;
        if (!File.Exists(file))
        {
            File.WriteAllText(file, $$"""
                {
                  "OpenBook": {
                    "WindowsServer": {
                      "Port": {{port ?? FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                    }
                  }
                }

                """);
        }

        try
        {
            return new ConfigurationBuilder().AddJsonFile(file, optional: false).Build();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"{file} can't be read: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>The usual port, or the next one nothing else on this computer listens on.</summary>
    internal static int FreePort()
    {
        var taken = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(l => l.Port).ToHashSet();
        return Enumerable.Range(WindowsServerHost.DefaultPort, 100).FirstOrDefault(p => !taken.Contains(p), WindowsServerHost.DefaultPort);
    }

    /// <summary>Starts the service, waits until it serves pages, and gives the address to open: the setup page, with its code, while there's no company.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<string> StartAsync(Installation installation)
    {
        File.Delete(installation.RunningFile!);
        using var service = Service() ?? throw new InvalidOperationException("The OpenBook service isn't installed.");
        if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            service.Start();

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StartLimit)
        {
            // The installer opens it, so nothing but a web address gets through.
            if (WindowsServerHost.RunningUrl(installation) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return File.Exists(installation.SetupCodeFile)
                    ? $"{uri.GetLeftPart(UriPartial.Authority)}/setup?code={Uri.EscapeDataString((await File.ReadAllTextAsync(installation.SetupCodeFile)).Trim())}"
                    : $"{uri.GetLeftPart(UriPartial.Authority)}/";
            }

            await Task.Delay(500);
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped && watch.Elapsed > TimeSpan.FromSeconds(5))
                break;
        }

        throw new InvalidOperationException($"OpenBook Server was installed but doesn't start. The newest log in {installation.LogFolder} says why (another program using its port, for instance: change it in {installation.SettingsFile}).");
    }

    [SupportedOSPlatform("windows")]
    private static void StopService()
    {
        using (var service = Service())
        {
            if (service is null)
                return;
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending)
                    service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, StopLimit);
            }
        }

        // The process ends just after Windows calls the service stopped; until then its files can't be replaced.
        foreach (var process in Process.GetProcessesByName("OpenBook.Web"))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && process.SessionId == 0)
                    process.WaitForExit(StopLimit);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveService()
    {
        StopService();
        using (var service = Service())
        {
            if (service is not null)
                Run("sc.exe", "delete", WindowsServerHost.ServiceName);
        }

        RunAnyway("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
    }

    [SupportedOSPlatform("windows")]
    private static ServiceController? Service()
    {
        ServiceController? found = null;
        foreach (var service in ServiceController.GetServices())
        {
            if (string.Equals(service.ServiceName, WindowsServerHost.ServiceName, StringComparison.OrdinalIgnoreCase))
                found = service;
            else
                service.Dispose();
        }

        return found;
    }

    private static void Run(string program, params string[] arguments)
    {
        var (code, output) = Execute(program, arguments);
        if (code != 0)
            throw new InvalidOperationException($"{program} {arguments[0]} failed ({code}): {output}");
    }

    /// <summary>For what may not be there to begin with.</summary>
    private static void RunAnyway(string program, params string[] arguments) => Execute(program, arguments);

    private static (int Code, string Output) Execute(string program, string[] arguments)
    {
        // By full path, so a program of the same name in the current folder is never run as administrator.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} couldn't be started.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd() + errors.GetAwaiter().GetResult();
        process.WaitForExit();
        return (process.ExitCode, string.Join(' ', output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    private static string? Option(string[] args, string name) => MoveToPostgresCommand.Option(args, name);
}
