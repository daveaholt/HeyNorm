using System.Text.RegularExpressions;

namespace HeyNorm.Knowledge;

public sealed record SearchHit(Passage Passage, double Score);

/// <summary>
/// BM25 keyword search over passages. Plain keywords suit plans: the questions are about
/// named parts and sizes ("bottom ply", "mast length", "18x50"), and the model rewords the
/// spoken question into search words before it gets here.
///
/// Plans written in British English meet questions transcribed in American English, so both
/// sides are folded to one spelling before matching: centreboard and centerboard, fibres and
/// fibers, mould and mold. "Plywood" folds to "ply", the word plans use.
/// </summary>
public sealed class KeywordIndex
{
    private const double K1 = 1.2, B = 0.75;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i",
        "in", "into", "is", "it", "its", "my", "of", "on", "or", "should", "so", "that", "the", "this",
        "to", "use", "used", "what", "when", "where", "which", "with", "you", "your", "plan", "plans",
        "boat", "say", "says", "tell", "me", "about", "much", "many", "thick", "big",
        // Question verbs: "what is it made of" once ranked an item reading "...made up from..." first.
        "made", "make", "need", "needs", "have", "has", "get", "go", "goes", "put", "there", "any",
        "would", "could", "will", "supposed",
    ];

    // Whole words first, then fragments inside compounds ("centreboard", "microfibres").
    private static readonly (string British, string American)[] Spellings =
    [
        ("centre", "center"), ("fibre", "fiber"), ("metre", "meter"), ("litre", "liter"),
        ("mould", "mold"), ("colour", "color"), ("glueing", "gluing"), ("aluminium", "aluminum"),
        ("tyre", "tire"), ("grey", "gray"), ("plough", "plow"), ("draught", "draft"),
    ];

    // How people ask versus how plans are written: "how long is the mast" against "5200mm
    // overall length"; a centreplate is a steel centreboard, and people say centreboard.
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["plywood"] = "ply",
        ["long"] = "length", ["longer"] = "length",
        ["wide"] = "width", ["wider"] = "width",
        ["centerplate"] = "centerboard",
        ["millimeter"] = "mm", ["millimeters"] = "mm", ["millimetre"] = "mm", ["millimetres"] = "mm",
        ["inch"] = "in", ["inches"] = "in",
    };

    private readonly List<(Passage Passage, Dictionary<string, int> Terms, int Length)> _docs = [];
    private readonly Dictionary<string, int> _docFrequency = [];
    private readonly double _averageLength;

    public KeywordIndex(IEnumerable<Passage> passages)
    {
        foreach (var p in passages)
        {
            // The heading counts toward matching: "rig" should find the rig notes' items.
            var terms = Tokenize($"{p.Heading} {p.Text}")
                .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            _docs.Add((p, terms, terms.Values.Sum()));
            foreach (var term in terms.Keys)
                _docFrequency[term] = _docFrequency.GetValueOrDefault(term) + 1;
        }
        _averageLength = _docs.Count == 0 ? 0 : _docs.Average(d => d.Length);
    }

    public int Count => _docs.Count;

    public IReadOnlyList<SearchHit> Search(string query, int max, string? project = null)
    {
        var terms = Tokenize(query).Distinct().ToList();
        if (terms.Count == 0 || _docs.Count == 0) return [];

        var hits = new List<SearchHit>();
        foreach (var (passage, docTerms, length) in _docs)
        {
            if (project is not null && !string.Equals(passage.Project, project, StringComparison.OrdinalIgnoreCase))
                continue;

            double score = 0;
            foreach (var term in terms)
            {
                if (!docTerms.TryGetValue(term, out var tf)) continue;
                var df = _docFrequency[term];
                var idf = Math.Log(1 + (_docs.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
            }
            if (score > 0) hits.Add(new SearchHit(passage, score));
        }
        return hits.OrderByDescending(h => h.Score).Take(max).ToList();
    }

    /// <summary>
    /// Lowercases, folds spelling, splits sizes into their numbers ("18x50" is searchable as
    /// "18x50", "18" and "50"; "9mm" as "9" and "mm"), drops filler words, and trims plurals
    /// and verb endings so "frames" finds "Frame" and "glued" finds "glue".
    /// </summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var lower = text.ToLowerInvariant().Replace('’', '\'');
        foreach (var (british, american) in Spellings) lower = lower.Replace(british, american);

        foreach (Match m in Regex.Matches(lower, @"[a-z0-9]+(?:x[0-9]+)?"))
        {
            var word = m.Value;
            if (Regex.IsMatch(word, @"^\d+x\d+$"))
            {
                yield return word;
                foreach (var part in word.Split('x')) yield return part;
                continue;
            }

            // "9mm" → "9", "mm"; "36x12g" was caught above only when it's a pure size.
            foreach (Match part in Regex.Matches(word, @"\d+|[a-z]+"))
            {
                var token = Synonyms.GetValueOrDefault(part.Value, part.Value);
                if (StopWords.Contains(token)) continue;
                yield return char.IsDigit(token[0]) ? token : Stem(token);
            }
        }
    }

    /// <summary>
    /// Crude, but the same on both sides, which is all matching needs. The final trailing "e"
    /// goes so glue, glued and gluing all land on "glu", and frame and frames on "fram".
    /// </summary>
    internal static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies")) w = w[..^3] + "y";
        else if (w.Length > 5 && w.EndsWith("ing")) w = w[..^3];
        else if (w.Length > 4 && w.EndsWith("ed")) w = w[..^2];
        else if (w.Length > 4 && (w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("xes") || w.EndsWith("sses"))) w = w[..^2];
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) w = w[..^1];

        return w.Length > 3 && w.EndsWith('e') ? w[..^1] : w;
    }
}
