using System.Net;
using System.Text.Json.Nodes;
using HeyNorm.Llm;

namespace HeyNorm.Tests;

public class ClaudeChatProviderTests
{
    private static (ClaudeChatProvider Provider, StubHandler Stub) Build(string response)
    {
        var stub = new StubHandler().Enqueue(response);
        return (new ClaudeChatProvider(new HttpClient(stub), "https://api.anthropic.com", "test-key"), stub);
    }

    private const string TextReply = """
        { "content": [ { "type": "text", "text": "Ten minutes it is." } ] }
        """;

    private static ChatRequest Request(params ChatMessage[] messages) => new()
    {
        Model = "claude-sonnet-5-5",
        SystemPrompt = "You are Norm.",
        Messages = messages,
        MaxTokens = 512,
    };

    [Fact]
    public async Task Sends_api_key_and_version_headers()
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        Assert.Equal("test-key", stub.Headers[0]["x-api-key"]);
        Assert.Equal("2023-06-01", stub.Headers[0]["anthropic-version"]);
        Assert.Equal("/v1/messages", stub.RequestUris[0].AbsolutePath);
    }

    [Fact]
    public async Task Sends_system_prompt_out_of_band_not_as_a_message()
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        var body = stub.Request();
        Assert.Equal("You are Norm.", (string?)body["system"]);
        Assert.Equal(512, (int?)body["max_tokens"]);
        var messages = body["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", (string?)messages[0]!["role"]);
    }

    [Fact]
    public async Task Concatenates_text_blocks_and_collects_tool_use_blocks()
    {
        var (provider, _) = Build("""
            {
              "content": [
                { "type": "text", "text": "Sure. " },
                { "type": "tool_use", "id": "toolu_01", "name": "set_timer", "input": { "minutes": 10 } },
                { "type": "text", "text": "Starting now." }
              ]
            }
            """);

        var response = await provider.CompleteAsync(Request(ChatMessage.User("ten minute timer")));

        Assert.Equal("Sure. Starting now.", response.Text);
        Assert.True(response.WantsTools);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_01", call.Id);
        Assert.Equal("set_timer", call.Name);
        Assert.Equal(10, call.Arguments.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Ignores_unknown_content_block_types()
    {
        var (provider, _) = Build("""
            {
              "content": [
                { "type": "thinking", "thinking": "hmm" },
                { "type": "text", "text": "Done." }
              ]
            }
            """);

        var response = await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        Assert.Equal("Done.", response.Text);
        Assert.False(response.WantsTools);
    }

    [Fact]
    public async Task Maps_tools_to_input_schema()
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
        Assert.Equal("set_timer", (string?)tool["name"]);
        Assert.Equal("Start a timer.", (string?)tool["description"]);
        Assert.Equal("object", (string?)tool["input_schema"]!["type"]);
    }

    [Fact]
    public async Task Omits_tools_entirely_when_there_are_none()
    {
        var (provider, stub) = Build(TextReply);

        await provider.CompleteAsync(Request(ChatMessage.User("hi")));

        Assert.Null(stub.Request()["tools"]);
    }

    [Fact]
    public async Task Assistant_tool_calls_become_tool_use_blocks()
    {
        var (provider, stub) = Build(TextReply);
        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = "Let me check.",
            ToolCalls = [new ToolCall("toolu_01", "get_current_time", Json.Empty)],
        };

        await provider.CompleteAsync(Request(ChatMessage.User("what time?"), assistant));

        var content = stub.Request()["messages"]!.AsArray()[1]!["content"]!.AsArray();
        Assert.Equal("text", (string?)content[0]!["type"]);
        Assert.Equal("Let me check.", (string?)content[0]!["text"]);
        Assert.Equal("tool_use", (string?)content[1]!["type"]);
        Assert.Equal("toolu_01", (string?)content[1]!["id"]);
    }

    [Fact]
    public async Task Assistant_message_with_no_text_emits_only_the_tool_use_block()
    {
        var (provider, stub) = Build(TextReply);
        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = "   ",
            ToolCalls = [new ToolCall("toolu_01", "get_current_time", Json.Empty)],
        };

        await provider.CompleteAsync(Request(ChatMessage.User("what time?"), assistant));

        var content = stub.Request()["messages"]!.AsArray()[1]!["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("tool_use", (string?)content[0]!["type"]);
    }

    [Fact]
    public async Task Groups_consecutive_tool_results_into_one_user_turn()
    {
        var (provider, stub) = Build(TextReply);
        var first = new ToolCall("toolu_01", "get_current_time", Json.Empty);
        var second = new ToolCall("toolu_02", "set_timer", Json.Empty);
        var assistant = new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [first, second] };

        await provider.CompleteAsync(Request(
            ChatMessage.User("time, then a timer"),
            assistant,
            ChatMessage.ToolResult(first, "3 pm"),
            ChatMessage.ToolResult(second, "Timer set.")));

        var messages = stub.Request()["messages"]!.AsArray();
        // user, assistant, and ONE user turn holding both results — not two.
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", (string?)messages[2]!["role"]);

        var results = messages[2]!["content"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("tool_result", (string?)results[0]!["type"]);
        Assert.Equal("toolu_01", (string?)results[0]!["tool_use_id"]);
        Assert.Equal("toolu_02", (string?)results[1]!["tool_use_id"]);
    }

    [Fact]
    public async Task Flags_failed_tool_results_as_errors()
    {
        var (provider, stub) = Build(TextReply);
        var call = new ToolCall("toolu_01", "set_timer", Json.Empty);

        await provider.CompleteAsync(Request(
            ChatMessage.User("timer"),
            new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [call] },
            ChatMessage.ToolResult(call, "Tool failed: boom", isError: true)));

        var result = stub.Request()["messages"]!.AsArray()[2]!["content"]!.AsArray()[0]!;
        Assert.True((bool?)result["is_error"]);
        Assert.Equal("Tool failed: boom", (string?)result["content"]);
    }

    [Fact]
    public async Task Trailing_tool_results_are_flushed()
    {
        // The loop only flushes on the next non-tool message, so a conversation that *ends*
        // on tool results exercises the final flush.
        var (provider, stub) = Build(TextReply);
        var call = new ToolCall("toolu_01", "get_current_time", Json.Empty);

        await provider.CompleteAsync(Request(
            ChatMessage.User("time?"),
            new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [call] },
            ChatMessage.ToolResult(call, "3 pm")));

        var messages = stub.Request()["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("tool_result", (string?)messages[2]!["content"]!.AsArray()[0]!["type"]);
    }

    [Fact]
    public async Task Throws_with_the_response_body_on_an_error_status()
    {
        var stub = new StubHandler().Enqueue(
            """{ "error": { "message": "credit balance too low" } }""", HttpStatusCode.BadRequest);
        var provider = new ClaudeChatProvider(new HttpClient(stub), "https://api.anthropic.com", "k");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync(Request(ChatMessage.User("hi"))));

        Assert.Contains("400", ex.Message);
        Assert.Contains("credit balance too low", ex.Message);
    }

    [Fact]
    public void Falls_back_to_the_public_base_url_when_config_is_blank()
    {
        var http = new HttpClient(new StubHandler());

        _ = new ClaudeChatProvider(http, "", "k");

        Assert.Equal("https://api.anthropic.com/", http.BaseAddress!.ToString());
    }
}
