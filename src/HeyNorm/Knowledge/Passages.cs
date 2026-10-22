using System.Text;
using System.Text.RegularExpressions;

namespace HeyNorm.Knowledge;

/// <summary>
/// One searchable piece of a document — a numbered item, a lettered step, or a paragraph —
/// with enough about where it came from for Norm to say so out loud.
/// </summary>
public sealed record Passage(string Project, string Document, int Page, string? Heading, string? Item, string Text)
{
    /// <summary>"Skiff 12 Specification, page 4, Key to Plans item 2".</summary>
    public string Source
    {
        get
        {
            var where = new StringBuilder($"{Document}, page {Page}");
            if (Heading is not null) where.Append($", {Heading}");
            if (Item is not null) where.Append(Heading is null ? $", item {Item}" : $" item {Item}");
            return where.ToString();
        }
    }
}

/// <summary>A page's text as extracted, in reading order. Empty for a page that is only an image.</summary>
public sealed record PageText(int Page, string Text);

/// <summary>
/// Splits a document's pages into passages. Built around how plans are written: numbered
/// items ("2.  Bottom ply from 2 layers of 9mm…") and lettered steps ("F. Brace the
/// gunwales…") each become a passage, so a search for one item doesn't drag in its
/// neighbours, and everything else is cut into paragraphs.
///
/// Headings matter: item numbers repeat across sections — in a real specification item 53 is
/// one mast in the Key to Plans and another in the notes for an alternative rig — so every passage carries the
/// heading it sits under. Items also run across page breaks, so the text is walked as one
/// stream and a passage keeps the page it started on.
/// </summary>
public static class PassageSplitter
{
    private const int MaxWords = 160;
    private const int MinWords = 12;

    // "2.  Bottom ply", "12 .   22x100", "F. Brace the gunwales". The lettered form needs a
    // capital after it, so "D. Fir" mid-sentence wrapping onto a new line isn't taken for a step.
    private static readonly Regex ItemStart = new(@"^\s*(?<n>\d{1,3}|[A-Z])\s?\.\s+(?=\S)", RegexOptions.Compiled);
    // A heading is a short line in capitals: "KEY TO PLANS", "NOTES FOR THE YAWL RIG".
    private static readonly Regex HeadingLine = new(@"^[A-Z0-9][A-Z0-9 &'’/,.\-]{2,60}$", RegexOptions.Compiled);
    private static readonly Regex PageNumberLine = new(@"^\s*\d{1,3}\s*$", RegexOptions.Compiled);
    // Word exports end a paragraph's last line with a stop and trailing whitespace; wrapped
    // lines have none. The stop can sit inside a bracket or quote — measured: a line ending
    // ").  " was missed, and the glue paragraph after it vanished into a long passage
    // that the search for "glue" no longer ranked.
    private static readonly Regex ParagraphEnd = new(@"[.!?][)""'’”]*[ \t]+$", RegexOptions.Compiled);

    public static List<Passage> Split(string project, string document, IReadOnlyList<PageText> pages)
    {
        var passages = new List<Passage>();
        string? heading = null;
        string? item = null;
        // Items count up within a section. A wrapped line that happens to start "03. Bevel
        // edges" — the tail of "as shown on drg. 03." — must not be taken for item 3.
        var lastItem = 0;
        var startPage = 0;
        var text = new StringBuilder();

        void Flush()
        {
            var body = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            text.Clear();
            if (body.Length == 0) return;

            // A long paragraph is cut at sentence ends so a passage stays small enough to read
            // back; an item is kept whole, since half an item is worse than a long one.
            foreach (var piece in item is null ? BySentences(body) : [body])
                passages.Add(new Passage(project, document, startPage, heading, item, piece));
        }

        foreach (var page in pages)
        {
            var lines = page.Text.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                if (line.Trim().Length == 0)
                {
                    // A blank line ends a paragraph, but not an item.
                    if (item is null) Flush();
                    continue;
                }
                // The page number printed at the top of each page.
                if (i == 0 && PageNumberLine.IsMatch(line)) continue;

                if (IsHeading(line))
                {
                    Flush();
                    heading = Title(line.Trim());
                    item = null;
                    lastItem = 0;
                    continue;
                }

                var match = ItemStart.Match(line);
                if (match.Success && ItemOrder(match.Groups["n"].Value) is { } order && order > lastItem)
                {
                    Flush();
                    item = match.Groups["n"].Value;
                    lastItem = order;
                    startPage = page.Page;
                    text.Append(line[match.Length..]).Append(' ');
                    continue;
                }

                if (text.Length == 0) startPage = page.Page;
                // Prose paragraphs end at a line closing a sentence with a trailing space —
                // how this Word export marks them — and get cut there; items carry on.
                text.Append(line.Trim()).Append(' ');
                if (item is null && ParagraphEnd.IsMatch(lines[i]) && WordCount(text.ToString()) >= MinWords) Flush();
            }
        }
        Flush();

        return MergeFragments(passages);
    }

    /// <summary>1, 2, 3… or A=1, B=2…; null for "03" and the like, which no list starts an item with.</summary>
    private static int? ItemOrder(string label) =>
        char.IsLetter(label[0]) ? label[0] - 'A' + 1
        : label.StartsWith('0') ? null
        : int.Parse(label);

    private static bool IsHeading(string line)
    {
        var t = line.Trim();
        // Needs at least two letters and no lowercase; numbered items and "1 " page marks aren't headings.
        return HeadingLine.IsMatch(t) && t.Count(char.IsLetter) >= 3 && !t.Any(char.IsLower) && !ItemStart.IsMatch(t);
    }

    /// <summary>"KEY TO PLANS" → "Key to Plans", which reads better aloud.</summary>
    private static string Title(string heading)
    {
        var small = new HashSet<string> { "to", "for", "the", "and", "of", "a", "on", "in" };
        var words = heading.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((w, i) =>
            i > 0 && small.Contains(w) ? w : w.Any(char.IsDigit) ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static IEnumerable<string> BySentences(string body)
    {
        if (WordCount(body) <= MaxWords) { yield return body; yield break; }

        var current = new StringBuilder();
        foreach (var sentence in Regex.Split(body, @"(?<=[.!?])\s+(?=[A-Z(])"))
        {
            if (current.Length > 0 && WordCount(current.ToString()) + WordCount(sentence) > MaxWords)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(sentence).Append(' ');
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }

    /// <summary>A stray short paragraph (a lone heading line, a caption) joins the one before it.</summary>
    private static List<Passage> MergeFragments(List<Passage> passages)
    {
        var merged = new List<Passage>();
        foreach (var p in passages)
        {
            if (p.Item is null && merged.Count > 0 && WordCount(p.Text) < MinWords &&
                merged[^1] is { Item: null } prev && prev.Heading == p.Heading)
            {
                merged[^1] = prev with { Text = prev.Text + " " + p.Text };
                continue;
            }
            merged.Add(p);
        }
        return merged;
    }

    internal static int WordCount(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
