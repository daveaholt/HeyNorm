using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyNorm.Llm;

namespace HeyNorm.Tests;

/// <summary>
/// Stands in for a real HTTP endpoint: records every request body it sees and replays a
/// queued list of canned responses, so provider tests cover both the request we send and
/// the parsing of what comes back.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, string? MediaType)> _responses = new();

    public List<JsonNode> Requests { get; } = [];
    public List<Uri> RequestUris { get; } = [];
    public List<HttpRequestHeaders> Headers { get; } = [];
    public List<HttpMethod> Methods { get; } = [];
    /// <summary>Every request's body as sent, "" when there was none — including form posts.</summary>
    public List<string> RawBodies { get; } = [];

    public StubHandler Enqueue(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, json, "application/json"));
        return this;
    }

    /// <summary>A body that isn't JSON, with no content type — how Spotify answers a player command.</summary>
    public StubHandler EnqueueText(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, body, null));
        return this;
    }

    /// <summary>The body of the Nth request, as a JsonNode.</summary>
    public JsonNode Request(int index = 0) => Requests[index];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestUris.Add(request.RequestUri!);
        Headers.Add(new HttpRequestHeaders(request.Headers));

        Methods.Add(request.Method);

        // GETs carry no body, Spotify's token endpoint takes a form, and its player PUTs are
        // often empty; only JSON bodies go into Requests.
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        RawBodies.Add(body);
        if (body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('['))
            Requests.Add(JsonNode.Parse(body)!);

        var (status, text, mediaType) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}", "application/json");
        var content = new StringContent(text, Encoding.UTF8);
        content.Headers.ContentType = mediaType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }
}

/// <summary>Stands in for a server that isn't there: every request fails to connect.</summary>
internal sealed class UnreachableHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
}

/// <summary>Snapshot of request headers — the live message is disposed before assertions run.</summary>
internal sealed class HttpRequestHeaders
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public HttpRequestHeaders(System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        foreach (var (key, values) in headers)
            _values[key] = string.Join(",", values);
    }

    public string? this[string name] => _values.TryGetValue(name, out var v) ? v : null;
}

/// <summary>An IChatProvider that replays a scripted list of responses and records its requests.</summary>
internal sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<ChatResponse> _responses;

    public FakeChatProvider(params ChatResponse[] responses) => _responses = new Queue<ChatResponse>(responses);

    public string Name => "Fake";

    public List<ChatRequest> Requests { get; } = [];
    public int CallCount => Requests.Count;

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Snapshot the history: the engine mutates the same list between rounds.
        Requests.Add(request with { Messages = request.Messages.ToList() });

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new ChatResponse("done", []);
        return Task.FromResult(response);
    }

    /// <summary>
    /// Feeds the scripted text through in three-character fragments, so sentence boundaries
    /// land mid-fragment the way they do in a real stream.
    /// </summary>
    public async Task<ChatResponse> StreamAsync(ChatRequest request, Action<string> onText, CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);
        for (var i = 0; i < response.Text.Length; i += 3)
            onText(response.Text.Substring(i, Math.Min(3, response.Text.Length - i)));
        return response;
    }

    public List<string> WarmedUp { get; } = [];

    public Task WarmUpAsync(string model, CancellationToken ct = default)
    {
        WarmedUp.Add(model);
        return Task.CompletedTask;
    }
}

internal static class Json
{
    /// <summary>Builds the JsonElement a tool call carries as its arguments.</summary>
    public static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement Empty => Element("{}");
}

/// <summary>A tool whose behaviour each test dictates.</summary>
internal sealed class StubTool : HeyNorm.Tools.ITool
{
    private readonly Func<JsonElement, CancellationToken, Task<string>> _body;

    public StubTool(string name, Func<JsonElement, CancellationToken, Task<string>> body) =>
        (Name, _body) = (name, body);

    public StubTool(string name, string result, string? preamble = null)
        : this(name, (_, _) => Task.FromResult(result)) => Preamble = preamble;

    public string Name { get; }
    public string? Preamble { get; }
    public string Description => $"Stub tool {Name}.";
    public JsonObject ParametersSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public int Invocations { get; private set; }

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        Invocations++;
        return _body(arguments, ct);
    }
}
