using System.Text.Json;
using System.Text.Json.Nodes;
using HeyNorm.Knowledge;

namespace HeyNorm.Tools;

/// <summary>
/// "I'm working on the Heron now": sets the project that plan questions use when they
/// don't name one. With a single project there's nothing to choose, but the tool is harmless.
/// </summary>
public sealed class ProjectTool : ITool
{
    private readonly DocumentLibrary _library;
    private readonly CurrentProject _current;

    public ProjectTool(DocumentLibrary library, CurrentProject current)
    {
        _library = library;
        _current = current;
    }

    public string Name => "set_current_project";
    public string Description =>
        "Remember which project the person is working on now, such as which boat they're building, so " +
        "questions about the plans use that project's documents. Use it when they say which project " +
        "they're working on or switching to. Use 'none' to stop.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The project as the person named it, or 'none'.",
            },
        },
        ["required"] = new JsonArray("project"),
    };

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var said = arguments.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!.Trim()
            : "";

        if (said.Length == 0 || said.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _current.Name = null;
            return Task.FromResult("No current project now. Questions about the plans will search all of them.");
        }

        var project = _library.ResolveProject(said);
        if (project is null)
        {
            var known = _library.Projects;
            return Task.FromResult(known.Count == 0
                ? "There are no plans in the documents folder yet."
                : $"There are no plans for {said}. The projects are {string.Join(", ", known)}.");
        }

        _current.Name = project;
        return Task.FromResult($"Now working on {project}. Questions about the plans will use its documents.");
    }
}
