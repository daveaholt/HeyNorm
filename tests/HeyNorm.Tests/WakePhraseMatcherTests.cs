using HeyNorm.Audio;
using HeyNorm.Config;

namespace HeyNorm.Tests;

public class WakePhraseMatcherTests
{
    private static WakePhraseMatcher Matcher(
        string[]? wake = null, string[]? stop = null) =>
        new(new WakeSettings
        {
            WakePhrases = [.. wake ?? ["hey norm"]],
            StopPhrases = [.. stop ?? ["norm done"]],
        });

    // --- waking -------------------------------------------------------------

    [Theory]
    [InlineData("hey norm")]
    [InlineData("Hey Norm")]
    [InlineData("HEY NORM")]
    [InlineData("Hey, Norm.")]
    [InlineData("  hey   norm  ")]
    [InlineData("Hey Norm!")]
    public void Wakes_on_the_phrase_however_whisper_punctuates_it(string transcript)
    {
        var match = Matcher().Match(transcript);

        Assert.Equal(Keyword.Wake, match.Keyword);
        Assert.Equal("", match.Remainder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("what time is it")]
    [InlineData("pass me the norm")]
    [InlineData("norm")]
    [InlineData("hey")]
    public void Ignores_anything_that_is_not_the_wake_phrase(string transcript)
    {
        Assert.Equal(Keyword.None, Matcher().Match(transcript).Keyword);
    }

    [Fact]
    public void Does_not_wake_when_the_phrase_is_buried_mid_sentence()
    {
        // Otherwise the radio saying "...and hey Norm Abram once said..." would start a session.
        Assert.Equal(Keyword.None, Matcher().Match("I was telling Dave hey norm would know").Keyword);
    }

    [Fact]
    public void Any_configured_wake_phrase_works()
    {
        var matcher = Matcher(wake: ["hey norm", "hey norman", "okay norm"]);

        Assert.Equal(Keyword.Wake, matcher.Match("Okay Norm").Keyword);
        Assert.Equal(Keyword.Wake, matcher.Match("Hey Norman!").Keyword);
    }

    // --- waking with a request in the same breath ---------------------------

    [Fact]
    public void Hands_back_the_rest_of_the_utterance_as_the_first_request()
    {
        var match = Matcher().Match("Hey Norm, set a ten minute timer for the glue up.");

        Assert.Equal(Keyword.Wake, match.Keyword);
        Assert.Equal("set a ten minute timer for the glue up.", match.Remainder);
    }

    [Fact]
    public void The_remainder_keeps_its_original_casing_and_punctuation()
    {
        var match = Matcher().Match("Hey Norm, what's the Imperial equivalent of 12mm?");

        Assert.Equal("what's the Imperial equivalent of 12mm?", match.Remainder);
    }

    [Theory]
    [InlineData("Hey Norm — what time is it?", "what time is it?")]
    [InlineData("Hey Norm. What time is it?", "What time is it?")]
    [InlineData("Hey Norm, what time is it?", "what time is it?")]
    [InlineData("hey norm what time is it", "what time is it")]
    public void Strips_the_separator_between_the_wake_phrase_and_the_request(
        string transcript, string expected)
    {
        Assert.Equal(expected, Matcher().Match(transcript).Remainder);
    }

    [Fact]
    public void A_bare_wake_phrase_leaves_no_remainder_so_norm_just_listens()
    {
        Assert.Equal("", Matcher().Match("Hey Norm.").Remainder);
    }

    // --- stopping -----------------------------------------------------------

    [Theory]
    [InlineData("norm done")]
    [InlineData("Norm, done.")]
    [InlineData("NORM DONE")]
    public void Stops_on_the_stop_phrase(string transcript)
    {
        Assert.True(Matcher().IsStop(transcript));
    }

    [Fact]
    public void Stops_when_the_phrase_closes_a_longer_sentence()
    {
        Assert.True(Matcher().IsStop("okay, Norm, done."));
    }

    [Fact]
    public void Does_not_stop_when_the_phrase_merely_opens_a_sentence()
    {
        // "Norm, done with the glue — what's next?" is a question, not a sign-off.
        Assert.False(Matcher().IsStop("Norm, done with the glue, what's next?"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("what time is it")]
    [InlineData("done")]
    [InlineData("norm")]
    public void Does_not_stop_on_unrelated_speech(string transcript)
    {
        Assert.False(Matcher().IsStop(transcript));
    }

    [Fact]
    public void Any_configured_stop_phrase_works()
    {
        var matcher = Matcher(stop: ["norm done", "thanks norm"]);

        Assert.True(matcher.IsStop("Thanks, Norm!"));
        Assert.True(matcher.IsStop("Norm done."));
    }

    [Fact]
    public void A_stop_phrase_heard_while_asleep_is_reported_as_a_stop_not_a_wake()
    {
        var match = Matcher().Match("Norm, done.");

        Assert.Equal(Keyword.Stop, match.Keyword);
    }

    // --- configuration edge cases -------------------------------------------

    [Fact]
    public void Blank_and_punctuation_only_phrases_are_discarded_rather_than_matching_everything()
    {
        var matcher = Matcher(wake: ["", "   ", "---", "hey norm"]);

        Assert.Equal(Keyword.Wake, matcher.Match("hey norm").Keyword);
        Assert.Equal(Keyword.None, matcher.Match("anything at all").Keyword);
    }

    [Fact]
    public void No_configured_phrases_means_nothing_ever_wakes_it()
    {
        var matcher = new WakePhraseMatcher(new WakeSettings { WakePhrases = [], StopPhrases = [] });

        Assert.Equal(Keyword.None, matcher.Match("hey norm").Keyword);
        Assert.False(matcher.IsStop("norm done"));
    }

    [Fact]
    public void Configured_phrases_are_normalised_the_same_way_transcripts_are()
    {
        var matcher = Matcher(wake: ["Hey, Norm!"]);

        Assert.Equal(Keyword.Wake, matcher.Match("hey norm").Keyword);
    }

    [Fact]
    public void A_longer_wake_phrase_is_matched_in_full()
    {
        var matcher = Matcher(wake: ["hey there norm"]);

        Assert.Equal(Keyword.None, matcher.Match("hey there").Keyword);
        Assert.Equal(Keyword.Wake, matcher.Match("hey there norm").Keyword);
        Assert.Equal("go", matcher.Match("hey there norm, go").Remainder);
    }
}
