using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;

namespace HeyNorm.Music;

/// <summary>One app's entry in the Windows volume mixer.</summary>
public interface IAppVolume
{
    /// <summary>0 to 1, scaling whatever the app itself plays at.</summary>
    float Volume { get; set; }
}

/// <summary>
/// Turns Spotify down in the Windows volume mixer while Norm is awake, so Whisper can hear the
/// person and the person can hear Norm, and back up when it goes to sleep. The mixer rather
/// than Spotify's own volume: it's local and instant, needs no sign-in, and leaves Spotify's
/// slider — which "turn it up" changes — alone.
///
/// Windows remembers each app's mixer level across restarts, so a Norm that died mid-session
/// would leave Spotify quiet indefinitely, somewhere nobody thinks to look. The normal level
/// is written down before ducking and put back the next time Norm finds Spotify.
/// </summary>
public sealed class SpotifyDucker
{
    private readonly Func<IReadOnlyList<IAppVolume>> _findSpotify;
    private readonly float _duckTo;
    private readonly string _markerPath;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private List<(IAppVolume App, float Normal)>? _ducked;

    /// <param name="duckToPercent">Spotify's mixer level while ducked, as a percent of normal.</param>
    public SpotifyDucker(Func<IReadOnlyList<IAppVolume>> findSpotify, int duckToPercent, string markerPath, Action<string> log)
    {
        _findSpotify = findSpotify;
        _duckTo = Math.Clamp(duckToPercent, 0, 100) / 100f;
        _markerPath = markerPath;
        _log = log;
    }

    public bool IsDucked
    {
        get { lock (_gate) return _ducked is not null; }
    }

    public void Duck()
    {
        lock (_gate)
        {
            if (_ducked is not null) return;
            try
            {
                var apps = _findSpotify();
                if (apps.Count == 0) return; // Spotify isn't running: nothing to turn down
                RecoverLocked(apps);

                _ducked = apps.Select(a => (a, a.Volume)).ToList();
                // Written first: if the process dies between these lines, the next start can
                // still put things back.
                File.WriteAllText(_markerPath, _ducked.Max(d => d.Normal).ToString(CultureInfo.InvariantCulture));
                foreach (var (app, normal) in _ducked) app.Volume = normal * _duckTo;
            }
            catch (Exception ex)
            {
                _log($"Couldn't turn the music down: {ex.Message}");
            }
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_ducked is null) return; // the common case: every rejected screening ends here
            try
            {
                foreach (var (app, normal) in _ducked) app.Volume = normal;
                File.Delete(_markerPath);
            }
            catch (Exception ex)
            {
                _log($"Couldn't turn the music back up: {ex.Message}");
            }
            finally
            {
                _ducked = null;
            }
        }
    }

    /// <summary>
    /// Run at startup: if the last run died while ducked, put Spotify back. If Spotify isn't
    /// running yet the marker stays, and the next duck puts it back first.
    /// </summary>
    public void RecoverFromCrash()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_markerPath)) RecoverLocked(_findSpotify());
            }
            catch (Exception ex)
            {
                _log($"Couldn't check Spotify's volume after a crash: {ex.Message}");
            }
        }
    }

    private void RecoverLocked(IReadOnlyList<IAppVolume> apps)
    {
        if (apps.Count == 0 || !File.Exists(_markerPath)) return;
        if (!float.TryParse(File.ReadAllText(_markerPath), NumberStyles.Float, CultureInfo.InvariantCulture, out var normal))
            normal = 1f;
        foreach (var app in apps) app.Volume = normal;
        File.Delete(_markerPath);
        _log("Put Spotify's volume back after Norm last closed with it turned down.");
    }
}

/// <summary>Finds Spotify's entries in the Windows mixer, on every output device in use.</summary>
public static class SpotifyMixer
{
    public static IReadOnlyList<IAppVolume> Find()
    {
        var found = new List<IAppVolume>();
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (IsSpotify(session.GetProcessID)) found.Add(new Session(session));
            }
        }
        return found;
    }

    private static bool IsSpotify(uint pid)
    {
        if (pid == 0) return false; // system sounds
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "Spotify", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // exited since the list was taken
        }
    }

    private sealed class Session(AudioSessionControl control) : IAppVolume
    {
        public float Volume
        {
            get => control.SimpleAudioVolume.Volume;
            set => control.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
        }
    }
}
