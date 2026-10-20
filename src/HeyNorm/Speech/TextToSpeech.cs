using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HeyNorm.Speech;

public interface ITextToSpeech
{
    Task SpeakAsync(string text, CancellationToken ct);
}

/// <summary>
/// Piper (https://github.com/rhasspy/piper): local neural TTS. Runs piper.exe per sentence
/// and plays the WAV it writes.
/// </summary>
public sealed class PiperTextToSpeech : ITextToSpeech
{
    private readonly string _exe;
    private readonly string _voice;

    public PiperTextToSpeech(string exePath, string voicePath)
    {
        _exe = Rooted(exePath);
        _voice = Rooted(voicePath);
    }

    public static bool IsConfigured(string exePath, string voicePath) =>
        !string.IsNullOrWhiteSpace(exePath) && File.Exists(Rooted(exePath)) &&
        !string.IsNullOrWhiteSpace(voicePath) && File.Exists(Rooted(voicePath));

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        // Synthesize one sentence at a time so long replies start talking sooner.
        foreach (var sentence in SentenceSplitter.Split(text))
        {
            ct.ThrowIfCancellationRequested();
            var wav = Path.Combine(Path.GetTempPath(), $"heynorm_{Guid.NewGuid():N}.wav");
            try
            {
                var psi = new ProcessStartInfo(_exe, $"--model \"{_voice}\" --output_file \"{wav}\"")
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi)!;
                await proc.StandardInput.WriteLineAsync(sentence);
                proc.StandardInput.Close();
                await proc.WaitForExitAsync(ct);

                await AudioPlayer.PlayFileAsync(wav, ct);
            }
            finally
            {
                try { File.Delete(wav); } catch { /* temp file, best effort */ }
            }
        }
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
