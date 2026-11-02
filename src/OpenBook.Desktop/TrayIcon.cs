using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace OpenBook.Desktop;

/// <summary>
/// OpenBook's icon in the system tray: open it in the browser (also a double-click), back it up now, or quit it. Quitting
/// asks the server to back up first, offers to quit anyway if that fails, then stops it. The icon goes away by itself when
/// the server stops some other way (Settings > System > Quit), since the server removes its running file.
/// </summary>
internal sealed class TrayIcon : ApplicationContext
{
    private static readonly TimeSpan BackupLimit = TimeSpan.FromMinutes(5);

    private readonly string _url;
    private readonly string _pipe;
    private readonly Process? _server;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _backup;
    private readonly ToolStripMenuItem _update;
    private readonly ToolStripMenuItem _quit;
    private readonly System.Windows.Forms.Timer _updates;
    private string? _announced;
    private readonly System.Windows.Forms.Timer _watch;
    private bool _busy;

    /// <param name="server">The server this started, if it did (else it belongs to an earlier start).</param>
    /// <param name="announce">Say once where OpenBook went, when this start is what opened it.</param>
    public TrayIcon(string url, string pipe, Process? server, bool announce)
    {
        _url = url;
        _pipe = pipe;
        _server = server;

        _open = new ToolStripMenuItem("Open OpenBook", null, (_, _) => Open()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _backup = new ToolStripMenuItem("Back up now", null, async (_, _) => await BackUpNowAsync());
        _quit = new ToolStripMenuItem("Quit OpenBook", null, async (_, _) => await QuitAsync());
        _update = new ToolStripMenuItem("Install update", null, async (_, _) => await InstallAsync()) { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_open, _backup, _update, new ToolStripSeparator(), _quit]);
        menu.Opening += async (_, _) => await ShowUpdateAsync(announce: false);

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "OpenBook",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Open();

        _watch = new System.Windows.Forms.Timer { Interval = 3000 };
        _watch.Tick += (_, _) =>
        {
            if (!_busy && (!File.Exists(Program.RunningFile) || _server is { HasExited: true }))
                Done();
        };
        _watch.Start();

        if (announce)
            _icon.ShowBalloonTip(5000, "OpenBook is running", "It keeps running after you close the browser. Use this icon to open it again or to quit.", ToolTipIcon.Info);

        // The server checks for updates itself; this asks it now and then whether one is ready to install.
        _updates = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(30).TotalMilliseconds };
        _updates.Tick += async (_, _) => await ShowUpdateAsync(announce: true);
        _updates.Start();
    }

    // Shows "Install update {version}" when the server has one downloaded and checked; says so once per version.
    private async Task ShowUpdateAsync(bool announce)
    {
        if (_busy)
            return;
        var answer = await SendAsync("update", TimeSpan.FromSeconds(5));
        var version = answer?.StartsWith("ready ", StringComparison.Ordinal) == true ? answer[6..] : null;
        _update.Visible = version is not null;
        _update.Text = $"Install OpenBook {version}";
        if (announce && version is not null && version != _announced)
        {
            _announced = version;
            _icon.ShowBalloonTip(8000, $"OpenBook {version} is ready", "Right-click the OpenBook icon and choose Install. Your books are backed up first.", ToolTipIcon.Info);
        }
    }

    private async Task InstallAsync()
    {
        var question = $"{_update.Text} now?" + Environment.NewLine + Environment.NewLine
            + "OpenBook backs up your books, closes, installs the update and starts again by itself in a minute or two.";
        if (MessageBox.Show(question, "OpenBook", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        if (!Busy(true, "OpenBook: installing the update…"))
            return;
        try
        {
            var answer = await SendAsync("install", BackupLimit);
            if (answer?.StartsWith("failed ", StringComparison.Ordinal) == true)
            {
                if (MessageBox.Show(answer[7..] + Environment.NewLine + Environment.NewLine + "Install the update anyway?",
                        "OpenBook", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                answer = await SendAsync("install-anyway", BackupLimit);
            }

            if (answer != "installing")
            {
                MessageBox.Show(answer is null ? "OpenBook isn't answering. Start it again from the Start menu." : answer, "OpenBook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // The installer closes this icon too, and starts a new one with the new version.
            for (var i = 0; i < 60 && File.Exists(Program.RunningFile) && _server is not { HasExited: true }; i++)
                await Task.Delay(500);
            Done();
        }
        finally
        {
            Busy(false);
        }
    }

    private void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"Open {_url} in your browser.", "OpenBook", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task BackUpNowAsync()
    {
        if (!Busy(true, "OpenBook: backing up…"))
            return;
        try
        {
            var answer = await SendAsync("backup", BackupLimit);
            var (title, text, icon) = Explain(answer);
            _icon.ShowBalloonTip(5000, title, text, icon);
        }
        finally
        {
            Busy(false);
        }
    }

    private async Task QuitAsync()
    {
        if (!Busy(true, "OpenBook: backing up before quitting…"))
            return;
        try
        {
            var answer = await SendAsync("backup", BackupLimit);
            if (answer is null)
            {
                // Nothing answers: the server has gone already.
                Done();
                return;
            }

            if (answer.StartsWith("failed", StringComparison.Ordinal)
                && MessageBox.Show($"{Explain(answer).Text}\n\nQuit OpenBook anyway?", "OpenBook", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _icon.Text = "OpenBook: stopping…";
            await SendAsync("quit", TimeSpan.FromSeconds(10));
            // Give it a moment to finish what it's doing and remove its running file.
            for (var i = 0; i < 60 && File.Exists(Program.RunningFile) && _server is not { HasExited: true }; i++)
                await Task.Delay(500);
            Done();
        }
        finally
        {
            Busy(false);
        }
    }

    private static (string Title, string Text, ToolTipIcon Icon) Explain(string? answer) => answer switch
    {
        null => ("OpenBook isn't answering", "It may have stopped. Start it again from the Start menu.", ToolTipIcon.Warning),
        "none" => ("Nothing to back up yet", "Set up your company first.", ToolTipIcon.Info),
        _ when answer.StartsWith("ok ", StringComparison.Ordinal) => ("Backed up", $"Your books were saved as {answer[3..]}.", ToolTipIcon.Info),
        _ when answer.StartsWith("failed ", StringComparison.Ordinal) => ("The backup didn't work", answer[7..], ToolTipIcon.Warning),
        _ => ("OpenBook", answer, ToolTipIcon.Info),
    };

    /// <summary>Sends one command down the server's pipe and returns its one-line answer; null when it can't be reached.</summary>
    private async Task<string?> SendAsync(string command, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(5000, timeout.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // One command at a time; the menu says what's happening meanwhile.
    private bool Busy(bool busy, string? status = null)
    {
        if (busy && _busy)
            return false;
        _busy = busy;
        _backup.Enabled = _quit.Enabled = _update.Enabled = !busy;
        _icon.Text = status ?? "OpenBook";
        return true;
    }

    private void Done()
    {
        _watch.Stop();
        _updates.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _updates.Dispose();
            _update.Dispose();
            _icon.Dispose();
            _open.Dispose();
            _backup.Dispose();
            _quit.Dispose();
        }

        base.Dispose(disposing);
    }
}
