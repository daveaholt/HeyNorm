using System.Net;
using HeyNorm.Tools;

namespace HeyNorm.Tests;

public class WebSearchToolTests
{
    private static (WebSearchTool Tool, StubHandler Stub) Build(string response, int maxResults = 4,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var stub = new StubHandler().Enqueue(response, status);
        return (new WebSearchTool(new HttpClient(stub), "http://localhost:8888", maxResults), stub);
    }

    private static Task<string> Search(WebSearchTool tool, string query) =>
        tool.InvokeAsync(Json.Element($$"""{ "query": "{{query}}" }"""), CancellationToken.None);

    // Trimmed from a real SearXNG response for "janka hardness white oak".
    private const string WhiteOak = """
        {
          "query": "janka hardness white oak",
          "answers": [], "infoboxes": [],
          "results": [
            { "engine": "brave", "title": "White Oak | The Wood Database (Hardwood)",
              "url": "https://www.wood-database.com/white-oak/",
              "content": "Janka Hardness: 1,350 lbf (5,990 N). Values are for side grain, measured at 12% MC." },
            { "engine": "brave", "title": "Hardwood Flooring - Understanding the Janka Rating",
              "url": "https://www.bruce.com/en-us/hardwood-flooring-understanding-the-janka-rating.html",
              "content": "Red Oak (1,290 lbf): A benchmark species in flooring.\n  White Oak (1,360 lbf): Slightly harder than red oak." },
            { "engine": "google cse", "title": "Why White Oak is Good for Flooring",
              "url": "https://fontanarchitecture.com/why-white-oak-is-good-for-flooring/",
              "content": "White Oak has a 1360 on the Janka Scale." }
          ],
          "unresponsive_engines": [["duckduckgo", "CAPTCHA"]]
        }
        """;

    [Fact]
    public async Task Asks_searxng_for_json_with_the_query_escaped()
    {
        var (tool, stub) = Build(WhiteOak);

        await Search(tool, "titebond 3 & clamps?");

        var uri = stub.RequestUris[0];
        Assert.Equal("/search", uri.AbsolutePath);
        Assert.Contains("format=json", uri.Query);
        Assert.Contains("q=titebond%203%20%26%20clamps%3F", uri.Query);
    }

    [Fact]
    public async Task Lists_results_with_title_site_and_snippet()
    {
        var (tool, _) = Build(WhiteOak);

        var text = await Search(tool, "janka hardness white oak");

        Assert.StartsWith("Top results for \"janka hardness white oak\":", text);
        Assert.Contains(
            "1. White Oak | The Wood Database (Hardwood) (wood-database.com): Janka Hardness: 1,350 lbf",
            text);
        // Whitespace inside a snippet is collapsed; the URL itself is left out.
        Assert.Contains("benchmark species in flooring. White Oak (1,360 lbf)", text);
        Assert.DoesNotContain("https://", text);
    }

    [Fact]
    public async Task Stops_at_the_configured_number_of_results()
    {
        var (tool, _) = Build(WhiteOak, maxResults: 2);

        var text = await Search(tool, "janka hardness white oak");

        Assert.Contains("2. ", text);
        Assert.DoesNotContain("3. ", text);
    }

    [Fact]
    public async Task Cuts_a_long_snippet_at_a_word_boundary()
    {
        var longSnippet = string.Join(" ", Enumerable.Repeat("sanding", 100));
        var (tool, _) = Build($$"""
            { "results": [ { "title": "Sanding", "url": "https://example.com/", "content": "{{longSnippet}}" } ] }
            """);

        var text = await Search(tool, "sanding");

        var snippet = text[(text.IndexOf("): ", StringComparison.Ordinal) + 3)..];
        Assert.True(snippet.Length <= 303, $"snippet was {snippet.Length} chars");
        Assert.EndsWith("sanding...", snippet);
    }

    [Fact]
    public async Task Puts_direct_answers_and_infoboxes_ahead_of_the_results()
    {
        var (tool, _) = Build("""
            {
              "answers": [ "1 inch = 25.4 mm", { "answer": "Twelve inches in a foot" } ],
              "infoboxes": [ { "infobox": "Quercus alba", "content": "White oak is a hardwood native to eastern North America." } ],
              "results": [ { "title": "Some page", "url": "https://example.com/", "content": "Snippet." } ]
            }
            """);

        var text = await Search(tool, "inches");

        var answer = text.IndexOf("Answer: 1 inch = 25.4 mm", StringComparison.Ordinal);
        Assert.True(answer > 0);
        Assert.Contains("Answer: Twelve inches in a foot", text);
        Assert.Contains("Quercus alba: White oak is a hardwood", text);
        Assert.True(text.IndexOf("1. Some page", StringComparison.Ordinal) > answer);
    }

    [Fact]
    public async Task Says_so_when_nothing_was_found()
    {
        var (tool, _) = Build("""{ "results": [], "answers": [], "infoboxes": [] }""");

        Assert.Equal("No results for \"qwxzv\".", await Search(tool, "qwxzv"));
    }

    [Fact]
    public async Task A_missing_query_asks_for_one_without_searching()
    {
        var (tool, stub) = Build(WhiteOak);

        var text = await tool.InvokeAsync(Json.Empty, CancellationToken.None);

        Assert.Equal("Tell me what to search for.", text);
        Assert.Empty(stub.RequestUris);
    }

    [Fact]
    public async Task Searxng_not_running_comes_back_as_a_plain_sentence()
    {
        var tool = new WebSearchTool(new HttpClient(new UnreachableHandler()), "http://localhost:8888", 4);

        var text = await Search(tool, "anything");

        // The model reads this out in its own words, so no exception text or port numbers.
        Assert.Equal("Web search isn't available right now.", text);
    }

    [Fact]
    public async Task Json_output_switched_off_is_explained()
    {
        var (tool, _) = Build("Forbidden", status: HttpStatusCode.Forbidden);

        var text = await Search(tool, "anything");

        Assert.Contains("JSON output", text);
    }

    [Fact]
    public async Task Another_error_status_is_reported_briefly()
    {
        var (tool, _) = Build("oops", status: HttpStatusCode.InternalServerError);

        Assert.Equal("Web search failed with status 500.", await Search(tool, "anything"));
    }

    [Fact]
    public void Its_preamble_is_a_short_spoken_sentence_that_names_no_tool()
    {
        var (tool, _) = Build(WhiteOak);

        Assert.Contains(tool.Preamble, WebSearchTool.Preambles);
        Assert.All(WebSearchTool.Preambles, p =>
        {
            Assert.EndsWith(".", p);
            Assert.True(p.Split(' ').Length <= 6, $"\"{p}\" is too long to fill a pause");
            Assert.DoesNotContain("search", p, StringComparison.OrdinalIgnoreCase);
        });
    }
}
