using System.Diagnostics;
using HeyNorm.Assistant;

namespace HeyNorm;

/// <summary>
/// No main window — just a tray icon with a status tooltip and a small menu.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NormAssistant _assistant;
    private readonly FileLog _log;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly SynchronizationContext _ui;

    public TrayApplicationContext(NormAssistant assistant, FileLog log)
    {
        _assistant = assistant;
        _log = log;
        // Marshals state updates from the audio thread back to the UI thread.
        _ui = new WindowsFormsSynchronizationContext();

        _status = new ToolStripMenuItem("Sleeping") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start session", null, (_, _) => _assistant.BeginSessionManually());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
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

    private void OpenLog()
    {
        if (File.Exists(_log.FilePath))
            Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        _icon.Visible = false;
        await _assistant.DisposeAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
