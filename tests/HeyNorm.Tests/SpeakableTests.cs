using HeyNorm.Assistant;
using HeyNorm.Config;
using HeyNorm.Llm;
using HeyNorm.Speech;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

public class SpeakableTests
{
    [Theory]
    [InlineData("Have a great day! 😊", "Have a great day!")]                  // measured: 1.0 s became 3.3 s
    [InlineData("Glue up looks good 👍 let me know.", "Glue up looks good let me know.")]
    [InlineData("Nice work 🔨🌟!", "Nice work!")]
    [InlineData("Shop's ready 👨‍🔧 to go.", "Shop's ready to go.")]           // a joined sequence
    [InlineData("Done ✅ and ✨ clean.", "Done and clean.")]
    [InlineData("Thumbs up 👍🏽 from me.", "Thumbs up from me.")]              // with a skin tone
    [InlineData("Starts at 7⃣ sharp.", "Starts at 7 sharp.")]                  // keycap: the digit stays
    [InlineData("Made in the 🇺🇸 shop.", "Made in the shop.")]                 // flag
    public void Emoji_are_removed(string said, string spoken)
    {
        Assert.Equal(spoken, Speakable.Clean(said));
    }

    [Theory]
    [InlineData("Sure, **two layers** of 9mm ply.", "Sure, two layers of 9mm ply.")] // measured: +2 s of asterisks
    [InlineData("Use __marine__ ply.", "Use marine ply.")]
    [InlineData("That's *really* important.", "That's really important.")]
    [InlineData("Set `minutes` to ten.", "Set minutes to ten.")]
    [InlineData("## Glue times", "Glue times")]
    [InlineData("- Two layers of ply.", "Two layers of ply.")]
    [InlineData("* Two layers of ply.", "Two layers of ply.")]
    public void Markdown_marks_are_removed_and_the_words_kept(string said, string spoken)
    {
        Assert.Equal(spoken, Speakable.Clean(said));
    }

    [Theory]
    [InlineData("It's 72° and dry.")]            // Piper reads ° as "degrees": it shares emoji's Unicode category
    [InlineData("Cut it 1½ inches.")]
    [InlineData("A 2×4 will do.")]
    [InlineData("Use a 2*4 there.")]             // a lone asterisk between digits isn't markdown
    [InlineData("Café-style finish, naïvely.")]
    [InlineData("Plane it to 9mm, then sand.")]
    public void Ordinary_text_is_left_alone(string said)
    {
        Assert.Equal(said, Speakable.Clean(said));
    }

    [Fact]
    public void A_sentence_that_was_only_emoji_becomes_empty()
    {
        Assert.Equal("", Speakable.Clean("😊🎉"));
    }

    private static ConversationEngine Engine(FakeChatProvider provider)
    {
        var settings = new AppSettings
        {
            Tasks = { ["conversation"] = new TaskSettings { Provider = "fake", Model = "m" } },
            Providers = { ["fake"] = new ProviderSettings { Type = "fake" } },
        };
        return new ConversationEngine(new ModelRouter(settings, _ => provider), new ToolRegistry(),
            new AssistantSettings { SystemPrompt = "You are Norm.", ConversationTask = "conversation" }, new SessionSignals());
    }

    [Fact]
    public async Task The_speaker_and_the_log_both_get_the_cleaned_text()
    {
        var sentences = new List<string>();
        var engine = Engine(new FakeChatProvider(new ChatResponse("Sure, **two layers** of 9mm. Have a great day! 😊", [])));

        var result = await engine.RunTurnAsync("bottom ply?", sentences.Add, CancellationToken.None);

        Assert.Equal(["Sure, two layers of 9mm.", "Have a great day!"], sentences);
        Assert.Equal("Sure, two layers of 9mm. Have a great day!", result.SpokenReply);
    }

    [Fact]
    public async Task An_emoji_on_its_own_is_not_spoken_at_all()
    {
        var sentences = new List<string>();
        var engine = Engine(new FakeChatProvider(new ChatResponse("Done. 🎉", [])));

        await engine.RunTurnAsync("thanks", sentences.Add, CancellationToken.None);

        Assert.Equal(["Done."], sentences);
    }
}
