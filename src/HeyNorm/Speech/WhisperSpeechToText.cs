using System.Text;
using Whisper.net;

namespace HeyNorm.Speech;

public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken ct);
}

/// <summary>Local transcription with whisper.cpp via Whisper.net. Model loads once at startup.</summary>
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly float _noSpeechThreshold;
    private readonly Action<string>? _log;

    public WhisperSpeechToText(string modelPath, float noSpeechThreshold = 0.7f, Action<string>? log = null)
    {
        _noSpeechThreshold = noSpeechThreshold;
        _log = log;
        var path = Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(AppContext.BaseDirectory, modelPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Whisper model not found. See README for the download link.", path);
        _factory = WhisperFactory.FromPath(path);
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken ct)
    {
        // A processor per utterance keeps it simple and thread-safe; creation is cheap next to the model load.
        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples16kMono, ct))
        {
            // Whisper invents fluent sentences from noise, and it knows when it is doing it.
            // Measured on base.en: speech at 3% volume under noise peaked at 0.64; clicks and
            // rustle started at 0.73.
            if (segment.NoSpeechProbability > _noSpeechThreshold)
            {
                _log?.Invoke($"Ignored as noise ({segment.NoSpeechProbability:F2}): {segment.Text.Trim()}");
                continue;
            }
            text.Append(segment.Text);
        }

        var cleaned = CleanUp(text.ToString());
        if (IsStockHallucination(cleaned))
        {
            _log?.Invoke($"Ignored as a stock Whisper phrase: {cleaned}");
            return "";
        }
        return cleaned;
    }

    // Whisper's training data was heavy on YouTube and podcasts, so these are what it reaches
    // for when the audio has no words in it — and not always with a high no-speech score.
    // Whole-transcript matches only, and nothing a person would plausibly say to Norm:
    // "thank you" stays out because people do thank him.
    private static readonly HashSet<string> StockHallucinations =
    [
        "you",
        "thank you for watching",
        "thanks for watching",
        "thank you for listening",
        "thanks for listening",
        "thank you for having me",
        "please subscribe",
        "like and subscribe",
        "see you in the next video",
        "see you next time",
    ];

    internal static bool IsStockHallucination(string text)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^a-z' ]", " ");
        return StockHallucinations.Contains(string.Join(' ', words.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    /// <summary>Whisper emits tags like "[BLANK_AUDIO]" or "(saw running)" for non-speech.</summary>
    internal static string CleanUp(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", "");
        return cleaned.Trim();
    }

    public void Dispose() => _factory.Dispose();
}
