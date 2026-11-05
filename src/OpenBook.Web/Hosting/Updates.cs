using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OpenBook.Application.Updates;

namespace OpenBook.Web.Hosting;

/// <summary>Where an update check stands: the version running, the last check, and what it found.</summary>
/// <param name="Available">A newer release than this one, signed by the release key.</param>
/// <param name="Ready">OpenBook Desktop: its installer, downloaded and checked, ready to run.</param>
/// <param name="Note">Why an available release can't be installed from here (a server, or too old a version to update straight from).</param>
public sealed record UpdateStatus(
    string Current, bool Enabled, DateTimeOffset? LastChecked, string? Error, UpdateManifest? Available, string? Ready, string? Note);

/// <summary>The public key releases must be signed with: the one built in (<see cref="UpdateKeys"/>); only tests use another.</summary>
public sealed record UpdateTrust(string PublicKey)
{
    public static readonly UpdateTrust BuiltIn = new(UpdateKeys.PublicKey);
}

/// <summary>Runs a downloaded installer (a seam for tests).</summary>
public interface IInstallerLauncher
{
    void Start(string installer, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment);
}

internal sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public void Start(string installer, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        // Not a shell start, so the environment carries over; the installer outlives this process, which stops next.
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        Process.Start(start)?.Dispose();
    }
}

/// <summary>
/// Updates: checks the release feed (<c>OpenBook:Updates:Feed</c>, a signed <see cref="UpdateManifest"/>) and, on OpenBook
/// Desktop, downloads the newer installer, checks its size and hash, and installs it on request: a backup first (as when
/// quitting), then the installer runs silently, this server stops, and the installer starts OpenBook again. A server only
/// reports what's available; whoever runs it updates it. Checking is on by default for an installed desktop copy only
/// (not a server, nor a copy run from source); <c>OpenBook:Updates:Check</c> and the System page's switch change that.
/// </summary>
public sealed partial class UpdateService(
    Installation installation, IConfiguration configuration, IHttpClientFactory http, DesktopQuit quit, IInstallerLauncher launcher,
    UpdateTrust trust, ILogger<UpdateService> logger) : IDisposable
{
    public const string HttpClientName = "openbook-updates";
    public const string DefaultFeed = "https://github.com/TronMach/openbook/releases/latest/download/latest.json";

    private readonly SemaphoreSlim _checking = new(1, 1);
    private DateTimeOffset? _lastChecked;
    private string? _error;
    private UpdateManifest? _available;
    private string? _ready;

    /// <summary>The version running, without the build suffix.</summary>
    public static string CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private string Feed => configuration["OpenBook:Updates:Feed"] is { Length: > 0 } feed ? feed : DefaultFeed;

    // Desktop: the person's own choice from the System page, beside the keys.
    private string? ChoiceFile => installation.AppFolder is null ? null : Path.Combine(installation.AppFolder, "updates.json");

    private string? DownloadFolder => installation.AppFolder is null ? null : Path.Combine(installation.AppFolder, "updates");

    public bool Enabled
    {
        get
        {
            if (ChoiceFile is { } file && File.Exists(file))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    if (json.RootElement.TryGetProperty("Check", out var check) && check.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return check.GetBoolean();
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                }
            }

            return configuration.GetValue<bool?>("OpenBook:Updates:Check")
                ?? installation.IsDesktop && File.Exists(Path.Combine(AppContext.BaseDirectory, Installation.InstallFile));
        }
    }

    /// <summary>Desktop: remembers whether to check automatically. A server's checking is set in its configuration.</summary>
    public void SetEnabled(bool enabled)
    {
        if (ChoiceFile is not { } file)
            throw new InvalidOperationException("Only OpenBook Desktop keeps this choice; a server sets OpenBook:Updates:Check.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new { Check = enabled }));
    }

    public UpdateStatus Status => new(CurrentVersion, Enabled, _lastChecked, _error, _available, _ready, Note(_available));

    private string? Note(UpdateManifest? available) => available switch
    {
        null => null,
        _ when !installation.IsDesktop => "Update the server yourself: get the new source, build it and restart (see the server guide).",
        _ when available.MinimumVersion is { } minimum && UpdateVersion.IsNewer(minimum, CurrentVersion) =>
            $"Version {available.Version} needs version {minimum} or later to update from; download its installer from the release notes instead.",
        _ => null,
    };

    /// <summary>Asks the feed for the latest release and, on desktop, downloads it. Failures are kept, not thrown.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        await _checking.WaitAsync(ct);
        try
        {
            _lastChecked = DateTimeOffset.UtcNow;
            _error = null;
            var client = http.CreateClient(HttpClientName);
            var manifest = UpdateManifest.Parse(await client.GetStringAsync(new Uri(Safe(Feed)), ct));
            if (manifest is null)
                throw new UpdateException("The release feed didn't send an update manifest.");
            if (!manifest.IsSignedBy(trust.PublicKey))
                throw new UpdateException("The release feed's manifest isn't signed by OpenBook's release key, so it was ignored.");
            if (!UpdateVersion.IsNewer(manifest.Version, CurrentVersion))
            {
                (_available, _ready) = (null, null);
                return Status;
            }

            _available = manifest;
            if (installation.IsDesktop && Note(manifest) is null && _ready is null)
                _ready = await DownloadAsync(client, manifest, ct);
            return Status;
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException or IOException or UriFormatException && !ct.IsCancellationRequested)
        {
            _error = ex is TaskCanceledException ? "The release feed didn't answer in time." : ex.Message;
            LogCheckFailed(_error);
            return Status;
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// OpenBook Desktop: installs the downloaded release. Backs up first; unless <paramref name="evenIfBackupFails"/>, a failed
    /// backup stops it. Then starts the installer silently (it closes anything still running from the program folder and
    /// starts OpenBook again with the same folders) and stops this server. Returns why it didn't, or null once started.
    /// </summary>
    public async Task<string?> InstallAsync(bool evenIfBackupFails, CancellationToken ct = default)
    {
        if (!installation.IsDesktop)
            return "Only OpenBook Desktop installs its own updates.";
        if (_ready is not { } installer || _available is not { } release || !File.Exists(installer))
            return "No update is downloaded yet. Check for updates first.";

        var backup = await quit.BackUpAsync(ct);
        if (backup is { Succeeded: false } && !evenIfBackupFails)
            return DesktopQuit.Describe(backup);

        LogInstalling(release.Version);
        launcher.Start(installer,
            ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1", $"/LOG={Path.Combine(DownloadFolder!, "install.log")}"],
            new Dictionary<string, string>
            {
                // So the copy the installer starts uses the same folders, port and browser setting as this one.
                ["OpenBook__Desktop__DataFolder"] = installation.DataFolder!,
                ["OpenBook__Desktop__AppFolder"] = installation.AppFolder!,
                ["OpenBook__Desktop__Port"] = installation.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["OpenBook__Desktop__OpenBrowser"] = installation.OpenBrowser ? "true" : "false",
            });
        quit.Stop();
        return null;
    }

    // The installer lands in the app folder's updates folder under its own name, checked against the manifest before it's kept.
    private async Task<string> DownloadAsync(HttpClient client, UpdateManifest manifest, CancellationToken ct)
    {
        var folder = DownloadFolder!;
        Directory.CreateDirectory(folder);
        var version = UpdateVersion.Parse(manifest.Version)!;
        var target = Path.Combine(folder, $"OpenBook-Setup-{(version.Revision == 0 ? version.ToString(3) : version.ToString())}.exe");
        if (!File.Exists(target) || !await MatchesAsync(target, manifest, ct))
        {
            var part = target + ".part";
            await using (var source = await client.GetStreamAsync(new Uri(Safe(manifest.Url)), ct))
            await using (var file = File.Create(part))
                await source.CopyToAsync(file, ct);
            if (!await MatchesAsync(part, manifest, ct))
            {
                File.Delete(part);
                throw new UpdateException($"The download of version {manifest.Version} didn't match its manifest, so it was thrown away.");
            }

            File.Move(part, target, overwrite: true);
        }

        // Only the newest installer is kept.
        foreach (var old in Directory.GetFiles(folder, "OpenBook-Setup-*.exe").Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
            File.Delete(old);
        LogDownloaded(manifest.Version);
        return target;
    }

    private static async Task<bool> MatchesAsync(string path, UpdateManifest manifest, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return file.Length == manifest.Size && string.Equals(await UpdateManifest.HashAsync(file, ct), manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Releases come over HTTPS (plain HTTP only from this computer, for testing).
    private static string Safe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            ? url
            : throw new UpdateException($"Updates only come over HTTPS; {url} isn't.");

    public void Dispose() => _checking.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking for updates failed: {Error}")]
    private partial void LogCheckFailed(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenBook {Version} downloaded and checked.")]
    private partial void LogDownloaded(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing OpenBook {Version}; stopping.")]
    private partial void LogInstalling(string version);
}

public sealed class UpdateException(string message) : Exception(message);

/// <summary>Checks for updates a minute after starting and then once a day, while checking is switched on.</summary>
internal sealed class UpdateCheckService(UpdateService updates, Diagnostics.JobStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            if (!updates.Enabled)
                continue;
            var started = DateTimeOffset.UtcNow;
            var result = await updates.CheckAsync(stoppingToken);
            status.Record("Update check", started, DateTimeOffset.UtcNow - started, result.Error);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
