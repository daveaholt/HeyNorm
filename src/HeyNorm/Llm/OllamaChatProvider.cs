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
        using var response = await _http.PostAsJsonAsync("/api/chat", BuildBody(request, stream: false), ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var message = doc.RootElement.GetProperty("message");
        var text = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

        var calls = new List<ToolCall>();
        ReadToolCalls(message, calls);
        return new ChatResponse(text.Trim(), calls);
    }

    /// <summary>
    /// Ollama streams one JSON object per line, each carrying a fragment of the message.
    /// Tool calls arrive whole in a single chunk rather than in pieces.
    /// </summary>
    public async Task<ChatResponse> StreamAsync(ChatRequest request, Action<string> onText, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(BuildBody(request, stream: true)),
        };
        // Without ResponseHeadersRead, HttpClient buffers the whole stream before returning.
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {raw}");
        }

        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            // A failure mid-generation comes as a 200 with an error line, not an error status.
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Ollama stream failed: {error}");

            if (root.TryGetProperty("message", out var chunk))
            {
                if (chunk.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } fragment)
                {
                    text.Append(fragment);
                    onText(fragment);
                }
                ReadToolCalls(chunk, calls);
            }

            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                break;
        }

        return new ChatResponse(text.ToString().Trim(), calls);
    }

    /// <summary>
    /// A chat request with no messages makes Ollama load the model and return without
    /// generating. Ollama unloads a model after five idle minutes, and reloading it costs
    /// seconds on the next question.
    /// </summary>
    public async Task WarmUpAsync(string model, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = model, ["messages"] = new JsonArray(), ["stream"] = false };
        using var response = await _http.PostAsJsonAsync("/api/chat", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {raw}");
        }
    }

    private static JsonObject BuildBody(ChatRequest request, bool stream)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt } };
        foreach (var m in request.Messages)
            messages.Add(ToOllama(m));

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["options"] = Options(request),
        };
        if (request.Think is bool think)
            body["think"] = think;
        if (request.ResponseSchema is { } schema)
            body["format"] = schema.DeepClone();

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
        return body;
    }

    private static void ReadToolCalls(JsonElement message, List<ToolCall> calls)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
            return;

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

    private static JsonObject Options(ChatRequest request)
    {
        var options = new JsonObject { ["num_predict"] = request.MaxTokens };
        if (request.Temperature is { } t) options["temperature"] = t;
        if (request.RepeatPenalty is { } r) options["repeat_penalty"] = r;
        if (request.ContextLength is { } n) options["num_ctx"] = n;
        return options;
    }

    private static JsonObject ToOllama(ChatMessage m) => m.Role switch
    {
        ChatRole.User => UserToOllama(m),
        ChatRole.Tool => new JsonObject { ["role"] = "tool", ["content"] = m.Text, ["tool_name"] = m.ToolName },
        ChatRole.Assistant => AssistantToOllama(m),
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    private static JsonObject UserToOllama(ChatMessage m)
    {
        var obj = new JsonObject { ["role"] = "user", ["content"] = m.Text };
        if (m.Images.Count > 0)
            obj["images"] = new JsonArray(m.Images.Select(i => (JsonNode)Convert.ToBase64String(i)).ToArray());
        return obj;
    }

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
