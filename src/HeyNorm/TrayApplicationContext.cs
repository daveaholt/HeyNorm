using System.Diagnostics;
using HeyNorm.Assistant;
using HeyNorm.Knowledge;
using HeyNorm.Music;

namespace HeyNorm;

/// <summary>
/// No main window — just a tray icon with a status tooltip and a small menu.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NormAssistant _assistant;
    private readonly FileLog _log;
    private readonly SpotifyFeature? _spotify;
    private readonly DocumentLibrary? _documents;
    private readonly Memory? _memory;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly SynchronizationContext _ui;

    public TrayApplicationContext(NormAssistant assistant, FileLog log, SpotifyFeature? spotify = null,
        DocumentLibrary? documents = null, Memory? memory = null)
    {
        _memory = memory;
        _assistant = assistant;
        _log = log;
        _spotify = spotify;
        _documents = documents;
        // Marshals state updates from the audio thread back to the UI thread.
        _ui = new WindowsFormsSynchronizationContext();

        _status = new ToolStripMenuItem("Sleeping") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start session", null, (_, _) => _assistant.BeginSessionManually());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        if (_documents is not null)
            menu.Items.Add("Open documents folder", null, (_, _) => OpenDocuments());
        if (_memory is not null)
            menu.Items.Add("Open memory", null, (_, _) => OpenMemory());
        if (_spotify?.Auth is not null)
            menu.Items.Add("Connect Spotify…", null, async (_, _) => await ConnectSpotifyAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // swap for a custom .ico (a little hand plane?)
            Text = "Hey Norm — sleeping",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _assistant.BeginSessionManually();

        _assistant.StateChanged += s => _ui.Post(_ => ShowState(s), null);
        _assistant.Log += line => _log.Write(line);
        _assistant.Start();
    }

    private void ShowState(NormState state)
    {
        var label = state switch
        {
            NormState.Sleeping => "Sleeping",
            NormState.Starting or NormState.Listening => "Listening…",
            NormState.Thinking => "Thinking…",
            NormState.Speaking => "Speaking…",
            _ => state.ToString(),
        };
        _status.Text = label;
        _icon.Text = $"Hey Norm — {label.ToLowerInvariant()}";
    }

    /// <summary>The plain text file of remembered things, to read, fix, or prune by hand.</summary>
    private void OpenMemory()
    {
        _memory!.EnsureFile();
        Process.Start(new ProcessStartInfo(_memory.Path) { UseShellExecute = true });
    }

    private void OpenDocuments()
    {
        // Created on first use, so "drop your plans in here" works on a fresh machine.
        Directory.CreateDirectory(_documents!.Folder);
        Process.Start(new ProcessStartInfo(_documents.Folder) { UseShellExecute = true });
    }

    private void OpenLog()
    {
        if (File.Exists(_log.FilePath))
            Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
    }

    /// <summary>Opens Spotify's consent page; also how the six-monthly re-sign-in is done.</summary>
    private async Task ConnectSpotifyAsync()
    {
        try
        {
            var auth = _spotify!.Auth!;
            await auth.LoginAsync(
                url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), CancellationToken.None);
            _log.Write($"Spotify connected; the sign-in lasts until about {auth.ExpiresAround:yyyy-MM-dd}.");
            _icon.ShowBalloonTip(3000, "Hey Norm", "Spotify is connected.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Write($"Spotify sign-in failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Spotify sign-in", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ExitAsync()
    {
        _icon.Visible = false;
        _documents?.Dispose();
        // Don't leave the shop's music at the ducked level if Norm is closed mid-session.
        _spotify?.Ducker?.Restore();
        await _assistant.DisposeAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
