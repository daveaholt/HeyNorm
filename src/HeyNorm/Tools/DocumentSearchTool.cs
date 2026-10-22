using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyNorm.Knowledge;

namespace HeyNorm.Tools;

/// <summary>
/// Answers questions from the person's own documents — boat plans, building specifications.
/// Returns the best-matching passages with where each came from, and tells the model to answer
/// only from them: a wrong number here costs timber, so "the plans don't say" beats a guess.
/// </summary>
public sealed class DocumentSearchTool : ITool
{
    // Each passage is read back in full; past this a long item is cut rather than drown the rest.
    private const int MaxPassageChars = 700;

    private readonly DocumentLibrary _library;
    private readonly int _maxResults;
    private readonly CurrentProject? _current;

    public DocumentSearchTool(DocumentLibrary library, int maxResults, CurrentProject? current = null)
    {
        _library = library;
        _maxResults = Math.Max(1, maxResults);
        _current = current;
    }

    public string Name => "search_documents";
    public string Description =>
        "Search the person's own documents, such as boat plans and building specifications. Use it for " +
        "any question about what their plans say: sizes, materials, plywood thickness, fastenings, glue, " +
        "building steps, rigging, dimensions. Returns the most relevant passages and where each came from.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Words likely to appear in the plans, e.g. 'bottom ply thickness' or 'mast length'.",
            },
            ["project"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Which project (which boat, say), only if the person named one in this question.",
            },
        },
        ["required"] = new JsonArray("query"),
    };

    public string Preamble => "Let me check the plans.";

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var query = Text(arguments, "query");
        if (query is null) return Task.FromResult("Tell me what to look up in the plans.");

        switch (_library.State)
        {
            case LibraryState.NoFolder:
                return Task.FromResult("There's no documents folder set up yet, so I have no plans to look in.");
            case LibraryState.Indexing:
                return Task.FromResult("I'm still reading the documents. Ask me again in a minute.");
        }

        // Which boat: one named in the question, else the one being worked on, else all of them.
        var named = Text(arguments, "project");
        var resolved = _library.ResolveProject(named);
        var current = _library.ResolveProject(_current?.Name);
        var project = resolved ?? current;
        var hits = _library.Search(query, _maxResults, project);

        // Two boats' plans must never be blended into one answer: if the best matches span
        // projects and nobody said which, ask rather than answer.
        var matched = hits.Select(h => h.Passage.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (project is null && matched.Count > 1)
        {
            return Task.FromResult(
                $"More than one project's plans match: {string.Join(" and ", matched)}. Don't answer from either. " +
                "Ask the person which one they mean, then search again with that project.");
        }

        var unreadable = _library.UnreadableDocuments(hits.FirstOrDefault()?.Passage.Project ?? project);
        var text = new StringBuilder();
        if (_library.Projects.Count > 1)
        {
            if (named is not null && resolved is null)
                text.AppendLine($"No project matched \"{named}\"; the projects are {string.Join(", ", _library.Projects)}.");
            if (resolved is null && current is not null)
                text.AppendLine($"Searched {current}, the project the person is working on.");
        }
        if (hits.Count == 0)
        {
            text.Append($"The documents don't mention {query}.");
        }
        else
        {
            text.AppendLine("From the person's documents. Answer only from these passages and say which " +
                            "document and item or page it came from. If they don't answer the question, say " +
                            "the plans don't say rather than guessing.");
            for (var i = 0; i < hits.Count; i++)
            {
                var p = hits[i].Passage;
                var body = p.Text.Length <= MaxPassageChars ? p.Text : p.Text[..MaxPassageChars] + "...";
                text.AppendLine($"{i + 1}. {(p.MachineRead ? "[MACHINE-READ, MAY BE WRONG] " : "")}{p.Source}: {body}");
            }

            // A wrong figure off a drawing costs timber, and the readings carry no meaning for
            // their numbers — only where they sit — so the warning and the location are not
            // optional, and the model mustn't decide which number measures what.
            if (hits.Any(h => h.Passage.MachineRead))
            {
                text.AppendLine("Passages marked MACHINE-READ are an automatic reading of a scanned drawing and may " +
                                "be wrong. When you use one, you must say it was machine-read, name the drawing and " +
                                "where on the sheet the figures are, and tell the person to check the drawing before " +
                                "cutting. Don't say which figure measures what; list the figures found there.");
            }
        }

        // Most dimensions live on the drawings, which are scans. Say so, so the model can point
        // the person at a sheet instead of implying the answer doesn't exist.
        if (unreadable.Count > 0)
        {
            text.Append($"\nNot readable yet: {string.Join(", ", unreadable)} are scanned drawings. " +
                        "If the answer is likely on a drawing, name the drawing the passages refer to.");
        }
        return Task.FromResult(text.ToString().TrimEnd());
    }

    private static string? Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s
            ? s
            : null;
}
