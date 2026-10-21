using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HeyNorm.Tools;

/// <summary>
/// Web search through a local SearXNG instance (see README). SearXNG has no index of its own:
/// it asks Google, Brave, Bing and others and merges what they return, which keeps this free
/// and keyless at the cost of running one container.
/// </summary>
public sealed class WebSearchTool : ITool
{
    // Long enough to carry an answer, short enough that a few of them stay cheap to read.
    private const int MaxSnippetChars = 300;

    private readonly HttpClient _http;
    private readonly int _maxResults;

    public WebSearchTool(HttpClient http, string baseUrl, int maxResults)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl);
        // Someone is standing there waiting for the answer. SearXNG gives up on slow engines
        // after about three seconds on its own, so this only catches it being down or stuck.
        _http.Timeout = TimeSpan.FromSeconds(10);
        _maxResults = Math.Max(1, maxResults);
    }

    public string Name => "web_search";
    public string Description =>
        "Search the web. Use it for facts you aren't sure of, anything that changes such as prices, " +
        "store hours, or news, and product details like glue cure times. Returns the top results " +
        "as short snippets; answer from them in a sentence or two.";
    // A few phrasings so a session of lookups doesn't sound canned.
    internal static readonly string[] Preambles = ["Let me look that up.", "One sec, I'll check.", "Let me check on that."];

    /// <summary>A search takes one to three seconds, which is a long silence out loud.</summary>
    public string Preamble => Preambles[Random.Shared.Next(Preambles.Length)];

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What to search for, worded the way you would type it into a search engine.",
            },
        },
        ["required"] = new JsonArray("query"),
    };

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var query = arguments.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString()!.Trim()
            : "";
        if (query.Length == 0)
            return "Tell me what to search for.";

        // Failures come back as text rather than exceptions so the model can say plainly
        // that search is down, instead of reading out a stack of error detail.
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"/search?q={Uri.EscapeDataString(query)}&format=json", ct);
        }
        catch (HttpRequestException)
        {
            return "Web search isn't available right now.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Web search took too long to answer.";
        }

        using (response)
        {
            // SearXNG answers 403 when its json format is switched off, which is its default.
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return "Web search is refusing requests. Its JSON output needs switching on in the search settings.";
            if (!response.IsSuccessStatusCode)
                return $"Web search failed with status {(int)response.StatusCode}.";

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Summarize(query, doc.RootElement);
        }
    }

    private string Summarize(string query, JsonElement root)
    {
        var text = new StringBuilder();

        // Direct answers and infoboxes, when an engine offers one, beat any snippet.
        foreach (var answer in Array(root, "answers"))
        {
            // Older SearXNG sends plain strings, newer sends objects.
            var value = answer.ValueKind == JsonValueKind.String ? answer.GetString() : Str(answer, "answer");
            if (!string.IsNullOrWhiteSpace(value))
                text.AppendLine($"Answer: {Clean(value)}");
        }
        foreach (var box in Array(root, "infoboxes"))
        {
            var content = Str(box, "content");
            if (!string.IsNullOrWhiteSpace(content))
                text.AppendLine($"{Clean(Str(box, "infobox") ?? "Summary")}: {Clean(content)}");
        }

        var n = 0;
        foreach (var result in Array(root, "results"))
        {
            var title = Str(result, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (++n > _maxResults) break;

            // The site name lets the model say where a figure came from; the full URL is
            // just tokens nobody will hear.
            var site = Uri.TryCreate(Str(result, "url"), UriKind.Absolute, out var url)
                ? $" ({Regex.Replace(url.Host, @"^www\.", "")})"
                : "";
            var snippet = Str(result, "content");
            text.Append($"{n}. {Clean(title)}{site}");
            text.AppendLine(string.IsNullOrWhiteSpace(snippet) ? "" : $": {Clean(snippet)}");
        }

        return text.Length == 0
            ? $"No results for \"{query}\"."
            : $"Top results for \"{query}\":\n{text.ToString().TrimEnd()}";
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Collapses whitespace and cuts at a word boundary.</summary>
    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= MaxSnippetChars) return s;

        var cut = s.LastIndexOf(' ', MaxSnippetChars);
        return s[..(cut > 0 ? cut : MaxSnippetChars)].TrimEnd(',', ';', ' ') + "...";
    }
}
