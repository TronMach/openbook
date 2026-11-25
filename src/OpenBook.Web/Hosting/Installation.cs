using System.Text.Json;

namespace OpenBook.Web.Hosting;

/// <summary>How this copy of OpenBook was installed. Never decides data or rules: a company file moves between the two.</summary>
public enum InstallKind
{
    /// <summary>The Docker image, a server or <c>dotnet run</c>: settings come from configuration, as before.</summary>
    Server,

    /// <summary>One person's computer: SQLite in Documents, reachable from this computer only, backups on by default.</summary>
    Desktop,

    /// <summary>
    /// The Windows installer's "several people" choice: a server in every way, run as a Windows service
    /// (<see cref="WindowsServerHost"/>), with its files in one folder only administrators and the service can open.
    /// </summary>
    WindowsServer,
}

/// <summary>
/// The install (<c>OpenBook:Install</c>, else <see cref="InstallFile"/> beside the app, else Server) and, for Desktop and Windows
/// Server, where its files go. Desktop ignores the database, URL and folder settings: the company file is
/// <c>Documents/OpenBook/openbook.db</c> (<c>OpenBook:Desktop:DataFolder</c> moves it), keys, logs and <see cref="RunningFile"/>
/// are in the user's local app data (<c>OpenBook:Desktop:AppFolder</c>). Windows Server keeps everything in
/// <c>%ProgramData%\OpenBook</c> (<c>OpenBook:WindowsServer:Folder</c>), settings in its <see cref="SettingsFile"/>.
/// </summary>
public sealed class Installation
{
    /// <summary>Put beside the app by the installer (<c>{"Install": "Desktop"}</c> or <c>"WindowsServer"</c>), so its commands act on the same company.</summary>
    public const string InstallFile = "openbook.install.json";

    public const int DefaultPort = 5180;

    private Installation(InstallKind kind, string? dataFolder, string? appFolder, int port, bool openBrowser)
    {
        Kind = kind;
        DataFolder = dataFolder;
        AppFolder = appFolder;
        Port = port;
        OpenBrowser = openBrowser;
    }

    public InstallKind Kind { get; }
    public bool IsDesktop => Kind == InstallKind.Desktop;
    public bool IsWindowsServer => Kind == InstallKind.WindowsServer;

    /// <summary>Desktop: the folder in Documents holding the company file and its backups. Windows Server: its one folder.</summary>
    public string? DataFolder { get; }

    /// <summary>The folder for keys, logs and <see cref="RunningFile"/>: in the user's local app data on Desktop, the same as <see cref="DataFolder"/> on Windows Server.</summary>
    public string? AppFolder { get; }

    /// <summary>Desktop: the port tried first; a free one is used when it's taken.</summary>
    public int Port { get; }

    /// <summary>Desktop: open the browser once started (off for tests and previews).</summary>
    public bool OpenBrowser { get; }

    public string? CompanyFile => DataFolder is null ? null : Path.Combine(DataFolder, "openbook.db");
    public string? BackupFolder => DataFolder is null ? null : Path.Combine(DataFolder, "Backups");
    public string? KeysFolder => AppFolder is null ? null : Path.Combine(AppFolder, "keys");
    public string? LogFolder => AppFolder is null ? null : Path.Combine(AppFolder, "logs");

    /// <summary>Desktop: add-ins live outside the program folder, which an update replaces.</summary>
    public string? PluginsFolder => AppFolder is null ? null : Path.Combine(AppFolder, "plugins");

    /// <summary>
    /// Where a running copy writes its address: on Desktop so starting it again opens the browser there, on Windows Server for
    /// the installer.
    /// </summary>
    public string? RunningFile => AppFolder is null ? null : Path.Combine(AppFolder, "running.json");

    /// <summary>Windows Server: its settings (the port, certificates and any other OpenBook setting), read at startup.</summary>
    public string? SettingsFile => IsWindowsServer ? Path.Combine(DataFolder!, "settings.json") : null;

    /// <summary>Windows Server: held open while it runs, so two copies never serve one company file.</summary>
    public string? LockFile => IsWindowsServer ? Path.Combine(DataFolder!, "running.lock") : null;

    /// <summary>Windows Server: the setup code, until a company exists; the installer opens the setup page with it.</summary>
    public string? SetupCodeFile => IsWindowsServer ? Path.Combine(DataFolder!, "setup-code.txt") : null;

    public static Installation Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration["OpenBook:Install"] ?? FromFile(Path.Combine(AppContext.BaseDirectory, InstallFile));
        if (string.Equals(configured?.Trim(), nameof(InstallKind.WindowsServer), StringComparison.OrdinalIgnoreCase))
        {
            var folder = configuration["OpenBook:WindowsServer:Folder"] is { Length: > 0 } chosen
                ? chosen
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBook");
            return new Installation(InstallKind.WindowsServer, Path.GetFullPath(folder), Path.GetFullPath(folder), 0, false);
        }

        if (!string.Equals(configured?.Trim(), nameof(InstallKind.Desktop), StringComparison.OrdinalIgnoreCase))
            return new Installation(InstallKind.Server, null, null, 0, false);

        var data = configuration["OpenBook:Desktop:DataFolder"] is { Length: > 0 } dataFolder
            ? dataFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create), "OpenBook");
        var app = configuration["OpenBook:Desktop:AppFolder"] is { Length: > 0 } appFolder
            ? appFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "OpenBook");
        var port = configuration.GetValue("OpenBook:Desktop:Port", DefaultPort);
        return new Installation(InstallKind.Desktop, Path.GetFullPath(data), Path.GetFullPath(app), port,
            configuration.GetValue("OpenBook:Desktop:OpenBrowser", true));
    }

    /// <summary>The <c>Install</c> value of the file beside the app, if there is one.</summary>
    public static string? FromFile(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("Install", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
