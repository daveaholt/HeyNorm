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
    private const int CacheVersion = 1;

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

    public IReadOnlyList<string> Projects =>
        _current.Files.Select(f => f.Project).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    /// <summary>Documents with pages Norm can't read yet, e.g. "Skiff 12 01" for a scanned drawing.</summary>
    public IReadOnlyList<string> UnreadableDocuments(string? project = null) =>
        _current.Files
            .Where(f => f.ImagePages.Count > 0 && (project is null || string.Equals(f.Project, project, StringComparison.OrdinalIgnoreCase)))
            .Where(f => f.ImagePages.Count == f.PageCount)
            .Select(f => f.Document).Order().ToList();

    /// <summary>A loosely named project ("skiff") matched to its folder name, if one fits.</summary>
    private string? ResolveProject(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return null;
        var p = project.Trim();
        return Projects.FirstOrDefault(x => x.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                                            p.Contains(x, StringComparison.OrdinalIgnoreCase));
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
            _current = new Snapshot(files, new KeywordIndex(files.SelectMany(f => f.Passages)));
            _state = LibraryState.Ready;
            if (changed) SaveCache(files);

            if (changed || failed > 0 || cached.Count == 0)
            {
                var unreadable = files.Sum(f => f.ImagePages.Count);
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
    }
}
