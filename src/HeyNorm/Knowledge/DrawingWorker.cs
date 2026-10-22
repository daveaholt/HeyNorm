using System.Drawing;
using System.Text.Json;
using HeyNorm.Llm;

namespace HeyNorm.Knowledge;

/// <summary>What the library needs from whoever reads its drawings.</summary>
public interface IDrawingReadings
{
    IReadOnlyList<Passage> PassagesFor(DocumentLibrary.CachedFile file);
    bool HasRead(DocumentLibrary.CachedFile file, int page);
}

/// <summary>
/// Reads the library's scanned pages with a vision model, in the background, only while Norm
/// is idle. A sheet takes minutes — about 32 requests at 5–15 s on a 4090 — and those
/// requests share the GPU with Whisper and the conversation model, so the worker waits for a
/// quiet minute, and a wake word cancels the request in flight. Every tile's reading is saved
/// as it lands, so a restart or an interruption resumes where it stopped instead of starting
/// the sheet again.
/// </summary>
public sealed class DrawingWorker : IDrawingReadings, IDisposable
{
    private const int CacheVersion = 1;
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);

    private readonly DocumentLibrary _library;
    private readonly ModelRouter _router;
    private readonly string _task;
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly TimeSpan _quietFor;
    private readonly TimeSpan _poll;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Dictionary<string, FileReadings> _cache;
    private CancellationTokenSource? _current;
    private DateTime _quietSince = DateTime.UtcNow;
    private bool _busy;
    private Task? _loop;

    /// <param name="quietFor">How long Norm must have been idle before a tile is started.</param>
    /// <param name="poll">How often to check; tests use a few milliseconds.</param>
    public DrawingWorker(DocumentLibrary library, ModelRouter router, string task, string cachePath, Action<string> log,
        TimeSpan? quietFor = null, TimeSpan? poll = null)
    {
        _library = library;
        _router = router;
        _task = task;
        _cachePath = cachePath;
        _log = log;
        _quietFor = quietFor ?? TimeSpan.FromSeconds(60);
        _poll = poll ?? TimeSpan.FromSeconds(5);
        _cache = Load();
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_shutdown.Token));

    /// <summary>A session started (busy) or ended. Busy cancels the request in flight.</summary>
    public void SetBusy(bool busy)
    {
        lock (_gate)
        {
            if (busy == _busy) return;
            _busy = busy;
            if (busy) _current?.Cancel();
            else _quietSince = DateTime.UtcNow;
        }
    }

    private bool Quiet
    {
        get { lock (_gate) return !_busy && DateTime.UtcNow - _quietSince >= _quietFor; }
    }

    /// <summary>Runs until disposed. Exposed for tests, which drive it directly.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var next = _library.State == LibraryState.Ready ? NextPage() : null;
                if (next is null || !Quiet)
                {
                    await Task.Delay(_poll, ct);
                    continue;
                }
                await ReadPageAsync(next.Value.File, next.Value.Page, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Ollama down, the model not pulled: say so once, then leave it alone a while.
                _log($"Drawings: paused for {Backoff.TotalMinutes:0} minutes — {ex.Message}");
                try { await Task.Delay(Backoff, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private (DocumentLibrary.CachedFile File, int Page)? NextPage() =>
        _library.Files
            .SelectMany(f => f.ImagePages.Select(p => (File: f, Page: p)))
            .Where(x => !HasRead(x.File, x.Page))
            .Select(x => ((DocumentLibrary.CachedFile, int)?)x)
            .FirstOrDefault();

    private async Task ReadPageAsync(DocumentLibrary.CachedFile file, int page, CancellationToken ct)
    {
        using var sheet = DrawingSheet.LoadScan(Path.Combine(_library.Folder, file.Path), page);
        var readings = Entry(file);
        var pageReadings = readings.Pages.GetValueOrDefault(page) ?? new PageReadings();
        if (sheet is null)
        {
            // An image-only page whose image GDI+ can't decode: mark it done so it isn't retried forever.
            Save(file, page, pageReadings with { Done = true });
            return;
        }

        var tiles = DrawingSheet.Layout(sheet.Width, sheet.Height);
        if (pageReadings.Tiles.Count == 0)
            _log($"Drawings: reading {file.Document}{(file.PageCount > 1 ? $" page {page}" : "")} — {tiles.Count} pieces while Norm is idle.");

        var (provider, task) = _router.Resolve(_task);
        foreach (var tile in tiles.Where(t => !pageReadings.Tiles.ContainsKey(t.Key)))
        {
            if (!Quiet) return; // resumes from here next time
            var read = await ReadTileAsync(provider, task, DrawingSheet.Crop(sheet, tile), ct);
            if (read is null) return; // interrupted by a session
            pageReadings = pageReadings with { Tiles = new(pageReadings.Tiles) { [tile.Key] = read } };
            Save(file, page, pageReadings);
        }

        var outcome = DrawingConsensus.Agree(file.Project, file.Document, page, sheet.Size,
            tiles.Select(t => (t, pageReadings.Tiles[t.Key])).ToList());
        Save(file, page, pageReadings with { Done = true, Passages = outcome.Passages.ToList() });
        _library.Rebuild();
        _log($"Drawings: {file.Document}{(file.PageCount > 1 ? $" page {page}" : "")} read — {outcome.Agreed} figures agreed between two reads and kept, " +
             $"{outcome.Dropped} read only once and dropped.");
    }

    /// <summary>One tile. Null when a session interrupted it; a second failure in a row records it as unreadable.</summary>
    private async Task<TileRead?> ReadTileAsync(IChatProvider provider, Config.TaskSettings task, byte[] jpeg, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var tileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate)
            {
                if (_busy) return null;
                _current = tileCts;
            }

            try
            {
                var response = await provider.CompleteAsync(new ChatRequest
                {
                    Model = task.Model,
                    SystemPrompt = "",
                    Messages = [new ChatMessage { Role = ChatRole.User, Text = TileRead.Prompt, Images = [jpeg] }],
                    MaxTokens = task.MaxTokens,
                    Think = task.Think,
                    Temperature = task.Temperature,
                    RepeatPenalty = task.RepeatPenalty,
                    ContextLength = task.ContextLength,
                    ResponseSchema = TileRead.Schema,
                }, tileCts.Token);
                return TileRead.Parse(response.Text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (InvalidOperationException) when (attempt == 1)
            {
                // Measured: Ollama occasionally answers 400 to a request that succeeds when repeated.
            }
            catch (InvalidOperationException ex)
            {
                _log($"Drawings: one piece couldn't be read ({ex.Message}); carrying on without it.");
                return TileRead.Empty;
            }
            finally
            {
                lock (_gate) _current = null;
            }
        }
    }

    // --- IDrawingReadings ----------------------------------------------------------

    public IReadOnlyList<Passage> PassagesFor(DocumentLibrary.CachedFile file)
    {
        lock (_gate)
            return _cache.TryGetValue(Key(file), out var r) ? r.Pages.Values.SelectMany(p => p.Passages).ToList() : [];
    }

    public bool HasRead(DocumentLibrary.CachedFile file, int page)
    {
        lock (_gate)
            return _cache.TryGetValue(Key(file), out var r) && r.Pages.TryGetValue(page, out var p) && p.Done;
    }

    // --- the cache -------------------------------------------------------------------

    public sealed record PageReadings
    {
        public Dictionary<string, TileRead> Tiles { get; init; } = [];
        public bool Done { get; init; }
        public List<Passage> Passages { get; init; } = [];
    }

    public sealed record FileReadings(Dictionary<int, PageReadings> Pages);

    private sealed record CacheFile(int Version, Dictionary<string, FileReadings> Files);

    /// <summary>A file's readings belong to that exact file: an edited PDF is read afresh.</summary>
    private static string Key(DocumentLibrary.CachedFile f) => $"{f.Path}|{f.Size}|{f.Modified.Ticks}";

    private FileReadings Entry(DocumentLibrary.CachedFile file)
    {
        lock (_gate)
            return _cache.TryGetValue(Key(file), out var r) ? r : new FileReadings([]);
    }

    private void Save(DocumentLibrary.CachedFile file, int page, PageReadings readings)
    {
        lock (_gate)
        {
            var entry = _cache.TryGetValue(Key(file), out var r) ? r : new FileReadings([]);
            entry.Pages[page] = readings;
            _cache[Key(file)] = entry;

            // Drop readings of files no longer in the library, so the cache doesn't grow forever.
            var live = _library.Files.Select(Key).ToHashSet();
            foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList()) _cache.Remove(stale);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(new CacheFile(CacheVersion, _cache)));
            }
            catch (IOException ex)
            {
                _log($"Drawings: couldn't save progress ({ex.Message}).");
            }
        }
    }

    private Dictionary<string, FileReadings> Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return [];
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
            return cache is { Version: CacheVersion } ? cache.Files : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _shutdown.Dispose();
    }
}
