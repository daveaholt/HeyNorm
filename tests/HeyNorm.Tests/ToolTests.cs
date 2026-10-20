using HeyNorm.Assistant;
using HeyNorm.Llm;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

public class ToolRegistryTests
{
    private static ToolCall Call(string name) => new("call_1", name, Json.Empty);

    [Fact]
    public async Task Invokes_a_registered_tool_and_tags_the_result_with_the_call()
    {
        var registry = new ToolRegistry().Add(new StubTool("echo", "hello"));

        var message = await registry.InvokeAsync(Call("echo"), CancellationToken.None);

        Assert.Equal(ChatRole.Tool, message.Role);
        Assert.Equal("hello", message.Text);
        Assert.Equal("call_1", message.ToolCallId);
        Assert.Equal("echo", message.ToolName);
        Assert.False(message.IsError);
    }

    [Fact]
    public async Task Unknown_tools_come_back_as_an_error_result_rather_than_throwing()
    {
        var registry = new ToolRegistry();

        var message = await registry.InvokeAsync(Call("nope"), CancellationToken.None);

        Assert.True(message.IsError);
        Assert.Contains("Unknown tool 'nope'", message.Text);
    }

    [Fact]
    public async Task A_throwing_tool_becomes_an_error_result_the_model_can_read()
    {
        var registry = new ToolRegistry()
            .Add(new StubTool("boom", (_, _) => throw new InvalidOperationException("the saw is unplugged")));

        var message = await registry.InvokeAsync(Call("boom"), CancellationToken.None);

        Assert.True(message.IsError);
        Assert.Contains("the saw is unplugged", message.Text);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_tool_error()
    {
        var registry = new ToolRegistry()
            .Add(new StubTool("slow", (_, ct) => Task.FromCanceled<string>(new CancellationToken(true))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.InvokeAsync(Call("slow"), CancellationToken.None));
    }

    [Fact]
    public void Tool_names_are_matched_case_insensitively()
    {
        var registry = new ToolRegistry().Add(new StubTool("Echo", "hi"));

        var definition = Assert.Single(registry.Definitions);

        Assert.Equal("Echo", definition.Name);
    }

    [Fact]
    public async Task Case_differences_still_resolve_to_the_same_tool()
    {
        var tool = new StubTool("Echo", "hi");
        var registry = new ToolRegistry().Add(tool);

        var message = await registry.InvokeAsync(Call("echo"), CancellationToken.None);

        Assert.False(message.IsError);
        Assert.Equal(1, tool.Invocations);
    }

    [Fact]
    public void Re_registering_a_name_replaces_the_earlier_tool()
    {
        var registry = new ToolRegistry()
            .Add(new StubTool("echo", "first"))
            .Add(new StubTool("echo", "second"));

        Assert.Single(registry.Definitions);
    }

    [Fact]
    public void Definitions_expose_name_description_and_schema_for_the_providers()
    {
        var registry = new ToolRegistry().Add(new CurrentTimeTool()).Add(new TimerTool(_ => { }));

        var names = registry.Definitions.Select(d => d.Name).ToList();

        Assert.Contains("get_current_time", names);
        Assert.Contains("set_timer", names);
        Assert.All(registry.Definitions, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            Assert.Equal("object", (string?)d.ParametersSchema["type"]);
        });
    }
}

public class BuiltInToolTests
{
    [Fact]
    public async Task Current_time_returns_a_spoken_form_date()
    {
        var result = await new CurrentTimeTool().InvokeAsync(Json.Empty, CancellationToken.None);

        Assert.Contains(DateTime.Now.Year.ToString(), result);
        Assert.Contains("at", result);
        // No ISO timestamps: this gets read aloud.
        Assert.DoesNotContain("T", result.Split(" at ")[1]);
    }

    [Fact]
    public async Task End_session_raises_the_signal_the_engine_reads()
    {
        var signals = new SessionSignals();

        var result = await new EndSessionTool(signals).InvokeAsync(Json.Empty, CancellationToken.None);

        Assert.True(signals.EndRequested);
        Assert.Contains("sign-off", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public async Task Timer_rejects_out_of_range_durations_without_scheduling_anything(double minutes)
    {
        var announced = false;
        var tool = new TimerTool(_ => announced = true);

        var result = await tool.InvokeAsync(
            Json.Element($$"""{ "minutes": {{minutes}} }"""), CancellationToken.None);

        Assert.Contains("between 0 and 1440", result);
        Assert.False(announced);
    }

    [Fact]
    public async Task Timer_accepts_a_duration_sent_as_a_string()
    {
        // llama3.2 sends "minutes": "10" despite the schema saying number.
        var tool = new TimerTool(_ => { });

        var result = await tool.InvokeAsync(
            Json.Element("""{ "minutes": "10", "label": "glue up" }"""), CancellationToken.None);

        Assert.Contains("10", result);
        Assert.Contains("glue up", result);
        Assert.DoesNotContain("Tool failed", result);
    }

    [Theory]
    [InlineData("""{ "minutes": "not a number" }""")]
    [InlineData("""{ "minutes": null }""")]
    [InlineData("""{ "label": "glue up" }""")]
    public async Task Timer_asks_for_a_duration_rather_than_throwing_when_it_cannot_read_one(string args)
    {
        var tool = new TimerTool(_ => { });

        var result = await tool.InvokeAsync(Json.Element(args), CancellationToken.None);

        Assert.Contains("how many minutes", result);
    }

    [Fact]
    public async Task A_string_duration_is_still_range_checked()
    {
        var tool = new TimerTool(_ => { });

        var result = await tool.InvokeAsync(Json.Element("""{ "minutes": "9999" }"""), CancellationToken.None);

        Assert.Contains("between 0 and 1440", result);
    }

    [Fact]
    public async Task Timer_confirms_the_duration_and_label()
    {
        var tool = new TimerTool(_ => { });

        var result = await tool.InvokeAsync(
            Json.Element("""{ "minutes": 10, "label": "glue up" }"""), CancellationToken.None);

        Assert.Contains("10", result);
        Assert.Contains("glue up", result);
    }

    [Fact]
    public async Task Timer_works_without_a_label()
    {
        var tool = new TimerTool(_ => { });

        var result = await tool.InvokeAsync(Json.Element("""{ "minutes": 5 }"""), CancellationToken.None);

        Assert.Contains("5", result);
        Assert.DoesNotContain("(", result);
    }

    [Fact]
    public async Task Timer_announces_when_it_elapses()
    {
        var announced = new TaskCompletionSource<string>();
        var tool = new TimerTool(text => announced.TrySetResult(text));

        await tool.InvokeAsync(
            Json.Element("""{ "minutes": 0.005, "label": "glue up" }"""), CancellationToken.None); // 300 ms

        var completed = await Task.WhenAny(announced.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(announced.Task, completed);
        Assert.Equal("Your glue up timer is done.", await announced.Task);
    }

    [Fact]
    public async Task An_unlabelled_timer_announces_generically()
    {
        var announced = new TaskCompletionSource<string>();
        var tool = new TimerTool(text => announced.TrySetResult(text));

        await tool.InvokeAsync(Json.Element("""{ "minutes": 0.005 }"""), CancellationToken.None);

        var completed = await Task.WhenAny(announced.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(announced.Task, completed);
        Assert.Equal("Your timer is done.", await announced.Task);
    }
}
