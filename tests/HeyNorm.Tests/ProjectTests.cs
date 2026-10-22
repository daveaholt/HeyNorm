using HeyNorm.Knowledge;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

// Two invented boats, both with a mast — the case where one boat's figure must never be
// given as the other's.

public class ProjectMatchingTests
{
    private static readonly string[] Projects = ["Heron 15", "Skiff 12"];

    [Theory]
    [InlineData("Heron 15", "Heron 15")]
    [InlineData("heron", "Heron 15")]
    [InlineData("the Heron", "Heron 15")]
    [InlineData("Herron", "Heron 15")]   // Whisper spelling an unusual name its own way
    [InlineData("skif", "Skiff 12")]
    public void A_spoken_name_finds_its_project(string said, string project)
    {
        Assert.Equal(project, DocumentLibrary.MatchProject(said, Projects));
    }

    [Theory]
    [InlineData("gaff yawl rig")]   // measured: things the model sent as a project
    [InlineData("unknown")]
    [InlineData("hull panels")]
    [InlineData("the boat")]
    [InlineData("")]
    public void Junk_matches_no_project_rather_than_the_nearest_one(string said)
    {
        Assert.Null(DocumentLibrary.MatchProject(said, Projects));
    }
}

public class ProjectScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heynorm-proj-{Guid.NewGuid():N}");
    private readonly string _state;

    public ProjectScopeTests() => _state = Path.Combine(_root, "state", "current-project.txt");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<DocumentLibrary> Library(params (string Project, string Text)[] specs)
    {
        var docs = new Dictionary<string, ReadDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (project, text) in specs)
        {
            var path = Path.Combine(_root, "docs", project, $"{project} Specification.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            docs[path] = new ReadDocument([new PageText(1, text)]);
        }
        var library = new DocumentLibrary(Path.Combine(_root, "docs"), Path.Combine(_root, "index.json"), _ => { }, p => docs[p]);
        await library.RefreshAsync();
        return library;
    }

    private Task<DocumentLibrary> TwoBoats() => Library(
        ("Heron 15", "KEY TO PLANS\n53. Spruce mast, 5200mm overall length.\n"),
        ("Skiff 12", "KEY TO PLANS\n40. Spruce mast, 3900mm overall length.\n"));

    private static Task<string> Search(DocumentSearchTool tool, string query, string? project = null) =>
        tool.InvokeAsync(Json.Element(project is null
            ? $$"""{ "query": "{{query}}" }"""
            : $$"""{ "query": "{{query}}", "project": "{{project}}" }"""), CancellationToken.None);

    [Fact]
    public async Task The_system_prompt_learns_the_project_names_without_naming_a_tool()
    {
        // Measured: "what nails does the [misheard boat] call for?" went to the web, 4 in 4.
        var section = (await TwoBoats()).PromptSection();

        Assert.Contains("Heron 15, Skiff 12", section);
        Assert.Contains("misheard", section);
        Assert.DoesNotContain("search_documents", section);
        Assert.DoesNotContain("web_search", section);
    }

    [Fact]
    public async Task No_documents_adds_nothing_to_the_prompt()
    {
        Assert.Equal("", (await Library()).PromptSection());
    }

    [Fact]
    public async Task With_two_boats_matching_and_none_named_it_asks_instead_of_answering()
    {
        var tool = new DocumentSearchTool(await TwoBoats(), 5, new CurrentProject(_state));

        var text = await Search(tool, "mast length");

        Assert.StartsWith("More than one project's plans match: Heron 15 and Skiff 12.", text);
        Assert.Contains("Ask the person which one they mean", text);
        // Neither boat's figure reaches the model, so neither can end up in the answer.
        Assert.DoesNotContain("5200", text);
        Assert.DoesNotContain("3900", text);
    }

    [Fact]
    public async Task Naming_a_boat_searches_only_that_boat()
    {
        var tool = new DocumentSearchTool(await TwoBoats(), 5, new CurrentProject(_state));

        var text = await Search(tool, "mast length", "skiff");

        Assert.Contains("3900", text);
        Assert.DoesNotContain("5200", text);
    }

    [Fact]
    public async Task The_current_project_answers_when_none_is_named_and_says_so()
    {
        var library = await TwoBoats();
        var current = new CurrentProject(_state) { Name = "Heron 15" };
        var tool = new DocumentSearchTool(library, 5, current);

        var text = await Search(tool, "mast length");

        Assert.StartsWith("Searched Heron 15, the project the person is working on.", text);
        Assert.Contains("5200", text);
        Assert.DoesNotContain("3900", text);
    }

    [Fact]
    public async Task A_named_boat_beats_the_current_one()
    {
        var tool = new DocumentSearchTool(await TwoBoats(), 5, new CurrentProject(_state) { Name = "Heron 15" });

        var text = await Search(tool, "mast length", "Skiff 12");

        Assert.Contains("3900", text);
        Assert.DoesNotContain("5200", text);
    }

    [Fact]
    public async Task An_unknown_name_is_reported_with_the_projects_there_are()
    {
        var tool = new DocumentSearchTool(await TwoBoats(), 5, new CurrentProject(_state) { Name = "Heron 15" });

        var text = await Search(tool, "mast length", "Dory");

        Assert.Contains("No project matched \"Dory\"; the projects are Heron 15, Skiff 12.", text);
    }

    [Fact]
    public async Task With_one_boat_nothing_changes()
    {
        var tool = new DocumentSearchTool(await Library(("Heron 15", "53. Spruce mast, 5200mm overall length.")), 5,
            new CurrentProject(_state));

        var text = await Search(tool, "mast length");

        Assert.StartsWith("From their documents.", text);
        Assert.Contains("5200", text);
    }

    [Fact]
    public async Task Saying_which_boat_sets_the_current_project_and_it_survives_a_restart()
    {
        var library = await TwoBoats();
        var tool = new ProjectTool(library, new CurrentProject(_state));

        var reply = await tool.InvokeAsync(Json.Element("""{ "project": "the herron" }"""), CancellationToken.None);

        Assert.Equal("Now working on Heron 15. Questions about the plans will use its documents.", reply);
        Assert.Equal("Heron 15", new CurrentProject(_state).Name); // read back fresh, as after a restart
    }

    [Fact]
    public async Task None_clears_the_current_project()
    {
        var library = await TwoBoats();
        var current = new CurrentProject(_state) { Name = "Heron 15" };

        await new ProjectTool(library, current).InvokeAsync(Json.Element("""{ "project": "none" }"""), CancellationToken.None);

        Assert.Null(new CurrentProject(_state).Name);
    }

    [Fact]
    public async Task A_boat_with_no_plans_is_not_set()
    {
        var library = await TwoBoats();
        var current = new CurrentProject(_state);

        var reply = await new ProjectTool(library, current).InvokeAsync(Json.Element("""{ "project": "Dory" }"""), CancellationToken.None);

        Assert.Equal("There are no plans for Dory. The projects are Heron 15, Skiff 12.", reply);
        Assert.Null(current.Name);
    }
}
