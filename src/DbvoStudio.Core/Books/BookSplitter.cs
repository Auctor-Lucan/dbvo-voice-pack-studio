using System.Text.RegularExpressions;

namespace DbvoStudio.Core.Books;

/// <summary>One part of a book's spoken text: [Start, End) and whether it ends a paragraph.</summary>
public readonly record struct TextPart(int Start, int End, bool ParagraphEnd)
{
    public int Length => End - Start;
}

/// <summary>
/// Splits a long spoken text into parts the voice engine can read in one request: at paragraph breaks first, then at
/// sentence ends; never inside a word or a sentence (a single sentence longer than the engine allows is the only
/// exception, split at a comma or semicolon). Abbreviations such as "Vol.", "Mr.", "Dr." and "St." don't end a sentence.
/// </summary>
public static partial class BookSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "vol", "mr", "mrs", "ms", "dr", "st", "jr", "sr", "mt", "ft", "no", "vs", "etc", "e.g", "i.e", "approx", "capt", "col", "gen", "lt", "sgt",
        "prof", "rev", "hon", "pt", "ch", "fig", "op", "cf", "ca", "c", "p", "pp",
    };

    /// <summary>A boundary between two parts: where the next part starts and how good a place it is.</summary>
    public readonly record struct Boundary(int At, bool Paragraph);

    [GeneratedRegex(@"\n[ \t]*\n\s*|-{4,}\s*")]
    private static partial Regex ParagraphRx();
    [GeneratedRegex(@"(?<end>[.!?…]+|\.{3})(?<close>[""'’”)\]]*)(?<ws>\s+)(?=[""'“‘(\[]*[\p{Lu}\p{N}<-])")]
    private static partial Regex SentenceRx();

    /// <summary>All places a part may start: after a paragraph break or a separator, or after a sentence end.</summary>
    public static List<Boundary> Boundaries(string text)
    {
        var set = new SortedDictionary<int, bool>();
        foreach (Match m in ParagraphRx().Matches(text))
        {
            int at = m.Index + m.Length;
            if (at > 0 && at < text.Length) set[at] = true;
        }
        foreach (Match m in SentenceRx().Matches(text))
        {
            int at = m.Index + m.Length;
            if (at <= 0 || at >= text.Length) continue;
            // The word before the period: an abbreviation ("Vol.") or a single initial ("J.") doesn't end a sentence.
            if (m.Groups["end"].Value == ".")
            {
                int ws = m.Index - 1;
                while (ws >= 0 && (char.IsLetter(text[ws]) || text[ws] == '.')) ws--;
                var word = text.Substring(ws + 1, m.Index - ws - 1).TrimEnd('.');
                if (Abbreviations.Contains(word)) continue;
                if (word.Length == 1 && char.IsUpper(word[0])) continue;
            }
            if (!set.ContainsKey(at)) set[at] = text.AsSpan(m.Groups["ws"].Index, m.Groups["ws"].Length).Contains('\n');
        }
        return set.Select(kv => new Boundary(kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// The best split of <paramref name="text"/>: every part at most <paramref name="max"/> characters where possible
    /// (never more than <paramref name="ceiling"/>), parts close to <paramref name="target"/>, paragraph breaks preferred.
    /// A text no longer than <paramref name="max"/> stays one part.
    /// </summary>
    public static List<TextPart> Split(string text, int target = 2000, int max = 2500, int ceiling = 5000)
    {
        int n = text.Length;
        if (n == 0) return new();
        max = Math.Max(200, Math.Min(max, ceiling));
        target = Math.Clamp(target, 100, max);
        if (n <= max) return new() { new TextPart(0, n, true) };
        var bounds = Boundaries(text);
        // Positions: 0, every boundary, n. Dynamic programming over them for the lowest total cost.
        var pos = new List<int> { 0 };
        var para = new List<bool> { true };
        foreach (var b in bounds) { pos.Add(b.At); para.Add(b.Paragraph); }
        pos.Add(n); para.Add(true);
        int k = pos.Count;
        var cost = new double[k];
        var prev = new int[k];
        for (int i = 1; i < k; i++) { cost[i] = double.PositiveInfinity; prev[i] = -1; }
        for (int j = 1; j < k; j++)
        {
            for (int i = j - 1; i >= 0; i--)
            {
                int len = pos[j] - pos[i];
                if (len > ceiling) break;
                if (double.IsPositiveInfinity(cost[i])) continue;
                double d = (len - target) / (double)target;
                double c = d * d;
                if (len > max) c += 4 + (len - max) / 100.0;
                if (j < k - 1 && !para[j]) c += 0.08; // a sentence break is fine, a paragraph break is better
                double total = cost[i] + c + 0.05; // a little cost per part keeps the count down
                if (total < cost[j]) { cost[j] = total; prev[j] = i; }
            }
        }
        if (double.IsPositiveInfinity(cost[k - 1]))
            return SplitHard(text, target, ceiling);
        var cuts = new List<int>();
        for (int j = k - 1; j > 0; j = prev[j]) cuts.Add(j);
        cuts.Reverse();
        var parts = new List<TextPart>();
        int start = 0;
        foreach (var j in cuts)
        {
            parts.Add(new TextPart(start, pos[j], para[j]));
            start = pos[j];
        }
        return parts;
    }

    /// <summary>Fallback when a single sentence is longer than the engine allows: split at clause punctuation, then at spaces.</summary>
    private static List<TextPart> SplitHard(string text, int target, int ceiling)
    {
        var parts = new List<TextPart>();
        int start = 0;
        var bounds = Boundaries(text).Select(b => b.At).ToHashSet();
        while (text.Length - start > ceiling)
        {
            int limit = start + ceiling;
            int cut = -1;
            for (int i = limit; i > start + target / 2; i--) if (bounds.Contains(i)) { cut = i; break; }
            if (cut < 0) for (int i = limit; i > start + target / 2; i--) if (i < text.Length && i > 0 && ",;:".Contains(text[i - 1]) && char.IsWhiteSpace(text[i])) { cut = i + 1; break; }
            if (cut < 0) for (int i = limit; i > start; i--) if (char.IsWhiteSpace(text[i - 1])) { cut = i; break; }
            if (cut < 0) cut = limit;
            parts.Add(new TextPart(start, cut, false));
            start = cut;
        }
        parts.Add(new TextPart(start, text.Length, true));
        return parts;
    }

    /// <summary>Parts from stored break offsets (kept when they still fall on a boundary), else a fresh split.</summary>
    public static List<TextPart> FromBreaks(string text, IReadOnlyList<int> breaks)
    {
        var ok = breaks.Where(b => b > 0 && b < text.Length).Distinct().OrderBy(b => b).ToList();
        var paraAt = Boundaries(text).ToDictionary(b => b.At, b => b.Paragraph);
        var parts = new List<TextPart>();
        int start = 0;
        foreach (var b in ok)
        {
            parts.Add(new TextPart(start, b, paraAt.TryGetValue(b, out var p) && p));
            start = b;
        }
        parts.Add(new TextPart(start, text.Length, true));
        return parts;
    }

    /// <summary>The nearest boundary to a wanted offset (for dragging a break in the editor).</summary>
    public static int Snap(string text, int at)
    {
        var b = Boundaries(text);
        if (b.Count == 0) return -1;
        return b.OrderBy(x => Math.Abs(x.At - at)).First().At;
    }
}
