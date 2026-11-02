using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Updates;
using OpenBook.Domain.Companies;
using OpenBook.Web.Hosting;

namespace OpenBook.Integration.Tests;

public sealed class UpdateManifestTests
{
    private static UpdateManifest Sample => new("1.2.0", "https://example.test/OpenBook-Setup-1.2.0.exe", new string('a', 64), 1234, null,
        "https://example.test/notes", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_signed_manifest_checks_out_only_unchanged_and_only_with_its_own_key()
    {
        var (privateKey, publicKey) = UpdateManifest.NewKeyPair();
        var signed = Sample.SignWith(privateKey);
        Assert.True(signed.IsSignedBy(publicKey));

        var parsed = UpdateManifest.Parse(signed.ToJson());
        Assert.NotNull(parsed);
        Assert.True(parsed.IsSignedBy(publicKey));

        // Any field changed, or another key, and it's refused.
        Assert.False((signed with { Url = "https://evil.test/setup.exe" }).IsSignedBy(publicKey));
        Assert.False((signed with { Sha256 = new string('b', 64) }).IsSignedBy(publicKey));
        Assert.False((signed with { Version = "9.0.0" }).IsSignedBy(publicKey));
        Assert.False(signed.IsSignedBy(UpdateManifest.NewKeyPair().PublicKeyPem));
        Assert.False((signed with { Signature = "not base64!" }).IsSignedBy(publicKey));
        // The key built into the app doesn't trust a manifest this test signed.
        Assert.False(signed.IsSignedBy(UpdateKeys.PublicKey));
    }

    [Fact]
    public void Versions_compare_by_their_numbers()
    {
        Assert.True(UpdateVersion.IsNewer("1.10.0", "1.9.3"));
        Assert.True(UpdateVersion.IsNewer("v2.0", "1.99.99"));
        Assert.False(UpdateVersion.IsNewer("1.2", "1.2.0"));
        Assert.False(UpdateVersion.IsNewer("1.2.0", "1.2.0+abc123"));
        Assert.False(UpdateVersion.IsNewer("1.1.9-beta", "1.2.0"));
        Assert.False(UpdateVersion.IsNewer("garbage", "1.0.0"));
        Assert.Null(UpdateManifest.Parse("{\"version\":\"1.0.0\"}"));
    }
}

/// <summary>Updates through the real desktop app, with a fake release feed and an installer launcher that only records.</summary>
public sealed class UpdateFlowTests : IAsyncLifetime
{
    private const string Feed = "https://updates.test/latest.json";
    private const string Installer = "https://updates.test/OpenBook-Setup-99.1.0.exe";
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("pretend this is an installer");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-update-{Guid.NewGuid():N}");
    private readonly (string Private, string Public) _key = UpdateManifest.NewKeyPair();
    private readonly FakeReleases _releases = new();
    private readonly RecordingLauncher _launcher = new();
    private WebApplicationFactory<Program>? _app;

    private string AppFolder => Path.Combine(_folder, "AppData");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<UpdateService> StartAsync(bool desktop = true)
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (desktop)
            {
                b.UseSetting("OpenBook:Install", "Desktop");
                b.UseSetting("OpenBook:Desktop:DataFolder", Path.Combine(_folder, "Documents"));
                b.UseSetting("OpenBook:Desktop:AppFolder", AppFolder);
                b.UseSetting("OpenBook:Desktop:OpenBrowser", "false");
            }
            else
            {
                b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={Path.Combine(_folder, "server.db")}");
                b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            }

            b.UseSetting("OpenBook:Updates:Feed", Feed);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton(new UpdateTrust(_key.Public));
                services.AddSingleton<IInstallerLauncher>(_launcher);
                services.AddHttpClient(UpdateService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _releases);
            });
        });
        _ = _app.Server;
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Desk Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery"));
        if (desktop)
            await scope.ServiceProvider.GetRequiredService<ScheduledBackupService>().SetUpDefaultAsync(Path.Combine(_folder, "Documents", "Backups"));
        return _app.Services.GetRequiredService<UpdateService>();
    }

    private void Publish(string version, byte[]? served = null, string? signWith = null)
    {
        var manifest = new UpdateManifest(version, Installer, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(InstallerBytes)),
            InstallerBytes.Length, null, "https://updates.test/notes", DateTimeOffset.UtcNow).SignWith(signWith ?? _key.Private);
        _releases.Manifest = manifest.ToJson();
        _releases.Installer = served ?? InstallerBytes;
    }

    [Fact]
    public async Task A_newer_signed_release_is_downloaded_checked_and_installed_after_a_backup()
    {
        var updates = await StartAsync();
        Publish("99.1.0");

        var status = await updates.CheckAsync();
        Assert.Null(status.Error);
        Assert.Equal("99.1.0", status.Available?.Version);
        Assert.Equal(Path.Combine(AppFolder, "updates", "OpenBook-Setup-99.1.0.exe"), status.Ready);
        Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(status.Ready!));

        // The tray asks over its pipe.
        Assert.Equal("ready 99.1.0", await PipeAsync("update"));

        var stopping = new TaskCompletionSource();
        _app!.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        Assert.Null(await updates.InstallAsync(evenIfBackupFails: false));
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var (path, arguments, environment) = Assert.Single(_launcher.Started);
        Assert.Equal(status.Ready, path);
        Assert.Contains("/VERYSILENT", arguments);
        Assert.Contains("/RELAUNCH=1", arguments);
        Assert.Equal(AppFolder, environment["OpenBook__Desktop__AppFolder"]);
        Assert.Equal("false", environment["OpenBook__Desktop__OpenBrowser"]);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "Documents", "Backups"), ScheduledBackupService.QuitPrefix + "*"));
    }

    [Fact]
    public async Task Releases_not_signed_by_the_release_key_or_not_matching_their_manifest_are_refused()
    {
        var updates = await StartAsync();

        Publish("99.1.0", signWith: UpdateManifest.NewKeyPair().PrivateKeyPem);
        var status = await updates.CheckAsync();
        Assert.Contains("isn't signed by OpenBook's release key", status.Error, StringComparison.Ordinal);
        Assert.Null(status.Available);

        Publish("99.1.0", served: Encoding.UTF8.GetBytes("something else entirely"));
        status = await updates.CheckAsync();
        Assert.Contains("didn't match its manifest", status.Error, StringComparison.Ordinal);
        Assert.Null(status.Ready);
        Assert.Empty(Directory.GetFiles(Path.Combine(AppFolder, "updates")));
        Assert.Equal("No update is downloaded yet. Check for updates first.", await updates.InstallAsync(false));
        Assert.Empty(_launcher.Started);

        // An older or equal version isn't offered.
        Publish("0.0.1");
        status = await updates.CheckAsync();
        Assert.Null(status.Error);
        Assert.Null(status.Available);
    }

    [Fact]
    public async Task A_server_only_says_what_is_available_and_checks_only_when_told_to()
    {
        var updates = await StartAsync(desktop: false);
        Assert.False(updates.Enabled);
        Publish("99.1.0");

        var status = await updates.CheckAsync();
        Assert.Equal("99.1.0", status.Available?.Version);
        Assert.Null(status.Ready);
        Assert.Contains("pull the new image", status.Note, StringComparison.Ordinal);
        Assert.Equal("Only OpenBook Desktop installs its own updates.", await updates.InstallAsync(false));
        Assert.Equal(0, _releases.InstallerRequests);
    }

    private async Task<string?> PipeAsync(string command)
    {
        var installation = _app!.Services.GetRequiredService<Installation>();
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", DesktopHost.ControlPipe(installation), System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(10_000);
        using var reader = new StreamReader(client, leaveOpen: true);
        await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(command);
        return await reader.ReadLineAsync();
    }

    private sealed class FakeReleases : HttpMessageHandler
    {
        public string Manifest { get; set; } = "";
        public byte[] Installer { get; set; } = [];
        public int InstallerRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == Feed)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) });
            if (url == UpdateFlowTests.Installer)
            {
                InstallerRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RecordingLauncher : IInstallerLauncher
    {
        public List<(string Path, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)> Started { get; } = [];

        public void Start(string installer, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) =>
            Started.Add((installer, arguments, environment));
    }
}
