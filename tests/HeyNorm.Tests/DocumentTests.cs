using System.Text.Json;
using HeyNorm.Knowledge;
using HeyNorm.Tools;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace HeyNorm.Tests;

// The sample text below is written in the style of a boat-plan specification — numbered key
// items, lettered building steps, British spelling — modelled on what a real one extracted to.
// It is invented; no real plans are checked in.

public class PassageSplitterTests
{
    private static List<Passage> Split(params string[] pages) =>
        PassageSplitter.Split("Skiff 12", "Skiff 12 Specification",
            pages.Select((t, i) => new PageText(i + 1, t)).ToList());

    private const string KeyPage = """
        4
        KEY TO PLANS
        1.    9mm ply hull side panels.
        2.    Bottom  ply  from 2 layers of 6mm - spread  plenty  of glue when glueing
        on the second layer and staple it down until cured.
        12 .   22x100 hardwood bearers,
        """;

    [Fact]
    public void Each_numbered_item_becomes_its_own_passage_with_its_wrapped_lines()
    {
        var passages = Split(KeyPage);

        Assert.Equal(["1", "2", "12"], passages.Select(p => p.Item));
        Assert.Equal("Bottom ply from 2 layers of 6mm - spread plenty of glue when glueing on the second layer and staple it down until cured.",
            passages[1].Text);
    }

    [Fact]
    public void Every_passage_carries_its_heading_in_title_case()
    {
        Assert.All(Split(KeyPage), p => Assert.Equal("Key to Plans", p.Heading));
    }

    [Fact]
    public void Item_numbers_that_repeat_under_another_heading_stay_apart()
    {
        // A real spec gives item 53 twice: one mast in the key, another in an alternative rig's notes.
        var passages = Split("""
            KEY TO PLANS
            53. Spruce mast as drawn.
            NOTES FOR THE GAFF RIG
            53. Spruce mast, 5200mm overall length.
            """);

        Assert.Equal(["Key to Plans", "Notes for the Gaff Rig"], passages.Select(p => p.Heading));
        Assert.Equal("Skiff 12 Specification, page 1, Notes for the Gaff Rig item 53", passages[1].Source);
    }

    [Fact]
    public void A_wrapped_line_starting_with_a_drawing_number_is_not_a_new_item()
    {
        // Measured: a drawing reference ("...on drg. 03.") wrapped so the next line began
        // "03.", and the item was cut in two with a bogus item 03.
        var passages = Split("""
            KEY TO PLANS
            19. 8mm steel centreplate with a tackle to raise it, as on drg.
            03. Grind the edges and paint with epoxy.
            20. Centreboard lift system.
            """);

        Assert.Equal(["19", "20"], passages.Select(p => p.Item));
        Assert.EndsWith("drg. 03. Grind the edges and paint with epoxy.", passages[0].Text);
    }

    [Fact]
    public void An_item_running_over_a_page_break_stays_whole_and_keeps_its_first_page()
    {
        var passages = Split(
            "KEY TO PLANS\n18. Keel batten cut from offcut ply, 300mm deep at the heel and\n",
            "5\ntapering to nothing at the stem.\n19. 8mm steel centreplate.\n");

        var skeg = Assert.Single(passages, p => p.Item == "18");
        Assert.Equal(1, skeg.Page);
        Assert.EndsWith("tapering to nothing at the stem.", skeg.Text);
        Assert.Equal(2, Assert.Single(passages, p => p.Item == "19").Page);
    }

    [Fact]
    public void A_paragraph_ending_inside_a_bracket_still_ends()
    {
        // Measured: a paragraph ending ").  " wasn't seen as ending, and the paragraph after
        // it — the one naming the glue — was buried where the search for glue didn't rank it.
        var passages = Split(
            "GENERAL\n" +
            "Fillets are thickened epoxy mixed to a stiff paste with filler powder. (The\n" +
            "maker's leaflet explains it).  \n",
            "2 \n" +
            "Fastenings are bronze ring nails set at 75mm centres where the timber allows. Glue\n" +
            "may be resorcinol or, better, epoxy, spread generously on both faces. \n");

        Assert.Equal(2, passages.Count);
        Assert.StartsWith("Fastenings are bronze ring nails", passages[1].Text);
        Assert.Equal(2, passages[1].Page);
    }

    [Fact]
    public void Lettered_building_steps_are_items_too()
    {
        var passages = Split("""
            BASIC BUILDING SCHEDULE
            A. Mark out and cut the planks.
            B. Join the planks, starting at the keel.
            """);

        Assert.Equal(["A", "B"], passages.Select(p => p.Item));
    }

    [Fact]
    public void The_page_number_printed_at_the_top_is_not_text()
    {
        Assert.DoesNotContain(Split(KeyPage), p => p.Text.StartsWith('4'));
    }

    [Fact]
    public void A_long_paragraph_is_cut_at_sentence_ends()
    {
        var sentence = "All plywood is marine grade and all solid timber is larch or cedar unless a note says otherwise. ";
        var passages = Split("GENERAL\n" + string.Concat(Enumerable.Repeat(sentence, 12)));

        Assert.True(passages.Count >= 2);
        Assert.All(passages, p => Assert.True(PassageSplitter.WordCount(p.Text) <= 170, $"{PassageSplitter.WordCount(p.Text)} words"));
        Assert.All(passages, p => Assert.EndsWith(".", p.Text));
    }
}

public class KeywordIndexTests
{
    private static Passage P(string text, string? item = null, string project = "Skiff 12", string? heading = "Key to Plans") =>
        new(project, $"{project} Specification", 4, heading, item, text);

    private static readonly Passage Bottom = P("Bottom ply from 2 layers of 6mm.", "2");
    private static readonly Passage Case = P("12mm ply centreboard case sides.", "10");
    private static readonly Passage Plate = P("8mm steel centreplate.", "19");
    private static readonly Passage Mast = P("Spruce mast, 5200mm overall length.", "53");
    private static readonly Passage Gunwale = P("18x50 inner gunwale fastened with ring nails at 100mm centres.", "8");
    private static readonly Passage Glue = P("Glue may be resorcinol and should be spread on both faces.");
    private static readonly KeywordIndex Index = new([Bottom, Case, Plate, Mast, Gunwale, Glue]);

    private static Passage Top(string query) => Index.Search(query, 1)[0].Passage;

    [Fact]
    public void American_spelling_finds_british_plans() => Assert.Equal(Case, Top("centerboard case"));

    [Fact]
    public void A_centreplate_counts_as_a_centerboard() =>
        Assert.Contains(Index.Search("centerboard", 5), h => h.Passage == Plate);

    [Fact]
    public void Plywood_finds_ply() => Assert.Equal(Bottom, Top("bottom plywood"));

    [Fact]
    public void How_long_finds_length() => Assert.Equal(Mast, Top("how long is the mast"));

    [Fact]
    public void A_timber_size_is_searchable_as_written() => Assert.Equal(Gunwale, Top("18x50"));

    [Fact]
    public void Glued_and_gluing_find_glue() => Assert.Equal(Glue, Top("what is it glued with"));

    [Fact]
    public void Question_words_alone_find_nothing() => Assert.Empty(Index.Search("what is it made of", 5));

    [Fact]
    public void A_project_filter_keeps_another_boats_plans_out()
    {
        var index = new KeywordIndex([P("Mast 5200mm long.", project: "Skiff 12"), P("Mast 6100mm long.", project: "Yawl 20")]);

        var hits = index.Search("mast", 5, "Yawl 20");

        Assert.Equal("Yawl 20", Assert.Single(hits).Passage.Project);
    }
}

public class DocumentLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heynorm-lib-{Guid.NewGuid():N}");
    private readonly string _cache;
    private readonly List<string> _logged = [];
    private readonly List<string> _reads = [];
    private readonly Dictionary<string, ReadDocument> _contents = new(StringComparer.OrdinalIgnoreCase);

    public DocumentLibraryTests()
    {
        Directory.CreateDirectory(_root);
        _cache = Path.Combine(_root, "..", $"heynorm-lib-cache-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { File.Delete(_cache); } catch (IOException) { }
    }

    /// <summary>A "PDF" on disk whose pages the fake reader returns.</summary>
    private void AddFile(string relative, params string[] pages)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("|", pages)); // size and time are what the cache keys on
        _contents[path] = new ReadDocument(pages.Select((t, i) => new PageText(i + 1, t)).ToList());
    }

    private DocumentLibrary Library() => new(_root, _cache, _logged.Add, path =>
    {
        _reads.Add(Path.GetFileName(path));
        return _contents.TryGetValue(path, out var doc) ? doc : throw new IOException("can't open");
    });

    [Fact]
    public async Task Indexes_the_folder_and_names_projects_after_their_folders()
    {
        AddFile(@"Sailboats\Skiff 12\Skiff 12 Specification.pdf", "KEY TO PLANS\n1. 9mm ply hull side panels.\n");
        var library = Library();

        await library.RefreshAsync();

        Assert.Equal(LibraryState.Ready, library.State);
        Assert.Equal(["Skiff 12"], library.Projects);
        Assert.Equal("Skiff 12 Specification", library.Search("hull side", 1)[0].Passage.Document);
    }

    [Fact]
    public async Task A_restart_reads_nothing_that_has_not_changed()
    {
        AddFile(@"Skiff\spec.pdf", "1. 9mm ply hull side panels.");
        await Library().RefreshAsync();
        _reads.Clear();

        var restarted = Library();
        await restarted.RefreshAsync();

        Assert.Empty(_reads);
        Assert.Single(restarted.Search("hull side", 5));
    }

    [Fact]
    public async Task A_changed_file_is_read_again_and_a_deleted_one_dropped()
    {
        AddFile(@"Skiff\spec.pdf", "1. 9mm ply hull side panels.");
        AddFile(@"Skiff\old.pdf", "1. Obsolete mast notes.");
        var library = Library();
        await library.RefreshAsync();
        _reads.Clear();

        AddFile(@"Skiff\spec.pdf", "1. 12mm ply hull side panels, revised.");
        File.SetLastWriteTimeUtc(Path.Combine(_root, @"Skiff\spec.pdf"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(_root, @"Skiff\old.pdf"));
        await library.RefreshAsync();

        Assert.Equal(["spec.pdf"], _reads);
        Assert.Contains("12mm", library.Search("hull side", 1)[0].Passage.Text);
        Assert.Empty(library.Search("obsolete mast", 5));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_logged_and_tried_again_next_time()
    {
        // e.g. a OneDrive file that couldn't download while offline.
        var path = Path.Combine(_root, @"Skiff\spec.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not yet");
        var library = Library();

        await library.RefreshAsync();
        Assert.Contains(_logged, l => l.Contains("couldn't read") && l.Contains("spec.pdf"));

        _contents[path] = new ReadDocument([new PageText(1, "1. 9mm ply hull side panels.")]);
        await library.RefreshAsync();
        Assert.Single(library.Search("hull side", 5));
    }

    [Fact]
    public async Task Scanned_drawings_are_listed_as_not_readable_yet()
    {
        AddFile(@"Skiff\Skiff 01.pdf", "");                 // a scanned sheet: one page, no text
        AddFile(@"Skiff\Skiff spec.pdf", "1. 9mm ply hull side panels.", "8"); // text, plus one sketch page
        var library = Library();

        await library.RefreshAsync();

        Assert.Equal(["Skiff 01"], library.UnreadableDocuments());
    }

    [Fact]
    public void A_missing_folder_says_so_rather_than_failing()
    {
        var library = new DocumentLibrary(Path.Combine(_root, "nope"), _cache, _logged.Add);

        library.Start();

        Assert.Equal(LibraryState.NoFolder, library.State);
        Assert.Contains(_logged, l => l.StartsWith("Documents: no folder"));
    }

    [Fact]
    public async Task Text_is_read_out_of_a_real_pdf()
    {
        // The default reader, end to end, on a PDF written here rather than checked in.
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText("KEY TO PLANS", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        page.AddText("2. Bottom ply from 2 layers of 6mm.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 730), font);
        var path = Path.Combine(_root, "Skiff", "Skiff spec.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, builder.Build());

        var library = new DocumentLibrary(_root, _cache, _logged.Add);
        await library.RefreshAsync();

        var hit = Assert.Single(library.Search("bottom plywood", 5)).Passage;
        Assert.Equal("Skiff spec, page 1, Key to Plans item 2", hit.Source);
    }
}

public class DocumentSearchToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heynorm-tool-{Guid.NewGuid():N}");
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"heynorm-tool-cache-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { File.Delete(_cache); } catch (IOException) { }
    }

    private async Task<DocumentSearchTool> Tool(params (string File, string Text)[] files)
    {
        var docs = new Dictionary<string, ReadDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, text) in files)
        {
            var path = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            docs[path] = new ReadDocument([new PageText(1, text)]);
        }
        var library = new DocumentLibrary(_root, _cache, _ => { }, p => docs[p]);
        await library.RefreshAsync();
        return new DocumentSearchTool(library, 3);
    }

    private static Task<string> Ask(DocumentSearchTool tool, string query) =>
        tool.InvokeAsync(Json.Element(JsonSerializer.Serialize(new { query })), CancellationToken.None);

    [Fact]
    public async Task Hands_back_passages_with_their_source_and_tells_the_model_not_to_guess()
    {
        var tool = await Tool((@"Skiff\Skiff spec.pdf", "KEY TO PLANS\n2. Bottom ply from 2 layers of 6mm.\n"));

        var text = await Ask(tool, "bottom ply");

        Assert.Contains("1. Skiff spec, page 1, Key to Plans item 2: Bottom ply from 2 layers of 6mm.", text);
        Assert.Contains("say the plans don't say rather than guessing", text);
    }

    [Fact]
    public async Task Points_at_the_scanned_drawings_it_cannot_read()
    {
        var tool = await Tool(
            (@"Skiff\Skiff spec.pdf", "1. Ply rudder as drg. 03."),
            (@"Skiff\Skiff 03.pdf", ""));

        var text = await Ask(tool, "rudder");

        Assert.Contains("Not readable yet: Skiff 03 are scanned drawings.", text);
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        var tool = await Tool((@"Skiff\Skiff spec.pdf", "1. 9mm ply hull side panels."));

        Assert.Equal("The documents don't mention outboard bracket.", await Ask(tool, "outboard bracket"));
    }

    [Fact]
    public async Task Before_indexing_finishes_it_asks_for_a_minute()
    {
        Directory.CreateDirectory(_root);
        var tool = new DocumentSearchTool(new DocumentLibrary(_root, _cache, _ => { }), 3); // never refreshed

        Assert.Equal("I'm still reading the documents. Ask me again in a minute.", await Ask(tool, "mast"));
    }

    [Fact]
    public async Task Without_a_folder_it_says_there_are_no_plans()
    {
        var tool = new DocumentSearchTool(new DocumentLibrary(Path.Combine(_root, "missing"), _cache, _ => { }), 3);

        Assert.Contains("no documents folder", await Ask(tool, "mast"));
    }

    [Fact]
    public void Its_preamble_says_what_it_is_doing()
    {
        Assert.Equal("Let me check the plans.", ((ITool)new DocumentSearchTool(
            new DocumentLibrary(_root, _cache, _ => { }), 3)).Preamble);
    }
}
