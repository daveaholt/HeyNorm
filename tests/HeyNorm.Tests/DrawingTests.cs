using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using HeyNorm.Config;
using HeyNorm.Knowledge;
using HeyNorm.Llm;
using HeyNorm.Tools;
using UglyToad.PdfPig.Writer;

namespace HeyNorm.Tests;

// Readings below are invented in the shape the real model returned; the figures echo the
// kinds of misreads measured on a real 300 dpi drawing (a small figure read with one wrong
// digit in one pass only).

internal static class Scans
{
    /// <summary>A white JPEG with a few lines on it, standing in for a scanned sheet.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.DrawLine(Pens.Black, 0, 0, width - 1, height - 1);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    /// <summary>A one-page PDF that is nothing but that scan, like a plan sheet.</summary>
    public static byte[] ScannedPdf(int width, int height)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(width * 72.0 / 300, height * 72.0 / 300);
        page.AddJpeg(Jpeg(width, height), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, width * 72.0 / 300, height * 72.0 / 300));
        return builder.Build();
    }
}

public class DrawingSheetTests
{
    [Fact]
    public void An_a1_scan_is_cut_into_a_4_by_3_grid_and_a_shifted_second_grid()
    {
        var tiles = DrawingSheet.Layout(9916, 7044);

        Assert.Equal(12, tiles.Count(t => t.Pass == "A"));
        Assert.True(tiles.Count(t => t.Pass == "B") > 12);
        // Every spot on the sheet is read in both passes.
        foreach (var (x, y) in new[] { (5, 5), (4958, 3522), (9910, 7040), (2480, 2350), (7400, 600) })
        {
            Assert.Contains(tiles, t => t.Pass == "A" && t.Area.Contains(x, y));
            Assert.Contains(tiles, t => t.Pass == "B" && t.Area.Contains(x, y));
        }
    }

    [Fact]
    public void Pass_b_crops_differ_from_pass_a_so_a_misread_is_unlikely_to_repeat()
    {
        var tiles = DrawingSheet.Layout(9916, 7044);
        var a = tiles.Where(t => t.Pass == "A").Select(t => t.Area).ToHashSet();

        Assert.DoesNotContain(tiles.Where(t => t.Pass == "B"), t => a.Contains(t.Area));
    }

    [Fact]
    public void A_small_sketch_is_read_whole_twice()
    {
        var tiles = DrawingSheet.Layout(1152, 1763);

        Assert.Equal(["A", "B"], tiles.Select(t => t.Pass));
        Assert.All(tiles, t => Assert.Equal(new Rectangle(0, 0, 1152, 1763), t.Area));
    }

    [Fact]
    public void Tiles_go_to_the_model_at_most_1600_px_on_their_longest_side()
    {
        // Measured: a tall edge tile sized by width alone went at 1548x2934 and Ollama said 400.
        using var sheet = new Bitmap(4000, 3000);
        var tall = new SheetTile("B", new Rectangle(3000, 0, 1000, 3000));

        using var crop = new Bitmap(new MemoryStream(DrawingSheet.Crop(sheet, tall)));

        Assert.Equal(1600, crop.Height);
        Assert.True(crop.Width < 1600);
    }

    [Fact]
    public void The_second_whole_read_of_a_small_sheet_is_at_a_different_size()
    {
        using var sheet = new Bitmap(1152, 1763);
        var whole = new Rectangle(0, 0, 1152, 1763);

        using var a = new Bitmap(new MemoryStream(DrawingSheet.Crop(sheet, new SheetTile("A", whole))));
        using var b = new Bitmap(new MemoryStream(DrawingSheet.Crop(sheet, new SheetTile("B", whole))));

        Assert.True(b.Height < a.Height);
    }

    [Theory]
    [InlineData(0, 0, "top left")]
    [InlineData(4500, 0, "top middle")]
    [InlineData(4500, 3000, "middle")]
    [InlineData(8000, 3000, "middle right")]
    [InlineData(4500, 6000, "bottom middle")]
    [InlineData(8000, 6000, "bottom right")]
    public void Positions_are_named_the_way_a_person_finds_them_on_paper(int x, int y, string where)
    {
        Assert.Equal(where, DrawingSheet.Where(new Rectangle(x, y, 900, 900), 9900, 7000));
    }

    [Fact]
    public void The_scan_is_pulled_out_of_the_pdf_at_full_resolution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heynorm-scan-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Scans.ScannedPdf(2400, 1700));
        try
        {
            using var scan = DrawingSheet.LoadScan(path, 1);

            Assert.NotNull(scan);
            Assert.Equal(new Size(2400, 1700), scan!.Size);
        }
        finally { File.Delete(path); }
    }
}

public class DrawingConsensusTests
{
    private static readonly Size Sheet = new(9000, 6000);
    private static readonly SheetTile A = new("A", new Rectangle(0, 0, 3000, 2000));
    private static readonly SheetTile B = new("B", new Rectangle(1500, 1000, 3000, 2000)); // overlaps A
    private static readonly SheetTile FarB = new("B", new Rectangle(6000, 4000, 3000, 2000)); // doesn't

    private static TileRead Read(string[] figures, string[]? labels = null, string[]? notes = null) =>
        new(figures, labels ?? [], notes ?? []);

    [Fact]
    public void A_figure_read_in_both_passes_is_kept_and_one_read_once_is_dropped()
    {
        var outcome = DrawingConsensus.Agree("Skiff 12", "Skiff 12 02", 1, Sheet,
            [(A, Read(["812", "1145", "655"])), (B, Read(["812mm", "1145", "635"]))]);

        var passage = Assert.Single(outcome.Passages);
        Assert.Contains("Figures in millimetres: 812, 1145.", passage.Text);
        Assert.DoesNotContain("655", passage.Text);
        Assert.Equal(2, outcome.Agreed);
        Assert.Equal(1, outcome.Dropped);
    }

    [Fact]
    public void Agreement_only_counts_between_overlapping_crops()
    {
        var outcome = DrawingConsensus.Agree("Skiff 12", "Skiff 12 02", 1, Sheet,
            [(A, Read(["812"])), (FarB, Read(["812"]))]);

        Assert.Empty(outcome.Passages);
    }

    [Fact]
    public void Labels_and_notes_match_despite_small_spelling_differences()
    {
        var outcome = DrawingConsensus.Agree("Skiff 12", "Skiff 12 02", 1, Sheet,
        [
            (A, Read(["812"], ["FRAME 'C'", "FRAME 'D'"], ["NOTE: SHAPE OF COACHHOUSE MAY BE ALTERED TO SUIT"])),
            (B, Read(["812"], ["FRAME C"], ["Note - shape of coach house may be altered to suit."])),
        ]);

        var text = Assert.Single(outcome.Passages).Text;
        Assert.Contains("Labels: FRAME 'C'.", text);
        Assert.DoesNotContain("FRAME 'D'", text);
        Assert.Contains("Note: \"NOTE: SHAPE OF COACHHOUSE MAY BE ALTERED TO SUIT\"", text);
    }

    [Fact]
    public void A_reading_is_flagged_and_located_on_the_sheet()
    {
        var passage = Assert.Single(DrawingConsensus.Agree("Skiff 12", "Skiff 12 02", 1, Sheet,
            [(A, Read(["812"])), (B, Read(["812"]))]).Passages);

        Assert.True(passage.MachineRead);
        Assert.Equal("Skiff 12 02, page 1, machine-read from the top left of the sheet", passage.Source);
    }

    [Theory]
    [InlineData("812", "812")]
    [InlineData("812 mm", "812")]
    [InlineData("3", null)]       // an item-number circle that slipped through
    [InlineData("6°", null)]
    [InlineData("[unclear]", null)]
    public void Only_plain_millimetre_figures_count(string value, string? figure)
    {
        Assert.Equal(figure, DrawingConsensus.Figure(value));
    }

    [Fact]
    public void A_reply_that_is_not_the_asked_for_json_reads_as_nothing()
    {
        Assert.Equal(TileRead.Empty, TileRead.Parse("FRAME D 812 FRAME C"));
        Assert.Equal(["812"], TileRead.Parse("""{ "dimensions": [ { "value": 812, "spans": "?" } ], "labels": [], "notes": [] }""").Figures);
    }
}

public class DrawingWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heynorm-dw-{Guid.NewGuid():N}");
    private readonly List<string> _log = [];

    public DrawingWorkerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "Skiff 12"));
        File.WriteAllBytes(Path.Combine(_root, "docs", "Skiff 12", "Skiff 12 02.pdf"), Scans.ScannedPdf(1200, 900));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A vision model whose behaviour each test scripts.</summary>
    private sealed class FakeVision(Func<ChatRequest, CancellationToken, Task<string>> answer) : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];
        public string Name => "FakeVision";

        public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return new ChatResponse(await answer(request, ct), []);
        }

        public Task<ChatResponse> StreamAsync(ChatRequest r, Action<string> onText, CancellationToken ct = default) => CompleteAsync(r, ct);
        public Task WarmUpAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
    }

    private const string Reading = """{ "dimensions": [ { "value": "812", "spans": "x" } ], "labels": ["FRAME C"], "notes": [] }""";

    private async Task<(DocumentLibrary Library, DrawingWorker Worker)> Build(FakeVision vision)
    {
        var settings = new AppSettings
        {
            Tasks = { ["drawings"] = new TaskSettings { Provider = "fake", Model = "vision", MaxTokens = 100, ContextLength = 8192 } },
            Providers = { ["fake"] = new ProviderSettings { Type = "fake" } },
        };
        var library = new DocumentLibrary(Path.Combine(_root, "docs"), Path.Combine(_root, "index.json"), _log.Add);
        var worker = new DrawingWorker(library, new ModelRouter(settings, _ => vision), "drawings",
            Path.Combine(_root, "readings.json"), _log.Add, quietFor: TimeSpan.Zero, poll: TimeSpan.FromMilliseconds(5));
        library.Readings = worker;
        await library.RefreshAsync();
        return (library, worker);
    }

    private static async Task RunUntil(DrawingWorker worker, Func<bool> done)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = worker.RunAsync(cts.Token);
        while (!done() && !cts.IsCancellationRequested) await Task.Delay(10);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Reads_a_scanned_sheet_twice_and_makes_its_figures_searchable_with_a_warning()
    {
        var vision = new FakeVision((_, _) => Task.FromResult(Reading));
        var (library, worker) = await Build(vision);

        await RunUntil(worker, () => library.UnreadableDocuments().Count == 0);

        Assert.Equal(2, vision.Requests.Count); // one whole read per pass for a small sheet
        Assert.All(vision.Requests, r =>
        {
            Assert.Single(Assert.Single(r.Messages).Images);
            Assert.NotNull(r.ResponseSchema);
            Assert.Equal(8192, r.ContextLength);
        });
        var hit = Assert.Single(library.Search("frame", 5)).Passage;
        Assert.True(hit.MachineRead);
        Assert.Contains("812", hit.Text);
        Assert.Contains(_log, l => l.Contains("Skiff 12 02 read"));
    }

    [Fact]
    public async Task Nothing_is_read_while_a_session_is_going()
    {
        var vision = new FakeVision((_, _) => Task.FromResult(Reading));
        var (_, worker) = await Build(vision);
        worker.SetBusy(true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.RunAsync(cts.Token);

        Assert.Empty(vision.Requests);
    }

    [Fact]
    public async Task A_wake_word_cancels_the_piece_in_flight_and_the_sheet_resumes_later()
    {
        var started = new TaskCompletionSource();
        var calls = 0;
        var vision = new FakeVision(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct); // hangs until cancelled
            }
            return Reading;
        });
        var (library, worker) = await Build(vision);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = worker.RunAsync(cts.Token);
        await started.Task;
        worker.SetBusy(true);  // "Hey Norm"
        await Task.Delay(100);
        Assert.NotEmpty(library.UnreadableDocuments());

        worker.SetBusy(false); // session over
        while (library.UnreadableDocuments().Count > 0 && !cts.IsCancellationRequested) await Task.Delay(10);
        cts.Cancel();
        await run;

        Assert.Empty(library.UnreadableDocuments());
        Assert.Equal(3, calls); // the cancelled piece was asked again, then the second pass
    }

    [Fact]
    public async Task Progress_survives_a_restart_so_nothing_is_read_twice()
    {
        var first = new FakeVision((_, _) => Task.FromResult(Reading));
        var (library, worker) = await Build(first);
        await RunUntil(worker, () => library.UnreadableDocuments().Count == 0);

        var second = new FakeVision((_, _) => Task.FromResult(Reading));
        var (restarted, _) = await Build(second);

        Assert.Empty(second.Requests);
        Assert.Empty(restarted.UnreadableDocuments());
        Assert.Single(restarted.Search("frame", 5));
    }

    [Fact]
    public async Task A_piece_ollama_keeps_refusing_is_skipped_rather_than_retried_forever()
    {
        var vision = new FakeVision((r, _) => throw new InvalidOperationException("Ollama returned 400: bad request"));
        var (library, worker) = await Build(vision);

        await RunUntil(worker, () => library.UnreadableDocuments().Count == 0);

        Assert.Equal(4, vision.Requests.Count); // each of the two reads tried twice
        Assert.Contains(_log, l => l.Contains("couldn't be read"));
    }
}

public class VisionWireFormatTests
{
    private static ChatRequest WithImage => new()
    {
        Model = "vision",
        SystemPrompt = "",
        Messages = [new ChatMessage { Role = ChatRole.User, Text = "Read this.", Images = [[1, 2, 3]] }],
        ResponseSchema = TileRead.Schema,
        Temperature = 0.1,
        RepeatPenalty = 1.15,
        ContextLength = 8192,
    };

    [Fact]
    public async Task Ollama_gets_base64_images_the_schema_and_the_sampling_options()
    {
        var stub = new StubHandler().Enqueue("""{ "message": { "content": "{}" } }""");
        await new OllamaChatProvider(new HttpClient(stub), "http://localhost:11434").CompleteAsync(WithImage);

        var body = stub.Request();
        Assert.Equal("AQID", (string?)body["messages"]![1]!["images"]![0]);
        Assert.Equal("object", (string?)body["format"]!["type"]);
        Assert.Equal(0.1, (double?)body["options"]!["temperature"]);
        Assert.Equal(1.15, (double?)body["options"]!["repeat_penalty"]);
        Assert.Equal(8192, (int?)body["options"]!["num_ctx"]);
    }

    [Fact]
    public async Task Claude_gets_an_image_block_before_the_text()
    {
        var stub = new StubHandler().Enqueue("""{ "content": [ { "type": "text", "text": "{}" } ] }""");
        await new ClaudeChatProvider(new HttpClient(stub), "https://api.anthropic.com", "k").CompleteAsync(WithImage);

        var content = stub.Request()["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", (string?)content[0]!["type"]);
        Assert.Equal("AQID", (string?)content[0]!["source"]!["data"]);
        Assert.Equal("Read this.", (string?)content[1]!["text"]);
        Assert.Equal(0.1, (double?)stub.Request()["temperature"]);
    }

    [Fact]
    public async Task A_plain_text_message_is_unchanged_for_both()
    {
        var ollama = new StubHandler().Enqueue("""{ "message": { "content": "hi" } }""");
        await new OllamaChatProvider(new HttpClient(ollama), "http://x").CompleteAsync(new ChatRequest
        { Model = "m", SystemPrompt = "s", Messages = [ChatMessage.User("hi")] });

        Assert.Null(ollama.Request()["messages"]![1]!["images"]);
        Assert.Null(ollama.Request()["format"]);
        Assert.Null(ollama.Request()["options"]!["temperature"]);
    }
}

public class MachineReadAnswerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heynorm-mr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedReadings(Passage passage) : IDrawingReadings
    {
        public IReadOnlyList<Passage> PassagesFor(DocumentLibrary.CachedFile file) =>
            file.Document == passage.Document ? [passage] : [];
        public bool HasRead(DocumentLibrary.CachedFile file, int page) => file.Document == passage.Document;
    }

    [Fact]
    public async Task A_drawing_reading_comes_with_the_warning_the_sheet_and_the_spot()
    {
        var path = Path.Combine(_root, "Skiff 12", "Skiff 12 02.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "scan");
        var library = new DocumentLibrary(_root, Path.Combine(_root, "..", $"mr-{Guid.NewGuid():N}.json"), _ => { },
            _ => new ReadDocument([new PageText(1, "")]))
        {
            Readings = new FixedReadings(new Passage("Skiff 12", "Skiff 12 02", 1,
                "machine-read from the top middle of the sheet", null,
                "Labels: FRAME C, FRAME B. Figures in millimetres: 812, 1145.", MachineRead: true)),
        };
        await library.RefreshAsync();
        var tool = new DocumentSearchTool(library, 3);

        var text = await tool.InvokeAsync(Json.Element("""{ "query": "frame" }"""), CancellationToken.None);

        Assert.Contains("1. [MACHINE-READ, MAY BE WRONG] Skiff 12 02, page 1, machine-read from the top middle of the sheet: " +
                        "Labels: FRAME C, FRAME B. Figures in millimetres: 812, 1145.", text);
        Assert.Contains("you must say it was machine-read, name the drawing and where on the sheet", text);
        Assert.Contains("tell the person to check the drawing before cutting", text);
        Assert.DoesNotContain("Not readable yet", text); // it has been read
    }
}
