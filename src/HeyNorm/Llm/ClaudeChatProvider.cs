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
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = request.SystemPrompt,
            ["messages"] = ToClaudeMessages(request.Messages),
        };

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
