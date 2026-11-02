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
    private readonly ToolStripMenuItem _quit;
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
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_open, _backup, new ToolStripSeparator(), _quit]);

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
        _backup.Enabled = _quit.Enabled = !busy;
        _icon.Text = status ?? "OpenBook";
        return true;
    }

    private void Done()
    {
        _watch.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _icon.Dispose();
            _open.Dispose();
            _backup.Dispose();
            _quit.Dispose();
        }

        base.Dispose(disposing);
    }
}
