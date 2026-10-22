using System.Text.Json;
using System.Text.Json.Nodes;
using HeyNorm.Assistant;

namespace HeyNorm.Tools;

/// <summary>Saves a preference, fact or correction for every future conversation.</summary>
public sealed class RememberTool(Memory memory) : ITool
{
    public string Name => "remember";
    public string Description =>
        "Save something the person wants you to keep for future conversations: a preference, a fact about " +
        "their shop, tools or projects, or a correction to something you got wrong. Use it when they ask you " +
        "to remember something, or correct you and want the correction kept. Write it as one short, " +
        "self-contained sentence in the third person.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["fact"] = new JsonObject { ["type"] = "string", ["description"] = "The thing to remember, as one sentence." },
        },
        ["required"] = new JsonArray("fact"),
    };

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var fact = arguments.TryGetProperty("fact", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()!.Trim() : "";
        if (fact.Length == 0) return Task.FromResult("Tell me what to remember.");

        var saved = memory.Remember(fact);
        return Task.FromResult(saved switch
        {
            { Full: true } => "That didn't work: memory is full. Tell the person to clear out old items using Open memory in Norm's tray menu.",
            { Replaced: { } old } => $"Remembered \"{saved.Item}\", replacing \"{old}\". Confirm briefly.",
            _ => $"Remembered \"{saved.Item}\". Confirm briefly.",
        });
    }
}

/// <summary>Removes one remembered item.</summary>
public sealed class ForgetTool(Memory memory) : ITool
{
    public string Name => "forget";
    public string Description =>
        "Forget one thing you were asked to remember, when the person says to forget it or that it's no longer true.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["about"] = new JsonObject { ["type"] = "string", ["description"] = "What to forget, in the person's words." },
        },
        ["required"] = new JsonArray("about"),
    };

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var about = arguments.TryGetProperty("about", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()!.Trim() : "";
        if (about.Length == 0) return Task.FromResult("Tell me what to forget.");

        var result = memory.Forget(about);
        return Task.FromResult(result switch
        {
            { Removed: { } item } => $"Forgot \"{item}\". Confirm briefly.",
            { Candidates.Count: > 1 } => "That matches more than one thing: " +
                string.Join(" / ", result.Candidates.Select(c => $"\"{c}\"")) + ". Ask the person which one to forget.",
            _ => "Nothing remembered matches that. To see or clear everything, use Open memory in Norm's tray menu.",
        });
    }
}
