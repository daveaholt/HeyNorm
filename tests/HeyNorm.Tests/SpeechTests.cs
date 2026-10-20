using HeyNorm.Config;
using HeyNorm.Speech;
using HeyNorm.Audio;

namespace HeyNorm.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void Splits_on_sentence_endings_so_tts_can_start_sooner()
    {
        var sentences = SentenceSplitter.Split("Sure thing. I'll set it now! Anything else?").ToList();

        Assert.Equal(["Sure thing.", "I'll set it now!", "Anything else?"], sentences);
    }

    [Fact]
    public void A_single_sentence_comes_back_whole()
    {
        Assert.Equal(["Ten minutes it is."], SentenceSplitter.Split("Ten minutes it is.").ToList());
    }

    [Fact]
    public void Text_with_no_terminator_is_still_one_chunk()
    {
        Assert.Equal(["no full stop here"], SentenceSplitter.Split("no full stop here").ToList());
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_away()
    {
        Assert.Equal(["Done."], SentenceSplitter.Split("   Done.\n ").ToList());
    }

    [Fact]
    public void Empty_input_yields_nothing_to_speak()
    {
        Assert.Empty(SentenceSplitter.Split("   "));
    }

    [Fact]
    public void A_decimal_point_does_not_split_a_number()
    {
        // The split needs whitespace after the period, so "1.5" survives intact.
        Assert.Equal(["Cut it to 1.5 inches."], SentenceSplitter.Split("Cut it to 1.5 inches.").ToList());
    }
}

public class WhisperCleanUpTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(saw running)", "")]
    [InlineData(" [BLANK_AUDIO] set a timer ", "set a timer")]
    [InlineData("Set a timer (table saw whirring) for ten minutes.", "Set a timer  for ten minutes.")]
    [InlineData("What time is it?", "What time is it?")]
    public void Strips_whisper_non_speech_tags(string input, string expected)
    {
        Assert.Equal(expected, WhisperSpeechToText.CleanUp(input));
    }

    [Fact]
    public void Removes_several_tags_from_one_transcript()
    {
        Assert.Equal("Norm set a timer", WhisperSpeechToText.CleanUp("[MUSIC] Norm set a timer (clattering)"));
    }

    [Fact]
    public void A_transcript_that_is_only_noise_becomes_empty_and_is_skipped_upstream()
    {
        // NormAssistant.RunTurnAsync treats whitespace as "heard nothing" and returns to listening.
        Assert.True(string.IsNullOrWhiteSpace(WhisperSpeechToText.CleanUp("[BLANK_AUDIO]")));
    }
}

public class PiperConfigurationTests
{
    [Fact]
    public void Is_not_configured_when_paths_are_blank()
    {
        Assert.False(PiperTextToSpeech.IsConfigured("", ""));
    }

    [Fact]
    public void Is_not_configured_when_the_files_are_missing()
    {
        Assert.False(PiperTextToSpeech.IsConfigured("piper/piper.exe", "piper/voice.onnx"));
    }

    [Fact]
    public void Is_configured_only_when_both_files_exist()
    {
        var dir = Directory.CreateTempSubdirectory("heynorm_piper_");
        try
        {
            var exe = Path.Combine(dir.FullName, "piper.exe");
            var voice = Path.Combine(dir.FullName, "voice.onnx");
            File.WriteAllText(exe, "");

            Assert.False(PiperTextToSpeech.IsConfigured(exe, voice));

            File.WriteAllText(voice, "");

            Assert.True(PiperTextToSpeech.IsConfigured(exe, voice));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class UtteranceRecorderTests
{
    private static SpeechSettings Settings(int idleSeconds = 1) => new()
    {
        VadAggressiveness = 3,
        EndOfUtteranceSilenceMs = 900,
        SessionIdleTimeoutSeconds = idleSeconds,
        MaxUtteranceSeconds = 30,
    };

    /// <summary>One mic-sized frame of digital silence.</summary>
    private static short[] Silence() => new short[512];

    private static UtteranceEvent FeedFor(UtteranceRecorder recorder, int seconds)
    {
        // 512 samples per frame at 16 kHz.
        var frames = MicrophoneSource.SampleRate * seconds / 512;
        var last = UtteranceEvent.Waiting;
        for (var i = 0; i < frames; i++)
        {
            last = recorder.Feed(Silence());
            if (last is UtteranceEvent.Complete or UtteranceEvent.IdleTimeout) return last;
        }
        return last;
    }

    [Fact]
    public void Silence_reports_waiting_rather_than_speech()
    {
        using var recorder = new UtteranceRecorder(Settings(idleSeconds: 60));

        Assert.Equal(UtteranceEvent.Waiting, recorder.Feed(Silence()));
    }

    [Fact]
    public void Silence_past_the_idle_timeout_ends_the_session()
    {
        using var recorder = new UtteranceRecorder(Settings(idleSeconds: 1));

        Assert.Equal(UtteranceEvent.IdleTimeout, FeedFor(recorder, seconds: 2));
    }

    [Fact]
    public void The_idle_timeout_is_not_reported_early()
    {
        using var recorder = new UtteranceRecorder(Settings(idleSeconds: 5));

        Assert.Equal(UtteranceEvent.Waiting, FeedFor(recorder, seconds: 2));
    }

    [Fact]
    public void Reset_clears_accumulated_idle_time()
    {
        using var recorder = new UtteranceRecorder(Settings(idleSeconds: 2));
        FeedFor(recorder, seconds: 1);

        recorder.Reset();

        Assert.Equal(UtteranceEvent.Waiting, FeedFor(recorder, seconds: 1));
    }

    [Fact]
    public void Taking_an_utterance_that_never_started_yields_no_samples()
    {
        using var recorder = new UtteranceRecorder(Settings());

        Assert.Empty(recorder.TakeUtterance());
    }
}

public class WhisperHallucinationTests
{
    [Theory]
    [InlineData("- Thank you for having me.")] // heard live in the shop with nobody speaking
    [InlineData("Thanks for watching!")]
    [InlineData(" you")]
    [InlineData("Please subscribe.")]
    public void Stock_phrases_are_recognised_despite_punctuation_and_case(string heard)
    {
        Assert.True(WhisperSpeechToText.IsStockHallucination(heard));
    }

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("Thanks.")]
    [InlineData("Thank you for the help with the timer.")]
    [InlineData("Can you see the clamp?")]
    public void Real_things_people_say_to_Norm_get_through(string heard)
    {
        Assert.False(WhisperSpeechToText.IsStockHallucination(heard));
    }
}
