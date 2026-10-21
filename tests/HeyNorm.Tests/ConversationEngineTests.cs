using HeyNorm.Assistant;
using HeyNorm.Config;
using HeyNorm.Llm;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

public class ConversationEngineTests
{
    private static (ConversationEngine Engine, SessionSignals Signals) Build(
        FakeChatProvider provider, ToolRegistry? tools = null, int maxRounds = 5) =>
        Build(provider, _ => tools ?? new ToolRegistry(), maxRounds);

    /// <param name="tools">
    /// Takes the session's signals, so tools that raise them (end_session) can be wired up.
    /// </param>
    private static (ConversationEngine Engine, SessionSignals Signals) Build(
        FakeChatProvider provider, Func<SessionSignals, ToolRegistry> tools, int maxRounds = 5)
    {
        var settings = new AppSettings
        {
            Tasks = { ["conversation"] = new TaskSettings { Provider = "fake", Model = "test-model", MaxTokens = 256 } },
            Providers = { ["fake"] = new ProviderSettings { Type = "fake" } },
        };
        var signals = new SessionSignals();
        var engine = new ConversationEngine(
            new ModelRouter(settings, _ => provider),
            tools(signals),
            new AssistantSettings
            {
                SystemPrompt = "You are Norm.",
                ConversationTask = "conversation",
                MaxToolRoundsPerTurn = maxRounds,
            },
            signals);
        return (engine, signals);
    }

    private static ChatResponse Says(string text) => new(text, []);

    private static ChatResponse Calls(string tool, string id = "call_1") =>
        new("", [new ToolCall(id, tool, Json.Empty)]);

    [Fact]
    public async Task Returns_a_plain_reply_without_calling_tools()
    {
        var provider = new FakeChatProvider(Says("Ten minutes it is."));
        var (engine, _) = Build(provider);

        var result = await engine.RunTurnAsync("set a timer", CancellationToken.None);

        Assert.Equal("Ten minutes it is.", result.SpokenReply);
        Assert.False(result.EndSession);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Passes_the_task_model_and_system_prompt_through_to_the_provider()
    {
        var provider = new FakeChatProvider(Says("ok"));
        var (engine, _) = Build(provider);

        await engine.RunTurnAsync("hi", CancellationToken.None);

        var request = provider.Requests[0];
        Assert.Equal("test-model", request.Model);
        Assert.Equal("You are Norm.", request.SystemPrompt);
        Assert.Equal(256, request.MaxTokens);
    }

    [Fact]
    public async Task Runs_a_requested_tool_and_feeds_the_result_back()
    {
        var tool = new StubTool("get_current_time", "3 pm");
        var provider = new FakeChatProvider(Calls("get_current_time"), Says("It's 3 pm."));
        var (engine, _) = Build(provider, new ToolRegistry().Add(tool));

        var result = await engine.RunTurnAsync("what time is it?", CancellationToken.None);

        Assert.Equal("It's 3 pm.", result.SpokenReply);
        Assert.Equal(1, tool.Invocations);
        Assert.Equal(2, provider.CallCount);

        // Second round sees: user, assistant(tool_call), tool result.
        var second = provider.Requests[1].Messages;
        Assert.Equal(3, second.Count);
        Assert.Equal(ChatRole.Tool, second[2].Role);
        Assert.Equal("3 pm", second[2].Text);
        Assert.Equal("call_1", second[2].ToolCallId);
    }

    [Fact]
    public async Task Runs_every_tool_in_a_parallel_request()
    {
        var time = new StubTool("get_current_time", "3 pm");
        var timer = new StubTool("set_timer", "Timer set.");
        var provider = new FakeChatProvider(
            new ChatResponse("", [
                new ToolCall("call_1", "get_current_time", Json.Empty),
                new ToolCall("call_2", "set_timer", Json.Empty),
            ]),
            Says("Done."));
        var (engine, _) = Build(provider, new ToolRegistry().Add(time).Add(timer));

        await engine.RunTurnAsync("time, then a timer", CancellationToken.None);

        Assert.Equal(1, time.Invocations);
        Assert.Equal(1, timer.Invocations);
        var second = provider.Requests[1].Messages;
        Assert.Equal(4, second.Count);
        Assert.Equal("call_1", second[2].ToolCallId);
        Assert.Equal("call_2", second[3].ToolCallId);
    }

    [Fact]
    public async Task Chains_several_tool_rounds()
    {
        var tool = new StubTool("get_current_time", "3 pm");
        var provider = new FakeChatProvider(
            Calls("get_current_time", "call_1"),
            Calls("get_current_time", "call_2"),
            Says("Twice checked."));
        var (engine, _) = Build(provider, new ToolRegistry().Add(tool));

        var result = await engine.RunTurnAsync("check twice", CancellationToken.None);

        Assert.Equal("Twice checked.", result.SpokenReply);
        Assert.Equal(2, tool.Invocations);
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task Gives_up_with_a_spoken_apology_when_the_model_never_stops_calling_tools()
    {
        var tool = new StubTool("get_current_time", "3 pm");
        var looping = Enumerable.Range(0, 10).Select(i => Calls("get_current_time", $"call_{i}")).ToArray();
        var provider = new FakeChatProvider(looping);
        var (engine, _) = Build(provider, new ToolRegistry().Add(tool), maxRounds: 3);

        var result = await engine.RunTurnAsync("loop forever", CancellationToken.None);

        Assert.Equal("Sorry, I got stuck on that one.", result.SpokenReply);
        // maxRounds: 3 means rounds 0..3 inclusive — four attempts.
        Assert.Equal(4, provider.CallCount);
    }

    [Fact]
    public async Task Reports_end_of_session_when_the_end_session_tool_fired()
    {
        var provider = new FakeChatProvider(Calls("end_session"), Says("Catch you later."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("thanks, that's all", CancellationToken.None);

        Assert.True(result.EndSession);
        Assert.Equal("Catch you later.", result.SpokenReply);
    }

    [Fact]
    public async Task A_turn_that_calls_no_tools_leaves_the_session_running()
    {
        var provider = new FakeChatProvider(Says("Sure thing."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("set a timer", CancellationToken.None);

        Assert.False(result.EndSession);
    }

    // --- models that type a tool name instead of calling it --------------------
    // llama3.2 does this when no tool fits the question: the reply text is literally
    // "end_session". Speaking that aloud is never right.

    [Fact]
    public async Task A_reply_that_is_only_a_tool_name_is_never_spoken()
    {
        var provider = new FakeChatProvider(Says("end_session"), Says("I can't check the weather."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("what's the weather today?", CancellationToken.None);

        Assert.Equal("I can't check the weather.", result.SpokenReply);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task The_model_is_told_what_it_did_wrong()
    {
        var provider = new FakeChatProvider(Says("end_session"), Says("Can't do that one."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        await engine.RunTurnAsync("what's the weather?", CancellationToken.None);

        var correction = provider.Requests[1].Messages.Last();
        Assert.Equal(ChatRole.User, correction.Role);
        Assert.Contains("tool name", correction.Text);
        // The correction must not name the tool either — that is what provokes the echo.
        Assert.DoesNotContain("end_session", correction.Text);
    }

    [Theory]
    [InlineData("end_session")]
    [InlineData("  end_session  ")]
    [InlineData("\"end_session\"")]
    [InlineData("`end_session`")]
    [InlineData("end_session()")]
    [InlineData("{end_session}")]
    [InlineData("end_session.")]
    public async Task Recognises_the_wrappings_models_put_around_a_bare_tool_name(string reply)
    {
        var provider = new FakeChatProvider(Says(reply), Says("Recovered."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("hi", CancellationToken.None);

        Assert.Equal("Recovered.", result.SpokenReply);
    }

    [Theory]
    [InlineData("You're welcome! Have a great day. End_session.", "You're welcome! Have a great day.")]
    [InlineData("You're all set. end_session", "You're all set.")]
    [InlineData("Sure thing. Goodbye. end_session()", "Sure thing. Goodbye.")]
    public async Task A_tool_name_tacked_onto_a_good_reply_is_stripped_before_speaking(
        string reply, string expected)
    {
        // qwen3 does this on sign-off: the answer is fine, the tool name is riding along.
        var provider = new FakeChatProvider(Says(reply));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("thanks, that's all", CancellationToken.None);

        Assert.Equal(expected, result.SpokenReply);
        Assert.Equal(1, provider.CallCount); // a usable answer survived — no retry needed
    }

    [Fact]
    public async Task A_tool_name_used_inside_a_real_sentence_is_left_alone()
    {
        var provider = new FakeChatProvider(Says("I'll call end_session when we're finished."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("how do you stop?", CancellationToken.None);

        Assert.Equal("I'll call end_session when we're finished.", result.SpokenReply);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task A_word_that_is_not_a_registered_tool_is_spoken_normally()
    {
        var provider = new FakeChatProvider(Says("Yes."));
        var (engine, _) = Build(provider);

        var result = await engine.RunTurnAsync("ready?", CancellationToken.None);

        Assert.Equal("Yes.", result.SpokenReply);
    }

    [Fact]
    public async Task A_model_that_keeps_typing_the_tool_name_gets_a_graceful_reply_not_the_name()
    {
        var provider = new FakeChatProvider(Says("end_session"), Says("end_session"));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var result = await engine.RunTurnAsync("what's the weather?", CancellationToken.None);

        Assert.Equal("Sorry, I got stuck on that one.", result.SpokenReply);
        Assert.DoesNotContain("end_session", result.SpokenReply);
    }

    [Fact]
    public async Task An_unknown_tool_comes_back_as_an_error_the_model_can_recover_from()
    {
        var provider = new FakeChatProvider(Calls("no_such_tool"), Says("I can't do that one."));
        var (engine, _) = Build(provider);

        var result = await engine.RunTurnAsync("do the thing", CancellationToken.None);

        Assert.Equal("I can't do that one.", result.SpokenReply);
        var toolResult = provider.Requests[1].Messages[2];
        Assert.True(toolResult.IsError);
        Assert.Contains("Unknown tool", toolResult.Text);
    }

    [Fact]
    public async Task History_accumulates_across_turns_within_a_session()
    {
        var provider = new FakeChatProvider(Says("Hello."), Says("Still here."));
        var (engine, _) = Build(provider);

        await engine.RunTurnAsync("hi", CancellationToken.None);
        await engine.RunTurnAsync("you there?", CancellationToken.None);

        var second = provider.Requests[1].Messages;
        Assert.Equal(3, second.Count); // user, assistant, user
        Assert.Equal("hi", second[0].Text);
        Assert.Equal("Hello.", second[1].Text);
        Assert.Equal("you there?", second[2].Text);
    }

    [Fact]
    public async Task Reset_clears_history_and_the_end_flag_so_the_next_session_starts_fresh()
    {
        var provider = new FakeChatProvider(Says("Hello."), Says("Fresh start."));
        var (engine, signals) = Build(provider);
        await engine.RunTurnAsync("hi", CancellationToken.None);
        signals.EndRequested = true;

        engine.Reset();
        var result = await engine.RunTurnAsync("hello again", CancellationToken.None);

        Assert.False(result.EndSession);
        var second = provider.Requests[1].Messages;
        Assert.Single(second);
        Assert.Equal("hello again", second[0].Text);
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_the_turn()
    {
        var provider = new FakeChatProvider(Says("never reached"));
        var (engine, _) = Build(provider);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.RunTurnAsync("hi", cts.Token));
    }

    // --- streaming: each sentence is handed over as it completes ----------------

    private static async Task<(TurnResult Result, List<string> Sentences)> RunStreamed(
        ConversationEngine engine, string userText)
    {
        var sentences = new List<string>();
        var result = await engine.RunTurnAsync(userText, sentences.Add, CancellationToken.None);
        return (result, sentences);
    }

    [Fact]
    public async Task Hands_over_the_reply_one_sentence_at_a_time()
    {
        var provider = new FakeChatProvider(Says("Sure thing. Ten minutes on the clock! Anything else?"));
        var (engine, _) = Build(provider);

        var (result, sentences) = await RunStreamed(engine, "set a timer");

        Assert.Equal(["Sure thing.", "Ten minutes on the clock!", "Anything else?"], sentences);
        Assert.Equal("Sure thing. Ten minutes on the clock! Anything else?", result.SpokenReply);
    }

    [Fact]
    public async Task A_tool_name_tacked_onto_a_streamed_reply_never_reaches_the_speaker()
    {
        var provider = new FakeChatProvider(Says("You're welcome! Have a great day. End_session."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var (_, sentences) = await RunStreamed(engine, "thanks");

        Assert.Equal(["You're welcome!", "Have a great day."], sentences);
    }

    [Fact]
    public async Task A_bare_tool_name_reply_is_retried_without_speaking_anything_from_it()
    {
        var provider = new FakeChatProvider(Says("end_session"), Says("I can't check the weather."));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var (_, sentences) = await RunStreamed(engine, "what's the weather?");

        Assert.Equal(["I can't check the weather."], sentences);
    }

    [Fact]
    public async Task Text_that_comes_with_a_tool_call_is_spoken_while_the_tool_runs()
    {
        // Claude often says what it's about to do before calling the tool. Speaking it fills
        // the silence while the tool runs.
        var tool = new StubTool("get_current_time", "3 pm");
        var provider = new FakeChatProvider(
            new ChatResponse("Let me check.", [new ToolCall("call_1", "get_current_time", Json.Empty)]),
            Says("It's 3 pm."));
        var (engine, _) = Build(provider, new ToolRegistry().Add(tool));

        var (result, sentences) = await RunStreamed(engine, "what time is it?");

        Assert.Equal(["Let me check.", "It's 3 pm."], sentences);
        Assert.Equal("Let me check. It's 3 pm.", result.SpokenReply);
    }

    [Fact]
    public async Task The_stuck_apology_is_handed_over_too()
    {
        var provider = new FakeChatProvider(Says("end_session"), Says("end_session"));
        var (engine, _) = Build(provider, signals => new ToolRegistry().Add(new EndSessionTool(signals)));

        var (_, sentences) = await RunStreamed(engine, "what's the weather?");

        Assert.Equal(["Sorry, I got stuck on that one."], sentences);
    }

    [Fact]
    public async Task Warming_up_preloads_the_conversation_tasks_model()
    {
        var provider = new FakeChatProvider();
        var (engine, _) = Build(provider);

        await engine.WarmUpAsync(CancellationToken.None);

        Assert.Equal(["test-model"], provider.WarmedUp);
        Assert.Equal(0, provider.CallCount); // loading only, no conversation
    }
}
