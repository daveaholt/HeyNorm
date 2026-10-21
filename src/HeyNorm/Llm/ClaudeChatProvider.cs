using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyNorm.Llm;

/// <summary>
/// Claude Messages API (POST /v1/messages) with tool use. Same contract as the Ollama provider,
/// so switching is a config change. The API key is read from an environment variable.
/// </summary>
public sealed class ClaudeChatProvider : IChatProvider
{
    private readonly HttpClient _http;

    public ClaudeChatProvider(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com" : baseUrl);
        _http.Timeout = TimeSpan.FromMinutes(2);
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public string Name => "Claude";

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = BuildBody(request, stream: false);
        using var response = await _http.PostAsJsonAsync("/v1/messages", body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API returned {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();

        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    text.Append(block.GetProperty("text").GetString());
                    break;
                case "tool_use":
                    calls.Add(new ToolCall(
                        block.GetProperty("id").GetString()!,
                        block.GetProperty("name").GetString()!,
                        block.GetProperty("input").Clone()));
                    break;
            }
        }

        return new ChatResponse(text.ToString().Trim(), calls);
    }

    /// <summary>
    /// Claude streams server-sent events. Text arrives as text_delta fragments; a tool call's
    /// input arrives as fragments of JSON that only parse once its block is finished.
    /// </summary>
    public async Task<ChatResponse> StreamAsync(ChatRequest request, Action<string> onText, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(BuildBody(request, stream: true)),
        };
        // Without ResponseHeadersRead, HttpClient buffers the whole stream before returning.
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Claude API returned {(int)response.StatusCode}: {raw}");
        }

        var text = new System.Text.StringBuilder();
        // Keyed by content-block index, which is also the order the calls were made in.
        var toolBlocks = new SortedDictionary<int, (string Id, string Name, System.Text.StringBuilder Input)>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // Each event's data line repeats its type, so the "event:" lines can be ignored.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            using var doc = JsonDocument.Parse(line.AsMemory(5));
            var data = doc.RootElement;
            switch (data.GetProperty("type").GetString())
            {
                case "content_block_start":
                    var block = data.GetProperty("content_block");
                    if (block.GetProperty("type").GetString() == "tool_use")
                    {
                        toolBlocks[data.GetProperty("index").GetInt32()] = (
                            block.GetProperty("id").GetString()!,
                            block.GetProperty("name").GetString()!,
                            new System.Text.StringBuilder());
                    }
                    break;

                case "content_block_delta":
                    var delta = data.GetProperty("delta");
                    switch (delta.GetProperty("type").GetString())
                    {
                        case "text_delta" when delta.GetProperty("text").GetString() is { Length: > 0 } fragment:
                            text.Append(fragment);
                            onText(fragment);
                            break;
                        case "input_json_delta"
                            when toolBlocks.TryGetValue(data.GetProperty("index").GetInt32(), out var tool):
                            tool.Input.Append(delta.GetProperty("partial_json").GetString());
                            break;
                    }
                    break;

                case "error":
                    // Overload and similar failures can arrive mid-stream, after a 200.
                    throw new InvalidOperationException($"Claude API stream failed: {data.GetProperty("error")}");

                case "message_stop":
                    return Finish();
            }
        }

        return Finish();

        ChatResponse Finish()
        {
            var calls = toolBlocks.Values.Select(t => new ToolCall(t.Id, t.Name, ParseInput(t.Input.ToString()))).ToList();
            return new ChatResponse(text.ToString().Trim(), calls);
        }
    }

    /// <summary>Hosted models are always loaded.</summary>
    public Task WarmUpAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>A tool with no arguments streams no input fragments at all.</summary>
    private static JsonElement ParseInput(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.Clone();
    }

    private static JsonObject BuildBody(ChatRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = request.SystemPrompt,
            ["messages"] = ToClaudeMessages(request.Messages),
        };
        if (stream)
            body["stream"] = true;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.ParametersSchema.DeepClone(),
                });
            }
            body["tools"] = tools;
        }

        return body;
    }

    /// <summary>
    /// Claude wants tool results as tool_result blocks inside a user turn, and consecutive
    /// results from one assistant turn grouped into a single user message.
    /// </summary>
    private static JsonArray ToClaudeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new JsonArray();
        JsonArray? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is null) return;
            result.Add(new JsonObject { ["role"] = "user", ["content"] = pendingToolResults });
            pendingToolResults = null;
        }

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool)
            {
                pendingToolResults ??= new JsonArray();
                pendingToolResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.Text,
                    ["is_error"] = m.IsError,
                });
                continue;
            }

            FlushToolResults();

            if (m.Role == ChatRole.User)
            {
                result.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                continue;
            }

            var content = new JsonArray();
            if (!string.IsNullOrWhiteSpace(m.Text))
                content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
            foreach (var call in m.ToolCalls)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["input"] = JsonNode.Parse(call.Arguments.GetRawText()),
                });
            }
            result.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
        }

        FlushToolResults();
        return result;
    }
}
