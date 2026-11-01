using System.Text.Json;

namespace OpenBook.Web.Hosting;

/// <summary>How this copy of OpenBook was installed. Never decides data or rules: a company file moves between the two.</summary>
public enum InstallKind
{
    /// <summary>The Docker image, a server or <c>dotnet run</c>: settings come from configuration, as before.</summary>
    Server,

    /// <summary>One person's computer: SQLite in Documents, reachable from this computer only, backups on by default.</summary>
    Desktop,
}

/// <summary>
/// The install (<c>OpenBook:Install</c>, else <see cref="InstallFile"/> beside the app, else Server) and, for Desktop, where its
/// files go. Desktop ignores the database, URL and folder settings: the company file is <c>Documents/OpenBook/openbook.db</c>
/// (<c>OpenBook:Desktop:DataFolder</c> moves it), keys, logs and <see cref="RunningFile"/> are in the user's local app data
/// (<c>OpenBook:Desktop:AppFolder</c>).
/// </summary>
public sealed class Installation
{
    /// <summary>Shipped beside the desktop app (<c>{"Install": "Desktop"}</c>), so its commands act on the desktop company too.</summary>
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

    /// <summary>Desktop: the folder in Documents holding the company file and its backups.</summary>
    public string? DataFolder { get; }

    /// <summary>Desktop: the folder in the user's local app data for keys, logs and <see cref="RunningFile"/>.</summary>
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

    /// <summary>Where a running desktop copy writes its address, so starting it again opens the browser there.</summary>
    public string? RunningFile => AppFolder is null ? null : Path.Combine(AppFolder, "running.json");

    public static Installation Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration["OpenBook:Install"] ?? FromFile(Path.Combine(AppContext.BaseDirectory, InstallFile));
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
