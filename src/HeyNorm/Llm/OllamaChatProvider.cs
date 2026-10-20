using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyNorm.Llm;

/// <summary>
/// Talks to a local Ollama server via POST /api/chat with native tool calling.
/// </summary>
public sealed class OllamaChatProvider : IChatProvider
{
    private readonly HttpClient _http;

    public OllamaChatProvider(HttpClient http, string baseUrl)
    {
        _http = http;
        _http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl);
        _http.Timeout = TimeSpan.FromMinutes(3); // first call can include model load time
    }

    public string Name => "Ollama";

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt } };
        foreach (var m in request.Messages)
            messages.Add(ToOllama(m));

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["options"] = new JsonObject { ["num_predict"] = request.MaxTokens },
        };
        if (request.Think is bool think)
            body["think"] = think;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.ParametersSchema.DeepClone(),
                    },
                });
            }
            body["tools"] = tools;
        }

        using var response = await _http.PostAsJsonAsync("/api/chat", body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var message = doc.RootElement.GetProperty("message");
        var text = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                // Ollama doesn't issue tool-call ids, so make one to keep the neutral model consistent.
                var id = call.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()!
                    : "call_" + Guid.NewGuid().ToString("N")[..12];
                var args = fn.TryGetProperty("arguments", out var a) ? a.Clone() : EmptyObject();
                calls.Add(new ToolCall(id, fn.GetProperty("name").GetString()!, args));
            }
        }

        return new ChatResponse(text.Trim(), calls);
    }

    private static JsonObject ToOllama(ChatMessage m) => m.Role switch
    {
        ChatRole.User => new JsonObject { ["role"] = "user", ["content"] = m.Text },
        ChatRole.Tool => new JsonObject { ["role"] = "tool", ["content"] = m.Text, ["tool_name"] = m.ToolName },
        ChatRole.Assistant => AssistantToOllama(m),
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    private static JsonObject AssistantToOllama(ChatMessage m)
    {
        var obj = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
        if (m.ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in m.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                    },
                });
            }
            obj["tool_calls"] = calls;
        }
        return obj;
    }

    private static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }
}
