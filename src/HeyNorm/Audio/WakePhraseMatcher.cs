using System.Text.RegularExpressions;
using HeyNorm.Config;

namespace HeyNorm.Audio;

public enum Keyword { None, Wake, Stop }

/// <summary>
/// What a transcript turned out to be. <see cref="Remainder"/> is everything the person said
/// after the wake phrase, so "Hey Norm, set a ten minute timer" wakes and asks in one breath.
/// </summary>
public sealed record PhraseMatch(Keyword Keyword, string Remainder = "")
{
    public static readonly PhraseMatch None = new(Keyword.None);
}

/// <summary>
/// Recognises the wake and stop phrases in a Whisper transcript.
///
/// This replaces a dedicated wake-word engine. It costs one Whisper pass per utterance heard
/// while sleeping, which is cheap on a GPU, and it inherits Whisper's noise tolerance —
/// worth more in a shop than the few milliseconds a purpose-built detector would save.
/// </summary>
public sealed class WakePhraseMatcher
{
    private readonly string[][] _wake;
    private readonly string[][] _stop;

    public WakePhraseMatcher(WakeSettings settings)
    {
        _wake = Prepare(settings.WakePhrases);
        _stop = Prepare(settings.StopPhrases);
    }

    /// <summary>Classifies a transcript heard while Norm is asleep.</summary>
    public PhraseMatch Match(string transcript)
    {
        var words = Tokenize(transcript);
        if (words.Count == 0) return PhraseMatch.None;

        foreach (var phrase in _wake)
        {
            if (!StartsWith(words, phrase)) continue;

            // Hand back the rest of the original text, punctuation and casing intact — it is
            // about to be spoken to the model as the first request of the session.
            var remainder = words.Count > phrase.Length
                ? transcript[words[phrase.Length].Start..].Trim().TrimStart(',', '.', '-', '—').Trim()
                : "";
            return new PhraseMatch(Keyword.Wake, remainder);
        }

        return IsStop(transcript) ? new PhraseMatch(Keyword.Stop) : PhraseMatch.None;
    }

    /// <summary>
    /// True when the person is signing off. Accepts a trailing match ("okay, Norm, done") but
    /// not a leading or buried one, so "Norm, done with the glue — what's next" keeps going.
    /// </summary>
    public bool IsStop(string transcript)
    {
        var words = Tokenize(transcript);
        return _stop.Any(phrase => EndsWith(words, phrase));
    }

    private static string[][] Prepare(IEnumerable<string>? phrases) =>
        (phrases ?? [])
            .Select(p => Tokenize(p).Select(w => w.Normalized).ToArray())
            .Where(p => p.Length > 0)
            .ToArray();

    private static bool StartsWith(List<Word> words, string[] phrase)
    {
        if (words.Count < phrase.Length) return false;
        for (var i = 0; i < phrase.Length; i++)
            if (words[i].Normalized != phrase[i]) return false;
        return true;
    }

    private static bool EndsWith(List<Word> words, string[] phrase)
    {
        if (words.Count < phrase.Length) return false;
        var offset = words.Count - phrase.Length;
        for (var i = 0; i < phrase.Length; i++)
            if (words[offset + i].Normalized != phrase[i]) return false;
        return true;
    }

    private readonly record struct Word(string Normalized, int Start);

    /// <summary>
    /// Splits into words, remembering where each began, and strips everything that isn't a
    /// letter or digit so "Hey, Norm." and "hey norm" compare equal. Words that normalise to
    /// nothing (a lone dash, say) are dropped.
    /// </summary>
    private static List<Word> Tokenize(string? text)
    {
        var words = new List<Word>();
        if (string.IsNullOrWhiteSpace(text)) return words;

        foreach (Match m in Regex.Matches(text, @"\S+"))
        {
            var normalized = new string(m.Value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (normalized.Length > 0) words.Add(new Word(normalized, m.Index));
        }
        return words;
    }
}
