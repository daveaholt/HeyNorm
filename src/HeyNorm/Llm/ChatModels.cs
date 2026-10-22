using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyNorm.Llm;

public enum ChatRole { User, Assistant, Tool }

/// <summary>
/// Provider-neutral message. Each provider translates to and from its own wire format,
/// so the rest of the app never sees Ollama or Claude specifics.
/// </summary>
public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public string Text { get; init; } = "";

    /// <summary>Set on assistant messages that requested tools.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Set on tool-result messages.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }

    /// <summary>Images attached to a user message, as JPEG bytes — for vision models.</summary>
    public IReadOnlyList<byte[]> Images { get; init; } = [];

    public static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };

    public static ChatMessage ToolResult(ToolCall call, string result, bool isError = false) => new()
    {
        Role = ChatRole.Tool,
        Text = result,
        ToolCallId = call.Id,
        ToolName = call.Name,
        IsError = isError,
    };
}

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolDefinition(string Name, string Description, JsonObject ParametersSchema);

public sealed record ChatRequest
{
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public int MaxTokens { get; init; } = 1024;
    public bool? Think { get; init; }

    /// <summary>
    /// A JSON Schema the reply must follow. Ollama enforces it; Claude gets no such
    /// parameter here and relies on the prompt.
    /// </summary>
    public JsonObject? ResponseSchema { get; init; }

    // Sampling settings a task may pin. Null leaves the model's default.
    public double? Temperature { get; init; }
    /// <summary>Ollama only. Measured: without it the drawing reader looped, counting up invented numbers.</summary>
    public double? RepeatPenalty { get; init; }
    /// <summary>Ollama only: context window. Image-heavy requests overflow the default.</summary>
    public int? ContextLength { get; init; }
}

public sealed record ChatResponse(string Text, IReadOnlyList<ToolCall> ToolCalls)
{
    public bool WantsTools => ToolCalls.Count > 0;
}
