using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HeyNorm.Knowledge;

/// <summary>What the vision model read in one tile.</summary>
public sealed record TileRead(IReadOnlyList<string> Figures, IReadOnlyList<string> Labels, IReadOnlyList<string> Notes)
{
    public static readonly TileRead Empty = new([], [], []);

    /// <summary>Asked for, and enforced by Ollama, so the answer parses rather than rambles.</summary>
    public static JsonObject Schema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            // "spans" is asked for though it's thrown away: asking what each dimension measures
            // keeps the model on dimension lines and off item-number circles.
            ["dimensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["value"] = new JsonObject { ["type"] = "string" },
                        ["spans"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("value", "spans"),
                },
            },
            ["labels"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["notes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("dimensions", "labels", "notes"),
    };

    public const string Prompt =
        "This is one section of a scanned, hand-lettered boat construction drawing; dimensions are in " +
        "millimetres. Return the dimension lines you can read: for each, its value and what it spans. " +
        "Ignore numbers inside small circles; those are item numbers. Return short labels (such as frame " +
        "names or view titles) and handwritten notes word for word. Leave out anything you cannot read " +
        "with certainty. Never guess a number.";

    /// <summary>Lenient: a missing or malformed part reads as nothing rather than failing the tile.</summary>
    public static TileRead Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new TileRead(
                Array(root, "dimensions").Select(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("value", out var v) ? v.ToString() : "")
                    .Where(v => v.Length > 0).ToList(),
                Strings(root, "labels"),
                Strings(root, "notes"));
        }
        catch (JsonException)
        {
            return Empty;
        }

        static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

        static List<string> Strings(JsonElement e, string name) =>
            Array(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(s => s.Length > 0).ToList();
    }
}

/// <summary>
/// Keeps only what both passes read. A figure counts when the same value was read in
/// overlapping tiles of the two passes; a label or note when overlapping tiles hold closely
/// similar text, since handwriting comes back spelled slightly differently each time.
///
/// What a figure measures is deliberately not kept. Measured: both passes agreed a short
/// dimension near a frame was the spacing between two frames — it wasn't; the real spacing was
/// a different figure on the same line — so a description that survives the cross-check can
/// still be wrong, and in speech it would be believed. Figures are reported by
/// where they sit on the sheet, next to the labels found there, for the person to check.
/// </summary>
public static class DrawingConsensus
{
    public sealed record Outcome(IReadOnlyList<Passage> Passages, int Agreed, int Dropped);

    public static Outcome Agree(string project, string document, int page, Size sheet,
        IReadOnlyList<(SheetTile Tile, TileRead Read)> reads)
    {
        var passages = new List<Passage>();
        int agreed = 0, dropped = 0;

        foreach (var (tile, read) in reads.Where(r => r.Tile.Pass == "A"))
        {
            var others = reads.Where(r => r.Tile.Pass == "B" && r.Tile.Area.IntersectsWith(tile.Area)).Select(r => r.Read).ToList();
            var otherFigures = others.SelectMany(o => o.Figures).Select(Figure).OfType<string>().ToHashSet();

            var figures = new List<string>();
            foreach (var value in read.Figures.Select(Figure).OfType<string>().Distinct())
            {
                if (otherFigures.Contains(value)) { figures.Add(value); agreed++; }
                else dropped++;
            }
            var labels = Confirmed(read.Labels, others.SelectMany(o => o.Labels).ToList(), 0.6);
            var notes = Confirmed(read.Notes, others.SelectMany(o => o.Notes).ToList(), 0.5);
            if (figures.Count == 0 && notes.Count == 0) continue;

            var text = new StringBuilder();
            if (labels.Count > 0) text.Append($"Labels: {string.Join(", ", labels)}. ");
            if (figures.Count > 0) text.Append($"Figures in millimetres: {string.Join(", ", figures)}. ");
            foreach (var note in notes) text.Append($"Note: \"{note}\" ");
            passages.Add(new Passage(project, document, page,
                $"machine-read from the {DrawingSheet.Where(tile.Area, sheet.Width, sheet.Height)} of the sheet",
                null, text.ToString().Trim(), MachineRead: true));
        }
        return new Outcome(passages, agreed, dropped);
    }

    /// <summary>"812", "939mm", "812 mm" → "812". Item numbers and stray single digits aren't figures.</summary>
    internal static string? Figure(string value)
    {
        var m = Regex.Match(value.Trim(), @"^(\d{2,5})(\s*mm)?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> Confirmed(IReadOnlyList<string> mine, IReadOnlyList<string> theirs, double threshold) =>
        mine.Where(m => theirs.Any(t => Similarity(m, t) >= threshold))
            .DistinctBy(Normalize).ToList();

    /// <summary>Overlap of letter-trigrams, so "FRAME 'C'" and "FRAME C" match and "FRAME D" doesn't.</summary>
    internal static double Similarity(string a, string b)
    {
        var (x, y) = (Trigrams(a), Trigrams(b));
        if (x.Count == 0 || y.Count == 0) return Normalize(a) == Normalize(b) ? 1 : 0;
        return (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    private static string Normalize(string s) => Regex.Replace(s.ToUpperInvariant(), @"[^A-Z0-9]", "");

    private static HashSet<string> Trigrams(string s)
    {
        var n = $"  {Normalize(s)} ";
        return Enumerable.Range(0, Math.Max(0, n.Length - 2)).Select(i => n.Substring(i, 3)).ToHashSet();
    }
}
