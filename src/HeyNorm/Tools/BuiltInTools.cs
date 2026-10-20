using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyNorm.Assistant;

namespace HeyNorm.Tools;

/// <summary>Sample tool: the simplest possible end-to-end check that tool calling works.</summary>
public sealed class CurrentTimeTool : ITool
{
    public string Name => "get_current_time";
    public string Description => "Get the current local date and time.";
    public JsonObject ParametersSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(DateTime.Now.ToString("dddd, MMMM d, yyyy 'at' h:mm tt"));
}

/// <summary>
/// Lets the model decide the job is finished, so Norm stops listening without a stop phrase.
/// </summary>
public sealed class EndSessionTool : ITool
{
    private readonly SessionSignals _signals;

    public EndSessionTool(SessionSignals signals) => _signals = signals;

    public string Name => "end_session";
    public string Description =>
        "End the voice session and stop listening. Call this when the person's request is complete " +
        "and they have nothing else, or when they say goodbye, thanks that's all, or similar.";
    public JsonObject ParametersSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        _signals.EndRequested = true;
        return Task.FromResult("Session will end after your reply. Give a brief sign-off.");
    }
}

/// <summary>Sample timer tool — a handy shop helper ("Hey Norm, ten minutes for the glue").</summary>
public sealed class TimerTool : ITool
{
    private readonly Action<string> _announce;

    public TimerTool(Action<string> announce) => _announce = announce;

    public string Name => "set_timer";
    public string Description => "Start a countdown timer. Norm will announce out loud when it finishes.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["minutes"] = new JsonObject { ["type"] = "number", ["description"] = "Duration in minutes." },
            ["label"] = new JsonObject { ["type"] = "string", ["description"] = "What the timer is for, e.g. 'glue up'." },
        },
        ["required"] = new JsonArray("minutes"),
    };

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("minutes", out var raw) || Minutes(raw) is not { } minutes)
            return Task.FromResult("Tell me how many minutes the timer is for.");

        var label = arguments.TryGetProperty("label", out var l) ? l.GetString() : null;
        if (minutes <= 0 || minutes > 24 * 60)
            return Task.FromResult("Timer must be between 0 and 1440 minutes.");

        _ = Task.Delay(TimeSpan.FromMinutes(minutes)).ContinueWith(_ =>
            _announce(label is null ? "Your timer is done." : $"Your {label} timer is done."));

        return Task.FromResult($"Timer set for {minutes} minutes{(label is null ? "" : $" ({label})")}.");
    }

    /// <summary>
    /// Models ignore the schema and send "10" as often as 10, so accept either rather than
    /// throwing and losing the timer.
    /// </summary>
    private static double? Minutes(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => double.TryParse(
            value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null,
    };
}
