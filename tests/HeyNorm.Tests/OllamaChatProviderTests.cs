using System.Net;
using System.Text.Json.Nodes;
using HeyNorm.Llm;

namespace HeyNorm.Tests;

public class OllamaChatProviderTests
{
    private static (OllamaChatProvider Provider, StubHandler Stub) Build(string response)
    {
        var stub = new StubHandler().Enqueue(response);
        return (new OllamaChatProvider(new HttpClient(stub), "http://localhost:11434"), stub);
    }

    private const string TextReply = """
        { "message": { "role": "assistant", "content": "Ten minutes it is." } }
        """;

    private static ChatRequest Request(params ChatMessage[] messages) => new()
    {
        Model = "qwen3:8b",
        SystemPrompt = "You are Norm.",
        Messages = messages,
        MaxTokens = 512,
    };

    [Fact]
    public async Task Prepends_the_system_prompt_as_the_first_message()
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        var body = stub.Request();
        Assert.Equal("/api/chat", stub.RequestUris[0].AbsolutePath);
        Assert.Equal("qwen3:8b", (string?)body["model"]);
        Assert.False((bool?)body["stream"]);
        Assert.Equal(512, (int?)body["options"]!["num_predict"]);

        var messages = body["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", (string?)messages[0]!["role"]);
        Assert.Equal("You are Norm.", (string?)messages[0]!["content"]);
        Assert.Equal("user", (string?)messages[1]!["role"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sends_the_think_flag_when_the_task_sets_one(bool think)
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")) with { Think = think });

        Assert.Equal(think, (bool?)stub.Request()["think"]);
    }

    [Fact]
    public async Task Omits_the_think_flag_when_the_task_leaves_it_unset()
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")) with { Think = null });

        Assert.Null(stub.Request()["think"]);
    }

    [Fact]
    public async Task Maps_tools_into_the_function_envelope()
    {
        var (provider, stub) = Build(TextReply);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["minutes"] = new JsonObject { ["type"] = "number" } },
        };

        await provider.CompleteAsync(Request(ChatMessage.User("hi")) with
        {
            Tools = [new ToolDefinition("set_timer", "Start a timer.", schema)],
        });

        var tool = stub.Request()["tools"]!.AsArray()[0]!;
        Assert.Equal("function", (string?)tool["type"]);
        Assert.Equal("set_timer", (string?)tool["function"]!["name"]);
        Assert.Equal("object", (string?)tool["function"]!["parameters"]!["type"]);
    }

    [Fact]
    public async Task Omits_tools_entirely_when_there_are_none()
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        Assert.Null(stub.Request()["tools"]);
    }

    [Fact]
    public async Task Parses_tool_calls_and_synthesizes_an_id_when_ollama_omits_one()
    {
        var (provider, _) = Build("""
            {
              "message": {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                  { "function": { "name": "set_timer", "arguments": { "minutes": 10, "label": "glue up" } } }
                ]
              }
            }
            """);

        var response = await provider.CompleteAsync(Request(ChatMessage.User("ten minute glue timer")));

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("set_timer", call.Name);
        Assert.StartsWith("call_", call.Id);
        Assert.Equal(10, call.Arguments.GetProperty("minutes").GetInt32());
        Assert.Equal("glue up", call.Arguments.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Each_synthesized_tool_call_id_is_distinct()
    {
        var (provider, _) = Build("""
            {
              "message": {
                "content": "",
                "tool_calls": [
                  { "function": { "name": "a", "arguments": {} } },
                  { "function": { "name": "b", "arguments": {} } }
                ]
              }
            }
            """);

        var response = await provider.CompleteAsync(Request(ChatMessage.User("both")));

        Assert.Equal(2, response.ToolCalls.Count);
        Assert.NotEqual(response.ToolCalls[0].Id, response.ToolCalls[1].Id);
    }

    [Fact]
    public async Task Prefers_an_id_that_ollama_does_supply()
    {
        var (provider, _) = Build("""
            {
              "message": {
                "content": "",
                "tool_calls": [
                  { "id": "call_abc", "function": { "name": "a", "arguments": {} } }
                ]
              }
            }
            """);

        var response = await provider.CompleteAsync(Request(ChatMessage.User("go")));

        Assert.Equal("call_abc", Assert.Single(response.ToolCalls).Id);
    }

    [Fact]
    public async Task Handles_a_message_with_no_content_field()
    {
        var (provider, _) = Build("""{ "message": { "role": "assistant" } }""");

        var response = await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        Assert.Equal("", response.Text);
        Assert.False(response.WantsTools);
    }

    [Fact]
    public async Task Trims_surrounding_whitespace_from_the_reply()
    {
        var (provider, _) = Build("""{ "message": { "content": "  Ten minutes.\n\n" } }""");

        var response = await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        Assert.Equal("Ten minutes.", response.Text);
    }

    [Fact]
    public async Task Round_trips_an_assistant_tool_call_back_onto_the_wire()
    {
        var (provider, stub) = Build(TextReply);
        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = "Checking.",
            ToolCalls = [new ToolCall("call_1", "set_timer", Json.Element("""{ "minutes": 10 }"""))],
        };

        await provider.CompleteAsync(Request(ChatMessage.User("timer"), assistant));

        var message = stub.Request()["messages"]!.AsArray()[2]!;
        Assert.Equal("assistant", (string?)message["role"]);
        Assert.Equal("Checking.", (string?)message["content"]);
        var fn = message["tool_calls"]!.AsArray()[0]!["function"]!;
        Assert.Equal("set_timer", (string?)fn["name"]);
        Assert.Equal(10, (int?)fn["arguments"]!["minutes"]);
    }

    [Fact]
    public async Task Tool_results_carry_the_tool_name_ollama_expects()
    {
        var (provider, stub) = Build(TextReply);
        var call = new ToolCall("call_1", "get_current_time", Json.Empty);

        await provider.CompleteAsync(Request(
            ChatMessage.User("time?"),
            new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [call] },
            ChatMessage.ToolResult(call, "3 pm")));

        var message = stub.Request()["messages"]!.AsArray()[3]!;
        Assert.Equal("tool", (string?)message["role"]);
        Assert.Equal("get_current_time", (string?)message["tool_name"]);
        Assert.Equal("3 pm", (string?)message["content"]);
    }

    [Fact]
    public async Task Throws_with_the_response_body_on_an_error_status()
    {
        var stub = new StubHandler().Enqueue(
            """{ "error": "model 'qwen3:8b' not found" }""", HttpStatusCode.NotFound);
        var provider = new OllamaChatProvider(new HttpClient(stub), "http://localhost:11434");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync(Request(ChatMessage.User("hi"))));

        Assert.Contains("404", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Falls_back_to_localhost_when_config_is_blank()
    {
        var http = new HttpClient(new StubHandler());

        _ = new OllamaChatProvider(http, "");

        Assert.Equal("http://localhost:11434/", http.BaseAddress!.ToString());
    }
}
