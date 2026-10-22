using HeyNorm.Knowledge;

namespace HeyNorm.Assistant;

/// <summary>
/// Things the person asked Norm to remember — preferences ("I work in fractions of an inch"),
/// facts about the shop, corrections to answers Norm got wrong. A plain text file, one item
/// per line, so it can be read and fixed by hand from the tray menu: a voice assistant will
/// sometimes save the wrong thing. Hand edits are picked up on the next turn.
///
/// Everything here goes into the system prompt on every turn, which is fine while it's a few
/// dozen lines. Past the cap, saving stops and the person is told to prune it.
/// </summary>
public sealed class Memory
{
    private const string Header =
        "# Things Norm remembers, one per line. Edit freely; lines starting with # are ignored.\n";

    private readonly string _path;
    private readonly int _max;
    private readonly object _gate = new();
    private List<string> _items = [];
    private DateTime _loadedAt = DateTime.MinValue;

    public Memory(string path, int max = 60)
    {
        _path = path;
        _max = max;
    }

    public string Path => _path;

    public IReadOnlyList<string> Items
    {
        get { lock (_gate) return Reload(); }
    }

    public sealed record Saved(string Item, string? Replaced, bool Full);

    /// <summary>
    /// Saves an item. One that closely matches an existing item replaces it, so a correction
    /// ("Titebond III open time is 10 minutes") overwrites what it corrects instead of sitting
    /// next to it, contradicting it.
    /// </summary>
    public Saved Remember(string item)
    {
        item = Tidy(item);
        lock (_gate)
        {
            var items = Reload();
            var index = items.FindIndex(i => Similarity(i, item) >= 0.6);
            if (index >= 0)
            {
                var old = items[index];
                items[index] = item;
                Write(items);
                return new Saved(item, string.Equals(old, item, StringComparison.OrdinalIgnoreCase) ? null : old, false);
            }
            if (items.Count >= _max) return new Saved(item, null, true);

            items.Add(item);
            Write(items);
            return new Saved(item, null, false);
        }
    }

    public sealed record Forgotten(string? Removed, IReadOnlyList<string> Candidates);

    /// <summary>
    /// Removes the one item that best matches. Two equally good matches remove nothing — the
    /// person is asked which — because forgetting the wrong thing is silent and hard to notice.
    /// </summary>
    public Forgotten Forget(string about)
    {
        lock (_gate)
        {
            var items = Reload();
            // Measured: the model passes the whole request ("Forget what I said about
            // fractions."), whose own words drowned the one that mattered. Those are dropped,
            // and the item sharing the most remaining words wins — if it's clearly ahead.
            var wanted = Words(about);
            wanted.ExceptWith(RequestWords);
            var scored = items.Select(i => (Item: i, Score: Words(i).Intersect(wanted).Count()))
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
            if (scored.Count == 0) return new Forgotten(null, []);
            if (scored.Count > 1 && scored[1].Score == scored[0].Score)
                return new Forgotten(null, scored.TakeWhile(x => x.Score == scored[0].Score).Take(3).Select(x => x.Item).ToList());

            items.Remove(scored[0].Item);
            Write(items);
            return new Forgotten(scored[0].Item, []);
        }
    }

    /// <summary>The system prompt addition, or nothing when there's nothing remembered.</summary>
    public string PromptSection()
    {
        var items = Items;
        if (items.Count == 0) return "";
        // Measured: asked a question a remembered correction answered, qwen3 searched the web
        // instead — the prompt's "look it up instead of guessing" won. So say it outright.
        return "\n\nThe person has asked you to remember the following. These are their own preferences, " +
               "facts and corrections: follow them, and trust them over your own knowledge and over search " +
               "results. When one of these answers the question, answer from it without looking anything up.\n" +
               string.Join("\n", items.Select(i => $"- {i}"));
    }

    /// <summary>Re-reads the file if it changed since last time, e.g. edited by hand.</summary>
    private List<string> Reload()
    {
        try
        {
            if (!File.Exists(_path)) return _items = [];
            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _loadedAt) return _items;

            _items = File.ReadAllLines(_path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => Tidy(l.TrimStart('-', '*', ' ')))
                .Where(l => l.Length > 0)
                .ToList();
            _loadedAt = stamp;
        }
        catch (IOException) { /* keep what we had; the file is probably open in an editor */ }
        return _items;
    }

    private void Write(List<string> items)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Header + string.Concat(items.Select(i => $"- {i}\n")));
        _items = items;
        _loadedAt = File.GetLastWriteTimeUtc(_path);
    }

    /// <summary>Opens nothing; makes sure the file exists, with its explanatory header, for the tray to open.</summary>
    public void EnsureFile()
    {
        lock (_gate)
            if (!File.Exists(_path)) Write(Reload());
    }

    private static string Tidy(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");
        // Measured: "They remember that Titebond 3 has an open time of about 10 minutes."
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"^(?:(?:they|the person|i|you)\s+)?(?:want(?:s)? (?:you|me) to\s+)?remember(?:s|ed)?\s+(?:that\s+)?", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (s.Length > 0 && char.IsLower(s[0])) s = char.ToUpperInvariant(s[0]) + s[1..];
        return s.Length > 0 && !".!?".Contains(s[^1]) ? s + "." : s;
    }

    // Matching reuses the plans' tokenizer, so "glued" finds "glue" and centre finds center.
    private static HashSet<string> Words(string s) => KeywordIndex.Tokenize(s).Where(t => !char.IsDigit(t[0])).ToHashSet();

    /// <summary>Same subject — word overlap ignoring numbers, so "open time is 10" replaces "open time is 15".</summary>
    private static double Similarity(string a, string b)
    {
        var (x, y) = (Words(a), Words(b));
        return x.Count == 0 || y.Count == 0 ? 0 : (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    // The words of asking to forget, as opposed to the words of what to forget.
    private static readonly HashSet<string> RequestWords =
        Words("forget forgetting forgot said say told tell mentioned remember remembered thing things stuff " +
              "that this they them their about anymore longer true note");
}
