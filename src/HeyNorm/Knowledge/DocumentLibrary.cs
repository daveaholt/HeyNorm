using System.Diagnostics;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace HeyNorm.Knowledge;

public enum LibraryState { NoFolder, Indexing, Ready }

/// <summary>What reading one PDF produced: its pages' text, and which pages are only images.</summary>
public sealed record ReadDocument(IReadOnlyList<PageText> Pages);

/// <summary>
/// The person's documents folder, indexed for search. Indexing happens on whatever machine
/// Norm runs on, in the background: at startup and whenever a PDF is added, changed or
/// removed. Each file is read once — the results are cached against its size and timestamp —
/// so a restart only reads what changed.
///
/// Subfolders name projects: "Sailboats\Skiff 12\Skiff 12 Specification.pdf" is in
/// project "Skiff 12", which keeps a second boat's plans from answering for the first.
///
/// Pages with no text — scanned drawings — are recorded rather than skipped, so a later pass
/// with a vision model can find them, and so Norm can say which sheets it can't read yet.
/// </summary>
public sealed class DocumentLibrary : IDisposable
{
    // Bump when the splitter or tokenizer changes what a cached file would contain.
    private const int CacheVersion = 2;

    private readonly string _folder;
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly Func<string, ReadDocument> _read;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private volatile Snapshot _current = new([], new KeywordIndex([]));
    private volatile LibraryState _state;

    private sealed record Snapshot(IReadOnlyList<CachedFile> Files, KeywordIndex Index);

    /// <param name="read">How a PDF becomes pages of text; tests substitute their own.</param>
    public DocumentLibrary(string folder, string cachePath, Action<string> log, Func<string, ReadDocument>? read = null)
    {
        _folder = folder;
        _cachePath = cachePath;
        _log = log;
        _read = read ?? ReadPdf;
        _state = Directory.Exists(folder) ? LibraryState.Indexing : LibraryState.NoFolder;
    }

    public LibraryState State => _state;
    public string Folder => _folder;
    public IReadOnlyList<CachedFile> Files => _current.Files;

    /// <summary>Readings of scanned pages, merged into search. Set once at startup, before Start.</summary>
    public IDrawingReadings? Readings { get; set; }

    /// <summary>Indexes in the background and starts watching. Returns at once.</summary>
    public void Start()
    {
        if (_state == LibraryState.NoFolder)
        {
            _log($"Documents: no folder at {_folder}");
            return;
        }

        _ = Task.Run(RefreshAsync);
        try
        {
            _watcher = new FileSystemWatcher(_folder, "*.pdf")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            // A copy or a OneDrive sync fires a burst of events; wait for it to go quiet.
            _debounce = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
            FileSystemEventHandler changed = (_, _) => _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            _watcher.Created += changed;
            _watcher.Changed += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _log($"Documents: can't watch {_folder} for changes ({ex.Message}); restart Norm to pick up new files.");
        }
    }

    public IReadOnlyList<SearchHit> Search(string query, int max, string? project = null) =>
        _current.Index.Search(query, max, ResolveProject(project));

    /// <summary>
    /// Machine readings from the drawings a typed passage points to: "shown on Drg. 02" means
    /// the "… 02" sheet in the same project; "Drg. No. XYZ/91/04" means "… 04". The spec says
    /// where the figure is; the reading of that sheet is the nearest thing to the figure itself.
    /// </summary>
    public IReadOnlyList<SearchHit> ReadingsReferencedBy(Passage typed, string query, int max)
    {
        var sheets = DrawingReference.Matches(typed.Text)
            .Select(m => int.Parse(m.Groups["n"].Value)).Distinct()
            .SelectMany(n => new[] { $" {n:00}", $" {n}" }).ToHashSet();
        if (sheets.Count == 0) return [];

        return _current.Index.Search(query, max, typed.Project, p =>
            p.MachineRead && sheets.Any(s => p.Document.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly System.Text.RegularExpressions.Regex DrawingReference = new(
        @"\bDrgs?\.?\s*(?:No\.?\s*)?(?:[A-Z0-9]+/)*0?(?<n>\d{1,2})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public IReadOnlyList<string> Projects =>
        _current.Files.Select(f => f.Project).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    /// <summary>Documents that are all scan and not yet read, e.g. "Skiff 12 01" for a drawing.</summary>
    public IReadOnlyList<string> UnreadableDocuments(string? project = null) =>
        _current.Files
            .Where(f => f.ImagePages.Count > 0 && (project is null || string.Equals(f.Project, project, StringComparison.OrdinalIgnoreCase)))
            .Where(f => f.ImagePages.Count == f.PageCount)
            .Where(f => Readings is null || !f.ImagePages.All(p => Readings.HasRead(f, p)))
            .Select(f => f.Document).Order().ToList();

    /// <summary>Rebuilds search from what's already read — after a drawing finishes, say.</summary>
    public void Rebuild()
    {
        var files = _current.Files;
        _current = new Snapshot(files, BuildIndex(files));
    }

    private KeywordIndex BuildIndex(IReadOnlyList<CachedFile> files) =>
        new(files.SelectMany(f => f.Passages.Concat(Readings?.PassagesFor(f) ?? [])));

    /// <summary>A loosely named project ("skiff") matched to its folder name, if one fits.</summary>
    public string? ResolveProject(string? project) => MatchProject(project, Projects);

    // Words that say nothing about which boat: a name made only of these matches nothing.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        { "the", "my", "boat", "boats", "plan", "plans", "project", "drawing", "drawings", "one", "other", "sailboat", "unknown" };

    /// <summary>
    /// A spoken project name matched to a folder name. Containment first ("heron" in
    /// "Heron 15"); then a near-miss on a distinctive word, because Whisper spells unusual
    /// names its own way ("Herron"). The model also sends junk here — measured: "gaff yawl
    /// rig", "unknown", "hull panels" — which must match nothing rather than the nearest boat.
    /// </summary>
    internal static string? MatchProject(string? spoken, IReadOnlyList<string> projects)
    {
        if (string.IsNullOrWhiteSpace(spoken) || projects.Count == 0) return null;
        var said = spoken.Trim();

        var contained = projects.FirstOrDefault(p => p.Contains(said, StringComparison.OrdinalIgnoreCase) ||
                                                     said.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (contained is not null) return contained;

        var saidWords = Words(said).Where(w => w.Length >= 4 && !Generic.Contains(w)).ToList();
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var project in projects)
        {
            foreach (var name in Words(project).Where(w => w.Length >= 4))
            {
                foreach (var word in saidWords)
                {
                    var distance = Levenshtein(word, name);
                    if (distance <= Math.Max(1, name.Length / 4) && distance < bestDistance)
                        (best, bestDistance) = (project, distance);
                }
            }
        }
        return best;

        static IEnumerable<string> Words(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), "[a-z]+").Select(m => m.Value);
    }

    private static int Levenshtein(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }

    /// <summary>Brings the index up to date with the folder. Safe to call at any time.</summary>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!Directory.Exists(_folder))
            {
                _state = LibraryState.NoFolder;
                return;
            }

            var clock = Stopwatch.StartNew();
            var cached = _current.Files.Count > 0 ? _current.Files : LoadCache();
            var byPath = cached.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var files = new List<CachedFile>();
            int read = 0, failed = 0;

            foreach (var path in Directory.EnumerateFiles(_folder, "*.pdf", SearchOption.AllDirectories).Order())
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(_folder, path);
                if (byPath.TryGetValue(relative, out var hit) && hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc)
                {
                    files.Add(hit);
                    continue;
                }

                try
                {
                    files.Add(Index(relative, info, _read(path)));
                    read++;
                }
                catch (Exception ex)
                {
                    // Not cached, so it's tried again next time — e.g. a OneDrive file that
                    // couldn't be downloaded while offline.
                    _log($"Documents: couldn't read {relative}: {ex.Message}");
                    failed++;
                }
            }

            var changed = read > 0 || files.Count != cached.Count;
            _current = new Snapshot(files, BuildIndex(files));
            _state = LibraryState.Ready;
            if (changed) SaveCache(files);

            if (changed || failed > 0 || cached.Count == 0)
            {
                var unreadable = files.Sum(f => f.ImagePages.Count(p => Readings?.HasRead(f, p) != true));
                _log($"Documents: {files.Count} files, {_current.Index.Count} passages " +
                     $"({read} read now in {clock.Elapsed.TotalSeconds:0.0} s" +
                     (unreadable > 0 ? $"; {unreadable} pages are images Norm can't read yet" : "") +
                     (failed > 0 ? $"; {failed} failed" : "") + ")");
            }
        }
        catch (Exception ex)
        {
            _log($"Documents: indexing failed: {ex.Message}");
            if (_state == LibraryState.Indexing) _state = LibraryState.Ready;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CachedFile Index(string relative, FileInfo info, ReadDocument doc)
    {
        // The folder holding the file names the project; files at the top level are their own.
        var folder = Path.GetDirectoryName(relative);
        var document = Path.GetFileNameWithoutExtension(relative);
        var project = string.IsNullOrEmpty(folder) ? document : Path.GetFileName(folder);

        return new CachedFile(
            relative, info.Length, info.LastWriteTimeUtc, project, document, doc.Pages.Count,
            doc.Pages.Where(p => PassageSplitter.WordCount(p.Text) < 5).Select(p => p.Page).ToList(),
            PassageSplitter.Split(project, document, doc.Pages));
    }

    private static ReadDocument ReadPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return new ReadDocument(pdf.GetPages().Select(p => new PageText(p.Number, ContentOrderTextExtractor.GetText(p))).ToList());
    }

    // --- the cache ------------------------------------------------------------------

    public sealed record CachedFile(
        string Path, long Size, DateTime Modified, string Project, string Document, int PageCount,
        List<int> ImagePages, List<Passage> Passages);

    private sealed record CacheFile(int Version, string Folder, List<CachedFile> Files);

    private List<CachedFile> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return [];
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
            // A different folder or an older format means start over rather than trust it.
            return cache is { Version: CacheVersion } && string.Equals(cache.Folder, _folder, StringComparison.OrdinalIgnoreCase)
                ? cache.Files
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void SaveCache(List<CachedFile> files)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(new CacheFile(CacheVersion, _folder, files)));
        }
        catch (IOException ex)
        {
            _log($"Documents: couldn't save the index ({ex.Message}); it will be rebuilt next start.");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        (Readings as IDisposable)?.Dispose();
    }
}
