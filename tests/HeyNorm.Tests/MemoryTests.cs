using HeyNorm.Assistant;
using HeyNorm.Config;
using HeyNorm.Llm;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

public class MemoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"heynorm-memory-{Guid.NewGuid():N}", "memory.txt");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { }
    }

    private Memory Fresh() => new(_path);

    [Fact]
    public void What_is_remembered_survives_a_restart()
    {
        Fresh().Remember("They work in fractions of an inch, not decimals.");

        Assert.Equal(["They work in fractions of an inch, not decimals."], Fresh().Items);
    }

    [Fact]
    public void A_correction_replaces_what_it_corrects_rather_than_contradicting_it()
    {
        var memory = Fresh();
        memory.Remember("Titebond 3 has an open time of about 15 minutes.");

        var saved = memory.Remember("Titebond 3 has an open time of about 10 minutes.");

        Assert.Equal("Titebond 3 has an open time of about 15 minutes.", saved.Replaced);
        Assert.Equal(["Titebond 3 has an open time of about 10 minutes."], memory.Items);
    }

    [Fact]
    public void Unrelated_things_are_kept_side_by_side()
    {
        var memory = Fresh();
        memory.Remember("They have a SawStop table saw.");
        memory.Remember("Their shop is unheated.");

        Assert.Equal(2, memory.Items.Count);
    }

    [Theory]
    [InlineData("They remember that Titebond 3 has an open time of about 10 minutes.")] // measured
    [InlineData("remember that Titebond 3 has an open time of about 10 minutes")]
    public void A_remember_that_prefix_is_dropped_and_the_sentence_tidied(string said)
    {
        var memory = Fresh();

        memory.Remember(said);

        Assert.Equal(["Titebond 3 has an open time of about 10 minutes."], memory.Items);
    }

    [Fact]
    public void Forgetting_finds_the_item_from_the_whole_request_the_model_passes()
    {
        // Measured: qwen3 sent "Forget what I said about fractions." and nothing matched.
        var memory = Fresh();
        memory.Remember("They work in fractions of an inch, not decimals.");
        memory.Remember("They have a SawStop table saw.");

        var result = memory.Forget("Forget what I said about fractions.");

        Assert.Equal("They work in fractions of an inch, not decimals.", result.Removed);
        Assert.Equal(["They have a SawStop table saw."], memory.Items);
    }

    [Fact]
    public void Two_equally_good_matches_forget_nothing_and_ask()
    {
        var memory = Fresh();
        memory.Remember("Their table saw blade is a 40 tooth rip blade.");
        memory.Remember("Their bandsaw blade is a half inch resaw blade.");

        var result = memory.Forget("the blade");

        Assert.Null(result.Removed);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(2, memory.Items.Count);
    }

    [Fact]
    public void A_hand_edit_is_picked_up_on_the_next_read()
    {
        var memory = Fresh();
        memory.Remember("They have a SawStop table saw.");

        File.WriteAllText(_path, "# edited by hand\n- Their shop is unheated.\n\n* They prefer metric for metalwork\n");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(["Their shop is unheated.", "They prefer metric for metalwork."], memory.Items);
    }

    [Fact]
    public void Saving_stops_at_the_cap_and_says_so()
    {
        var memory = new Memory(_path, max: 2);
        memory.Remember("They have a SawStop table saw.");
        memory.Remember("Their shop is unheated.");

        Assert.True(memory.Remember("They use hide glue for chairs.").Full);
        Assert.Equal(2, memory.Items.Count);
    }

    [Fact]
    public void Nothing_remembered_adds_nothing_to_the_prompt()
    {
        Assert.Equal("", Fresh().PromptSection());
    }

    [Fact]
    public void Remembered_things_go_in_the_prompt_ahead_of_lookups()
    {
        var memory = Fresh();
        memory.Remember("Titebond 3 has an open time of about 10 minutes.");

        var section = memory.PromptSection();

        Assert.Contains("- Titebond 3 has an open time of about 10 minutes.", section);
        // Measured: without this, qwen3 searched the web instead of using the correction.
        Assert.Contains("without looking anything up", section);
    }
}

public class MemoryToolTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"heynorm-memtool-{Guid.NewGuid():N}", "memory.txt");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Remember_confirms_and_reports_what_a_correction_replaced()
    {
        var memory = new Memory(_path);
        var tool = new RememberTool(memory);
        await tool.InvokeAsync(Json.Element("""{ "fact": "Titebond 3 open time is about 15 minutes" }"""), CancellationToken.None);

        var reply = await tool.InvokeAsync(Json.Element("""{ "fact": "Titebond 3 open time is about 10 minutes" }"""), CancellationToken.None);

        Assert.StartsWith("Remembered \"Titebond 3 open time is about 10 minutes.\", replacing \"Titebond 3 open time is about 15 minutes.\"", reply);
    }

    [Fact]
    public async Task A_full_memory_reads_as_a_failure()
    {
        var memory = new Memory(_path, max: 0);

        var reply = await new RememberTool(memory).InvokeAsync(Json.Element("""{ "fact": "anything" }"""), CancellationToken.None);

        Assert.StartsWith("That didn't work: memory is full.", reply);
    }

    [Fact]
    public async Task Forget_asks_which_when_two_match()
    {
        var memory = new Memory(_path);
        memory.Remember("Their table saw blade is a 40 tooth rip blade.");
        memory.Remember("Their bandsaw blade is a half inch resaw blade.");

        var reply = await new ForgetTool(memory).InvokeAsync(Json.Element("""{ "about": "blade" }"""), CancellationToken.None);

        Assert.Contains("matches more than one thing", reply);
        Assert.Contains("Ask the person which one", reply);
    }

    [Fact]
    public void No_tool_description_carries_an_example_the_model_could_take_for_a_memory()
    {
        // Measured: the remember tool's description once gave "They work in fractions of an
        // inch, not decimals." as an example, and qwen3 — asked what it remembered, in a fresh
        // session with nothing about fractions in memory — answered with it, 2 times in 3.
        var memory = new Memory(_path);
        ITool[] tools = [new RememberTool(memory), new ForgetTool(memory)];

        Assert.All(tools, t => Assert.DoesNotContain("e.g.", t.Description));
        Assert.All(tools, t => Assert.DoesNotContain("for example", t.Description, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task What_is_remembered_reaches_the_model_on_the_very_next_round()
    {
        // Saved mid-turn, it must apply to the model's next request in the same turn.
        var memory = new Memory(_path);
        var provider = new FakeChatProvider(
            new ChatResponse("", [new ToolCall("c1", "remember", Json.Element("""{ "fact": "Their shop is unheated." }"""))]),
            new ChatResponse("Got it.", []));
        var settings = new AppSettings
        {
            Tasks = { ["conversation"] = new TaskSettings { Provider = "fake", Model = "m" } },
            Providers = { ["fake"] = new ProviderSettings { Type = "fake" } },
        };
        var engine = new HeyNorm.Assistant.ConversationEngine(new ModelRouter(settings, _ => provider),
            new ToolRegistry().Add(new RememberTool(memory)),
            new AssistantSettings { SystemPrompt = "You are Norm.", ConversationTask = "conversation" },
            new SessionSignals(), memory.PromptSection);

        await engine.RunTurnAsync("remember my shop is unheated", CancellationToken.None);

        Assert.DoesNotContain("unheated", provider.Requests[0].SystemPrompt);
        Assert.Contains("- Their shop is unheated.", provider.Requests[1].SystemPrompt);
    }
}
