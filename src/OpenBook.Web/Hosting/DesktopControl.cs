using System.IO.Pipes;
using System.Security.Claims;
using System.Text;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Web.Security;

namespace OpenBook.Web.Hosting;

/// <summary>
/// Quitting OpenBook Desktop: a backup to the schedule's destination first (so closing always leaves a fresh copy), then
/// the server stops. Used by Settings > System's Quit button and by the tray icon through <see cref="DesktopControlService"/>.
/// </summary>
public sealed class DesktopQuit(IServiceScopeFactory scopes, SetupState setup, IHostApplicationLifetime lifetime)
{
    private static readonly ClaimsPrincipal SystemPrincipal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, RecurringPostingService.Actor)], authenticationType: "system"));

    /// <summary>Null when there's nothing to back up yet (no company, or no backup schedule).</summary>
    public async Task<BackupRunResult?> BackUpAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!await setup.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), ct))
            return null;
        scope.ServiceProvider.GetRequiredService<CircuitUser>().Principal = SystemPrincipal;
        return await scope.ServiceProvider.GetRequiredService<ScheduledBackupService>().RunBeforeQuitAsync(ct);
    }

    public void Stop() => lifetime.StopApplication();

    /// <summary>How a backup before quitting went, in a sentence for people.</summary>
    public static string Describe(BackupRunResult? result) => result switch
    {
        null => "There was nothing to back up yet.",
        { Succeeded: true } => $"Your books were backed up first ({result.FileName}).",
        _ => $"The backup before quitting didn't work: {result.Error}",
    };
}

/// <summary>
/// The tray icon's line to the server it belongs to: a named pipe only the Windows user running OpenBook can open
/// (<see cref="PipeOptions.CurrentUserOnly"/>), named in <see cref="Installation.RunningFile"/>. One command per
/// connection, a line each way: <c>backup</c> answers <c>ok {file}</c>, <c>none</c> or <c>failed {reason}</c>;
/// <c>quit</c> answers <c>bye</c> and stops the server (the tray backs up first); <c>update</c> answers
/// <c>ready {version}</c> (downloaded and checked), <c>available {version}</c> or <c>none</c>; <c>install</c> (or
/// <c>install-anyway</c>, past a failed backup) backs up, starts the installer and stops, answering <c>installing</c> or
/// <c>failed {reason}</c>.
/// </summary>
internal sealed partial class DesktopControlService(
    Installation installation, DesktopQuit quit, UpdateService updates, ILogger<DesktopControlService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = DesktopHost.ControlPipe(installation);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                await AnswerAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Whatever one command does, the tray keeps working: an exception here would stop the whole app.
                LogFailed(ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var command = (await reader.ReadLineAsync(ct))?.Trim();
        switch (command)
        {
            case "backup":
                var result = await quit.BackUpAsync(ct);
                await writer.WriteLineAsync(result switch
                {
                    null => "none",
                    { Succeeded: true } => $"ok {result.FileName}",
                    _ => $"failed {OneLine(result.Error)}",
                });
                break;
            case "quit":
                LogQuit();
                await writer.WriteLineAsync("bye");
                quit.Stop();
                break;
            case "update":
                await writer.WriteLineAsync(updates.Status switch
                {
                    { Ready: not null, Available: { } ready } => $"ready {ready.Version}",
                    { Available: { } available } => $"available {available.Version}",
                    _ => "none",
                });
                break;
            case "install" or "install-anyway":
                var refusal = await updates.InstallAsync(command == "install-anyway", ct);
                await writer.WriteLineAsync(refusal is null ? "installing" : $"failed {OneLine(refusal)}");
                break;
            default:
                await writer.WriteLineAsync("unknown");
                break;
        }
    }

    private static string OneLine(string? text) => (text ?? "unknown error").ReplaceLineEndings(" ");

    [LoggerMessage(Level = LogLevel.Information, Message = "Quit from the tray icon.")]
    private partial void LogQuit();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tray icon's connection failed.")]
    private partial void LogFailed(Exception ex);
}
