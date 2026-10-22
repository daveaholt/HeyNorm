using HeyNorm.Assistant;
using HeyNorm.Config;
using HeyNorm.Knowledge;
using HeyNorm.Llm;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

// What the first voice test of the plans got wrong, each pinned down. Invented text in the
// shapes of the real specification and drawing readings.

public class SearchCoverageTests
{
    private static Passage Typed(string text, string item) => new("Skiff 12", "Skiff 12 Specification", 3, "Basic Building Schedule", item, text);

    private static Passage Reading(string doc, string text) =>
        new("Skiff 12", doc, 1, "machine-read from the top middle of the sheet", null, text, MachineRead: true);

    [Fact]
    public void A_passage_about_only_part_of_the_question_is_left_out_when_others_cover_all_of_it()
    {
        // Measured: a note on the spacing between stations was handed over for "frame spacing", and
        // qwen3 gave that as the frames' spacing, 5 times in 5.
        var stations = Reading("Skiff 12 01", "Note: \"SPACING BETWEEN STATIONS IS 300MM.\"");
        var stepF = Typed("Brace the gunwales apart at the frame positions to the distances shown on Drg. 02.", "F");
        var index = new KeywordIndex([stations, stepF]);

        var hits = index.Search("frame spacing", 5);

        Assert.Equal([stepF], hits.Select(h => h.Passage));
    }

    [Theory]
    [InlineData("how far apart are the frames")]
    [InlineData("distance between frames")]
    [InlineData("frame spacing")]
    public void Apart_distance_and_spacing_are_one_idea(string question)
    {
        var stepF = Typed("Brace the gunwales at the frame positions to the distances shown on Drg. 02.", "F");
        var cleat = Typed("18x50 side frame cleat.", "6");

        Assert.Equal(stepF, new KeywordIndex([stepF, cleat]).Search(question, 5)[0].Passage);
    }

    [Fact]
    public void Nail_centres_are_not_frame_spacing()
    {
        // Mapped as spacing, "100mm centres" came back for "frame spacing" and was given as the
        // frames' spacing.
        var nails = Typed("9mm ply deck. Glue and fasten with ring nails at 100mm centres.", "25");
        var stepF = Typed("Brace the gunwales at the frame positions to the distances shown on Drg. 02.", "F");

        Assert.DoesNotContain(new KeywordIndex([nails, stepF]).Search("frame spacing", 5), h => h.Passage == nails);
    }
}

public class FollowTheDrawingReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heynorm-ref-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Passage Reading(string doc, string text) =>
        new("Skiff 12", doc, 1, "machine-read from the top middle of the sheet", null, text, MachineRead: true);

    private sealed class Readings(params Passage[] passages) : IDrawingReadings
    {
        public IReadOnlyList<Passage> PassagesFor(DocumentLibrary.CachedFile file) =>
            passages.Where(p => p.Document == file.Document).ToList();
        public bool HasRead(DocumentLibrary.CachedFile file, int page) => passages.Any(p => p.Document == file.Document);
    }

    private async Task<DocumentSearchTool> Tool(string spec, params Passage[] readings)
    {
        var files = new Dictionary<string, ReadDocument>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string text)
        {
            var path = Path.Combine(_root, "Skiff 12", name + ".pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            files[path] = new ReadDocument([new PageText(1, text)]);
        }
        Add("Skiff 12 Specification", spec);
        foreach (var doc in readings.Select(r => r.Document).Distinct()) Add(doc, "");

        var library = new DocumentLibrary(_root, Path.Combine(_root, "..", $"ref-{Guid.NewGuid():N}.json"), _ => { }, p => files[p])
        {
            Readings = new Readings(readings),
        };
        await library.RefreshAsync();
        return new DocumentSearchTool(library, 5);
    }

    private static Task<string> Ask(DocumentSearchTool tool, string query) =>
        tool.InvokeAsync(Json.Element($$"""{ "query": "{{query}}" }"""), CancellationToken.None);

    [Fact]
    public async Task A_typed_passage_pointing_at_a_drawing_brings_that_drawings_readings()
    {
        var tool = await Tool(
            "BASIC BUILDING SCHEDULE\nF. Brace the gunwales apart at the frame positions to the distances shown on Drg. 02.\n",
            Reading("Skiff 12 02", "Labels: FRAME 'B', FRAME 'A'. Figures in millimetres: 1010, 880."),
            Reading("Skiff 12 05", "Labels: FRAME 'C'. Figures in millimetres: 777."));

        var text = await Ask(tool, "frame spacing");

        Assert.Contains("item F", text);
        Assert.Contains("Skiff 12 02", text);
        Assert.Contains("1010, 880", text);
        Assert.DoesNotContain("777", text); // a sheet the passage doesn't point to
    }

    [Fact]
    public async Task A_full_drawing_number_finds_its_sheet_too()
    {
        var tool = await Tool(
            "NOTES FOR THE YAWL RIG\n53. Spruce mast much as Drg. No. SK12/91/04, 5200mm overall length.\n",
            Reading("Skiff 12 04", "Labels: SPRUCE MAST. Figures in millimetres: 140."));

        Assert.Contains("Skiff 12 04", await Ask(tool, "mast length"));
    }

    [Fact]
    public async Task Typed_passages_come_first_and_readings_carry_their_caveat_beside_the_figures()
    {
        var tool = await Tool(
            "NOTES FOR THE YAWL RIG\n53. Spruce mast, 5200mm overall length, see Drg. 07.\n",
            Reading("Skiff 12 07", "Labels: MIZZEN MAST. Figures in millimetres: 1235."));

        var text = await Ask(tool, "mast length");

        // Measured: the typed 5200mm ranked second and a mizzen reading third; qwen3 gave the reading's figure.
        Assert.True(text.IndexOf("5200mm", StringComparison.Ordinal) < text.IndexOf("1235", StringComparison.Ordinal));
        Assert.Contains("Figures in millimetres: 1235. (Which figure measures what is not known.)", text);
        Assert.Contains("use a MACHINE-READ one only if no typed passage answers", text);
    }

    [Fact]
    public async Task The_instructions_never_say_the_person_should()
    {
        // qwen3 read "tell the person to check the drawing" back aloud as "The person should check…".
        var tool = await Tool("F. Distances shown on Drg. 02 for the frames.\n",
            Reading("Skiff 12 02", "Labels: FRAME 'A'. Figures in millimetres: 1010."));

        Assert.DoesNotContain("the person", await Ask(tool, "frame distances"), StringComparison.OrdinalIgnoreCase);
    }
}

public class SilentRetryTests
{
    private static (ConversationEngine Engine, FakeChatProvider Provider, StubTool Tool) Build(params ChatResponse[] responses)
    {
        var provider = new FakeChatProvider(responses);
        var tool = new StubTool("search_documents", "Frames at the distances on Drg. 02.", preamble: "Let me check the plans.");
        var settings = new AppSettings
        {
            Tasks = { ["conversation"] = new TaskSettings { Provider = "fake", Model = "m" } },
            Providers = { ["fake"] = new ProviderSettings { Type = "fake" } },
        };
        var engine = new ConversationEngine(new ModelRouter(settings, _ => provider), new ToolRegistry().Add(tool),
            new AssistantSettings { SystemPrompt = "You are Norm.", ConversationTask = "conversation" }, new SessionSignals());
        return (engine, provider, tool);
    }

    [Fact]
    public async Task A_reply_that_only_promises_a_lookup_is_asked_again_silently()
    {
        // Measured: 2 of 5 times, "I'll need to check the plans. Let me look that up." and no call.
        var (engine, provider, tool) = Build(
            new ChatResponse("I'll need to check the plans to find the distance. Let me look that up.", []),
            new ChatResponse("", [new ToolCall("c1", "search_documents", Json.Empty)]),
            new ChatResponse("They're on drawing 02.", []));
        var sentences = new List<string>();

        await engine.RunTurnAsync("how far apart are the frames?", sentences.Add, CancellationToken.None);

        Assert.Equal(1, tool.Invocations);
        // Nothing was added for the model to react to; the stall isn't in what it sees next.
        Assert.Single(provider.Requests[1].Messages);
        Assert.DoesNotContain(provider.Requests[2].Messages, m => m.Text.Contains("Let me look that up"));
        // Spoken once, and no second "let me check" on top of it.
        Assert.Equal(["I'll need to check the plans to find the distance.", "Let me look that up.", "They're on drawing 02."], sentences);
    }

    [Fact]
    public async Task It_is_asked_again_only_once()
    {
        var (engine, provider, _) = Build(
            new ChatResponse("Let me look that up.", []),
            new ChatResponse("Let me look that up.", []),
            new ChatResponse("never asked", []));

        await engine.RunTurnAsync("frames?", CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Theory]
    [InlineData("Let me know if you need anything else.")]
    [InlineData("I'll be here if you need me.")]
    [InlineData("Let me check. The frames are on drawing 02.")] // an answer came with it
    public async Task Sign_offs_and_real_answers_are_not_stalls(string reply)
    {
        var (engine, provider, _) = Build(new ChatResponse(reply, []), new ChatResponse("unexpected", []));

        await engine.RunTurnAsync("hi", CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
    }
}
