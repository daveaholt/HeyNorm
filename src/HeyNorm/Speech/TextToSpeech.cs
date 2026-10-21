using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HeyNorm.Speech;

public interface ITextToSpeech
{
    Task SpeakAsync(string text, CancellationToken ct);
}

/// <summary>
/// Piper (https://github.com/rhasspy/piper): local neural TTS. Keeps one piper.exe running for
/// the life of the app and sends it a JSON line per sentence; it writes the WAV and echoes the
/// path back when done. Launching it per sentence cost about 0.3 s each time, mostly spent
/// reloading the voice.
/// </summary>
public sealed class PiperTextToSpeech : ITextToSpeech, IDisposable
{
    // Synthesis runs at about a twentieth of real time; this only catches a hung process.
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(15);

    private readonly string _exe;
    private readonly string _voice;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _recentErrors = new();
    private Process? _piper;

    public PiperTextToSpeech(string exePath, string voicePath)
    {
        _exe = Rooted(exePath);
        _voice = Rooted(voicePath);
        // Start now so the voice is loaded before the first reply needs it.
        _piper = Launch();
    }

    public static bool IsConfigured(string exePath, string voicePath) =>
        !string.IsNullOrWhiteSpace(exePath) && File.Exists(Rooted(exePath)) &&
        !string.IsNullOrWhiteSpace(voicePath) && File.Exists(Rooted(voicePath));

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        foreach (var sentence in SentenceSplitter.Split(text))
        {
            ct.ThrowIfCancellationRequested();
            var wav = Path.Combine(Path.GetTempPath(), $"heynorm_{Guid.NewGuid():N}.wav");
            try
            {
                await SynthesizeAsync(sentence, wav, ct);
                await AudioPlayer.PlayFileAsync(wav, ct);
            }
            finally
            {
                try { File.Delete(wav); } catch { /* temp file, best effort */ }
            }
        }
    }

    private async Task SynthesizeAsync(string sentence, string wav, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // A crashed or killed process is replaced on the next sentence.
            var piper = _piper is { HasExited: false } running ? running : _piper = Launch();
            try
            {
                await piper.StandardInput.WriteLineAsync(
                    JsonSerializer.Serialize(new { text = sentence, output_file = wav }));

                // Not cancelled by ct: an abandoned read would leave this sentence's reply in
                // the pipe to be mistaken for the next one's. Synthesis is short, so let it land.
                var read = piper.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(SynthesisTimeout, CancellationToken.None)) != read)
                    throw new TimeoutException($"Piper took over {SynthesisTimeout.TotalSeconds:0} s on one sentence.");

                var written = await read
                    ?? throw new InvalidOperationException($"Piper exited. {RecentErrors()}");
                if (!string.Equals(Path.GetFullPath(written.Trim()), wav, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Piper answered with \"{written}\". {RecentErrors()}");
            }
            catch
            {
                // Whatever went wrong, the process can no longer be trusted to stay in step.
                Stop(piper);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
        ct.ThrowIfCancellationRequested();
    }

    private Process Launch()
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // JSON escapes non-ASCII anyway, but the console code page would mangle the path echo.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "--model", _voice, "--json-input", "--output_dir", Path.GetTempPath() })
            psi.ArgumentList.Add(arg);

        var piper = Process.Start(psi)!;
        piper.StandardInput.AutoFlush = true;
        // Piper logs a line per sentence to stderr. It must be drained or the pipe fills and
        // Piper blocks; the last few lines are kept to explain a crash.
        piper.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_recentErrors)
            {
                _recentErrors.Enqueue(e.Data);
                while (_recentErrors.Count > 5) _recentErrors.Dequeue();
            }
        };
        piper.BeginErrorReadLine();
        return piper;
    }

    private string RecentErrors()
    {
        lock (_recentErrors)
            return string.Join(" / ", _recentErrors);
    }

    private static void Stop(Process piper)
    {
        try
        {
            // Closing stdin is Piper's cue to exit cleanly.
            piper.StandardInput.Close();
            if (!piper.WaitForExit(2000)) piper.Kill();
        }
        catch { /* already gone */ }
        piper.Dispose();
    }

    public void Dispose()
    {
        if (_piper is not null) Stop(_piper);
        _gate.Dispose();
    }

    private static string Rooted(string p) => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
}

/// <summary>Built-in Windows voice. Robotic, but zero setup.</summary>
public sealed class SapiTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly System.Speech.Synthesis.SpeechSynthesizer _synth = new();

    public Task SpeakAsync(string text, CancellationToken ct)
    {
        using var reg = ct.Register(() => _synth.SpeakAsyncCancelAll());
        return Task.Run(() =>
        {
            try { _synth.Speak(text); }
            catch (OperationCanceledException) { }
        }, ct);
    }

    public void Dispose() => _synth.Dispose();
}

public static class SentenceSplitter
{
    public static IEnumerable<string> Split(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?])\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s));
}

/// <summary>
/// Collects streamed text and hands back each sentence once it is complete, splitting exactly
/// where <see cref="SentenceSplitter"/> would. A sentence counts as complete only once the
/// whitespace after its full stop arrives, so "1.5" split across fragments stays whole.
/// </summary>
public sealed class SentenceBuffer
{
    private static readonly System.Text.RegularExpressions.Regex Boundary = new(@"[.!?]\s+");
    private readonly System.Text.StringBuilder _pending = new();

    public IReadOnlyList<string> Append(string fragment)
    {
        _pending.Append(fragment);
        var text = _pending.ToString();
        var matches = Boundary.Matches(text);
        if (matches.Count == 0) return [];

        var cut = matches[^1].Index + matches[^1].Length;
        _pending.Clear().Append(text, cut, text.Length - cut);
        return SentenceSplitter.Split(text[..cut]).Select(s => s.Trim()).ToList();
    }

    /// <summary>Whatever is left when the stream ends, which may lack a full stop.</summary>
    public IReadOnlyList<string> Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        return SentenceSplitter.Split(rest).Select(s => s.Trim()).ToList();
    }
}

public static class AudioPlayer
{
    public static Task PlayFileAsync(string path, CancellationToken ct)
    {
        var reader = new AudioFileReader(path);
        return PlayAsync(reader, ct, () => reader.Dispose());
    }

    /// <summary>Short sine chime: rising for "listening", falling for "done".</summary>
    public static async Task ChimeAsync(bool rising)
    {
        var (first, second) = rising ? (660.0, 990.0) : (990.0, 660.0);
        foreach (var f in new[] { first, second })
        {
            var tone = new SignalGenerator(22050, 1) { Frequency = f, Gain = 0.15, Type = SignalGeneratorType.Sin }
                .Take(TimeSpan.FromMilliseconds(110));
            await PlayAsync(tone.ToWaveProvider(), CancellationToken.None);
        }
    }

    private static async Task PlayAsync(IWaveProvider provider, CancellationToken ct, Action? onDone = null)
    {
        var done = new TaskCompletionSource();
        using var output = new WaveOutEvent();
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(provider);
        output.Play();
        using (ct.Register(() => output.Stop()))
            await done.Task;
        onDone?.Invoke();
    }
}
