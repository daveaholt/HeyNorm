using System.Text.Json;
using System.Text.Json.Nodes;
using HeyNorm.Llm;

namespace HeyNorm.Tools;

/// <summary>
/// An action the model may take. Add a class, register it in ToolRegistry, done.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema for the arguments object.</summary>
    JsonObject ParametersSchema { get; }

    /// <summary>Returns text the model reads back. Keep it short; it costs tokens.</summary>
    Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct);

    /// <summary>
    /// Spoken just before a slow tool runs ("Let me look that up."), so the person hears
    /// something instead of seconds of silence. Null for tools that answer instantly.
    /// </summary>
    string? Preamble => null;
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry Add(ITool tool)
    {
        _tools[tool.Name] = tool;
        return this;
    }

    public IReadOnlyList<ToolDefinition> Definitions =>
        _tools.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersSchema)).ToList();

    public bool Knows(string name) => _tools.ContainsKey(name);

    public string? PreambleFor(string name) => _tools.TryGetValue(name, out var tool) ? tool.Preamble : null;

    public async Task<ChatMessage> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ChatMessage.ToolResult(call, $"Unknown tool '{call.Name}'.", isError: true);

        try
        {
            return ChatMessage.ToolResult(call, await tool.InvokeAsync(call.Arguments, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChatMessage.ToolResult(call, $"Tool failed: {ex.Message}", isError: true);
        }
    }
}
