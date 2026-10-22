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

    public DocumentSearchTool(DocumentLibrary library, int maxResults)
    {
        _library = library;
        _maxResults = Math.Max(1, maxResults);
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
                ["description"] = "Which project, if the person named one, e.g. 'Skiff 12'.",
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

        var project = Text(arguments, "project");
        var hits = _library.Search(query, _maxResults, project);
        var unreadable = _library.UnreadableDocuments(hits.FirstOrDefault()?.Passage.Project ?? project);

        var text = new StringBuilder();
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
                text.AppendLine($"{i + 1}. {p.Source}: {body}");
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
