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
        // Typed passages fill the answer first; machine readings only take slots the typed spec
        // doesn't need. Measured: two readings mentioning fillets pushed out the paragraph that
        // actually names the glue.
        var found = _library.Search(query, _maxResults * 3, project);
        var typedHits = found.Where(h => !h.Passage.MachineRead).Take(_maxResults).ToList();
        var hits = typedHits.Concat(found.Where(h => h.Passage.MachineRead).Take(_maxResults - typedHits.Count)).ToList();

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
            // Measured by voice: asked the mast length, qwen3 skipped the typed spec (the mast
            // overall length, ranked second) for a machine reading ranked third, and called its
            // figure the length. Typed passages now come first and win; a passage must be about
            // what was asked — it once answered "frames" with the spacing of stations.
            text.AppendLine("From their documents. Use only a passage that is about exactly what was asked. " +
                            "Typed passages come first and win; use a MACHINE-READ one only if no typed passage " +
                            "answers. Answer in one or two short sentences: the answer, then the document and item " +
                            "or page. If nothing here answers it, say the plans don't say.");
            var ordered = hits.OrderBy(h => h.Passage.MachineRead).ToList();

            // Follow "see Drg. 02": the typed passage says which sheet, the readings of that
            // sheet carry the figures. Measured: asked how far apart the frames are, the
            // readings near the frame labels ranked below nail spacing, because they hold no
            // word for spacing — only the building step pointing at Drg. 02 said that.
            foreach (var typed in ordered.Where(h => !h.Passage.MachineRead).Take(2).ToList())
            {
                foreach (var reading in _library.ReadingsReferencedBy(typed.Passage, query, 2))
                {
                    if (!ordered.Any(h => h.Passage == reading.Passage)) ordered.Add(reading);
                }
            }
            for (var i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i].Passage;
                var body = p.Text.Length <= MaxPassageChars ? p.Text : p.Text[..MaxPassageChars] + "...";
                // The caveat sits right beside the figures, where it's hardest to skip.
                text.AppendLine(p.MachineRead
                    ? $"{i + 1}. [MACHINE-READ, MAY BE WRONG] {p.Source}: {body} (Which figure measures what is not known.)"
                    : $"{i + 1}. {p.Source}: {body}");
            }

            // A wrong figure off a drawing costs timber, and the readings carry no meaning for
            // their numbers — only where they sit — so the warning and the location are not
            // optional. Worded so the model says it in its own words: "tell the person to check"
            // came back aloud as "The person should check the drawing".
            if (hits.Any(h => h.Passage.MachineRead))
            {
                text.AppendLine("If you use a MACHINE-READ passage: say it was machine-read, name the drawing and where " +
                                "on the sheet, list the figures found there without saying what any of them measures, " +
                                "and say to check the drawing before cutting.");
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
